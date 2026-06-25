using FailureAnalyzer.Models;

namespace FailureAnalyzer.Services;

/// <summary>
/// Drop-in replacement for AzureOpenAIAnalyzer when no API key is available.
/// Returns realistic-looking analysis based on keyword heuristics so you can
/// test the full pipeline (TRX parsing → analysis → HTML report → ADO publish)
/// without spending a single API token.
/// Switch to the real analyzer by removing --mock from the CLI / pipeline.
/// </summary>
public class MockAnalyzer
{
    private static readonly Random Rng = new(42);

    public Task<FailureAnalysis> AnalyzeFailureAsync(
        TestResult failure, string logSnippet, string environment, string? extraContext)
    {
        var (category, severity) = Categorize(failure.ErrorMessage + failure.StackTrace);

        var analysis = new FailureAnalysis
        {
            TestName     = failure.TestName,
            ShortName    = failure.ShortName,
            AttachmentPaths = failure.AttachmentPaths,
            Category     = category,
            Severity     = severity,
            ErrorSummary = Truncate(failure.ErrorMessage, 220),
            PrimaryCause = PrimaryCause(category, failure),
            ContributingFactors = ContributingFactors(category, environment),
            Suggestions  = Suggestions(category),
            CodeSnippet  = CodeSnippet(category, failure)
        };

        Console.WriteLine($"    [MOCK] {failure.ShortName} → {severity}/{category}");
        return Task.FromResult(analysis);
    }

    public Task<(List<string> Patterns, string EnvNotes)> DetectPatternsAsync(
        List<FailureAnalysis> failures, string environment)
    {
        var categories = failures
            .GroupBy(f => f.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} failure(s) categorized as '{g.Key}'")
            .ToList();

        var patterns = new List<string>
        {
            $"[MOCK] {failures.Count} failure(s) analyzed across this run",
            categories.FirstOrDefault() ?? "Mixed failure types detected",
            "Consider reviewing locator stability after any recent UI changes",
            "CI timing differences from local environment may be amplifying flakiness"
        };

        var envNotes = environment.Contains("DevOps", StringComparison.OrdinalIgnoreCase)
            ? "[MOCK] Azure DevOps hosted agents have shorter timeouts than local dev machines — verify all explicit waits use configurable values."
            : "[MOCK] Verify test environment matches expected application state before each run.";

        return Task.FromResult((patterns, envNotes));
    }

    // ── Heuristics ──────────────────────────────────────────────────────────

    private static (string category, string severity) Categorize(string text)
    {
        text = text.ToLowerInvariant();

        if (Contains(text, "elementnotavailable", "stale", "element is no longer"))
            return ("locator", "critical");

        if (Contains(text, "timeout", "timed out", "waituntil", "not found within"))
            return ("timing", "high");

        if (Contains(text, "window not found", "application", "process", "failed to start", "crashed"))
            return ("app_crash", "critical");

        if (Contains(text, "assert", "expected", "actual", "isequal", "istrue", "isfalse"))
            return ("assertion", "medium");

        if (Contains(text, "connection", "network", "server", "database", "sql", "404", "500"))
            return ("environment", "high");

        if (Contains(text, "login", "auth", "credential", "token", "session"))
            return ("data", "high");

        return ("other", "medium");
    }

    private static string PrimaryCause(string category, TestResult f) => category switch
    {
        "locator"     => $"[MOCK] The UI element referenced in '{f.ShortName}' became unavailable mid-test, likely due to a page re-render or recent UI change that altered the element hierarchy or AutomationId.",
        "timing"      => $"[MOCK] A hard-coded wait or timeout in '{f.ShortName}' was insufficient — the element or condition did not appear within the expected window, which is common when CI agents are slower than local machines.",
        "app_crash"   => $"[MOCK] The application under test did not start or became unresponsive before '{f.ShortName}' could interact with it. On Azure DevOps hosted agents this is often caused by missing interactive session or slow startup.",
        "assertion"   => $"[MOCK] The test assertion failed because the actual UI state did not match the expected state in '{f.ShortName}'. This may indicate a regression in application logic or a test data issue.",
        "environment" => $"[MOCK] A network, database, or infrastructure dependency was unavailable or returned an error during '{f.ShortName}'. This type of failure is usually environment-specific rather than a test code bug.",
        "data"        => $"[MOCK] Test data required by '{f.ShortName}' was missing, stale, or in an unexpected state. Pre-test data setup may need to be more robust.",
        _             => $"[MOCK] '{f.ShortName}' failed for an unclassified reason. Review the stack trace and error message for specific clues."
    };

