using FailureAnalyzer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace FailureAnalyzer.Services;

/// <summary>
/// Google Gemini API-based failure analyzer implementation.
/// Uses Google AI Studio API (generativelanguage.googleapis.com).
/// FREE tier: 60 requests/minute for Gemini 1.5 Pro, 15 req/min for Flash.
/// </summary>
public class GeminiFailureAnalyzer : IFailureAnalyzer
{
    private readonly HttpClient _client;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxRetries;
    private readonly int _maxOutputTokens;

    public GeminiFailureAnalyzer(
        string apiKey,
        string model = "gemini-flash-latest",
        int maxRetries = 3,
        int maxOutputTokens = 8192)
    {
        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // Increased to 10 minutes for large prompts
        _apiKey = apiKey;
        _model = model;
        _maxRetries = maxRetries;
        _maxOutputTokens = maxOutputTokens;
    }

    /// <summary>
    /// Analyze a single test failure using Gemini with two-call architecture.
    /// Call A: Investigation (notes, root cause, classification)
    /// Call B: Fixes (suggestions, code snippet)
    /// </summary>
    public async Task<FailureAnalysis> AnalyzeFailureAsync(
        TestResult failure,
        string logSnippet,
        string environment,
        string? extraContext)
    {
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // CALL A: Investigation Phase
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Console.WriteLine($"  [Gemini] 📋 Call A: Investigation Phase...");
        var investigationPrompt = PromptBuilderSimple.BuildInvestigationPrompt(failure, logSnippet, environment, extraContext);

        Console.WriteLine("=== INVESTIGATION PROMPT ===");
        Console.WriteLine(investigationPrompt.Length > 600 ? investigationPrompt[..600] + "\n...[truncated]" : investigationPrompt);
        Console.WriteLine("=============================");

        string rawInvestigation;
        try
        {
            rawInvestigation = await CallWithRetryAsync(investigationPrompt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Investigation call failed: {ex.Message}");
            throw;
        }

        // Parse investigation response with repair logic
        JObject investigationData;
        try
        {
            investigationData = FailureAnalysisParser.ParseInvestigation(rawInvestigation, failure, "Gemini");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Investigation parsing failed completely: {ex.Message}");
            return FailureAnalysisParser.FallbackAnalysis(failure, rawInvestigation);
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // CALL B: Fix Suggestions Phase
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Console.WriteLine($"  [Gemini] 🔧 Call B: Fix Suggestions Phase...");

        // Build investigation summary for Call B context
        var investigationSummary = $"""
            Category: {investigationData["category"]}
            Severity: {investigationData["severity"]}
            Error Summary: {investigationData["error_summary"]}
            Primary Cause: {investigationData["primary_cause"]}
            Issue Owner: {investigationData["issue_owner"]}

            Investigation Notes:
            {investigationData["investigation_notes"]}
            """;

        var fixPrompt = PromptBuilderSimple.BuildFixPrompt(failure, investigationSummary, extraContext);

        Console.WriteLine("=== FIX PROMPT ===");
        Console.WriteLine(fixPrompt.Length > 600 ? fixPrompt[..600] + "\n...[truncated]" : fixPrompt);
        Console.WriteLine("==================");

        JObject fixesData;
        try
        {
            var rawFixes = await CallWithRetryAsync(fixPrompt);
            fixesData = FailureAnalysisParser.ParseFixes(rawFixes, failure, "Gemini");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Fix suggestions call failed: {ex.Message}");
            Console.WriteLine($"  ℹ️  Continuing with investigation results only (no fix suggestions)");
            // Use empty fixes as fallback - investigation is still valid
            fixesData = new JObject
            {
                ["suggestions"] = new JArray(),
                ["code_snippet"] = null
            };
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // MERGE: Combine both calls into final FailureAnalysis
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Console.WriteLine($"  [Gemini] ✅ Merging investigation + fixes...");

        List<FailureSuggestion> suggestions;
        try
        {
            suggestions = fixesData["suggestions"]?.ToObject<List<FailureSuggestion>>() ?? new();
        }
        catch
        {
            suggestions = new();
        }

        var category = investigationData["category"]?.Value<string>() ?? "other";
        var issueOwner = investigationData["issue_owner"]?.Value<string>() ?? DefaultIssueOwnerForCategory(category);

        var analysis = new FailureAnalysis
        {
            TestName = failure.TestName,
            ShortName = failure.ShortName,
            Category = category,
            CategoryConfidence = investigationData["category_confidence"]?.Value<int>() ?? 0,
            Severity = investigationData["severity"]?.Value<string>() ?? "medium",
            SeverityConfidence = investigationData["severity_confidence"]?.Value<int>() ?? 0,
            ErrorSummary = investigationData["error_summary"]?.Value<string>() ?? failure.ErrorMessage[..Math.Min(200, failure.ErrorMessage.Length)],
            PrimaryCause = investigationData["primary_cause"]?.Value<string>() ?? "Could not determine cause",
            IssueOwner = issueOwner,
            IssueOwnerConfidence = investigationData["issue_owner_confidence"]?.Value<int>() ?? 0,
            IssueOwnerRationale = investigationData["issue_owner_rationale"]?.Value<string>() ?? "",
            InvestigationNotes = investigationData["investigation_notes"]?.Value<string>(),
            ContributingFactors = investigationData["contributing_factors"]?.ToObject<List<string>>() ?? new(),
            Suggestions = suggestions,
            CodeSnippet = fixesData["code_snippet"]?.Value<string>()
        };

        // Parse fault_attribution if present
        if (investigationData["fault_attribution"] != null)
        {
            try
            {
                var faultAttr = investigationData["fault_attribution"];
                analysis.Attribution = new FaultAttribution
                {
                    Primary = faultAttr["primary"]?.Value<string>() ?? "INDETERMINATE",
                    Confidence = faultAttr["confidence"]?.Value<int>() ?? 0,
                    SecondaryFactors = faultAttr["secondary_contributing_factors"]?.ToObject<List<ContributingFactor>>() ?? new()
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️  Failed to parse fault_attribution: {ex.Message}");
            }
        }

        // Parse suggested_fix if present
        if (investigationData["suggested_fix"] != null)
        {
            try
            {
                var suggestedFix = investigationData["suggested_fix"];
                var filePath = suggestedFix["file_path"]?.Value<string>();

                // Only create SuggestedFix if it's not null/empty (meaning LLM proposed a fix)
                if (!string.IsNullOrWhiteSpace(filePath) || !string.IsNullOrWhiteSpace(suggestedFix["explanation"]?.Value<string>()))
                {
                    analysis.Fix = new SuggestedFix
                    {
                        FilePath = filePath ?? "",
                        CurrentCode = suggestedFix["current_code"]?.Value<string>() ?? "",
                        ProposedCode = suggestedFix["proposed_code"]?.Value<string>() ?? "",
                        Explanation = suggestedFix["explanation"]?.Value<string>() ?? "",
                        ConfidenceLevel = suggestedFix["confidence_level"]?.Value<string>() ?? "low",
                        GatingReason = suggestedFix["gating_reason"]?.Value<string>() ?? ""
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️  Failed to parse suggested_fix: {ex.Message}");
            }
        }

        return analysis;
    }

    /// <summary>
    /// Analyze a single test failure using Gemini with separated evidence for better classification.
    /// Call A: Investigation with TEST-SIDE vs APPLICATION-SIDE evidence
    /// Call B: Fixes (suggestions, code snippet)
    /// </summary>
    public async Task<FailureAnalysis> AnalyzeFailureWithSeparatedEvidenceAsync(
        TestResult failure,
        SeparatedEvidence evidence,
        string environment,
        string? extraContext,
        List<ScreenshotAnalysis>? screenshots = null)
    {
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // CALL A: Investigation Phase with Separated Evidence
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Console.WriteLine($"  [Gemini] 📋 Call A: Investigation Phase...");
        var investigationPrompt = PromptBuilderSimple.BuildInvestigationPromptWithSeparatedEvidence(
            failure, evidence, environment, extraContext, screenshots);

        Console.WriteLine("=== INVESTIGATION PROMPT ===");
        Console.WriteLine(investigationPrompt.Length > 600 ? investigationPrompt[..600] + "\n...[truncated]" : investigationPrompt);
        Console.WriteLine("=============================");

        string rawInvestigation;
        try
        {
            rawInvestigation = await CallWithRetryAsync(investigationPrompt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Investigation call failed: {ex.Message}");
            throw;
        }

        // Parse investigation response with repair logic
        JObject investigationData;
        try
        {
            investigationData = FailureAnalysisParser.ParseInvestigation(rawInvestigation, failure, "Gemini");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Investigation parsing failed completely: {ex.Message}");
            return FailureAnalysisParser.FallbackAnalysis(failure, rawInvestigation);
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // CALL B: Fix Suggestions Phase
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Console.WriteLine($"  [Gemini] 🔧 Call B: Fix Suggestions Phase...");

        // Build investigation summary for Call B context
        var investigationSummary = $"""
            Category: {investigationData["category"]}
            Severity: {investigationData["severity"]}
            Error Summary: {investigationData["error_summary"]}
            Primary Cause: {investigationData["primary_cause"]}
            Issue Owner: {investigationData["issue_owner"]}

            Investigation Notes:
            {investigationData["investigation_notes"]}
            """;

        var fixPrompt = PromptBuilderSimple.BuildFixPrompt(failure, investigationSummary, extraContext);

        Console.WriteLine("=== FIX PROMPT ===");
        Console.WriteLine(fixPrompt.Length > 600 ? fixPrompt[..600] + "\n...[truncated]" : fixPrompt);
        Console.WriteLine("==================");

        JObject fixesData;
        try
        {
            var rawFixes = await CallWithRetryAsync(fixPrompt);
            fixesData = FailureAnalysisParser.ParseFixes(rawFixes, failure, "Gemini");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Fix suggestions call failed: {ex.Message}");
            Console.WriteLine($"  ℹ️  Continuing with investigation results only (no fix suggestions)");
            fixesData = new JObject
            {
                ["suggestions"] = new JArray(),
                ["code_snippet"] = null
            };
        }

        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        // MERGE: Combine both calls into final FailureAnalysis
        // ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Console.WriteLine($"  [Gemini] ✅ Merging investigation + fixes...");

        List<FailureSuggestion> suggestions;
        try
        {
            suggestions = fixesData["suggestions"]?.ToObject<List<FailureSuggestion>>() ?? new();
        }
        catch
        {
            suggestions = new();
        }

        var category = investigationData["category"]?.Value<string>() ?? "other";
        var issueOwner = investigationData["issue_owner"]?.Value<string>() ?? DefaultIssueOwnerForCategory(category);

        var analysis = new FailureAnalysis
        {
            TestName = failure.TestName,
            ShortName = failure.ShortName,
            Category = category,
            CategoryConfidence = investigationData["category_confidence"]?.Value<int>() ?? 0,
            Severity = investigationData["severity"]?.Value<string>() ?? "medium",
            SeverityConfidence = investigationData["severity_confidence"]?.Value<int>() ?? 0,
            ErrorSummary = investigationData["error_summary"]?.Value<string>() ?? failure.ErrorMessage,
            PrimaryCause = investigationData["primary_cause"]?.Value<string>() ?? "",
            IssueOwner = issueOwner,
            IssueOwnerConfidence = investigationData["issue_owner_confidence"]?.Value<int>() ?? 0,
            IssueOwnerRationale = investigationData["issue_owner_rationale"]?.Value<string>() ?? "",
            InvestigationNotes = investigationData["investigation_notes"]?.Value<string>() ?? "",
            ContributingFactors = investigationData["contributing_factors"]?.ToObject<List<string>>() ?? new(),
            Suggestions = suggestions,
            CodeSnippet = fixesData["code_snippet"]?.Value<string>(),

            // Multi-hypothesis support
            Hypotheses = investigationData["hypotheses"]?.ToObject<List<Hypothesis>>() ?? new(),
            PrimaryHypothesis = investigationData["primary_hypothesis"]?.Value<int>() ?? 0,
            OverallConfidence = investigationData["overall_confidence"]?.Value<string>() ?? "medium",
            RecommendedInvestigation = investigationData["recommended_investigation"]?.ToObject<List<string>>() ?? new()
        };

        // Parse fault_attribution if present
        if (investigationData["fault_attribution"] != null)
        {
            try
            {
                var faultAttr = investigationData["fault_attribution"];
                analysis.Attribution = new FaultAttribution
                {
                    Primary = faultAttr["primary"]?.Value<string>() ?? "INDETERMINATE",
                    Confidence = faultAttr["confidence"]?.Value<int>() ?? 0,
                    SecondaryFactors = faultAttr["secondary_contributing_factors"]?.ToObject<List<ContributingFactor>>() ?? new()
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️  Failed to parse fault_attribution: {ex.Message}");
            }
        }

        // Parse suggested_fix if present
        if (investigationData["suggested_fix"] != null)
        {
            try
            {
                var suggestedFix = investigationData["suggested_fix"];
                var filePath = suggestedFix["file_path"]?.Value<string>();

                // Only create SuggestedFix if it's not null/empty (meaning LLM proposed a fix)
                if (!string.IsNullOrWhiteSpace(filePath) || !string.IsNullOrWhiteSpace(suggestedFix["explanation"]?.Value<string>()))
                {
                    analysis.Fix = new SuggestedFix
                    {
                        FilePath = filePath ?? "",
                        CurrentCode = suggestedFix["current_code"]?.Value<string>() ?? "",
                        ProposedCode = suggestedFix["proposed_code"]?.Value<string>() ?? "",
                        Explanation = suggestedFix["explanation"]?.Value<string>() ?? "",
                        ConfidenceLevel = suggestedFix["confidence_level"]?.Value<string>() ?? "low",
                        GatingReason = suggestedFix["gating_reason"]?.Value<string>() ?? ""
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️  Failed to parse suggested_fix: {ex.Message}");
            }
        }

        return analysis;
    }

    /// <summary>
    /// Default issue owner based on category when AI doesn't provide one.
    /// </summary>
    private static string DefaultIssueOwnerForCategory(string category)
    {
        return category switch
        {
            "locator" => "script",
            "timing" => "script",
            "assertion" => "script",
            "app_crash" => "application",
            "environment" => "uncertain",
            "data" => "uncertain",
            _ => "uncertain"
        };
    }

    /// <summary>
    /// Detect cross-cutting patterns across multiple failures.
    /// </summary>
    public async Task<(List<string> Patterns, string EnvNotes)> DetectPatternsAsync(
        List<FailureAnalysis> failures,
        string environment)
    {
        if (failures.Count == 0)
            return (new List<string>(), "No failures to analyze.");

        var patterns = failures
            .SelectMany(f => new[] { f.Category, f.ErrorSummary.Split(' ').FirstOrDefault() ?? "" })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .GroupBy(p => p)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        var envNotes = $"Analyzed {failures.Count} failures in {environment}.";
        return (patterns, envNotes);
    }

    /// <summary>
    /// Call Gemini API with retry logic for transient failures.
    /// </summary>
    private async Task<string> CallWithRetryAsync(string prompt)
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            try
            {
                Console.WriteLine($"  [Gemini] Attempt {attempt}/{_maxRetries}...");
                return await CallGeminiApiAsync(prompt);
            }
            catch (TaskCanceledException ex) when (attempt < _maxRetries)
            {
                // Timeout exception - retry with longer wait
                lastException = ex;
                var delay = TimeSpan.FromSeconds(5 * attempt);
                Console.WriteLine($"  [Gemini] Request timeout after {_client.Timeout.TotalSeconds}s. Retrying in {delay.TotalSeconds}s...");
                await Task.Delay(delay);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("TooManyRequests") || ex.Message.Contains("429"))
            {
                // Quota exhaustion - fail immediately without retry
                Console.WriteLine($"  [Gemini] ❌ Quota exceeded. Daily free-tier limit reached.");
                Console.WriteLine($"  [Gemini] The Gemini free tier has a daily limit. Wait until tomorrow or upgrade your plan.");
                throw new Exception($"Gemini API quota exceeded. Visit https://ai.google.dev/gemini-api/docs/rate-limits for details.", ex);
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                lastException = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                Console.WriteLine($"  [Gemini] Request failed: {ex.Message}. Retrying in {delay.TotalSeconds}s...");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                lastException = ex;
                Console.WriteLine($"  [Gemini] Error: {ex.Message}");
                throw;
            }
        }

        throw new Exception($"Failed after {_maxRetries} attempts", lastException);
    }

    /// <summary>
    /// Call the Gemini API and return the response text.
    /// API docs: https://ai.google.dev/api/rest/v1/models/generateContent
    /// Note: Using v1beta for gemini-1.5-* models; v1 only supports gemini-2.0+
    /// </summary>
    private async Task<string> CallGeminiApiAsync(string prompt)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var systemPrompt = PromptBuilderSimple.SystemPrompt;
        var fullPrompt = $"{systemPrompt}\n\n{prompt}";

        // Log prompt size for debugging
        var promptChars = fullPrompt.Length;
        var estimatedTokens = promptChars / 4; // Rough estimate: 1 token ≈ 4 chars
        Console.WriteLine($"  [Gemini] Prompt size: {promptChars:N0} chars (~{estimatedTokens:N0} tokens)");

        if (estimatedTokens > 30000)
        {
            Console.WriteLine($"  [Gemini] ⚠️  Large prompt detected - this may take longer or hit limits");
        }

        // Gemini API request format
        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = fullPrompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,  // Zero temperature for fully deterministic, consistent responses
                // Using maximum allowed output tokens for Gemini 1.5 Flash/Pro
                // Two-call architecture keeps per-call responses smaller to avoid truncation
                maxOutputTokens = 8192,  // Gemini model hard limit
                responseMimeType = "application/json"
            }
        };

        var jsonContent = JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync(url, content);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  [Gemini] API Error: {response.StatusCode}");
            Console.WriteLine($"  [Gemini] Response: {responseText}");
            throw new HttpRequestException($"Gemini API returned {response.StatusCode}: {responseText}");
        }

        // Parse Gemini response format
        var jsonResponse = JObject.Parse(responseText);
        var candidates = jsonResponse["candidates"];

        if (candidates == null || !candidates.Any())
        {
            throw new Exception("Gemini returned no candidates in response");
        }

        var firstCandidate = candidates[0];
        var finishReason = firstCandidate?["finishReason"]?.ToString();

        // Check if response was truncated
        if (finishReason == "MAX_TOKENS" || finishReason == "LENGTH")
        {
            Console.WriteLine($"  ⚠️  WARNING: Gemini response was TRUNCATED (finishReason: {finishReason})");
            Console.WriteLine($"  ⚠️  The response exceeded the {_maxOutputTokens:N0} token output limit for {_model}.");
            Console.WriteLine($"  ⚠️  This happens when:");
            Console.WriteLine($"       • Investigation notes are very detailed");
            Console.WriteLine($"       • Code fixes are long");
            Console.WriteLine($"       • Multiple suggestions are provided");
            Console.WriteLine($"  💡 The parser will attempt to extract partial results from the incomplete JSON.");
            Console.WriteLine($"  💡 Consider: Reduce log context size or use a model with higher output limits.");
        }

        var content_parts = firstCandidate?["content"]?["parts"];
        if (content_parts == null || !content_parts.Any())
        {
            throw new Exception("Gemini response missing content.parts");
        }

        var textResponse = content_parts[0]?["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(textResponse))
        {
            throw new Exception("Gemini returned empty text response");
        }

        Console.WriteLine("=== GEMINI JSON for " + textResponse.Substring(0, Math.Min(50, textResponse.Length)) + " ===");
        Console.WriteLine(textResponse);
        Console.WriteLine("=== END GEMINI JSON ===");

        return textResponse;
    }
}
