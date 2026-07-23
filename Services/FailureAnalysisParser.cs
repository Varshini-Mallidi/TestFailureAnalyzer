using FailureAnalyzer.Models;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FailureAnalyzer.Services;

/// <summary>
/// Centralizes all JSON parsing and response processing logic for AI-generated failure analyses.
/// This class is provider-agnostic and handles common issues like truncated JSON, malformed responses,
/// and missing fields.
/// </summary>
public static class FailureAnalysisParser
{
    /// <summary>
    /// Parse AI response into FailureAnalysis, with automatic repair for truncated/malformed JSON.
    /// </summary>
    /// <param name="rawResponse">Raw text response from AI</param>
    /// <param name="failure">Original test failure (used for fallback)</param>
    /// <param name="providerName">Name of provider (for logging, e.g., "Ollama", "Azure OpenAI")</param>
    /// <returns>Parsed or fallback FailureAnalysis</returns>
    public static FailureAnalysis Parse(string rawResponse, TestResult failure, string providerName = "AI")
    {
        var json = StripFences(rawResponse);

        // DEBUG: Always show what the AI returned
        Console.WriteLine($"\n=== {providerName.ToUpper()} JSON for {failure.ShortName} ===");
        Console.WriteLine(json.Length > 800 ? json[..800] + "\n...[truncated]" : json);
        Console.WriteLine($"=== END {providerName.ToUpper()} JSON ===\n");

        try
        {
            return ParseJson(json, failure);
        }
        catch (Exception ex)
        {
            // First attempt failed — try repair
            Console.WriteLine($"  [Warning] Initial parse failed for {failure.ShortName} ({ex.Message}) — attempting repair");
            try
            {
                var repaired = RepairTruncatedJson(json);
                Console.WriteLine($"\n--- REPAIRED {providerName.ToUpper()} RESPONSE ---\n{repaired}\n---------------------------\n");
                return ParseJson(repaired, failure);
            }
            catch (Exception ex2)
            {
                // Repair failed — try aggressive field extraction as last resort
                Console.WriteLine($"  [Warning] Repair failed ({ex2.Message}) — attempting field extraction");
                try
                {
                    var extracted = ExtractPartialJson(json, failure);
                    if (extracted != null)
                    {
                        Console.WriteLine($"  [Success] Partial field extraction succeeded");
                        return extracted;
                    }
                }
                catch (Exception ex3)
                {
                    Console.WriteLine($"  [Warning] Field extraction also failed: {ex3.Message}");
                }

                Console.WriteLine($"  Warning: Could not parse {providerName} response for {failure.ShortName} even after all repair attempts");
                Console.WriteLine($"\n--- RAW (UNPARSEABLE) {providerName.ToUpper()} RESPONSE ---\n{rawResponse}\n---------------------------\n");
                return FallbackAnalysis(failure, rawResponse);
            }
        }
    }

