using System.Xml;
using System.Xml.Linq;
using FailureAnalyzer.Models;

namespace FailureAnalyzer.Services;

public class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public TestRun Parse(string trxPath)
    {
        if (!File.Exists(trxPath))
            throw new FileNotFoundException($"TRX file not found: {trxPath}");

        Console.WriteLine($"  Parsing: {Path.GetFileName(trxPath)}");

        XDocument xml;
        XElement root;

        try
        {
            xml = XDocument.Load(trxPath);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException(
                $"TRX file is corrupted or not valid XML: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Error reading TRX file (may be locked or on unstable network): {ex.Message}", ex);
        }

        if (xml.Root == null)
            throw new InvalidOperationException("TRX file is empty or has no root element - file may be corrupted");

        if (xml.Root.Name.LocalName != "TestRun")
            throw new InvalidOperationException(
                $"Not a valid TRX file - expected root element 'TestRun', found '{xml.Root.Name.LocalName}'");

        root = xml.Root;

        var run = new TestRun
        {
            RunName = root.Attribute("name")?.Value ?? Path.GetFileNameWithoutExtension(trxPath),
            StartTime = root.Element(Ns + "Times")?.Attribute("start")?.Value ?? "",
            FinishTime = root.Element(Ns + "Times")?.Attribute("finish")?.Value ?? ""
        };

        var definitions = root
            .Descendants(Ns + "UnitTest")
            .ToDictionary(
                u => u.Element(Ns + "Execution")?.Attribute("id")?.Value ?? "",
                u => u.Attribute("name")?.Value ?? ""
            );

        // Extract test categories from UnitTest definitions
        var categoriesMap = root
            .Descendants(Ns + "UnitTest")
            .ToDictionary(
                u => u.Element(Ns + "Execution")?.Attribute("id")?.Value ?? "",
                u => u.Element(Ns + "TestCategory")
                      ?.Elements(Ns + "TestCategoryItem")
                      .Select(item => item.Attribute("TestCategory")?.Value ?? "")
                      .Where(cat => !string.IsNullOrWhiteSpace(cat))
                      .ToList() ?? new List<string>()
            );

        // 1. Create a temporary list to hold ALL attempts (including retries)
        var allResults = new List<TestResult>();

        foreach (var result in root.Descendants(Ns + "UnitTestResult"))
        {
            var execId = result.Attribute("executionId")?.Value ?? "";
            var testName = definitions.TryGetValue(execId, out var def) ? def
                         : result.Attribute("testName")?.Value ?? "";

            // Validate test name exists - skip results with no name
            if (string.IsNullOrWhiteSpace(testName))
            {
                Console.WriteLine($"  [TRX] ⚠ WARNING: Test result with execution ID '{execId}' has no name - skipping");
                continue;
            }

            var categories = categoriesMap.TryGetValue(execId, out var cats) ? cats : new List<string>();

            var output = result.Element(Ns + "Output");
            var errorInfo = output?.Element(Ns + "ErrorInfo");

            // Extract attachment paths and resolve relative paths
            var trxDirectory = Path.GetDirectoryName(Path.GetFullPath(trxPath)) ?? "";
            var attachmentElements = result.Descendants(Ns + "ResultFile");
            var attachments = new List<string>();
            var missingAttachments = new List<string>();

            foreach (var rf in attachmentElements)
            {
                var path = rf.Attribute("path")?.Value;
                if (string.IsNullOrEmpty(path)) continue;

                // If path is relative, resolve it relative to TRX directory
                if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(trxDirectory))
                {
                    path = Path.GetFullPath(Path.Combine(trxDirectory, path));
                }

                // Track which files exist vs missing
                if (File.Exists(path))
                {
                    attachments.Add(path);
                }
                else
                {
                    missingAttachments.Add(path);
                }
            }

            // Warn about missing attachments
            if (missingAttachments.Any())
            {
                Console.WriteLine($"  [TRX] ⚠ Test '{testName}': {missingAttachments.Count} attachment(s) not found:");
                foreach (var missing in missingAttachments.Take(3))
                {
                    Console.WriteLine($"       - {Path.GetFileName(missing)}");
                }
                if (missingAttachments.Count > 3)
                    Console.WriteLine($"       ... and {missingAttachments.Count - 3} more");
            }

            // Safely parse the start and endTime
            DateTime.TryParse(result.Attribute("startTime")?.Value, out DateTime startTime);
            DateTime.TryParse(result.Attribute("endTime")?.Value, out DateTime endTime);

            var outcome = result.Attribute("outcome")?.Value ?? "Unknown";
            var errorMessage = CleanText(errorInfo?.Element(Ns + "Message")?.Value);
            var stackTrace = CleanText(errorInfo?.Element(Ns + "StackTrace")?.Value);

            // Validate failed tests have error information
            if (outcome == "Failed" && string.IsNullOrEmpty(errorMessage) && string.IsNullOrEmpty(stackTrace))
            {
                Console.WriteLine($"  [TRX] ⚠ WARNING: Test '{testName}' is marked as Failed but has no error message or stack trace");
                Console.WriteLine($"       This may indicate a test framework issue or incomplete TRX generation");
            }

            allResults.Add(new TestResult
            {
                TestName = testName,
                ShortName = ExtractShortName(testName),
                Outcome = outcome,
                Duration = result.Attribute("duration")?.Value ?? "",
                ErrorMessage = errorMessage,
                StackTrace = stackTrace,
                AttachmentPaths = attachments,
                Categories = categories,
                StartTime = startTime,
                EndTime = endTime
            });
        }

        // 2. Deduplication with retry detection
        // Group by the test name, and detect retries
        run.Results = allResults
            .GroupBy(r => r.ShortName)
            .Select(group =>
            {
                var attempts = group.OrderBy(r => r.EndTime).ToList();
                var totalAttempts = attempts.Count;

                // Keep the LAST attempt but mark if it was retried
                var finalAttempt = attempts.Last();
                finalAttempt.WasRetried = totalAttempts > 1;
                finalAttempt.AttemptNumber = totalAttempts;
                finalAttempt.TotalAttempts = totalAttempts;

                // If analyzing a failure and there were retries, warn about it
                if (finalAttempt.WasRetried && finalAttempt.Outcome == "Failed")
                {
                    Console.WriteLine($"  [TRX] ⚠ Test '{finalAttempt.ShortName}' was retried {totalAttempts} times and failed on attempt #{totalAttempts}");

                    // Check if any earlier attempts had different outcomes
                    var firstFailure = attempts.FirstOrDefault(a => a.Outcome == "Failed");
                    if (firstFailure != null && firstFailure.EndTime != finalAttempt.EndTime)
                    {
                        Console.WriteLine($"  [TRX] Note: First failure was at {firstFailure.StartTime:HH:mm:ss}, final failure at {finalAttempt.StartTime:HH:mm:ss}");
                    }
                }

                return finalAttempt;
            })
            .ToList();

        // 3. Recalculate totals based on the cleaned-up list
        int totalFailed = run.Results.Count(r => r.Outcome == "Failed");
        int totalPassed = run.Results.Count(r => r.Outcome == "Passed");
        int totalSkipped = run.Results.Count(r => r.Outcome == "NotExecuted");

        Console.WriteLine($"  Found {run.Results.Count} unique tests — {totalFailed} failed, {totalPassed} passed, {totalSkipped} skipped");

        // Diagnostic: Show attachment statistics
        var testsWithAttachments = run.Results.Count(r => r.AttachmentPaths.Any());
        var totalAttachments = run.Results.Sum(r => r.AttachmentPaths.Count);
        var screenshotAttachments = run.Results.Sum(r => r.AttachmentPaths.Count(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)));

        if (testsWithAttachments > 0)
        {
            Console.WriteLine($"  📎 Attachments: {testsWithAttachments} test(s) with {totalAttachments} file(s) ({screenshotAttachments} screenshot(s))");
        }

        return run;
    }

    private static string ExtractShortName(string fullName)
    {
        var parts = fullName.Split('.');
        return parts.Length > 1 ? parts[^1] : fullName;
    }

    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text.Trim().Replace("\r\n", "\n").Replace("\r", "\n");
    }
}