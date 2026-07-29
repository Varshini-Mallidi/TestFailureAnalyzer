using FailureAnalyzer.Models;
using System.Text;

namespace FailureAnalyzer.Services;

public static class PromptBuilderSimple
{
    // ==================================================================================
    // SYSTEM PROMPT
    // ==================================================================================

    public const string SystemPrompt = """
        You are an AI test failure analyst. You analyze test failures by examining evidence and reasoning through the problem.

        Your job:
        1. Read the evidence (exception, logs, source code, screenshots if available)
        2. Reason about what happened and why
        3. Determine whether the most likely root cause is a script, application, or environment issue
        4. Suggest a fix based on the evidence

        Core principles:
        - Quote evidence when making claims
        - Never invent information
        - Say "uncertain" or "need more evidence" when appropriate
        - Use FlaUI patterns (NOT Selenium)

        Return ONLY valid JSON. No markdown fences, no explanatory text outside the JSON structure.
        """;

    // ==================================================================================
    // SHARED RULES - Extracted to eliminate redundancy
    // ==================================================================================

    private const string FRAMEWORK_CONTEXT = """
        **Automation Framework:** FlaUI (Windows UI Automation for WPF apps)
        **Test Framework:** MSTest
        **Application Under Test:** AVEVA Dabacon (WPF desktop application)

        ⚠️ CRITICAL: This codebase uses FlaUI, NOT Selenium/WebDriver.
        Do NOT suggest WebDriverWait, By.Id, ExpectedConditions, or any Selenium API.
        """;

    private const string EVIDENCE_BASED_RULES = """
        ## CORE ANALYSIS RULES

        - Base your analysis only on the provided evidence (TRX, logs, screenshots, retrieved source code).
        - Quote relevant evidence when making claims (specific log lines, stack trace locations, screenshot text).
        - Use retrieved source code only as contextual reference showing what the test tried to do—not as proof of runtime behavior.
        - If the evidence is insufficient to determine the root cause, clearly state the uncertainty and explain what additional information would help.
        """;

    private const string RAG_CODE_ANALYSIS_RULES = """
        ## SOURCE CODE ANALYSIS

        When source code is available:

        - Prioritize the method referenced in the stack trace.
        - Inspect any directly related locator, property, or helper methods.
        - Use the code to explain the likely cause of the failure.
        - Do not treat retrieved source code as proof of runtime state.
        """;

    private const string CLASSIFICATION_GUIDANCE = """
        ## ISSUE CLASSIFICATION

        Classify the root cause into one of these categories based on the available evidence:

        - **script**: Problems in the automation/test code (locators, waits, assertions, test data, UI interaction logic, page-object implementation)
        - **application**: Problems in the product under test (crashes, unhandled exceptions, backend failures, incorrect business logic)
        - **environment**: Problems in the execution environment (permissions, network, configuration, missing services/files, dependency availability)

        Choose the category **best supported by the available evidence**. 

        If the evidence is insufficient or points to multiple plausible causes, use **insufficient_evidence** or **uncertain** and explain why. Provide multiple hypotheses when appropriate.
        """;

            private const string FIX_GATING_RULES = """
        ## CODE FIX GATING

        Only propose a concrete code fix when ALL of the following are true:

        1. The failing file and method can be confidently identified from the stack trace or exact symbol lookup.
        2. The evidence indicates a **SCRIPT** issue, or SCRIPT is a clear contributing factor.
        3. The retrieved source code corresponds to the failing implementation and contains enough context to make a safe recommendation.

        Do NOT propose a concrete code patch when:

        ❌ The analysis is based only on semantic/embedding retrieval and the failing method cannot be confidently confirmed.
        ❌ The primary root cause is **ENVIRONMENT** or **APPLICATION** with no meaningful SCRIPT contribution.
        ❌ The retrieved code does not clearly correspond to the stack-trace context.
        ❌ The proposed fix would require assumptions about types, methods, locators, or APIs that are not present in the retrieved source code.

        When a fix is proposed:

        - Use only names, types, methods, and patterns that appear in the retrieved source code.
        - Clearly separate **confirmed evidence** from **assumptions or hypotheses**.
        - Add the disclaimer: **"Verify signatures and surrounding context before applying."**
        - Use confidence levels:
          - **high** → exact stack-trace match and straightforward change
          - **medium** → mostly confirmed context with limited assumptions
          - **low** → fallback retrieval or incomplete context

        If the evidence is insufficient for a safe patch, provide **investigation guidance and possible next steps instead of code changes**.
        """;

