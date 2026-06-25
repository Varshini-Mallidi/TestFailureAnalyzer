using Azure;
using Azure.AI.OpenAI;
using FailureAnalyzer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FailureAnalyzer.Services;

public class AzureOpenAIAnalyzer
{
    private readonly OpenAIClient _client;
    private readonly string _deployment;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;

    private const string SystemPrompt = """
        You are a senior C# test automation engineer with deep expertise in:
        - MSTest framework and TRX result files
        - FlaUI UI automation library (Windows desktop automation)
        - Azure DevOps CI/CD pipeline test failures
        - Common failure patterns: stale element references, timing/wait issues, 
          locator brittleness, environment differences between local and CI agents,
          application startup failures on hosted runners

        Your job is to analyze test failure artifacts and return structured JSON only.
        Be specific and actionable. Reference actual method names, line numbers, and 
        class names from the stack traces when available.
        Return ONLY valid JSON — no markdown fences, no preamble, no explanation outside the JSON.
        """;

    public AzureOpenAIAnalyzer(string endpoint, string apiKey, string deployment,
        int maxRetries = 3, int retryDelayMs = 1000)
    {
        _client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _deployment = deployment;
        _maxRetries = maxRetries;
        _retryDelayMs = retryDelayMs;
    }

    // ── Per-failure analysis ────────────────────────────────────────────────

    public async Task<FailureAnalysis> AnalyzeFailureAsync(
        TestResult failure, string logSnippet, string environment, string? extraContext)
    {
        var userPrompt = BuildFailurePrompt(failure, logSnippet, environment, extraContext);

        var raw = await CallWithRetryAsync(userPrompt);
        return ParseFailureAnalysis(raw, failure);
    }

    private static string BuildFailurePrompt(TestResult f, string log, string env, string? ctx) => $$"""
        Analyze this MSTest + FlaUI test failure and return JSON matching this exact schema:
        {
          "category": "locator|timing|environment|data|app_crash|assertion|flaky|other",
          "severity": "critical|high|medium|low",
          "error_summary": "1-2 sentence plain English summary of what failed",
          "primary_cause": "1-2 sentences explaining the most likely root cause",
          "contributing_factors": ["factor 1", "factor 2", "factor 3"],
          "suggestions": [
            {
              "action": "specific actionable fix description",
              "type": "locator|wait|code|environment|data|infrastructure",
              "priority": "immediate|soon|later"
            }
          ],
          "code_snippet": "optional C# snippet showing the fix, or null"
        }

        TEST: {{f.TestName}}
        ENVIRONMENT: {{env}}
        DURATION: {{f.Duration}}
        {{(ctx != null ? $"CONTEXT: {ctx}" : "")}}

        ERROR MESSAGE:
        {{f.ErrorMessage}}

        STACK TRACE:
        {{f.StackTrace}}

        {{(log.Length > 0 ? $"RELEVANT LOG OUTPUT:\n{log}" : "")}}
        """;

    private FailureAnalysis ParseFailureAnalysis(string raw, TestResult failure)
    {
        try
        {
            var json = StripFences(raw);
            var obj = JObject.Parse(json);

            return new FailureAnalysis
            {
                TestName = failure.TestName,
                ShortName = failure.ShortName,
                AttachmentPaths = failure.AttachmentPaths,
                Category = obj["category"]?.Value<string>() ?? "other",
                Severity = obj["severity"]?.Value<string>() ?? "medium",
                ErrorSummary = obj["error_summary"]?.Value<string>() ?? failure.ErrorMessage[..Math.Min(200, failure.ErrorMessage.Length)],
                PrimaryCause = obj["primary_cause"]?.Value<string>() ?? "",
                ContributingFactors = obj["contributing_factors"]?.ToObject<List<string>>() ?? new(),
                Suggestions = obj["suggestions"]?.ToObject<List<FailureSuggestion>>() ?? new(),
                CodeSnippet = obj["code_snippet"]?.Value<string>()
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Could not parse AI response for {failure.ShortName}: {ex.Message}");
            return FallbackAnalysis(failure);
        }
    }

    // ── Cross-run pattern detection ─────────────────────────────────────────

    public async Task<(List<string> Patterns, string EnvNotes)> DetectPatternsAsync(
        List<FailureAnalysis> failures, string environment)
    {
        if (failures.Count == 0) return (new(), "");

        var summaries = failures.Select(f =>
            $"- [{f.Severity}/{f.Category}] {f.ShortName}: {f.PrimaryCause}");

        var prompt = $$"""
            Given these {{failures.Count}} test failure summaries from an MSTest + FlaUI run on {{environment}},
            identify cross-cutting patterns and return JSON:
            {
              "patterns": ["pattern 1", "pattern 2", "pattern 3"],
              "environment_notes": "1-2 sentences about environment or infrastructure concerns"
            }

            Failure summaries:
            {{string.Join("\n", summaries)}}

            Categories found: {{string.Join(", ", failures.Select(f => f.Category).Distinct())}}
            Severities: {{string.Join(", ", failures.Select(f => f.Severity).Distinct())}}
            """;

        try
        {
            var raw = await CallWithRetryAsync(prompt);
            var obj = JObject.Parse(StripFences(raw));
            var patterns = obj["patterns"]?.ToObject<List<string>>() ?? new();
            var envNotes = obj["environment_notes"]?.Value<string>() ?? "";
            return (patterns, envNotes);
        }
        catch
        {
            return (new List<string> { $"{failures.Count} failures detected across this run" }, "");
        }
    }

    // ── HTTP call with retry ────────────────────────────────────────────────

    private async Task<string> CallWithRetryAsync(string userPrompt)
    {
        var messages = new ChatCompletionsOptions(_deployment, new ChatRequestMessage[]
    {
         new ChatRequestSystemMessage(SystemPrompt),
        new ChatRequestUserMessage(userPrompt)
    })
        {
            MaxTokens = 1500,
            Temperature = 0.2f   // Low temp for consistent structured output
        };

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                var response = await _client.GetChatCompletionsAsync(messages);
                return response.Value.Choices[0].Message.Content;
            }
            catch (RequestFailedException ex) when (ex.Status == 429 || ex.Status >= 500)
            {
                if (attempt == _maxRetries) throw;
                var delay = _retryDelayMs * (int)Math.Pow(2, attempt - 1);
                Console.WriteLine($"  Retry {attempt}/{_maxRetries} after {delay}ms (HTTP {ex.Status})");
                await Task.Delay(delay);
            }
        }

        throw new Exception("All retries exhausted");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string StripFences(string raw)
        => raw.Replace("```json", "").Replace("```", "").Trim();

    private static FailureAnalysis FallbackAnalysis(TestResult f) => new()
    {
        TestName = f.TestName,
        ShortName = f.ShortName,
        AttachmentPaths = f.AttachmentPaths,
        Category = "other",
        Severity = "medium",
        ErrorSummary = f.ErrorMessage.Length > 200 ? f.ErrorMessage[..200] + "…" : f.ErrorMessage,
        PrimaryCause = "AI analysis unavailable — review stack trace manually.",
        ContributingFactors = new() { f.ErrorMessage },
        Suggestions = new()
        {
            new FailureSuggestion
            {
                Action = "Review stack trace and error message manually",
                Type = "code",
                Priority = "immediate"
            }
        }
    };
}