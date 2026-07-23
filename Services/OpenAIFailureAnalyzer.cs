using FailureAnalyzer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FailureAnalyzer.Services;

/// <summary>
/// Direct OpenAI API-based failure analyzer implementation.
/// Uses the public OpenAI API (api.openai.com) rather than Azure.
/// Uses shared PromptBuilder and FailureAnalysisParser for provider-agnostic behavior.
/// </summary>
public class OpenAIFailureAnalyzer : IFailureAnalyzer
{
    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxRetries;
    private readonly int _maxOutputTokens;

    public OpenAIFailureAnalyzer(
        string apiKey,
        string model = "gpt-4o",
        int maxRetries = 3,
        int maxOutputTokens = 16384)
    {
        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _apiKey = apiKey;
        _model = model;
        _maxRetries = maxRetries;
        _maxOutputTokens = maxOutputTokens;
    }

    /// <summary>
    /// Analyze a single test failure using OpenAI.
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

        // Call OpenAI API with retry logic
        var rawResponse = await CallWithRetryAsync(userPrompt);

        // Use shared parser
        return FailureAnalysisParser.Parse(rawResponse, failure, "OpenAI");
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
            var rawResponse = await CallWithRetryAsync(prompt);
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
    /// Call OpenAI API with automatic retry on transient failures.
    /// </summary>
    private async Task<string> CallWithRetryAsync(string userPrompt)
    {
        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                var requestBody = new
                {
                    model = _model,
                    messages = new[]
                    {
                        new { role = "system", content = PromptBuilderSimple.SystemPrompt },
                        new { role = "user", content = userPrompt }
                    },
                    temperature = 0.0,
                    // Max output tokens configured per provider in appsettings.json
                    // GPT-4o: 16,384 tokens | GPT-4 Turbo: 4,096 tokens
                    max_tokens = _maxOutputTokens,
                    response_format = new { type = "json_object" }
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _client.DefaultRequestHeaders.Clear();
                _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

                var response = await _client.PostAsync(
                    "https://api.openai.com/v1/chat/completions",
                    content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                        (int)response.StatusCode >= 500)
                    {
                        if (attempt == _maxRetries)
                            throw new HttpRequestException($"{response.StatusCode}: {errorBody}");

                        Console.WriteLine($"  OpenAI rate limit/error. Retry {attempt}/{_maxRetries}...");
                        await Task.Delay(2000 * attempt);
                        continue;
                    }

                    throw new HttpRequestException($"{response.StatusCode}: {errorBody}");
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var responseObj = JObject.Parse(responseJson);
                var messageContent = responseObj["choices"]?[0]?["message"]?["content"]?.ToString();

                return messageContent ?? "{}";
            }
            catch (HttpRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == _maxRetries)
                    throw;

                Console.WriteLine($"  OpenAI failed. Retry {attempt}/{_maxRetries}... ({ex.Message})");
                await Task.Delay(2000 * attempt);
            }
        }

        throw new Exception("All retries to OpenAI exhausted.");
    }
}