    // ==================================================================================
    // JSON SCHEMAS
    // ==================================================================================

    private const string INVESTIGATION_NOTES_FORMAT = """
        investigation_notes format (max 400 words):
        1. TRX Evidence: Exception type, message, stack location (file:line)
        2. Log Evidence: Timestamps, durations, actions, timeouts
        3. Code Analysis: Failing statement, locator definition, helpers
        4. Possible Explanation: Inference backed by evidence from 1-3
        """;

    private const string COMMON_ANALYSIS_FIELDS = """
          "investigation_notes": "<string>",
          "category": "<string>",
          "category_confidence": <0-100>,
          "severity": "<string>",
          "severity_confidence": <0-100>,
          "error_summary": "<string>"
        """;

    private static string GetFailureAnalysisSchema(string concisenessGuidance = "") => $$"""
        {{INVESTIGATION_NOTES_FORMAT}}
        {{concisenessGuidance}}

        Return ONLY this JSON (no extra text):

        {
        {{COMMON_ANALYSIS_FIELDS}},
          "primary_cause": "<Root cause with evidence citation>",
          "issue_owner": "<script|application|insufficient_evidence|uncertain>",
          "issue_owner_confidence": <0-100>,
          "issue_owner_rationale": "<Quote TRX/log/code evidence>",
          "fault_attribution": {
            "primary": "<SCRIPT|APPLICATION|ENVIRONMENT|DATA|INDETERMINATE>",
            "confidence": <0-100>,
            "secondary_contributing_factors": [
              {
                "type": "<SCRIPT|APPLICATION|ENVIRONMENT|DATA>",
                "description": "<Short description>",
                "why_it_matters": "<Impact if only primary fixed>"
              }
            ]
          },
          "contributing_factors": ["<Factual observation>"],
          "suggested_fix": {
            "file_path": "<Relative path or null>",
            "current_code": "<Problematic snippet or null>",
            "proposed_code": "<Fix or null>",
            "explanation": "<Why this fixes root cause>",
            "confidence_level": "<high|medium|low>",
            "gating_reason": "<Reason for fix decision>"
          },
          "suggestions": [
            {
              "action": "<Fix with file:line>",
              "type": "<locator|wait|code|environment|data|infrastructure|logic>",
              "priority": "<immediate|soon|later>"
            }
          ],
          "code_snippet": "<Corrected code or null>"
        }

        Confidence ranges: 90-100% (very confident), 70-89% (confident), 50-69% (moderate), 30-49% (low), 0-29% (very low - use "insufficient_evidence")
        """;

    private static string GetInvestigationSchema() => $$"""
        {{INVESTIGATION_NOTES_FORMAT}}

        Return ONLY this JSON (no extra text):

        {
        {{COMMON_ANALYSIS_FIELDS}},
          "contributing_factors": ["<Factual observation>"],
          "hypotheses": [
            {
              "explanation": "<What might have happened>",
              "issue_owner": "<script|application|insufficient_evidence|uncertain>",
              "confidence": <0-100>,
              "evidence": "<Quote from TRX/logs/source>",
              "required_to_confirm": "<Info to prove/disprove>"
            }
          ],
          "primary_hypothesis": <0-based index>,
          "overall_confidence": "<low|medium|high>",
          "recommended_investigation": ["<Concrete step to gather evidence>"]
        }

        overall_confidence: high (80%+), medium (60-79%), low (<60% or competing hypotheses)
        If top hypothesis <70%, provide at least 2 hypotheses
        """;