    /// <summary>
    /// Parse validated JSON into FailureAnalysis with smart defaults for missing fields.
    /// </summary>
    private static FailureAnalysis ParseJson(string json, TestResult failure)
    {
        var obj = JObject.Parse(json);
        var notes = obj["investigation_notes"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(notes))
            Console.WriteLine($"  [AI Scratchpad] {notes}");

        List<FailureSuggestion> suggestions;
        try
        {
            suggestions = obj["suggestions"]?.ToObject<List<FailureSuggestion>>() ?? new();
        }
        catch
        {
            // A malformed suggestions block shouldn't sink the entire diagnosis —
            // everything else parsed fine, so keep it and just drop suggestions.
            suggestions = new();
        }

        var category = obj["category"]?.Value<string>() ?? "other";

        // Parse hypotheses (NEW multi-hypothesis support)
        List<Hypothesis> hypotheses;
        try
        {
            hypotheses = obj["hypotheses"]?.ToObject<List<Hypothesis>>() ?? new();
        }
        catch
        {
            hypotheses = new();
        }

        var primaryHypothesis = obj["primary_hypothesis"]?.Value<int>() ?? 0;
        var overallConfidence = obj["overall_confidence"]?.Value<string>() ?? "";

        List<string> recommendedInvestigation;
        try
        {
            recommendedInvestigation = obj["recommended_investigation"]?.ToObject<List<string>>() ?? new();
        }
        catch
        {
            recommendedInvestigation = new();
        }

        // Legacy single-answer fields (kept for backward compatibility)
        // If hypotheses exist, derive these from primary hypothesis
        string issueOwner;
        string issueOwnerRationale;
        int issueOwnerConfidence;

        if (hypotheses.Any() && primaryHypothesis < hypotheses.Count)
        {
            var primary = hypotheses[primaryHypothesis];
            issueOwner = primary.IssueOwner;
            issueOwnerRationale = $"Primary hypothesis: {primary.Explanation}";
            issueOwnerConfidence = primary.Confidence;

            Console.WriteLine($"  [DEBUG] Using hypothesis-based ownership: '{issueOwner}' ({issueOwnerConfidence}% confidence)");
            Console.WriteLine($"  [DEBUG] Found {hypotheses.Count} hypothesis/hypotheses, overall confidence: {overallConfidence}");
        }
        else
        {
            // Fallback to old single-answer fields
            issueOwner = obj["issue_owner"]?.Value<string>() ?? "";
            issueOwnerRationale = obj["issue_owner_rationale"]?.Value<string>() ?? "";
            issueOwnerConfidence = obj["issue_owner_confidence"]?.Value<int>() ?? 0;

            if (string.IsNullOrWhiteSpace(issueOwner))
            {
                Console.WriteLine($"  [DEBUG] issue_owner was MISSING - falling back to category-based default for '{category}'");
                issueOwner = DefaultIssueOwnerForCategory(category);
                if (string.IsNullOrWhiteSpace(issueOwnerRationale))
                    issueOwnerRationale = "Inferred from failure category only — the AI response didn't include an ownership call.";
            }
            else
            {
                Console.WriteLine($"  [DEBUG] issue_owner found: '{issueOwner}' with rationale: '{issueOwnerRationale}'");
            }
        }

        return new FailureAnalysis
        {
            TestName = failure.TestName,
            ShortName = failure.ShortName,
            Category = category,
            CategoryConfidence = obj["category_confidence"]?.Value<int>() ?? 0,
            Severity = obj["severity"]?.Value<string>() ?? "medium",
            SeverityConfidence = obj["severity_confidence"]?.Value<int>() ?? 0,
            ErrorSummary = obj["error_summary"]?.Value<string>() ?? failure.ErrorMessage[..Math.Min(200, failure.ErrorMessage.Length)],
            PrimaryCause = obj["primary_cause"]?.Value<string>() ?? "",

            // Multi-hypothesis support (NEW)
            Hypotheses = hypotheses,
            PrimaryHypothesis = primaryHypothesis,
            OverallConfidence = overallConfidence,
            RecommendedInvestigation = recommendedInvestigation,

            // Legacy single-answer fields
            IssueOwner = issueOwner,
            IssueOwnerConfidence = issueOwnerConfidence,
            IssueOwnerRationale = issueOwnerRationale,

            InvestigationNotes = notes ?? "",  // ADDED: Store full investigation notes
            ContributingFactors = ParseContributingFactors(obj["contributing_factors"]),
            Suggestions = suggestions,
            CodeSnippet = obj["code_snippet"]?.Value<string>(),

            // Evidence sections (NEW)
            Evidence = ParseEvidenceSections(obj, failure)
        };
    }

    /// <summary>
    /// Fallback logic when AI doesn't provide issue_owner. With evidence-based classification,
    /// we should avoid assuming defaults and prefer "insufficient_evidence" for ambiguous cases.
    /// Only assign "script" or "application" when the category itself provides strong evidence.
    /// </summary>
    private static string DefaultIssueOwnerForCategory(string category) => category switch
    {
        "assertion" => "application",           // Assertion failures typically indicate app behavior mismatch
        "app_crash" => "application",           // Clear evidence of application issue
        "locator" => "insufficient_evidence",   // Could be wrong locator (script) OR element didn't render (app)
        "timing" => "insufficient_evidence",    // Could be wrong wait (script) OR slow app (application)
        "environment" => "script",              // Test environment setup issue
        "data" => "script",                     // Test data issue
        _ => "insufficient_evidence"            // Default to insufficient when unclear
    };

