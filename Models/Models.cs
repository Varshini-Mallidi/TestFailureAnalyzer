using CommandLine;

namespace FailureAnalyzer.Models;

// ── Raw TRX data ────────────────────────────────────────────────────────────

public class TestResult
{
    public string TestName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Outcome { get; set; } = "";      
    public string Duration { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public List<string> AttachmentPaths { get; set; } = new();
    public List<string> Categories { get; set; } = new();  // Test categories from TRX, logs, or screenshots

    // Retry tracking
    public bool WasRetried { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public int TotalAttempts { get; set; } = 1;
}

public class TestRun
{
    public string RunName { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string FinishTime { get; set; } = "";
    public List<TestResult> Results { get; set; } = new();
    public int Total => Results.Count;
    public int Passed => Results.Count(r => r.Outcome == "Passed");
    public int Failed => Results.Count(r => r.Outcome == "Failed");
    public int Skipped => Results.Count(r => r.Outcome == "NotExecuted");
}

public class EliminatedCause
{
    public string Cause { get; set; } = "";
    public string WhyRuledOut { get; set; } = "";
}


// ── AI analysis output ──────────────────────────────────────────────────────

public class FailureSuggestion
{
    public string Action { get; set; } = "";
    public string Type { get; set; } = "";       // locator | wait | code | environment | data | infrastructure
    public string Priority { get; set; } = "";   // immediate | soon | later
    public int? AppliesToHypothesis { get; set; } = null;  // Index of hypothesis this suggestion applies to, null = applies to all
}

public class Hypothesis
{
    public string Explanation { get; set; } = "";        // What might have happened
    public string IssueOwner { get; set; } = "";         // script | application | uncertain
    public int Confidence { get; set; } = 0;             // 0-100 (may be capped)
    public int? OriginalConfidence { get; set; }         // Original LLM confidence before policy cap
    public string? ConfidenceCapReason { get; set; }     // Why confidence was capped (if applicable)
    public string Evidence { get; set; } = "";           // Quote from logs/stack/source supporting this
    public string RequiredToConfirm { get; set; } = "";  // What info would prove/disprove this
    public string Relationship { get; set; } = "";       // How this hypothesis relates to others: "root-cause" | "contributing-factor" | "consequence" | "alternative"
    public int? RelatedToHypothesisIndex { get; set; }   // Index of related hypothesis (if applicable)
}

public class RetrievedChunk
{
    public string SourcePath { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public float RelevanceScore { get; set; }
    public float SemanticScore { get; set; }
    public float KeywordScore { get; set; }
    public string Content { get; set; } = "";
    public bool IsExactMatch { get; set; } = true;  // False if from semantic fallback search
    public string RetrievalMethod { get; set; } = "exact";  // "exact" | "semantic" | "keyword"
}

/// <summary>
/// Represents a debug-focused code snippet with context about why it's relevant.
/// Used for stack-trace-first retrieval to show exact failing statements,
/// locator definitions, and calling test context.
/// </summary>
public class DebugSnippet
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string MethodName { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int? FocusLine { get; set; }  // The exact line that failed
    public string Content { get; set; } = "";
    public string Category { get; set; } = "";  // "Failing Statement", "Locator Definition", "Calling Test", etc.
    public string Reason { get; set; } = "";  // Why this snippet is relevant for debugging
}

// ── Screenshot Analysis (NEW) ────────────────────────────────────────────────

/// <summary>
/// Represents AI analysis of a test failure screenshot using vision models.
/// Captures what UI elements are visible, any errors shown, and diagnostic relevance.
/// </summary>
public class ScreenshotAnalysis
{
    public string ScreenshotPath { get; set; } = "";  // Local path to the screenshot file
    public string Description { get; set; } = "";  // AI description of what's visible in the screenshot
    public List<string> ObservedElements { get; set; } = new();  // UI elements visible (buttons, dialogs, etc.)
    public List<string> ErrorsVisible { get; set; } = new();  // Error dialogs, messages, or warnings visible
    public List<string> CategoriesVisible { get; set; } = new();  // Test categories visible in the screenshot
    public string RelevanceToFailure { get; set; } = "";  // How this screenshot helps diagnose the failure
    public int ConfidenceScore { get; set; } = 0;  // 0-100: AI confidence in the analysis
}

// ── Evidence Bundle (Immutable Evidence Contract) ──────────────────────────────

/// <summary>
/// Immutable evidence bundle containing all gathered evidence for a single test failure.
/// Once assembled, this bundle should be the single source of truth for report generation,
/// preventing run-to-run variance caused by re-gathering evidence.
/// </summary>
public class EvidenceBundle
{
    // Core test metadata
    public string TestName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public DateTime AssembledAt { get; set; } = DateTime.UtcNow;

    // TRX evidence (what the test framework captured)
    public string ExceptionType { get; set; } = "";
    public string ExceptionMessage { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Duration { get; set; } = "";
    public List<string> TestCategories { get; set; } = new();

    // Log evidence
    public string TestLog { get; set; } = "";  // Automation/test framework log (FlaUI, Selenium, etc.)
    public string ApplicationLog { get; set; } = "";  // Application-side log (if available)
    public bool HasApplicationLog { get; set; } = false;

    // Screenshot evidence (analyzed once, cached)
    public List<ScreenshotAnalysis> Screenshots { get; set; } = new();
    public bool HasScreenshots { get; set; } = false;

    // Retrieved source code
    public List<RetrievedChunk> SourceCodeChunks { get; set; } = new();
    public bool HasExactSymbolMatch { get; set; } = false;  // True if crash site was found via exact symbol lookup

    // Missing evidence tracking
    public List<string> MissingEvidence { get; set; } = new();

    /// <summary>
    /// Returns a summary of available evidence types for transparency in reports.
    /// </summary>
    public string GetEvidenceSummary()
    {
        var available = new List<string>();
        available.Add("TRX (exception, stack trace, timing)");
        available.Add("Test automation log");
        if (HasApplicationLog) available.Add("Application log");
        if (HasScreenshots) available.Add($"Screenshot analysis ({Screenshots.Count} image(s))");
        if (SourceCodeChunks.Any()) available.Add($"Source code ({SourceCodeChunks.Count} chunk(s))");
        if (HasExactSymbolMatch) available.Add("✓ Exact crash-site match");

        return string.Join(", ", available);
    }
}

// ── Evidence Summary & Validation (Single Source of Truth) ─────────────────────

/// <summary>
/// Centralized evidence summary that serves as the single source of truth
/// for all evidence-related decisions in report generation.
/// Prevents contradictions between confidence caps, missing evidence lists, and badges.
/// </summary>
public class EvidenceSummary
{
    public bool HasStackTrace { get; set; }
    public bool HasApplicationLog { get; set; }
    public bool HasScreenshots { get; set; }
    public bool HasDialogText { get; set; }
    public bool HasExactSymbolMatch { get; set; }
    public bool HasQuotedErrorText { get; set; }
    public int ScreenshotCount { get; set; }
    public int SourceCodeChunkCount { get; set; }
    public List<string> MissingCategories { get; set; } = new();
    public string EvidenceTier { get; set; } = "";  // "complete" | "partial" | "minimal"

    /// <summary>
    /// Returns a human-readable explanation of what evidence is available
    /// </summary>
    public string GetAvailableEvidenceDescription()
    {
        var parts = new List<string>();
        if (HasStackTrace) parts.Add("stack trace with file:line info");
        if (HasScreenshots) parts.Add($"{ScreenshotCount} screenshot(s)");
        if (HasDialogText) parts.Add("quoted error dialog text");
        if (HasApplicationLog) parts.Add("application logs");
        if (HasExactSymbolMatch) parts.Add("exact crash site match");
        if (SourceCodeChunkCount > 0) parts.Add($"{SourceCodeChunkCount} source code chunk(s)");

        return parts.Any() ? string.Join(", ", parts) : "minimal evidence (TRX only)";
    }

    /// <summary>
    /// Returns a human-readable explanation of what evidence is missing
    /// </summary>
    public string GetMissingEvidenceDescription()
    {
        if (!MissingCategories.Any()) return "none";
        return string.Join(", ", MissingCategories);
    }
}

/// <summary>
/// Centralized evidence validator that ensures all report sections
/// use the same evidence checks, preventing contradictions.
/// </summary>
public static class EvidenceValidator
{
    /// <summary>
    /// Creates a comprehensive evidence summary from a bundle.
    /// This is the SINGLE SOURCE OF TRUTH for all evidence-related decisions.
    /// </summary>
    public static EvidenceSummary GetSummary(EvidenceBundle bundle)
    {
        var summary = new EvidenceSummary
        {
            HasStackTrace = !string.IsNullOrEmpty(bundle.StackTrace),
            HasApplicationLog = bundle.HasApplicationLog && !string.IsNullOrEmpty(bundle.ApplicationLog),
            HasScreenshots = bundle.HasScreenshots && bundle.Screenshots.Any(),
            HasExactSymbolMatch = bundle.HasExactSymbolMatch,
            ScreenshotCount = bundle.Screenshots.Count,
            SourceCodeChunkCount = bundle.SourceCodeChunks.Count,
            MissingCategories = new List<string>(bundle.MissingEvidence)
        };

        // Check for quoted dialog text in screenshots
        summary.HasDialogText = bundle.Screenshots.Any(s =>
            !string.IsNullOrEmpty(s.Description) &&
            (s.Description.Contains("dialog", StringComparison.OrdinalIgnoreCase) ||
             s.Description.Contains("message box", StringComparison.OrdinalIgnoreCase) ||
             s.Description.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
             s.Description.Contains("'", StringComparison.Ordinal)));  // Contains quoted text

        // Check for quoted error text in any evidence
        summary.HasQuotedErrorText = summary.HasDialogText ||
            (!string.IsNullOrEmpty(bundle.ExceptionMessage) && bundle.ExceptionMessage.Contains("'")) ||
            bundle.Screenshots.Any(s => s.ErrorsVisible.Any());

        // Determine evidence tier
        summary.EvidenceTier = DetermineEvidenceTier(summary);

        return summary;
    }

    private static string DetermineEvidenceTier(EvidenceSummary summary)
    {
        // Complete: All major evidence types present
        if (summary.HasStackTrace &&
            summary.HasScreenshots &&
            summary.HasApplicationLog &&
            summary.HasExactSymbolMatch)
        {
            return "complete";
        }

        // Partial: At least 2 major evidence types
        int evidenceCount = 0;
        if (summary.HasStackTrace) evidenceCount++;
        if (summary.HasScreenshots) evidenceCount++;
        if (summary.HasApplicationLog) evidenceCount++;
        if (summary.HasExactSymbolMatch) evidenceCount++;

        if (evidenceCount >= 2)
        {
            return "partial";
        }

        // Minimal: Only TRX data
        return "minimal";
    }
}

// ── Fault Attribution (Script vs. Application Classification) ──────────────────

/// <summary>
/// Structured fault attribution with primary cause and secondary contributing factors.
/// Replaces the informal category/issue_owner fields with an explicit hierarchy.
/// </summary>
public class FaultAttribution
{
    public string Primary { get; set; } = "";  // "SCRIPT" | "APPLICATION" | "ENVIRONMENT" | "DATA" | "INDETERMINATE"
    public int Confidence { get; set; } = 0;  // 0-100
    public List<ContributingFactor> SecondaryFactors { get; set; } = new();
}

public class ContributingFactor
{
    public string Type { get; set; } = "";  // "SCRIPT" | "APPLICATION" | "ENVIRONMENT" | "DATA"
    public string Description { get; set; } = "";
    public string WhyItMatters { get; set; } = "";  // What happens if only the primary cause is fixed
}

// ── Suggested Fix (Gated Code Change Proposal) ─────────────────────────────────

/// <summary>
/// Code fix suggestion, only generated when:
/// 1. Crash site was matched via exact symbol lookup (not semantic fallback)
/// 2. Fault attribution is SCRIPT (or SCRIPT is a secondary factor)
/// 3. Retrieved source shows the actual failing logic
/// </summary>
public class SuggestedFix
{
    public string FilePath { get; set; } = "";
    public string CurrentCode { get; set; } = "";  // Exact problematic snippet
    public string ProposedCode { get; set; } = "";  // Corrected snippet
    public string Explanation { get; set; } = "";  // Why this addresses the root cause, tied to evidence
    public string ConfidenceLevel { get; set; } = "";  // "high" | "medium" | "low" (same tiering as fault attribution)
    public string GatingReason { get; set; } = "";  // Why this fix is/isn't safe to apply (e.g., "Exact crash site confirmed" or "Semantic match only, avoid fixing wrong file")
}


public class FailureAnalysis
{
    public string TestName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Category { get; set; } = "";   // locator | timing | environment | data | app_crash | assertion | flaky | other
    public int CategoryConfidence { get; set; } = 0;  // AI confidence in category classification (0-100%)
    public string Severity { get; set; } = "";   // critical | high | medium | low
    public int SeverityConfidence { get; set; } = 0;  // AI confidence in severity assessment (0-100%)
    public string ErrorSummary { get; set; } = "";
    public string PrimaryCause { get; set; } = "";

    // Multiple hypothesis support (NEW)
    public List<string> Observations { get; set; } = new();        // Factual observations before hypothesizing
    public List<Hypothesis> Hypotheses { get; set; } = new();  // Multiple possible explanations
    public int PrimaryHypothesis { get; set; } = 0;            // Index of most likely hypothesis
    public string OverallConfidence { get; set; } = "";        // low | medium | high
    public List<string> RecommendedInvestigation { get; set; } = new();  // Steps to narrow down cause

    // Legacy single-answer fields (kept for backward compatibility)
    public string IssueOwner { get; set; } = "";
    public int IssueOwnerConfidence { get; set; } = 0;  // AI confidence in issue_owner classification (0-100%)
    public string IssueOwnerRationale { get; set; } = "";

    public string? InvestigationNotes { get; set; }  // ADDED: Full LLM reasoning with all details (nullable to allow fallback cases)
    public List<string> ContributingFactors { get; set; } = new();
    public List<EliminatedCause> EliminatedCauses { get; set; } = new();
    public List<FailureSuggestion> Suggestions { get; set; } = new();
    public string? CodeSnippet { get; set; }
    public string CodeSnippetConfidence { get; set; } = "";
    public bool EvidenceVerified { get; set; } = true;
    public List<string> AttachmentPaths { get; set; } = new();
    public List<RetrievedChunk> RetrievedChunks { get; set; } = new();

    // Evidence-driven report sections (NEW)
    public EvidenceSections Evidence { get; set; } = new();

    // Screenshot analysis (NEW - for vision model integration)
    public List<ScreenshotAnalysis> Screenshots { get; set; } = new();

    // Evidence bundle (immutable evidence contract)
    public EvidenceBundle? Bundle { get; set; }

    // Fault attribution (structured script vs. application classification)
    public FaultAttribution? Attribution { get; set; }

    // Suggested fix (gated on exact match + script attribution)
    public SuggestedFix? Fix { get; set; }

}

public class RunAnalysis
{
    public TestRun Run { get; set; } = new();
    public List<FailureAnalysis> Failures { get; set; } = new();
    public List<string> Patterns { get; set; } = new();
    public string EnvironmentNotes { get; set; } = "";
    public string Environment { get; set; } = "";
    public string? ExtraContext { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

// ── CLI options ─────────────────────────────────────────────────────────────

public class CliOptions
{
    [CommandLine.Option("trx", Required = false, HelpText = "Path to .trx file (or glob pattern). Required unless --rag-query or --index is used.")]
    public string TrxPath { get; set; } = "";

    [CommandLine.Option("logs", Required = false, HelpText = "Directory containing .log / .txt files")]
    public string? LogDirectory { get; set; }

    [CommandLine.Option("output", Required = false, Default = "failure-report.html", HelpText = "Output HTML report path")]
    public string Output { get; set; } = "failure-report.html";

    [CommandLine.Option("env", Required = false, Default = "Azure DevOps CI", HelpText = "Environment name")]
    public string Environment { get; set; } = "Azure DevOps CI";

    [CommandLine.Option("context", Required = false, HelpText = "Extra context for the AI (branch, recent changes, etc.)")]
    public string? Context { get; set; }

    [CommandLine.Option("max-failures", Required = false, Default = 2, HelpText = "Max failures to analyze via AI")]
    public int MaxFailures { get; set; } = 2;

    [CommandLine.Option("fail-on-critical", Required = false, Default = true, HelpText = "Exit code 2 if critical failures found")]
    public bool FailOnCritical { get; set; } = true;

    [CommandLine.Option("ollama", Required = false, Default = false, HelpText = "Use local Ollama instance for AI analysis (Free & Private)")]
    public bool Ollama { get; set; } = false;

    [CommandLine.Option("openai", Required = false, Default = false, HelpText = "Use OpenAI API directly (requires API key in appsettings.json)")]
    public bool OpenAI { get; set; } = false;

    [CommandLine.Option("openai-key", Required = false, HelpText = "OpenAI API key (overrides appsettings.json)")]
    public string? OpenAIKey { get; set; }

    [CommandLine.Option("openai-model", Required = false, Default = "gpt-4o", HelpText = "OpenAI model to use")]
    public string OpenAIModel { get; set; } = "gpt-4o";

    [CommandLine.Option("gemini", Required = false, Default = false, HelpText = "Use Google Gemini AI (FREE tier available - no credit card needed)")]
    public bool Gemini { get; set; } = false;

    [CommandLine.Option("gemini-key", Required = false, HelpText = "Google AI Studio API key (get free at https://aistudio.google.com/app/apikey)")]
    public string? GeminiKey { get; set; }

    [CommandLine.Option("gemini-model", Required = false, Default = "gemini-flash-latest", HelpText = "Gemini model: gemini-flash-latest (free, fast), gemini-pro-latest (free, best)")]
    public string GeminiModel { get; set; } = "gemini-flash-latest";

    [CommandLine.Option("source-dir", Required = false, Separator = ',', HelpText = "Path(s) to your local repository to extract source code for RAG (comma-separated or specify multiple times)")]
    public IEnumerable<string>? SourceDirectories { get; set; }

    [CommandLine.Option("index", Required = false, Default = false, HelpText = "Run ingestion pipeline to index source code (separate from analysis). Use with --source-dir")]
    public bool Index { get; set; } = false;

    [CommandLine.Option("rag-query", Required = false, HelpText = "Diagnostic mode: run a single RAG retrieval for this text against the existing index and print the results, then exit. No TRX needed. Use to sanity-check RAG without running the full pipeline.")]
    public string? RagQuery { get; set; }

    [CommandLine.Option("test-exception", Required = false, Default = false, HelpText = "Run exception extraction regression tests")]
    public bool TestException { get; set; } = false;

    [CommandLine.Option("force-reindex", Required = false, Default = false, HelpText = "Force full re-index of source code even if vector store exists")]
    public bool ForceReindex { get; set; } = false;

    [CommandLine.Option("audit-index", Required = false, Default = false, HelpText = "Audit the vector store to see what files are indexed and identify gaps")]
    public bool AuditIndex { get; set; } = false;

    // ── Azure DevOps Integration ──

    [CommandLine.Option("ado-latest", Required = false, Default = false, HelpText = "Fetch latest test run from Azure DevOps (uses settings from appsettings.json)")]
    public bool AdoLatest { get; set; } = false;

    [CommandLine.Option("ado-build", Required = false, HelpText = "Fetch test results from specific Azure DevOps build ID")]
    public int? AdoBuildId { get; set; }

    [CommandLine.Option("ado-pipeline", Required = false, HelpText = "Fetch latest test run from specific Azure DevOps pipeline ID")]
    public int? AdoPipelineId { get; set; }

    // ── Screenshot Analysis ──

    [CommandLine.Option("inventory-screenshots", Required = false, Default = false, HelpText = "List all screenshots found in TRX file and check if they exist on disk")]
    public bool InventoryScreenshots { get; set; } = false;

    [CommandLine.Option("test-screenshots", Required = false, Default = false, HelpText = "Test screenshot analysis with mock images (no real test failures needed)")]
    public bool TestScreenshots { get; set; } = false;

    [CommandLine.Option("mock", Required = false, Default = "all", HelpText = "Mock screenshot types: error, timeout, locator, all")]
    public string MockTypes { get; set; } = "all";

    [CommandLine.Option("screenshot-output", Required = false, Default = "./screenshots", HelpText = "Output directory for mock screenshots")]
    public string ScreenshotOutput { get; set; } = "./screenshots";

    [CommandLine.Option("vision-provider", Required = false, HelpText = "Vision AI provider: Gemini (default), Azure, OpenAI")]
    public string? VisionProvider { get; set; }

    [CommandLine.Option("analyze-screenshots", Required = false, Default = true, HelpText = "Analyze screenshots found in TRX file with vision AI (enabled by default)")]
    public bool AnalyzeScreenshots { get; set; } = true;

    [CommandLine.Option("skip-screenshot-analysis", Required = false, Default = false, HelpText = "Skip screenshot analysis to save API quota (disables --analyze-screenshots)")]
    public bool SkipScreenshotAnalysis { get; set; } = false;

    [CommandLine.Option("analyze-image", Required = false, HelpText = "Analyze a specific image file (no TRX needed) - standalone test mode")]
    public string? AnalyzeImage { get; set; }

    [CommandLine.Option("compare-all-providers", Required = false, Default = false, HelpText = "Compare all vision AI providers (Gemini, Azure, OpenAI) side-by-side")]
    public bool CompareAllProviders { get; set; } = false;

}

// ── Configuration Models ────────────────────────────────────────────────────

public class AzureDevOpsConfig
{
    public string OrganizationUrl { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string PersonalAccessToken { get; set; } = "";
    public int DefaultPipelineId { get; set; }
    public string TempDownloadPath { get; set; } = ".ado-downloads";
}

// ── Evidence Sections for Engineering Reports ──────────────────────────────

/// <summary>
/// Structured evidence sections for engineering-focused failure reports.
/// Separates observed facts from AI reasoning.
/// </summary>
public class EvidenceSections
{
    /// <summary>
    /// Chronological timeline of test execution events extracted from logs/stack trace.
    /// Each entry should be a timestamped event (e.g., "00:15:13 - Right-click succeeded").
    /// </summary>
    public List<string> Timeline { get; set; } = new();

    /// <summary>
    /// Test-side evidence: what the test framework captured (exception, stack trace, timeout values).
    /// Facts only - no interpretation.
    /// </summary>
    public string TestFrameworkEvidence { get; set; } = "";

    /// <summary>
    /// Application-side evidence: what the application logs show (UI states, performance gaps, errors).
    /// Facts only - no interpretation.
    /// </summary>
    public string ApplicationEvidence { get; set; } = "";

    /// <summary>
    /// Locator details extracted from error message (parent element, search scope, control type, etc.).
    /// </summary>
    public LocatorDetails? Locator { get; set; }

    /// <summary>
    /// List of evidence types that are missing and would help determine root cause.
    /// E.g., "Screenshot at failure", "Application logs", "UI automation tree", "Video recording".
    /// </summary>
    public List<string> MissingEvidence { get; set; } = new();

    /// <summary>
    /// Explanation of why each RAG chunk was retrieved (e.g., "Stack trace match", "Locator definition").
    /// Maps chunk index to reason.
    /// </summary>
    public Dictionary<int, string> RagRetrievalReasons { get; set; } = new();
}

/// <summary>
/// Locator details extracted from error messages and stack traces.
/// </summary>
public class LocatorDetails
{
    public string ElementName { get; set; } = "";
    public string ControlType { get; set; } = "";
    public string SearchScope { get; set; } = "";
    public string ParentElement { get; set; } = "";
    public string TimeoutDuration { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string SearchCondition { get; set; } = "";
}