    private static string GetSeparatedEvidenceSchema() => $$"""
        {{INVESTIGATION_NOTES_FORMAT}}

        Return ONLY this JSON (no extra text):

        {
        {{COMMON_ANALYSIS_FIELDS}},
          "fault_attribution": {
            "primary": "<SCRIPT|APPLICATION|ENVIRONMENT|DATA|INDETERMINATE>",
            "confidence": <0-100>,
            "secondary_contributing_factors": [
              {
                "type": "<SCRIPT|APPLICATION|ENVIRONMENT|DATA>",
                "description": "<Short description>",
                "why_it_matters": "<Impact if only primary fixed>"
              }
            ]
          },
          "contributing_factors": ["<Factual observation>"],
          "suggested_fix": {
            "file_path": "<Relative path or null>",
            "current_code": "<Snippet or null>",
            "proposed_code": "<Fix or null>",
            "explanation": "<Why or reason no fix>",
            "confidence_level": "<high|medium|low>",
            "gating_reason": "<Reason>"
          },
          "hypotheses": [
            {
              "explanation": "<What might have happened>",
              "issue_owner": "<script|application|uncertain>",
              "confidence": <0-100>,
              "evidence": "<Quote from evidence>",
              "required_to_confirm": "<What to prove/disprove>"
            }
          ],
          "primary_hypothesis": <index>,
          "overall_confidence": "<low|medium|high>",
          "recommended_investigation": ["<Step>"]
        }
        """;

    private static string GetFixSchema() => """
        Return ONLY this JSON (no extra text):

        {
          "suggestions": [
            {
              "action": "<Fix with file:line>",
              "type": "<locator|wait|code|environment|data|infrastructure|logic|investigation>",
              "priority": "<immediate|soon|later>",
              "applies_to_hypothesis": <index or null>
            }
          ],
          "code_snippet": "<Corrected code or null>"
        }

        Types: locator (fix identifier), wait (timeout), code (logic), environment (config), data (test data), infrastructure (service), logic (app code), investigation (gather evidence)
        Priority: immediate (blocking), soon (frequent), later (low impact)
        """;

    // ==================================================================================
    // HELPER METHODS
    // ==================================================================================

    private static string TruncateWithMarker(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;

        return text[..maxLength] + "\n...[truncated]";
    }

    private static string FormatRagContext(string? context, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(context))
            return "(not provided)";

        if (context.Length > maxLength)
            context = context[..maxLength] + "\n\n[Truncated — showing first " + maxLength + " chars]";