    private static List<string> ContributingFactors(string category, string environment) => category switch
    {
        "locator" => new()
        {
            "AutomationId or element Name may have changed after a recent UI update",
            "Element located before the page/dialog has fully rendered",
            "No retry-on-stale pattern in the page object layer",
            "FlaUI element cache not invalidated after navigation"
        },
        "timing" => new()
        {
            $"Timeout values tuned for local dev, not {environment}",
            "Thread.Sleep used instead of condition-based polling",
            "Backend API response slower under CI network conditions",
            "No adaptive wait strategy implemented in WaitHelper"
        },
        "app_crash" => new()
        {
            "Azure DevOps hosted agent may lack an interactive Windows session",
            "Application startup exceeded the window check timeout",
            "Missing .NET runtime version or app config on the agent",
            "AppDriver does not capture process stdout/stderr on launch failure"
        },
        "assertion" => new()
        {
            "Application may have a regression introduced since last passing run",
            "Test data left in unexpected state by a previous test",
            "Race condition between UI update and assertion check",
            "Expected value hardcoded rather than read from a shared constant"
        },
        _ => new()
        {
            "Review recent commits for changes that could affect this test",
            "Check environment parity between local and CI",
            "Verify test isolation — shared state from other tests",
        }
    };

    private static List<FailureSuggestion> Suggestions(string category) => category switch
    {
        "locator" => new()
        {
            new() { Action = "Audit AutomationIds post-UI change — confirm they match what FlaUI is searching for", Type = "locator", Priority = "immediate" },
            new() { Action = "Wrap FindFirstDescendant calls in a RetryFind() helper that re-queries on ElementNotAvailableException", Type = "code", Priority = "immediate" },
            new() { Action = "Add explicit wait after any navigation or dialog open before locating child elements", Type = "wait", Priority = "soon" }
        },
        "timing" => new()
        {
            new() { Action = "Replace Thread.Sleep with FlaUI WaitUntilEnabled / WaitUntilClickable or a custom polling loop", Type = "wait", Priority = "immediate" },
            new() { Action = "Read timeouts from appsettings.json and override via environment variable in ADO pipeline (e.g. TEST_TIMEOUT_MS=15000)", Type = "environment", Priority = "soon" },
            new() { Action = "Increase default WaitForElement timeout from 5 s to 15 s for CI runs", Type = "wait", Priority = "immediate" }
        },
        "app_crash" => new()
        {
            new() { Action = "Switch to a self-hosted ADO agent configured with 'Allow interactive process' for UI automation", Type = "infrastructure", Priority = "immediate" },
            new() { Action = "Add a smoke-test pipeline step before the test run: 'dotnet run -- --smoke' to verify the app launches", Type = "environment", Priority = "soon" },
            new() { Action = "Capture application process stdout/stderr in AppDriver.GetMainWindow() and attach to test result on failure", Type = "code", Priority = "soon" }
        },
        "assertion" => new()
        {
            new() { Action = "Add a test setup step to reset application state / database to a known baseline before this test", Type = "data", Priority = "immediate" },
            new() { Action = "Compare actual vs expected values in the failure message to narrow down which field diverged", Type = "code", Priority = "soon" },
            new() { Action = "Run the test in isolation (dotnet test --filter TestName=...) to rule out shared state pollution", Type = "environment", Priority = "soon" }
        },
        _ => new()
        {
            new() { Action = "Review the stack trace and identify the earliest frame in your own code", Type = "code", Priority = "immediate" },
            new() { Action = "Add more detailed logging around the failing step to improve future diagnostics", Type = "code", Priority = "soon" }
        }
    };

    private static string? CodeSnippet(string category, TestResult f) => category switch
    {
        "locator" => """
            // RetryFind helper — add to a shared BasePageObject class
            protected AutomationElement RetryFind(
                Func<AutomationElement?> finder,
                int retries = 3,
                int delayMs = 500)
            {
                for (int i = 0; i < retries; i++)
                {
                    try
                    {
                        var el = finder();
                        if (el != null) return el;
                    }
                    catch (ElementNotAvailableException) { /* stale ref — retry */ }

                    Thread.Sleep(delayMs);
                }
                throw new ElementNotFoundException(
                    $"Element not found after {retries} retries");
            }

            // Usage in your page object:
            var btn = RetryFind(() =>
                _window.FindFirstDescendant(cf => cf.ByAutomationId("btnLogin")));
            btn.AsButton().Invoke();
            """,

        "timing" => """
            // Configurable wait helper — reads timeout from env var or default
            public static class WaitHelper
            {
                private static int DefaultMs =>
                    int.TryParse(Environment.GetEnvironmentVariable("TEST_TIMEOUT_MS"),
                        out var v) ? v : 10_000;

                public static AutomationElement WaitForElement(
                    AutomationElement parent,
                    string automationId,
                    int? timeoutMs = null)
                {
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs ?? DefaultMs);
                    while (DateTime.UtcNow < deadline)
                    {
                        var el = parent.FindFirstDescendant(
                            cf => cf.ByAutomationId(automationId));
                        if (el != null) return el;
                        Thread.Sleep(250);
                    }
                    throw new TimeoutException(
                        $"Element '{automationId}' not found within {timeoutMs ?? DefaultMs}ms");
                }
            }
            """,

        _ => null
    };

    private static bool Contains(string text, params string[] keywords)
        => keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