    /// <summary>
    /// Last-resort field extraction from severely malformed JSON.
    /// Uses regex to extract individual fields even if JSON structure is broken.
    /// </summary>
    private static FailureAnalysis? ExtractPartialJson(string brokenJson, TestResult failure)
    {
        // Try to extract at least some fields using regex patterns
        var extractedFields = new Dictionary<string, string?>();

        // Pattern: "field_name": "value"
        var stringFieldPattern = @"""(\w+)""\s*:\s*""([^""\\]*(?:\\.[^""\\]*)*)""";
        var stringMatches = Regex.Matches(brokenJson, stringFieldPattern);
        foreach (Match match in stringMatches)
        {
            if (match.Groups.Count >= 3)
            {
                var key = match.Groups[1].Value;
                var value = match.Groups[2].Value;
                if (!extractedFields.ContainsKey(key))
                    extractedFields[key] = value;
            }
        }

        // Pattern: "field_name": 123
        var intFieldPattern = @"""(\w+)""\s*:\s*(\d+)";
        var intMatches = Regex.Matches(brokenJson, intFieldPattern);
        foreach (Match match in intMatches)
        {
            if (match.Groups.Count >= 3)
            {
                var key = match.Groups[1].Value;
                var value = match.Groups[2].Value;
                if (!extractedFields.ContainsKey(key))
                    extractedFields[key] = value;
            }
        }

        // If we got at least category or primary_cause, build a partial analysis
        if (extractedFields.ContainsKey("category") || extractedFields.ContainsKey("primary_cause") || extractedFields.ContainsKey("investigation_notes"))
        {
            var category = extractedFields.GetValueOrDefault("category", "other");
            var issueOwner = extractedFields.GetValueOrDefault("issue_owner", DefaultIssueOwnerForCategory(category));

            // Try to extract primary cause from investigation notes if not present
            var primaryCause = extractedFields.GetValueOrDefault("primary_cause", null);
            if (string.IsNullOrWhiteSpace(primaryCause) && extractedFields.ContainsKey("investigation_notes"))
            {
                // Extract first meaningful sentence from investigation notes as primary cause
                var notes = extractedFields["investigation_notes"] ?? "";
                var sentences = notes.Split(['.', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                primaryCause = sentences.FirstOrDefault(s => s.Length > 50 && s.Length < 300) 
                    ?? sentences.FirstOrDefault() 
                    ?? "See investigation notes for details";
            }

            Console.WriteLine($"  ⚠️  Partial extraction succeeded - extracted {extractedFields.Count} fields from malformed JSON");

            return new FailureAnalysis
            {
                TestName = failure.TestName,
                ShortName = failure.ShortName,
                Category = category,
                CategoryConfidence = extractedFields.ContainsKey("category_confidence") && 
                    int.TryParse(extractedFields["category_confidence"], out var cc) ? cc : 0,
                Severity = extractedFields.GetValueOrDefault("severity", "medium"),
                SeverityConfidence = extractedFields.ContainsKey("severity_confidence") && 
                    int.TryParse(extractedFields["severity_confidence"], out var sc) ? sc : 0,
                ErrorSummary = extractedFields.GetValueOrDefault("error_summary", 
                    failure.ErrorMessage[..Math.Min(200, failure.ErrorMessage.Length)]),
                PrimaryCause = primaryCause ?? "See detailed investigation notes below",
                IssueOwner = issueOwner,
                IssueOwnerConfidence = extractedFields.ContainsKey("issue_owner_confidence") && 
                    int.TryParse(extractedFields["issue_owner_confidence"], out var ioc) ? ioc : 0,
                IssueOwnerRationale = extractedFields.GetValueOrDefault("issue_owner_rationale", 
                    "Classification based on partial AI response - review investigation notes for full context"),
                InvestigationNotes = "⚠️ WARNING: AI response was truncated (exceeded 8,192 token output limit).\n" +
                    "The following analysis is PARTIAL and may be incomplete:\n\n" +
                    extractedFields.GetValueOrDefault("investigation_notes", 
                        $"[Partial Extraction] The AI response was cut off mid-generation.\n" +
                        $"Extracted {extractedFields.Count} fields from incomplete JSON.\n\n" +
                        $"To avoid truncation:\n" +
                        $"• Switch to Azure OpenAI (16K token limit vs Gemini's 8K)\n" +
                        $"• Reduce log context size (fewer log lines)\n" +
                        $"• Use a model with higher output limits"),
                ContributingFactors = new List<string>(),
                Suggestions = new List<FailureSuggestion>(),
                CodeSnippet = extractedFields.GetValueOrDefault("code_snippet", null)
            };
        }

        return null;
    }

    /// <summary>
    /// String-aware JSON repair for responses truncated mid-generation (hit num_predict
    /// before the model could close its output). Walks the text tracking string/escape
    /// state and open-bracket depth, trims back to the last position outside any string,
    /// strips a dangling trailing comma/colon, then appends the closing brackets needed
    /// to balance whatever was left open.
    /// </summary>
    public static string RepairTruncatedJson(string json)
    {
        bool inString = false;
        bool escape = false;
        int lastSafeIndex = -1;
        var stack = new Stack<char>();

        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];

            if (escape) { escape = false; continue; }
            if (inString && c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; if (!inString) lastSafeIndex = i; continue; }
            if (inString) continue;

            if (c == '{' || c == '[') stack.Push(c);
            else if (c == '}' || c == ']') { if (stack.Count > 0) stack.Pop(); }

            lastSafeIndex = i;
        }

        var safe = inString && lastSafeIndex >= 0 ? json[..(lastSafeIndex + 1)] : json;

        // Recompute the open-bracket stack over the (possibly trimmed) safe text.
        stack.Clear();
        inString = false; escape = false;
        for (int i = 0; i < safe.Length; i++)
        {
            char c = safe[i];
            if (escape) { escape = false; continue; }
            if (inString && c == '\\') { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{' || c == '[') stack.Push(c);
            else if (c == '}' || c == ']') { if (stack.Count > 0) stack.Pop(); }
        }

        var trimmed = safe.TrimEnd().TrimEnd(',', ':');

        var sb = new StringBuilder(trimmed);
        foreach (var open in stack)
            sb.Append(open == '{' ? '}' : ']');

        return sb.ToString();
    }

    /// <summary>
    /// Truncate text if it contains repetition loops (LLM got stuck repeating same phrase).
    /// </summary>
    private static string TruncateIfRepeating(string text, int maxLength = 600)
    {
        // Hard cap for safety
        if (text.Length > maxLength)
            text = text[..maxLength] + "…";

        // Detect repetition loop: same 30-char window repeated 4+ times
        // e.g. "CAUSAL GAP: CAUSAL GAP: CAUSAL GAP: CAUSAL GAP:"
        if (text.Length > 80)
        {
            var window = text[..30];
            var escaped = Regex.Escape(window);
            var matches = Regex.Matches(text, escaped);
            if (matches.Count >= 4)
            {
                // Repetition loop — keep only the first occurrence
                int firstEnd = matches[0].Index + matches[0].Length;
                text = text[..firstEnd] + "…";
            }
        }

        return text;
    }

    /// <summary>
    /// Parse contributing factors array, handling various JSON formats the AI might return.
    /// </summary>
    private static List<string> ParseContributingFactors(JToken? token)
    {
        if (token == null) return new();

        var result = new List<string>();

        foreach (var item in token)
        {
            string text;

            if (item.Type == JTokenType.String)
            {
                // Normal case — plain string
                text = item.Value<string>() ?? "";
            }
            else if (item.Type == JTokenType.Object)
            {
                // AI returned { "LAST SUCCESS:": "value" } — iterate ALL properties
                // (old code only looked for "label"/"text" keys which don't exist here)
                var sb = new StringBuilder();
                foreach (var prop in ((JObject)item).Properties())
                {
                    if (sb.Length > 0) sb.Append(" ");
                    sb.Append(prop.Name.TrimEnd(':', ' ').Trim());
                    sb.Append(": ");
                    sb.Append(prop.Value.Type == JTokenType.String
                        ? prop.Value.Value<string>()
                        : prop.Value.ToString());
                }
                text = sb.ToString();
            }
            else
            {
                // Fallback — just stringify it
                text = item.ToString();
            }

            // Fix double-escaped Windows paths: C:\\la\\ → C:\la\
            text = text.Replace("\\\\", "\\");
            text = text
            .Replace("\f", "\\")
            .Replace("\b", "\\")
            .Replace("\v", "\\")
            .Replace("\t", " ")
            .Replace("\r\n", " ")
            .Replace("\r", " ")
            .Replace("\n", " ");



            // Also strip any remaining "in C:..." with no line number after it
            text = Regex.Replace(
                text,
                @"\s+in\s+[A-Za-z]:[\w\s.\\]*?\.cs\b",
                "",
                RegexOptions.IgnoreCase);


            // Guard against LLaMA 3 repetition loops
            text = TruncateIfRepeating(text, maxLength: 600);

            if (!string.IsNullOrWhiteSpace(text))
                result.Add(text);
        }

        return result;
    }

    /// <summary>
    /// Normalize a value against an allowed set, with fallback.
    /// </summary>
    public static string Normalise(string? value, string[] allowed, string fallback)
    {
        if (value == null) return fallback;
        var v = value.ToLowerInvariant().Trim();
        return allowed.Contains(v) ? v : fallback;
    }

    /// <summary>
    /// Strip markdown JSON fences and extract JSON from AI response.
    /// Handles cases where AI adds text before/after the JSON.
    /// </summary>
    public static string StripFences(string raw)
    {
        // Remove markdown code fences
        raw = raw.Replace("```json", "").Replace("```", "").Trim();

        // Try to find the first '{' and last '}'
        int firstBrace = raw.IndexOf('{');
        int lastBrace = raw.LastIndexOf('}');

        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            // Extract only the JSON portion
            raw = raw.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return raw.Trim();
    }

    /// <summary>
    /// Generate fallback analysis when AI response cannot be parsed.
    /// </summary>
    public static FailureAnalysis FallbackAnalysis(TestResult failure, string? rawResponse = null)
    {
        // The old message ("Local AI analysis failed.") gave no hint of *why*, so every
        // fallback looked identical whether the model timed out, produced malformed JSON,
        // or (previously) got confused trying to force a non-UI failure into the FlaUI
        // forensic protocol. Surface a short, sanitized tail of the raw output so this is
        // diagnosable straight from the report instead of only from console logs.
        var hint = "";
        string? investigationNotes = null;

        if (!string.IsNullOrWhiteSpace(rawResponse))
        {
            var tail = rawResponse.Length > 200 ? rawResponse[^200..] : rawResponse;
            tail = tail.Replace("\r", " ").Replace("\n", " ").Trim();
            hint = $" AI response could not be parsed as JSON (likely truncated mid-generation). Last ~200 chars of raw response: \"...{tail}\"";

            // Preserve the full raw response in investigation notes so users can see what the model actually said
            investigationNotes = $"⚠️ The AI response could not be parsed as valid JSON. This usually means the model's output was truncated mid-generation or returned malformed JSON.\n\nRaw AI Response:\n{rawResponse}";
        }

        return new FailureAnalysis
        {
            TestName = failure.TestName,
            ShortName = failure.ShortName,
            Category = "other",
            Severity = "medium",
            ErrorSummary = failure.ErrorMessage.Length > 200 ? failure.ErrorMessage[..200] + "…" : failure.ErrorMessage,
            PrimaryCause = "Local AI analysis failed." + hint,
            IssueOwner = "uncertain",
            IssueOwnerRationale = "AI analysis failed to parse — no evidence was successfully extracted to make an ownership call.",
            InvestigationNotes = investigationNotes,
            Suggestions = new() { new FailureSuggestion { Action = "Review manually — see primary cause for a snippet of the raw AI response that failed to parse", Type = "code", Priority = "immediate" } },

        };
    }

    /// <summary>
    /// Parse Call A (Investigation) response with repair/retry logic.
    /// Returns partial analysis with investigation fields populated.
    /// </summary>
    /// <param name="rawResponse">Raw JSON from Call A</param>
    /// <param name="failure">Original test failure</param>
    /// <param name="providerName">Provider name for logging</param>
    /// <returns>Parsed investigation fields as JObject</returns>
    public static JObject ParseInvestigation(string rawResponse, TestResult failure, string providerName = "AI")
    {
        var json = StripFences(rawResponse);

        Console.WriteLine($"\n=== {providerName.ToUpper()} INVESTIGATION JSON ===");
        Console.WriteLine(json.Length > 600 ? json[..600] + "\n...[truncated]" : json);
        Console.WriteLine($"=== END INVESTIGATION JSON ===\n");

        try
        {
            var obj = JObject.Parse(json);
            ValidateInvestigationFields(obj);
            return obj;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Warning] Investigation parse failed for {failure.ShortName} ({ex.Message}) — attempting repair");

            try
            {
                var repaired = RepairTruncatedJson(json);
                Console.WriteLine($"\n--- REPAIRED INVESTIGATION ---\n{repaired}\n---------------------------\n");
                var obj = JObject.Parse(repaired);
                ValidateInvestigationFields(obj);
                return obj;
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"  [Warning] Repair failed ({ex2.Message}) — attempting field extraction");
                try
                {
                    var extracted = ExtractInvestigationFields(json);
                    Console.WriteLine($"  [Success] Partial field extraction succeeded");
                    return extracted;
                }
                catch (Exception ex3)
                {
                    Console.WriteLine($"  [Error] Field extraction failed: {ex3.Message}");
                    throw new Exception($"Could not parse investigation response after all repair attempts: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>
    /// Parse Call B (Fixes) response with repair/retry logic.
    /// Returns suggestions and code snippet.
    /// </summary>
    /// <param name="rawResponse">Raw JSON from Call B</param>
    /// <param name="failure">Original test failure</param>
    /// <param name="providerName">Provider name for logging</param>
    /// <returns>Parsed fixes fields as JObject</returns>
    public static JObject ParseFixes(string rawResponse, TestResult failure, string providerName = "AI")
    {
        var json = StripFences(rawResponse);

        Console.WriteLine($"\n=== {providerName.ToUpper()} FIXES JSON ===");
        Console.WriteLine(json.Length > 600 ? json[..600] + "\n...[truncated]" : json);
        Console.WriteLine($"=== END FIXES JSON ===\n");

        try
        {
            var obj = JObject.Parse(json);
            ValidateFixesFields(obj);
            return obj;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Warning] Fixes parse failed for {failure.ShortName} ({ex.Message}) — attempting repair");

            try
            {
                var repaired = RepairTruncatedJson(json);
                Console.WriteLine($"\n--- REPAIRED FIXES ---\n{repaired}\n---------------------------\n");
                var obj = JObject.Parse(repaired);
                ValidateFixesFields(obj);
                return obj;
            }
            catch (Exception ex2)
            {
                Console.WriteLine($"  [Warning] Repair failed ({ex2.Message}) — using minimal fallback");
                // For fixes, we can tolerate failure - just return empty suggestions
                var fallback = new JObject
                {
                    ["suggestions"] = new JArray(),
                    ["code_snippet"] = null
                };
                return fallback;
            }
        }
    }

    /// <summary>
    /// Validate that investigation response has minimum required fields.
    /// Supports both legacy single-answer and new multi-hypothesis formats.
    /// </summary>
    private static void ValidateInvestigationFields(JObject obj)
    {
        // Always required
        var required = new[] { "investigation_notes", "category", "error_summary" };
        foreach (var field in required)
        {
            if (obj[field] == null)
                throw new Exception($"Missing required field: {field}");
        }

        // Check for either new hypotheses format OR legacy single-answer format
        var hasHypotheses = obj["hypotheses"] != null && obj["hypotheses"] is JArray hypothesesArray && hypothesesArray.Any();
        var hasLegacyOwner = obj["issue_owner"] != null;

        if (!hasHypotheses && !hasLegacyOwner)
        {
            throw new Exception("Missing ownership determination: must have either 'hypotheses' array or 'issue_owner' field");
        }
    }

    /// <summary>
    /// Validate that fixes response has expected structure.
    /// </summary>
    private static void ValidateFixesFields(JObject obj)
    {
        if (obj["suggestions"] == null)
            throw new Exception("Missing required field: suggestions");
    }

    /// <summary>
    /// Extract investigation fields from malformed/truncated JSON using regex.
    /// </summary>
    private static JObject ExtractInvestigationFields(string json)
    {
        var result = new JObject();

        // Extract each field with regex
        result["investigation_notes"] = ExtractField(json, "investigation_notes") ?? "Analysis incomplete due to parsing error";
        result["category"] = ExtractField(json, "category") ?? "other";
        result["category_confidence"] = int.TryParse(ExtractField(json, "category_confidence"), out var cc) ? cc : 50;
        result["severity"] = ExtractField(json, "severity") ?? "medium";
        result["severity_confidence"] = int.TryParse(ExtractField(json, "severity_confidence"), out var sc) ? sc : 50;
        result["error_summary"] = ExtractField(json, "error_summary") ?? "Error details could not be extracted";
        result["primary_cause"] = ExtractField(json, "primary_cause") ?? "Root cause could not be determined from incomplete response";
        result["issue_owner"] = ExtractField(json, "issue_owner") ?? "uncertain";
        result["issue_owner_confidence"] = int.TryParse(ExtractField(json, "issue_owner_confidence"), out var ic) ? ic : 50;
        result["issue_owner_rationale"] = ExtractField(json, "issue_owner_rationale") ?? "Could not determine rationale from incomplete response";

        // Extract array fields
        var factors = ExtractArrayField(json, "contributing_factors");
        result["contributing_factors"] = new JArray(factors);

        return result;
    }

    /// <summary>
    /// Extract a single field value from JSON using regex.
    /// </summary>
    private static string? ExtractField(string json, string fieldName)
    {
        var pattern = $"\"{fieldName}\"\\s*:\\s*\"([^\"]+)\"";
        var match = Regex.Match(json, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Extract an array field from JSON.
    /// </summary>
    private static List<string> ExtractArrayField(string json, string fieldName)
    {
        var pattern = $"\"{fieldName}\"\\s*:\\s*\\[([^\\]]+)\\]";
        var match = Regex.Match(json, pattern);
        if (!match.Success) return new List<string>();

        var arrayContent = match.Groups[1].Value;
        var items = Regex.Matches(arrayContent, "\"([^\"]+)\"");
        return items.Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>
    /// Parse evidence sections from AI response (if available) or extract from existing fields.
    /// This provides backward compatibility while supporting the new evidence-driven format.
    /// </summary>
    private static EvidenceSections ParseEvidenceSections(JObject obj, TestResult failure)
    {
        var evidence = new EvidenceSections();

        // Try to parse new evidence structure if AI provided it
        var evidenceObj = obj["evidence"] as JObject;

        if (evidenceObj != null)
        {
            // AI provided structured evidence sections
            evidence.Timeline = evidenceObj["timeline"]?.ToObject<List<string>>() ?? new();
            evidence.TestFrameworkEvidence = evidenceObj["test_framework_evidence"]?.Value<string>() ?? "";
            evidence.ApplicationEvidence = evidenceObj["application_evidence"]?.Value<string>() ?? "";
            evidence.MissingEvidence = evidenceObj["missing_evidence"]?.ToObject<List<string>>() ?? new();

            // Parse locator details
            var locatorObj = evidenceObj["locator"] as JObject;
            if (locatorObj != null)
            {
                evidence.Locator = new LocatorDetails
                {
                    ElementName = locatorObj["element_name"]?.Value<string>() ?? "",
                    ControlType = locatorObj["control_type"]?.Value<string>() ?? "",
                    SearchScope = locatorObj["search_scope"]?.Value<string>() ?? "",
                    ParentElement = locatorObj["parent_element"]?.Value<string>() ?? "",
                    TimeoutDuration = locatorObj["timeout_duration"]?.Value<string>() ?? "",
                    AutomationId = locatorObj["automation_id"]?.Value<string>() ?? "",
                    SearchCondition = locatorObj["search_condition"]?.Value<string>() ?? ""
                };
            }
        }
        else
        {
            // Fallback: Extract evidence from error message and stack trace
            evidence.TestFrameworkEvidence = ExtractTestFrameworkEvidence(failure);
            evidence.ApplicationEvidence = "(No application logs available in analysis)";
            evidence.Timeline = ExtractTimelineFromInvestigationNotes(obj["investigation_notes"]?.Value<string>() ?? "");
            evidence.Locator = ExtractLocatorFromError(failure.ErrorMessage, failure.StackTrace);
            evidence.MissingEvidence = GetDefaultMissingEvidence();
        }

        return evidence;
    }

    /// <summary>
    /// Extract test framework evidence from the TestResult object.
    /// </summary>
    private static string ExtractTestFrameworkEvidence(TestResult failure)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Exception: {failure.ErrorMessage.Split('\n').FirstOrDefault() ?? "Unknown"}");
        sb.AppendLine($"Stack Trace: {failure.StackTrace}");
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Extract timeline events from investigation notes by looking for timestamps.
    /// </summary>
    private static List<string> ExtractTimelineFromInvestigationNotes(string notes)
    {
        var timeline = new List<string>();
        if (string.IsNullOrWhiteSpace(notes)) return timeline;

        // Look for timestamps like "00:15:13" or "00:15:13,177"
        var timestampPattern = @"(\d{2}:\d{2}:\d{2}(?:[,\.]\d{3})?)\s*[-–—]\s*([^\n\.]{10,100})";
        var matches = Regex.Matches(notes, timestampPattern);

        foreach (Match match in matches)
        {
            if (match.Groups.Count >= 3)
            {
                var timestamp = match.Groups[1].Value;
                var event_desc = match.Groups[2].Value.Trim();
                timeline.Add($"{timestamp} — {event_desc}");
            }
        }

        // If no structured timeline found, look for quoted evidence with action verbs
        if (timeline.Count == 0)
        {
            var actionPattern = @"(succeeded|failed|timed out|clicked|found|not found|searched|located|appeared|disappeared)[^\n\.]{10,80}";
            var actionMatches = Regex.Matches(notes, actionPattern, RegexOptions.IgnoreCase);

            foreach (Match match in actionMatches.Cast<Match>().Take(5))
            {
                timeline.Add(match.Value.Trim());
            }
        }

        return timeline;
    }

    /// <summary>
    /// Extract locator details from error message and stack trace.
    /// </summary>
    private static LocatorDetails? ExtractLocatorFromError(string errorMessage, string stackTrace)
    {
        if (string.IsNullOrWhiteSpace(errorMessage)) return null;

        var locator = new LocatorDetails();

        // Extract timeout duration
        var timeoutMatch = Regex.Match(errorMessage, @"(\d+)\s*seconds?");
        if (timeoutMatch.Success)
            locator.TimeoutDuration = $"{timeoutMatch.Groups[1].Value} seconds";

        // Extract element name from "Name = ..." pattern
        var nameMatch = Regex.Match(errorMessage, @"Name\s*=\s*([^\s\]]+)");
        if (nameMatch.Success)
            locator.ElementName = nameMatch.Groups[1].Value.Trim();

        // Extract control type
        var ctrlMatch = Regex.Match(errorMessage, @"CtrlType\s*=\s*(\w+)");
        if (ctrlMatch.Success)
            locator.ControlType = ctrlMatch.Groups[1].Value;

        // Extract search scope
        var scopeMatch = Regex.Match(errorMessage, @"scope:\s*(\w+)", RegexOptions.IgnoreCase);
        if (scopeMatch.Success)
            locator.SearchScope = scopeMatch.Groups[1].Value;

        // Extract parent element
        var parentMatch = Regex.Match(errorMessage, @"Parent Element Name:\s*'([^']+)'");
        if (parentMatch.Success)
            locator.ParentElement = parentMatch.Groups[1].Value;

        // Extract AutomationId
        var autoIdMatch = Regex.Match(errorMessage, @"AutomationId\s*[=:]\s*([^\s\],]+)");
        if (autoIdMatch.Success)
            locator.AutomationId = autoIdMatch.Groups[1].Value.Trim();

        // Extract full search condition
        var conditionMatch = Regex.Match(errorMessage, @"Condition:\s*(.+?)(?:\n|$)");
        if (conditionMatch.Success)
            locator.SearchCondition = conditionMatch.Groups[1].Value.Trim();

        // Only return if we extracted at least some meaningful data
        if (!string.IsNullOrWhiteSpace(locator.ElementName) || 
            !string.IsNullOrWhiteSpace(locator.SearchCondition) ||
            !string.IsNullOrWhiteSpace(locator.TimeoutDuration))
        {
            return locator;
        }

        return null;
    }

    /// <summary>
    /// Return default list of commonly missing evidence types.
    /// </summary>
    private static List<string> GetDefaultMissingEvidence()
    {
        return new List<string>
        {
            "Screenshot captured at failure time",
            "UI Automation tree inspection",
            "Application logs",
            "Video recording of test execution"
        };
    }
}


