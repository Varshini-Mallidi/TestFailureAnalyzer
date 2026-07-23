using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FailureAnalyzer.Services;

/// <summary>
/// Improved log reader with streaming, timestamp filtering, log-level awareness, and context windows.
/// Uses weighted compound pattern matching for intelligent log prioritization.
/// </summary>
public class LogReaderV2
{
    /// <summary>
    /// Read logs for a specific test with improved performance and context extraction.
    /// </summary>
    public string ReadLogsForTest(
        string logPath, 
        string testShortName,
        string? runDate = null, 
        int contextLines = 10,
        int maxTotalLines = 500)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return "";

        try
        {
            var logFiles = GetLogFiles(logPath);
            var relevantFiles = FindFilesContainingTest(logFiles, testShortName);

            if (!relevantFiles.Any())
            {
                Console.WriteLine($"  [LogReader] No logs found for test: {testShortName}");
                return "";
            }

            var logEntries = ExtractRelevantLogEntries(relevantFiles, testShortName, runDate, contextLines);

            // Step 1: Use priority to SELECT the most relevant entries
            var selectedEntries = logEntries
                .OrderByDescending(e => e.Priority)
                .Take(maxTotalLines)
                .ToList();

            // Step 2: Re-order chronologically for PRESENTATION
            var chronologicalEntries = selectedEntries
                .OrderBy(e => e.Timestamp ?? DateTime.MinValue)
                .ThenBy(e => e.LineNumber)
                .ToList();

            Console.WriteLine($"  [LogReader] Extracted {chronologicalEntries.Count} log lines in chronological order from {relevantFiles.Count} file(s)");

            return FormatLogsWithTimeline(chronologicalEntries);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [LogReader] Error: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Read logs for a test using name-based extraction.
    /// Our automation framework always writes test names to log files, so name-based extraction always succeeds.
    /// </summary>
    public string ReadLogsForTestByTime(
        string logPath,
        string testShortName,
        DateTime? startTime,
        DateTime? endTime,
        int contextLines = 20,
        int maxTotalLines = 500)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return "";

        try
        {
            var logFiles = GetLogFiles(logPath);
            if (!logFiles.Any())
            {
                Console.WriteLine($"  [LogReader] No log files found at path: {logPath}");
                return "";
            }

            Console.WriteLine($"  [LogReader] Scanning {logFiles.Count} log file(s) for test '{testShortName}'");

            // Find logs mentioning the test name (always succeeds in our automation framework)
            var relevantFiles = FindFilesContainingTest(logFiles, testShortName);

            if (!relevantFiles.Any())
            {
                Console.WriteLine($"  [LogReader] ⚠ Test name '{testShortName}' not found in any log file");
                return "";
            }

            Console.WriteLine($"  [LogReader] ✓ Test name found in {relevantFiles.Count} file(s): {string.Join(", ", relevantFiles.Select(f => Path.GetFileName(f)))}");

            // Use name-based extraction
            var logEntries = ExtractRelevantLogEntries(relevantFiles, testShortName, null, contextLines);

            // Step 1: Use priority to SELECT the most relevant entries
            var selectedEntries = logEntries
                .OrderByDescending(e => e.Priority)
                .Take(maxTotalLines)
                .ToList();

            // Step 2: Re-order chronologically for PRESENTATION
            var chronologicalEntries = selectedEntries
                .OrderBy(e => e.Timestamp ?? DateTime.MinValue)
                .ThenBy(e => e.LineNumber)
                .ToList();

            Console.WriteLine($"  [LogReader] Extracted {chronologicalEntries.Count} log lines from test-specific sections");
            return FormatLogsWithTimeline(chronologicalEntries);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [LogReader] Error: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Read logs with separated application vs test evidence for better AI classification.
    /// Returns structured evidence separating app-side signals from test-side actions.
    /// </summary>
    public SeparatedEvidence ReadLogsWithSeparatedEvidence(
        string logPath,
        string testShortName,
        DateTime? startTime,
        DateTime? endTime,
        int contextLines = 20,
        int maxTotalLines = 500)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return new SeparatedEvidence 
            { 
                ApplicationEvidence = "(No log path provided)",
                TestEvidence = "(No log path provided)",
                HasActualApplicationLogFiles = false
            };

        try
        {
            var logFiles = GetLogFiles(logPath);
            if (!logFiles.Any())
            {
                Console.WriteLine($"  [LogReader] No log files found at path: {logPath}");
                return new SeparatedEvidence
                {
                    ApplicationEvidence = "(No log files found)",
                    TestEvidence = "(No log files found)",
                    HasActualApplicationLogFiles = false
                };
            }

            Console.WriteLine($"  [LogReader] Scanning {logFiles.Count} log file(s) for test '{testShortName}'");

            // Detect if any log files are actual application logs (not test framework logs)
            bool hasRealAppLogs = logFiles.Any(f =>
            {
                var name = Path.GetFileName(f);
                return name.StartsWith("Admin-", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("Application-", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("App-", StringComparison.OrdinalIgnoreCase) ||
                       (name.Contains("Admin", StringComparison.OrdinalIgnoreCase) && 
                        !name.Contains("Automation", StringComparison.OrdinalIgnoreCase));
            });

            // Find logs mentioning the test name (always succeeds in our automation framework)
            var relevantFiles = FindFilesContainingTest(logFiles, testShortName);

            if (!relevantFiles.Any())
            {
                Console.WriteLine($"  [LogReader] ⚠ Test name '{testShortName}' not found in any log file");
                return new SeparatedEvidence
                {
                    ApplicationEvidence = "(Test not found in logs)",
                    TestEvidence = "(Test not found in logs)",
                    HasActualApplicationLogFiles = false
                };
            }

            Console.WriteLine($"  [LogReader] ✓ Test name found in {relevantFiles.Count} file(s)");
            var logEntries = ExtractRelevantLogEntries(relevantFiles, testShortName, null, contextLines);

            var selectedEntries = logEntries
                .OrderByDescending(e => e.Priority)
                .Take(maxTotalLines)
                .ToList();

            var chronologicalEntries = selectedEntries
                .OrderBy(e => e.Timestamp ?? DateTime.MinValue)
                .ThenBy(e => e.LineNumber)
                .ToList();

            // Separate application evidence from test evidence
            var result = SeparateApplicationEvidence(chronologicalEntries);
            result.HasActualApplicationLogFiles = hasRealAppLogs;
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [LogReader] Error: {ex.Message}");
            return new SeparatedEvidence
            {
                ApplicationEvidence = $"(Error reading logs: {ex.Message})",
                TestEvidence = $"(Error reading logs: {ex.Message})",
                HasActualApplicationLogFiles = false
            };
        }
    }

    private List<string> GetLogFiles(string logPath)
    {
        if (File.Exists(logPath))
            return new List<string> { logPath };

        if (Directory.Exists(logPath))
        {
            var allFiles = Directory.GetFiles(logPath, "*.*", SearchOption.AllDirectories);
            var logFiles = allFiles.Where(f => IsLogFile(f)).ToList();

            Console.WriteLine($"  [LogReader] DEBUG: Found {allFiles.Length} total files, {logFiles.Count} are log files");
            foreach (var file in logFiles)
            {
                Console.WriteLine($"  [LogReader] DEBUG:   - {Path.GetFileName(file)}");
            }

            return logFiles;
        }

        return new List<string>();
    }

    private bool IsLogFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        // Match .log and .txt extensions
        if (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || 
            fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return true;

        // Match numbered backups with various formats:
        // - Simple: .log.1, .txt.2
        // - Parentheses: .txt(3), .log(5)
        // - With spaces: .txt (3).1, .log (2).5
        // - Combined: .txt(3).1, .log(2).5
        // - Multiple dots: .txt.old.1
        // Pattern explanation: after .log or .txt, allow optional space, then . or (, then eventually a digit
        if (Regex.IsMatch(fileName, @"\.(log|txt)\s*[\.\(].*\d", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private List<string> FindFilesContainingTest(List<string> logFiles, string testName)
    {
        var matchingFiles = new List<string>();

        foreach (var file in logFiles)
        {
            try
            {
                Console.WriteLine($"  [LogReader] DEBUG: Scanning {Path.GetFileName(file)} for '{testName}'...");
                // Stream search instead of loading entire file
                if (FileContainsTest(file, testName))
                {
                    Console.WriteLine($"  [LogReader] DEBUG:   ✓ FOUND in {Path.GetFileName(file)}");
                    matchingFiles.Add(file);
                }
                else
                {
                    Console.WriteLine($"  [LogReader] DEBUG:   ✗ NOT FOUND in {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [LogReader] Warning: Could not scan {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return matchingFiles;
    }

    private bool FileContainsTest(string filePath, string testName)
    {
        using var reader = new StreamReader(filePath);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Contains(testName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private List<LogEntry> ExtractRelevantLogEntries(
        List<string> files, 
        string testName, 
        string? runDate,
        int contextLines)
    {
        var entries = new List<LogEntry>();

        foreach (var file in files)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                var testMentionIndices = new List<int>();

                // Find all lines mentioning the test
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(testName, StringComparison.OrdinalIgnoreCase))
                        testMentionIndices.Add(i);
                }

                // Extract context windows around test mentions
                foreach (var index in testMentionIndices)
                {
                    int start = Math.Max(0, index - contextLines);
                    int end = Math.Min(lines.Length - 1, index + contextLines);

                    for (int i = start; i <= end; i++)
                    {
                        var entry = ParseLogEntry(lines[i], file, i + 1);

                        // Filter by date if provided
                        if (runDate != null && entry.Timestamp != null)
                        {
                            if (!entry.Timestamp.Value.ToString("yyyy-MM-dd").Contains(runDate))
                                continue;
                        }

                        entries.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [LogReader] Warning: Error reading {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return entries;
    }



    private LogEntry ParseLogEntry(string line, string sourceFile, int lineNumber)
    {
        var entry = new LogEntry
        {
            Line = line.Trim(),
            SourceFile = Path.GetFileName(sourceFile),
            LineNumber = lineNumber,
            Level = DetermineLogLevel(line),
            Timestamp = ExtractTimestamp(line)
        };

        // Calculate priority score
        entry.Priority = CalculatePriority(entry);

        return entry;
    }

    private LogLevel DetermineLogLevel(string line)
    {
        var upper = line.ToUpperInvariant();

        // Error level - matches high-priority patterns
        if (upper.Contains("EXCEPTION") || upper.Contains("ERROR") || 
            upper.Contains("FATAL") || upper.Contains("CRASH") || upper.Contains("FAILED"))
            return LogLevel.Error;

        // Warning level
        if (upper.Contains("WARN") || upper.Contains("WARNING"))
            return LogLevel.Warning;

        // Critical UI - FlaUI patterns
        if (line.Contains("AutomationId", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Click", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Find", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Invoke", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Element search", StringComparison.OrdinalIgnoreCase))
            return LogLevel.CriticalUI;

        // Info level
        if (upper.Contains("INFO"))
            return LogLevel.Info;

        // Debug level
        if (upper.Contains("DEBUG") || upper.Contains("TRACE"))
            return LogLevel.Debug;

        return LogLevel.Unknown;
    }

    private DateTime? ExtractTimestamp(string line)
    {
        // Pattern 1: yyyy-MM-dd HH:mm:ss,fff (your actual log format)
        var match = Regex.Match(line, @"(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2}:\d{2})[,\.](\d{1,3})");
        if (match.Success)
        {
            try
            {
                var datePart = match.Groups[1].Value;
                var timePart = match.Groups[2].Value;
                var milliseconds = match.Groups[3].Value.PadRight(3, '0'); // Ensure 3 digits

                var timestampStr = $"{datePart} {timePart}.{milliseconds}";
                if (DateTime.TryParseExact(timestampStr, "yyyy-MM-dd HH:mm:ss.fff", 
                    System.Globalization.CultureInfo.InvariantCulture, 
                    System.Globalization.DateTimeStyles.None, 
                    out var timestamp))
                {
                    return timestamp;
                }
            }
            catch { }
        }

        // Pattern 2: yyyy-MM-dd HH:mm:ss (no milliseconds)
        match = Regex.Match(line, @"\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}");
        if (match.Success && DateTime.TryParse(match.Value, out var ts2))
            return ts2;

        // Pattern 3: MM/dd/yyyy HH:mm:ss
        match = Regex.Match(line, @"\d{2}/\d{2}/\d{4}\s+\d{2}:\d{2}:\d{2}");
        if (match.Success && DateTime.TryParse(match.Value, out var ts3))
            return ts3;

        // Pattern 4: ISO 8601 with milliseconds [yyyy-MM-ddTHH:mm:ss.fffZ]
        match = Regex.Match(line, @"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)\]");
        if (match.Success && DateTime.TryParse(match.Groups[1].Value, out var ts4))
            return ts4;

        return null;
    }

    private int CalculatePriority(LogEntry entry)
    {
        var line = entry.Line;
        var upper = line.ToUpperInvariant();
        int score = 0;

        // ═══════════════════════════════════════════════════════════════════════
        // TIER 1: Highly Specific Compound Patterns (180-200 points)
        // These are the most relevant - specific failures with context
        // ═══════════════════════════════════════════════════════════════════════

        // Exception with exact file:line location (stack trace with context)
        if (upper.Contains("EXCEPTION") && line.Contains(".cs:line"))
        {
            score = 200;  // Highest priority - crash with exact location
            return score;
        }

        // FlaUI element failure with specific AutomationId
        // Matches: "AutomationId=_paneDockAreaTop not found", "AutomationId=!!ADMINELEMDB timed out"
        if (Regex.IsMatch(line, @"AutomationId\s*=\s*[A-Za-z_!][A-Za-z0-9_!]*.*?(not found|timed?\s?out|failed)", 
            RegexOptions.IgnoreCase))
        {
            score = 180;  // Specific UI element failure with ID
            return score;
        }

        // App-specific crash patterns (customize for your app)
        if (upper.Contains("DABACON") && (upper.Contains("CRASH") || upper.Contains("ERROR")))
        {
            score = 170;  // Known application crash pattern
            return score;
        }

        // Database-specific errors with context
        if ((upper.Contains("MDB") || upper.Contains("DATABASE")) && 
            (upper.Contains("ERROR") || upper.Contains("FAILED") || upper.Contains("FULL")))
        {
            score = 165;  // Database errors are often app issues
            return score;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // TIER 2: Specific Signals (100-150 points)
        // Context-rich signals that help understand the failure
        // ═══════════════════════════════════════════════════════════════════════

        // Stack trace lines (without exception keyword already handled above)
        if (line.Contains("at ") && line.Contains(".cs:line"))
        {
            score = 140;  // Stack trace frame - shows execution path
            return score;
        }

        // Specific exception types (more valuable than generic "EXCEPTION")
        if (upper.Contains("NULLREFERENCEEXCEPTION"))
        {
            score = 130;  // Very common, usually app bug
            return score;
        }
        if (upper.Contains("ARGUMENTEXCEPTION") || upper.Contains("ARGUMENTNULLEXCEPTION"))
        {
            score = 125;  // Parameter validation failure
            return score;
        }
        if (upper.Contains("INVALIDOPERATIONEXCEPTION"))
        {
            score = 125;  // State management issue
            return score;
        }

        // FlaUI element reference (generic - no failure keyword)
        if (Regex.IsMatch(line, @"AutomationId\s*=\s*[A-Za-z_!][A-Za-z0-9_!]*"))
        {
            score = 120;  // UI element mentioned, might be relevant
            return score;
        }

        // Timeout patterns (often critical for UI automation)
        if (upper.Contains("TIMEOUT") || upper.Contains("TIMED OUT"))
        {
            score = 115;  // Timing issues are common failure cause
            return score;
        }

        // Element not found patterns
        if (upper.Contains("NOT FOUND") || upper.Contains("NOTFOUND"))
        {
            score = 110;  // Missing elements/resources
            return score;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // TIER 3: Generic Signals (50-90 points)
        // Standard log levels - useful but not specific enough alone
        // ═══════════════════════════════════════════════════════════════════════

        // Generic exception (less specific than named exceptions above)
        if (upper.Contains("EXCEPTION"))
        {
            score = 90;
            return score;
        }

        // Generic error keywords
        if (upper.Contains("ERROR") || upper.Contains("FATAL"))
        {
            score = 80;
            return score;
        }

        // Failed keyword (very generic)
        if (upper.Contains("FAILED") || upper.Contains("FAILURE"))
        {
            score = 75;
            return score;
        }

        // Crash keyword
        if (upper.Contains("CRASH"))
        {
            score = 85;
            return score;
        }

        // Warnings (precursors to failures)
        if (upper.Contains("WARN") || upper.Contains("WARNING"))
        {
            score = 50;
            return score;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // TIER 4: Context/Informational (1-30 points)
        // Background information - useful context but not failures
        // ═══════════════════════════════════════════════════════════════════════

        // Informational logs
        if (upper.Contains("INFO"))
        {
            score = 10;
            return score;
        }

        // Debug/trace logs (lowest priority)
        if (upper.Contains("DEBUG") || upper.Contains("TRACE"))
        {
            score = 1;
            return score;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // NEGATIVE PATTERNS: Reduce score for success messages
        // These contain keywords but are actually positive outcomes
        // ═══════════════════════════════════════════════════════════════════════

        // Success indicators - reduce score significantly
        if (upper.Contains("SUCCESS") || upper.Contains("SUCCESSFUL") || upper.Contains("PASSED"))
        {
            score = Math.Max(0, score - 50);  // Success messages are low priority
        }

        // Default for unmatched lines
        if (score == 0)
        {
            score = 5;  // Minimal score for context lines
        }

        return score;
    }

    /// <summary>
    /// Format logs with timeline context for better AI comprehension.
    /// </summary>
    private string FormatLogsWithTimeline(List<LogEntry> entries)
    {
        if (!entries.Any()) return "";

        var sb = new System.Text.StringBuilder();

        // Add timeline context
        var first = entries.First();
        var last = entries.Last();

        if (first.Timestamp.HasValue && last.Timestamp.HasValue)
        {
            var duration = last.Timestamp.Value - first.Timestamp.Value;
            sb.AppendLine($"=== Log Timeline: {first.Timestamp.Value:HH:mm:ss} to {last.Timestamp.Value:HH:mm:ss} (duration: {duration.TotalMinutes:F1} min) ===");
        }
        else
        {
            sb.AppendLine("=== Log Entries (timestamps may be incomplete) ===");
        }

        // Summary of severity distribution
        var errorCount = entries.Count(e => e.Level == LogLevel.Error);
        var warnCount = entries.Count(e => e.Level == LogLevel.Warning);
        var criticalUICount = entries.Count(e => e.Level == LogLevel.CriticalUI);

        if (errorCount > 0 || warnCount > 0)
        {
            sb.AppendLine($"Summary: {errorCount} errors, {warnCount} warnings, {criticalUICount} critical UI operations");
        }

        // ── LOG GAP DETECTION ──
        // Pre-compute time gaps so the AI doesn't have to infer them
        var gaps = DetectLogGaps(entries, thresholdSeconds: 10);
        if (gaps.Any())
        {
            sb.AppendLine($"\n⚠️  DETECTED {gaps.Count} SIGNIFICANT TIME GAP(S) (>10 seconds of application silence):");
            foreach (var gap in gaps)
            {
                sb.AppendLine($"  • {gap.GapDuration:F1}s gap between {gap.BeforeTime:HH:mm:ss} and {gap.AfterTime:HH:mm:ss}");
                sb.AppendLine($"    After: {gap.AfterLine.Trim()}");
                sb.AppendLine($"    Before: {gap.BeforeLine.Trim()}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== Chronological Log Entries (cause → effect timeline) ===");
        sb.AppendLine();

        // Present logs in chronological order WITH gap alerts injected inline
        for (int i = 0; i < entries.Count; i++)
        {
            // Inject gap alert BEFORE the current line if this is where a gap ends
            var gapHere = gaps.FirstOrDefault(g => g.AfterIndex == i);
            if (gapHere != null)
            {
                sb.AppendLine($"[⚠️  APPLICATION SILENCE: {gapHere.GapDuration:F1}s gap]");
            }

            sb.AppendLine(entries[i].Line);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Detects gaps between consecutive log entries where timestamps jump by more than the threshold.
    /// Returns a list of gap details for AI analysis.
    /// </summary>
    private List<LogGap> DetectLogGaps(List<LogEntry> entries, double thresholdSeconds)
    {
        var gaps = new List<LogGap>();

        for (int i = 1; i < entries.Count; i++)
        {
            var prev = entries[i - 1];
            var curr = entries[i];

            if (!prev.Timestamp.HasValue || !curr.Timestamp.HasValue)
                continue;

            var gapSeconds = (curr.Timestamp.Value - prev.Timestamp.Value).TotalSeconds;

            if (gapSeconds > thresholdSeconds)
            {
                gaps.Add(new LogGap
                {
                    BeforeIndex = i - 1,
                    AfterIndex = i,
                    BeforeTime = prev.Timestamp.Value,
                    AfterTime = curr.Timestamp.Value,
                    GapDuration = gapSeconds,
                    BeforeLine = prev.Line,
                    AfterLine = curr.Line
                });
            }
        }

        return gaps;
    }

    /// <summary>
    /// Separate application-side evidence from test-side evidence for better classification.
    /// </summary>
    public SeparatedEvidence SeparateApplicationEvidence(List<LogEntry> entries)
    {
        var appSignals = new List<string>();
        var testActions = new List<string>();

        // Detect time gaps (application performance issue indicator)
        var gaps = DetectLogGaps(entries, thresholdSeconds: 10);

        foreach (var entry in entries)
        {
            var line = entry.Line;
            var isApplicationSignal = false;

            // Application error signals
            if (entry.Level == LogLevel.Error)
            {
                // Strong application signals
                if (line.Contains("Dabacon Error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("E_ACCESSDENIED", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("MainWindow") && line.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("ElementNotAvailableException") ||
                    line.Contains("Application") && line.Contains("crashed", StringComparison.OrdinalIgnoreCase))
                {
                    appSignals.Add(line);
                    isApplicationSignal = true;
                }
                // Generic errors - could be either side, include in both for context
                else if (line.Contains("ERROR:") || line.Contains("EXCEPTION:") || line.Contains("FATAL:"))
                {
                    // Only add to app signals if not obviously a test assertion
                    if (!line.Contains("Assert.") && !line.Contains("Test method") && !line.Contains("FlaUI"))
                    {
                        appSignals.Add(line);
                        isApplicationSignal = true;
                    }
                }
            }

            // Application warning signals
            if (entry.Level == LogLevel.Warning)
            {
                if (line.Contains("performance", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("slow", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("timeout", StringComparison.OrdinalIgnoreCase) && !line.Contains("Element search"))
                {
                    appSignals.Add(line);
                    isApplicationSignal = true;
                }
            }

            // If not an app signal, it's a test action
            if (!isApplicationSignal)
            {
                testActions.Add(line);
            }
        }

        // Format application signals section
        var appEvidenceText = "";
        if (appSignals.Any() || gaps.Any())
        {
            var sb = new System.Text.StringBuilder();

            if (appSignals.Any())
            {
                sb.AppendLine("**Application Errors/Warnings:**");
                foreach (var signal in appSignals.Take(20)) // Limit to avoid overflow
                {
                    sb.AppendLine($"  {signal}");
                }
                if (appSignals.Count > 20)
                {
                    sb.AppendLine($"  ... and {appSignals.Count - 20} more application errors");
                }
                sb.AppendLine();
            }

            if (gaps.Any())
            {
                sb.AppendLine("**Performance Issues Detected:**");
                foreach (var gap in gaps)
                {
                    sb.AppendLine($"  • {gap.GapDuration:F1}s gap between {gap.BeforeTime:HH:mm:ss} and {gap.AfterTime:HH:mm:ss}");
                    sb.AppendLine($"    Context: {gap.AfterLine.Trim()}");
                }
            }

            appEvidenceText = sb.ToString();
        }
        else
        {
            appEvidenceText = "(No application errors, exceptions, or significant performance issues detected in logs)";
        }

        // Format test actions section
        var testEvidenceText = "";
        if (testActions.Any())
        {
            var sb = new System.Text.StringBuilder();
            var first = entries.First();
            var last = entries.Last();

            if (first.Timestamp.HasValue && last.Timestamp.HasValue)
            {
                var duration = last.Timestamp.Value - first.Timestamp.Value;
                sb.AppendLine($"Test execution timeline: {first.Timestamp.Value:HH:mm:ss} to {last.Timestamp.Value:HH:mm:ss} (duration: {duration.TotalMinutes:F1} min)");
            }

            sb.AppendLine();
            foreach (var action in testActions.Take(100)) // Limit for readability
            {
                sb.AppendLine(action);
            }
            if (testActions.Count > 100)
            {
                sb.AppendLine($"... and {testActions.Count - 100} more test actions");
            }

            testEvidenceText = sb.ToString();
        }
        else
        {
            testEvidenceText = "(No test action logs captured)";
        }

        return new SeparatedEvidence
        {
            ApplicationEvidence = appEvidenceText,
            TestEvidence = testEvidenceText,
            HasApplicationSignals = appSignals.Any() || gaps.Any(),
            ApplicationSignalCount = appSignals.Count,
            PerformanceGapCount = gaps.Count
        };
    }
}

public class LogEntry
{
    public string Line { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public int LineNumber { get; set; }
    public LogLevel Level { get; set; }
    public DateTime? Timestamp { get; set; }
    public int Priority { get; set; }
}

public class LogGap
{
    public int BeforeIndex { get; set; }
    public int AfterIndex { get; set; }
    public DateTime BeforeTime { get; set; }
    public DateTime AfterTime { get; set; }
    public double GapDuration { get; set; }
    public string BeforeLine { get; set; } = "";
    public string AfterLine { get; set; } = "";
}

public enum LogLevel
{
    Unknown = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    CriticalUI = 4,
    Error = 5
}

public class SeparatedEvidence
{
    public string ApplicationEvidence { get; set; } = "";
    public string TestEvidence { get; set; } = "";
    public bool HasApplicationSignals { get; set; }
    public int ApplicationSignalCount { get; set; }
    public int PerformanceGapCount { get; set; }

    /// <summary>
    /// True if we found actual application log files (Admin-*.log, etc.), not just filtered excerpts from test logs
    /// </summary>
    public bool HasActualApplicationLogFiles { get; set; }
}

public class CategoryExtractor
{
    private static readonly Regex[] CategoryPatterns = new[]
    {
        new Regex(@"Category:\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase),
        new Regex(@"\[TestCategory\(""([^""]+)""\)\]", RegexOptions.IgnoreCase),
        new Regex(@"\[Category\(""([^""]+)""\)\]", RegexOptions.IgnoreCase),
        new Regex(@"Test Category:\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase),
        new Regex(@"@Category\s+([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase),
        new Regex(@"TestCategory=([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)
    };

    /// <summary>
    /// Extract test categories mentioned in log text
    /// </summary>
    public static List<string> ExtractCategoriesFromLogs(string logText)
    {
        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(logText))
            return categories.ToList();

        foreach (var pattern in CategoryPatterns)
        {
            var matches = pattern.Matches(logText);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var category = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(category))
                    {
                        categories.Add(category);
                    }
                }
            }
        }

        return categories.ToList();
    }
}
