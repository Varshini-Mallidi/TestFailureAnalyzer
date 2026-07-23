using FailureAnalyzer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FailureAnalyzer.Services;

/// <summary>
/// Ollama-based failure analyzer implementation.
/// Handles HTTP communication with local Ollama instance and retry logic.
/// Uses shared PromptBuilder and FailureAnalysisParser for provider-agnostic behavior.
/// </summary>
public class OllamaFailureAnalyzer : IFailureAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly int _maxRetries;
    private readonly int _maxOutputTokens;

    public OllamaFailureAnalyzer(
        string modelName = "llama3",
        string endpointUrl = "http://localhost:11434",
        int maxRetries = 3,
        int maxOutputTokens = 4096)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(endpointUrl) };
        _httpClient.Timeout = TimeSpan.FromMinutes(8);
        _modelName = modelName;
        _maxRetries = maxRetries;
        _maxOutputTokens = maxOutputTokens;
    }

    /// <summary>
    /// Analyze a single test failure using Ollama.
    /// </summary>
    public async Task<FailureAnalysis> AnalyzeFailureAsync(
        TestResult failure,
        string logSnippet,
        string environment,
        string? extraContext)
    {
        // Use shared prompt builder
        var userPrompt = PromptBuilderSimple.BuildFailurePrompt(failure, logSnippet, environment, extraContext);

        Console.WriteLine("=== RAW PROMPT SENT TO AI ===");
        Console.WriteLine(userPrompt);
        Console.WriteLine("=============================");

        // Call Ollama API with retry logic
        var rawResponse = await CallOllamaWithRetryAsync(userPrompt);

        // Use shared parser
        return FailureAnalysisParser.Parse(rawResponse, failure, "Ollama");
    }

    /// <summary>
    /// Detect cross-cutting patterns across multiple failures.
    /// </summary>
    public async Task<(List<string> Patterns, string EnvNotes)> DetectPatternsAsync(
        List<FailureAnalysis> failures,
        string environment)
    {
        if (failures.Count == 0)
            return (new List<string>(), "");

        var summaries = failures.Select(f =>
            $"- [{f.Severity}/{f.Category}] {f.ShortName}: {f.PrimaryCause}");

        var prompt = $$"""
            Given these {{failures.Count}} test failure summaries, identify cross-cutting patterns and return JSON:
            {
              "patterns": ["pattern 1", "pattern 2"],
              "environment_notes": "1-2 sentences about environment concerns"
            }
            Failure summaries:
            {{string.Join("\n", summaries)}}
            """;

        try
        {
            var rawResponse = await CallOllamaWithRetryAsync(prompt);
            var json = FailureAnalysisParser.StripFences(rawResponse);
            var obj = JObject.Parse(json);

            var patterns = obj["patterns"]?.ToObject<List<string>>() ?? new List<string>();
            var envNotes = obj["environment_notes"]?.Value<string>() ?? "";

            return (patterns, envNotes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Warning] Pattern detection failed: {ex.Message}");
            return (new List<string> { $"{failures.Count} failures detected" }, "");
        }
    }

    /// <summary>
    /// Call Ollama API with automatic retry on transient failures.
    /// </summary>
    private async Task<string> CallOllamaWithRetryAsync(string userPrompt)
    {
        var payload = new
        {
            model = _modelName,
            system = PromptBuilderSimple.SystemPrompt,
            prompt = userPrompt,
            stream = false,
            format = "json",
            options = new
            {
                temperature = 0.0,
                num_ctx = 8192,
                // Max output tokens (num_predict) configured per provider in appsettings.json
                // Varies by model: Llama 3.1 70B ~4K, Qwen 2.5 72B ~8K
                num_predict = _maxOutputTokens
            }
        };

        var content = new StringContent(
            JsonConvert.SerializeObject(payload),
            Encoding.UTF8,
            "application/json");

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                Console.WriteLine($"  [Ollama] Attempt {attempt}/{_maxRetries}...");
                var response = await _httpClient.PostAsync("/api/generate", content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException(
                        $"Ollama returned {response.StatusCode}: {errorBody}");
                }

                var responseString = await response.Content.ReadAsStringAsync();
                var jsonDoc = JObject.Parse(responseString);

                var result = jsonDoc["response"]?.ToString();
                if (string.IsNullOrWhiteSpace(result))
                {
                    throw new Exception("Ollama returned empty response");
                }

                return result;
            }
            catch (TaskCanceledException)
            {
                if (attempt == _maxRetries)
                    throw new Exception($"Ollama request timed out after {_httpClient.Timeout.TotalMinutes} minutes. " +
                        "The model may be too slow or not running. Try a smaller/faster model or increase timeout in appsettings.json.");

                Console.WriteLine($"  [Ollama] Timeout. Retry {attempt + 1}/{_maxRetries}...");
                await Task.Delay(2000);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == _maxRetries)
                    throw new Exception($"Failed to connect to Ollama at {_httpClient.BaseAddress}. " +
                        $"Make sure Ollama is running and the model '{_modelName}' is installed. " +
                        $"Error: {ex.Message}");

                Console.WriteLine($"  [Ollama] Connection failed. Retry {attempt + 1}/{_maxRetries}... ({ex.Message})");
                await Task.Delay(2000);
            }
            catch (Exception ex)
            {
                if (attempt == _maxRetries)
                    throw new Exception($"Ollama analysis failed: {ex.Message}");

                Console.WriteLine($"  [Ollama] Error. Retry {attempt + 1}/{_maxRetries}... ({ex.Message})");
                await Task.Delay(2000);
            }
        }

        throw new Exception("All retries to Ollama exhausted.");
    }
}