        return context;
    }

    private static string FormatScreenshotEvidence(List<ScreenshotAnalysis>? screenshots)
    {
        if (screenshots == null || !screenshots.Any())
            return "(No screenshots captured at failure time)";

        var sb = new StringBuilder();
        sb.AppendLine($"**{screenshots.Count} screenshot(s) captured:**\n");

        foreach (var screenshot in screenshots)
        {
            sb.AppendLine($"Screenshot: {Path.GetFileName(screenshot.ScreenshotPath)}");
            sb.AppendLine($"Confidence: {screenshot.ConfidenceScore}%");
            sb.AppendLine($"\nDescription: {screenshot.Description}");

            if (!string.IsNullOrEmpty(screenshot.RelevanceToFailure))
                sb.AppendLine($"\nRelevance: {screenshot.RelevanceToFailure}");

            if (screenshot.ObservedElements.Any())
            {
                sb.AppendLine($"\nObserved Elements:");
                foreach (var element in screenshot.ObservedElements.Take(10))
                    sb.AppendLine($"  • {element}");
            }

            if (screenshot.ErrorsVisible.Any())
            {
                sb.AppendLine($"\n**CRITICAL - Error Dialogs Visible:**");
                foreach (var error in screenshot.ErrorsVisible)
                    sb.AppendLine($"  • \"{error}\"");
            }

            if (screenshot.CategoriesVisible.Any())
                sb.AppendLine($"\nCategories: {string.Join(", ", screenshot.CategoriesVisible)}");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ==================================================================================
    // PUBLIC PROMPT BUILDERS
    // ==================================================================================

    public static string BuildFailurePrompt(
        TestResult failure,
        string log,
        string environment,
        string? ragContext)
    {
        var errorMsg = TruncateWithMarker(failure.ErrorMessage, 800);
        var stackTrace = TruncateWithMarker(failure.StackTrace, 2000);
        var logSnippet = TruncateWithMarker(log, 3000);
        var ragContextFormatted = FormatRagContext(ragContext, 4000);

        // Estimate input size for conciseness guidance
        var estimatedInputSize = (failure.TestName.Length + errorMsg.Length + stackTrace.Length + 
                              logSnippet.Length + ragContextFormatted.Length + 2000) / 4;
        var conciseMode = estimatedInputSize > 6000;

        if (conciseMode)
            Console.WriteLine($"  [Prompt] Large input (~{estimatedInputSize:N0} tokens) - requesting concise response");

        var concisenessGuidance = conciseMode 
            ? "\n⚠️ Large input. Keep response CONCISE:\n" +
              "- investigation_notes: Key findings only (max 400 words)\n" +
              "- code_snippet: Changed method/section only\n" +
              "- suggestions: Top 2-3 actionable items\n"
            : "";

        return $$"""
            # {{failure.TestName}}

            ═══════════════════════════════════════════════════════════════════════════
            ## EVIDENCE
            ═══════════════════════════════════════════════════════════════════════════

            {{FRAMEWORK_CONTEXT}}

            **Test:** {{failure.TestName}}
            **Environment:** {{environment}}

            **Exception:**
            {{errorMsg}}

            **Stack Trace:**
            {{stackTrace}}

            **Automation Logs:**
            {{(logSnippet.Length > 0 ? logSnippet : "(none provided)")}}

            **Retrieved Source Code (intent, not runtime state):**
            {{(ragContextFormatted != "(not provided)" ? ragContextFormatted : "(none retrieved)")}}

            ═══════════════════════════════════════════════════════════════════════════
            ## YOUR TASK
            ═══════════════════════════════════════════════════════════════════════════

            {{EVIDENCE_BASED_RULES}}

            {{RAG_CODE_ANALYSIS_RULES}}

            {{CLASSIFICATION_GUIDANCE}}

            {{FIX_GATING_RULES}}

            {{GetFailureAnalysisSchema(concisenessGuidance)}}
            """;
    }

    public static string BuildInvestigationPrompt(
        TestResult failure,
        string log,
        string environment,
        string? ragContext)
    {
        var errorMsg = TruncateWithMarker(failure.ErrorMessage, 800);
        var stackTrace = TruncateWithMarker(failure.StackTrace, 2000);
        var logSnippet = TruncateWithMarker(log, 3000);
        var ragContextFormatted = FormatRagContext(ragContext, 4000);

        return $$"""
            # {{failure.TestName}} — Investigation Phase

            ═══════════════════════════════════════════════════════════════════════════
            ## EVIDENCE
            ═══════════════════════════════════════════════════════════════════════════

            {{FRAMEWORK_CONTEXT}}

            **Test:** {{failure.TestName}}
            **Environment:** {{environment}}

            **Exception:**
            {{errorMsg}}

            **Stack Trace:**
            {{stackTrace}}

            **Automation Logs:**
            {{(logSnippet.Length > 0 ? logSnippet : "(none provided)")}}

            **Retrieved Source Code (intent, not runtime state):**
            {{(ragContextFormatted != "(not provided)" ? ragContextFormatted : "(none retrieved)")}}

            ═══════════════════════════════════════════════════════════════════════════
            ## YOUR TASK
            ═══════════════════════════════════════════════════════════════════════════

            {{EVIDENCE_BASED_RULES}}

            {{RAG_CODE_ANALYSIS_RULES}}

            {{CLASSIFICATION_GUIDANCE}}

            {{GetInvestigationSchema()}}
            """;
    }

    public static string BuildInvestigationPromptWithSeparatedEvidence(
        TestResult failure,
        SeparatedEvidence evidence,
        string environment,
        string? ragContext,
        List<ScreenshotAnalysis>? screenshots = null)
    {
        var errorMsg = TruncateWithMarker(failure.ErrorMessage, 800);
        var stackTrace = TruncateWithMarker(failure.StackTrace, 2000);
        var ragContextFormatted = FormatRagContext(ragContext, 4000);
        var screenshotEvidence = FormatScreenshotEvidence(screenshots);

        return $$"""
            # {{failure.TestName}} — Investigation Phase

            ═══════════════════════════════════════════════════════════════════════════
            ## TEST-SIDE EVIDENCE
            ═══════════════════════════════════════════════════════════════════════════

            {{FRAMEWORK_CONTEXT}}

            **Test:** {{failure.TestName}}
            **Environment:** {{environment}}

            **Test Exception:**
            {{errorMsg}}

            **Test Stack Trace:**
            {{stackTrace}}

            **Test Execution Logs:**
            {{(evidence.TestEvidence.Length > 0 ? evidence.TestEvidence : "(none captured)")}}

            **Test Source Code (from stack trace):**
            {{(ragContextFormatted != "(not provided)" ? ragContextFormatted : "(none retrieved)")}}

            ═══════════════════════════════════════════════════════════════════════════
            ## APPLICATION-SIDE EVIDENCE
            ═══════════════════════════════════════════════════════════════════════════

            {{evidence.ApplicationEvidence}}

            ═══════════════════════════════════════════════════════════════════════════
            ## SCREENSHOT EVIDENCE
            ═══════════════════════════════════════════════════════════════════════════

            {{screenshotEvidence}}

            ═══════════════════════════════════════════════════════════════════════════
            ## CLASSIFICATION GUIDANCE
            ═══════════════════════════════════════════════════════════════════════════

            Use the available evidence to determine the most likely issue owner:

            - **script**: automation logic, locators, waits, assertions, test data, or page-object implementation issues.
            - **application**: product crashes, unhandled exceptions, backend/service failures reported by the application, or incorrect application behaviour.
            - **environment**: permissions, network, configuration, missing services/files, dependency availability, or infrastructure problems.
            - **uncertain**: evidence is incomplete or supports multiple plausible causes.

            Screenshots and runtime logs should be treated as stronger evidence than retrieved source code.

            ═══════════════════════════════════════════════════════════════════════════
            ## YOUR TASK
            ═══════════════════════════════════════════════════════════════════════════

            {{EVIDENCE_BASED_RULES}}

            {{RAG_CODE_ANALYSIS_RULES}}

            {{FIX_GATING_RULES}}

            {{GetSeparatedEvidenceSchema()}}
            """;
    }

    public static string BuildFixPrompt(
        TestResult failure,
        string investigationSummary,
        string? ragContext)
    {
        var ragContextFormatted = FormatRagContext(ragContext, 4000);

        return $$"""
            # {{failure.TestName}} — Fix Suggestions Phase

            ═══════════════════════════════════════════════════════════════════════════
            ## INVESTIGATION SUMMARY (from previous analysis)
            ═══════════════════════════════════════════════════════════════════════════

            {{investigationSummary}}

            ═══════════════════════════════════════════════════════════════════════════
            ## SOURCE CODE CONTEXT
            ═══════════════════════════════════════════════════════════════════════════

            {{(ragContextFormatted != "(not provided)" ? ragContextFormatted : "(none retrieved)")}}

            ═══════════════════════════════════════════════════════════════════════════
            ## YOUR TASK
            ═══════════════════════════════════════════════════════════════════════════

            Based on the investigation, provide concrete fix suggestions and code.

            **Code Fix Requirements:**
            - Use actual patterns/names from retrieved source
            - Include method signature + corrected logic
            - Use FlaUI patterns (NOT Selenium/WebDriver)
            - Return null if: no source provided, environmental issue, or app bug

            {{GetFixSchema()}}
            """;
    }
}
