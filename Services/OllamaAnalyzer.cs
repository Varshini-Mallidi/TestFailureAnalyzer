using FailureAnalyzer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace FailureAnalyzer.Services;

public class OllamaAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly int _maxRetries;

    private const string SystemPrompt = """
        You are a highly analytical Senior C# Test Automation Architect.
        Your job is to read MSTest and FlaUI test failures and perform forensic analysis.
        You absolutely ABHOR generic advice. 
        You MUST extract specific variables, AutomationIds, ControlTypes, line numbers, and method names directly from the provided RELEVANT LOG OUTPUT and ERROR MESSAGE.
        Return ONLY valid JSON without any markdown formatting.
        """;

    public OllamaAnalyzer(string modelName = "llama3", string endpointUrl = "http://localhost:11434", int maxRetries = 3)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(endpointUrl) };
        _httpClient.Timeout = TimeSpan.FromMinutes(3);
        _modelName = modelName;
        _maxRetries = maxRetries;
    }

    public async Task<FailureAnalysis> AnalyzeFailureAsync(
        TestResult failure, string logSnippet, string environment, string? extraContext)
    {
        var userPrompt = BuildFailurePrompt(failure, logSnippet, environment, extraContext);
        var raw = await CallOllamaWithRetryAsync(userPrompt);
        return ParseFailureAnalysis(raw, failure);
    }

    private static string BuildFailurePrompt(TestResult f, string log, string env, string? ctx){ return $$"""
        Analyze this test failure. 
        
        CRITICAL EXTRACTION RULES:
        1. If the error is a timeout or missing element, you MUST read the 'RELEVANT LOG OUTPUT' and find the LAST element the test tried to interact with before crashing.
        2. In 'primary_cause', explicitly state the exact 'Name' or 'AutomationId' from the log. If no AutomationId or UI element is found, you MUST provide a 1-sentence summary of the stack trace exception. DO NOT leave it blank.
        3. In 'contributing_factors', extract the specific method name from the Stack Trace.
        4. In 'suggestions', provide a specific fix mentioning the exact UI element.

        Return JSON matching this exact schema:
        {
          "category": "locator|timing|environment|data|app_crash|assertion|flaky|other",
          "severity": "critical|high|medium|low",
          "error_summary": "1 sentence plain English summary of what failed",
          "primary_cause": "The root cause. YOU MUST NAME THE EXACT AutomationId OR ELEMENT NAME HERE.",
          "contributing_factors": ["Specific method name", "Specific UI element missing"],
          "suggestions": [
            {
              "action": "Specific actionable fix (e.g., 'Add Wait.Until for AutomationId X')",
              "type": "locator|wait|code|environment",
              "priority": "immediate"
            }
          ],
          "code_snippet": "null"
        }

        TEST: {{f.TestName}}
        ENVIRONMENT: {{env}}
        ERROR MESSAGE: {{f.ErrorMessage}}
        STACK TRACE: {{f.StackTrace}}
        {{(log.Length > 0 ? $"RELEVANT LOG OUTPUT:\n{log}" : "NO LOGS FOUND. Rely only on stack trace.")}}
        {{(!string.IsNullOrWhiteSpace(ctx) ? $"=== RELEVANT SOURCE CODE (RAG) ===\n{ctx}" : "")}}
        """;
        }
       

    private FailureAnalysis ParseFailureAnalysis(string raw, TestResult failure)
    {
        try
        {
            var json = StripFences(raw);
            Console.WriteLine($"\n--- RAW OLLAMA RESPONSE ---\n{json}\n---------------------------\n");

            var obj = JObject.Parse(json);

            return new FailureAnalysis
            {
                TestName = failure.TestName,
                ShortName = failure.ShortName,
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
            Console.WriteLine($"  Warning: Could not parse Ollama response for {failure.ShortName}: {ex.Message}");
            return FallbackAnalysis(failure);
        }
    }

    public async Task<(List<string> Patterns, string EnvNotes)> DetectPatternsAsync(
        List<FailureAnalysis> failures, string environment)
    {
        if (failures.Count == 0) return (new(), "");
        var summaries = failures.Select(f => $"- [{f.Severity}/{f.Category}] {f.ShortName}: {f.PrimaryCause}");
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
            var raw = await CallOllamaWithRetryAsync(prompt);
            var obj = JObject.Parse(StripFences(raw));
            return (obj["patterns"]?.ToObject<List<string>>() ?? new(), obj["environment_notes"]?.Value<string>() ?? "");
        }
        catch
        {
            return (new List<string> { $"{failures.Count} failures detected" }, "");
        }
    }

    private async Task<string> CallOllamaWithRetryAsync(string userPrompt)
    {
        var payload = new
        {
            model = _modelName,
            system = SystemPrompt,
            prompt = userPrompt,
            stream = false,
            format = "json",
            options = new { temperature = 0.0 } // 0.0 temperature forces the AI to be completely literal and analytical, stopping it from making up generic advice
        };

        var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                var response = await _httpClient.PostAsync("/api/generate", content);
                response.EnsureSuccessStatusCode();
                var responseString = await response.Content.ReadAsStringAsync();
                var jsonDoc = JObject.Parse(responseString);
                return jsonDoc["response"]?.ToString() ?? "{}";
            }
            catch (Exception ex)
            {
                if (attempt == _maxRetries) throw;
                Console.WriteLine($"  Ollama busy/failed. Retry {attempt}/{_maxRetries}... ({ex.Message})");
                await Task.Delay(2000);
            }
        }
        throw new Exception("All retries to Ollama exhausted.");
    }

    private static string StripFences(string raw) => raw.Replace("```json", "").Replace("```", "").Trim();

    private static FailureAnalysis FallbackAnalysis(TestResult f) => new()
    {
        TestName = f.TestName,
        ShortName = f.ShortName,
        Category = "other",
        Severity = "medium",
        ErrorSummary = f.ErrorMessage.Length > 200 ? f.ErrorMessage[..200] + "…" : f.ErrorMessage,
        PrimaryCause = "Local AI analysis failed.",
        Suggestions = new() { new FailureSuggestion { Action = "Review manually", Type = "code", Priority = "immediate" } }
    };
}