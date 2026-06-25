using System;
using System.IO;
using System.Linq;

namespace FailureAnalyzer.Services;

public class LogReader
{
   
    public string ReadLogsForTest(string logDirectory, string testShortName, int maxLines = 150)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory)) return "";

        try
        {
            // Find the log file that matches the test name
            var logFile = Directory.GetFiles(logDirectory, $"*{testShortName}*", SearchOption.AllDirectories)
                                   .FirstOrDefault(f => f.EndsWith(".txt") || f.EndsWith(".log"));

            if (logFile == null) return "";

            var allLines = File.ReadAllLines(logFile);
            if (allLines.Length == 0) return "";

            // 1. Grab the standard bottom lines to see the actual crash output
            var lastLines = allLines.TakeLast(maxLines).ToList();

            // 2. THE SILVER BULLET: Scan the ENTIRE log file for crucial UI interaction keywords.
            // This ensures that even if a 60-second timeout pushed the element name out of the 
            // bottom 150 lines, we still capture the element it was trying to find!
            var criticalKeywords = new[] { "AutomationId", "Name=", "ClassName=", "Click", "Find", "Invoke", "Element search" };

            var criticalLines = allLines
                .Where(line => criticalKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .TakeLast(20) // Get the last 20 exact UI actions performed right before the crash
                .ToList();

            // 3. Combine the critical actions with the crash logs, remove any duplicates, 
            // and send this highly condensed, information-rich snippet to the AI.
            var combinedLines = criticalLines.Concat(lastLines)
                .Distinct()
                .ToList();

            return string.Join("\n", combinedLines);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [LogReader] Warning: Could not read log file. {ex.Message}");
            return "";
        }
    }

       

    private string ReadLastLines(string filePath, int maxLines)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var allLines = sr.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        // Clean lines for specific files too
        var lastLines = allLines.TakeLast(maxLines).Select(CleanLogLine);
        return string.Join(Environment.NewLine, lastLines);
    }

    // This strips the "2026-06-19... [?]- " prefix from your logs!
    private string CleanLogLine(string line)
    {
        int splitIndex = line.IndexOf("[?]- ");
        if (splitIndex > 0)
        {
            return line.Substring(splitIndex + 5).Trim();
        }
        return line.Trim();
    }
}