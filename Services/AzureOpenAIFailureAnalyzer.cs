using Azure;
using Azure.AI.OpenAI;
using FailureAnalyzer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FailureAnalyzer.Services;

/// <summary>
/// Azure OpenAI-based failure analyzer implementation.
/// Handles communication with Azure OpenAI service using the official SDK.
/// Uses shared PromptBuilderSimple and FailureAnalysisParser for provider-agnostic behavior.
/// </summary>
public class AzureOpenAIFailureAnalyzer : IFailureAnalyzer
{
    private readonly OpenAIClient _client;
    private readonly string _deployment;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;
    private readonly int _maxOutputTokens;

    public AzureOpenAIFailureAnalyzer(
        string endpoint,
        string apiKey,
        string deployment,
        int maxRetries = 3,
        int retryDelayMs = 1000,
        int maxOutputTokens = 16384)
    {
        _client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _deployment = deployment;
        _maxRetries = maxRetries;
        _retryDelayMs = retryDelayMs;
        _maxOutputTokens = maxOutputTokens;
    }

    /// <summary>
    /// Analyze a single test failure using Azure OpenAI.
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

        // Call Azure OpenAI with retry logic
        var rawResponse = await CallWithRetryAsync(userPrompt);

        // Use shared parser
        return FailureAnalysisParser.Parse(rawResponse, failure, "Azure OpenAI");
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
    /// Call Azure OpenAI API with automatic retry on transient failures.
    /// </summary>
    private async Task<string> CallWithRetryAsync(string userPrompt)
    {
        var chatOptions = new ChatCompletionsOptions
        {
            DeploymentName = _deployment,
            Messages =
            {
                new ChatRequestSystemMessage(PromptBuilderSimple.SystemPrompt),
                new ChatRequestUserMessage(userPrompt)
            },
            Temperature = 0f,
            // Max output tokens configured per provider in appsettings.json
            // Azure OpenAI GPT-4o: 16,384 tokens | GPT-4 Turbo: 4,096 tokens
            MaxTokens = _maxOutputTokens,
            ResponseFormat = ChatCompletionsResponseFormat.JsonObject
        };

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                var response = await _client.GetChatCompletionsAsync(chatOptions);
                var content = response.Value.Choices[0].Message.Content;
                return content ?? "{}";
            }
            catch (RequestFailedException ex) when (ex.Status == 429 || ex.Status >= 500)
            {
                if (attempt == _maxRetries)
                    throw;

                Console.WriteLine($"  Azure OpenAI rate limit/error. Retry {attempt}/{_maxRetries}... ({ex.Message})");
                await Task.Delay(_retryDelayMs * attempt);
            }
            catch (Exception ex)
            {
                if (attempt == _maxRetries)
                    throw;

                Console.WriteLine($"  Azure OpenAI failed. Retry {attempt}/{_maxRetries}... ({ex.Message})");
                await Task.Delay(_retryDelayMs * attempt);
            }
        }

        throw new Exception("All retries to Azure OpenAI exhausted.");
    }
}
