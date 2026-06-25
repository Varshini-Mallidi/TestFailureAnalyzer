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
        var xml = XDocument.Load(trxPath);
        var root = xml.Root!;

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

        // 1. Create a temporary list to hold ALL attempts (including retries)
        var allResults = new List<TestResult>();

        foreach (var result in root.Descendants(Ns + "UnitTestResult"))
        {
            var execId = result.Attribute("executionId")?.Value ?? "";
            var testName = definitions.TryGetValue(execId, out var def) ? def
                         : result.Attribute("testName")?.Value ?? "Unknown";

            var output = result.Element(Ns + "Output");
            var errorInfo = output?.Element(Ns + "ErrorInfo");

            var attachments = result
                .Descendants(Ns + "ResultFile")
                .Select(rf => rf.Attribute("path")?.Value ?? "")
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            // Safely parse the endTime so we know which attempt is the newest
            DateTime.TryParse(result.Attribute("endTime")?.Value, out DateTime endTime);

            allResults.Add(new TestResult
            {
                TestName = testName,
                ShortName = ExtractShortName(testName),
                Outcome = result.Attribute("outcome")?.Value ?? "Unknown",
                Duration = result.Attribute("duration")?.Value ?? "",
                ErrorMessage = CleanText(errorInfo?.Element(Ns + "Message")?.Value),
                StackTrace = CleanText(errorInfo?.Element(Ns + "StackTrace")?.Value),
                AttachmentPaths = attachments,
                EndTime = endTime 
            });
        }

        // 2. Deduplication
        // Group by the test name, sort by time, and keep ONLY the final attempt
        run.Results = allResults
            .GroupBy(r => r.ShortName)
            .Select(group => group.OrderByDescending(r => r.EndTime).First())
            .ToList();

        // 3. Recalculate totals based on the cleaned-up list
        int totalFailed = run.Results.Count(r => r.Outcome == "Failed");
        int totalPassed = run.Results.Count(r => r.Outcome == "Passed");
        int totalSkipped = run.Results.Count(r => r.Outcome == "NotExecuted");

        Console.WriteLine($"  Found {run.Results.Count} unique tests — {totalFailed} failed, {totalPassed} passed, {totalSkipped} skipped");
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