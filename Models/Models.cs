namespace FailureAnalyzer.Models;

// ── Raw TRX data ────────────────────────────────────────────────────────────

public class TestResult
{
    public string TestName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Outcome { get; set; } = "";      // Passed | Failed | NotExecuted
    public string Duration { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public DateTime EndTime { get; set; }
    public List<string> AttachmentPaths { get; set; } = new();
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

// ── AI analysis output ──────────────────────────────────────────────────────

public class FailureSuggestion
{
    public string Action { get; set; } = "";
    public string Type { get; set; } = "";       // locator | wait | code | environment | data | infrastructure
    public string Priority { get; set; } = "";   // immediate | soon | later
}

public class FailureAnalysis
{
    public string TestName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Category { get; set; } = "";   // locator | timing | environment | data | app_crash | assertion | flaky | other
    public string Severity { get; set; } = "";   // critical | high | medium | low
    public string ErrorSummary { get; set; } = "";
    public string PrimaryCause { get; set; } = "";
    public List<string> ContributingFactors { get; set; } = new();
    public List<FailureSuggestion> Suggestions { get; set; } = new();
    public string? CodeSnippet { get; set; }
    public List<string> AttachmentPaths { get; set; } = new();
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
    [CommandLine.Option("trx", Required = true, HelpText = "Path to .trx file (or glob pattern)")]
    public string TrxPath { get; set; } = "";

    [CommandLine.Option("logs", Required = false, HelpText = "Directory containing .log / .txt files")]
    public string? LogDirectory { get; set; }

    [CommandLine.Option("output", Required = false, Default = "failure-report.html", HelpText = "Output HTML report path")]
    public string Output { get; set; } = "failure-report.html";

    [CommandLine.Option("env", Required = false, Default = "Azure DevOps CI", HelpText = "Environment name")]
    public string Environment { get; set; } = "Azure DevOps CI";

    [CommandLine.Option("context", Required = false, HelpText = "Extra context for the AI (branch, recent changes, etc.)")]
    public string? Context { get; set; }

    [CommandLine.Option("max-failures", Required = false, Default = 15, HelpText = "Max failures to analyze via AI")]
    public int MaxFailures { get; set; } = 15;

    [CommandLine.Option("fail-on-critical", Required = false, Default = true, HelpText = "Exit code 2 if critical failures found")]
    public bool FailOnCritical { get; set; } = true;

    [CommandLine.Option("mock", Required = false, Default = false, HelpText = "Use mock AI responses (no API key needed) — for local testing")]
    public bool Mock { get; set; } = false;

    [CommandLine.Option("ollama", Required = false, Default = false, HelpText = "Use local Ollama instance for AI analysis (Free & Private)")]
    public bool Ollama { get; set; } = false;

    [CommandLine.Option("source-dir", Required = false, HelpText = "Path to your local repository to extract source code for RAG")]
    public string? SourceDirectory { get; set; }

}
