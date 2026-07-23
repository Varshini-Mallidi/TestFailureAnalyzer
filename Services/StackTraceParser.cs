using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FailureAnalyzer.Services;

/// <summary>
/// Parses stack traces to extract file paths, line numbers, and method names.
/// Supports debugging-focused code retrieval by identifying exact failure locations.
/// </summary>
public static class StackTraceParser
{
    // Regex: parses stack trace lines with file info
    //   at Namespace.ClassName.MethodName(params) in C:\path\File.cs:line 123
    private static readonly Regex StackFrameWithFileRegex = new(
        @"at\s+([\w\.<>]+)\.(\w+)\s*\([^)]*\)\s+in\s+(.+\.cs):line\s+(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex: parses stack trace lines without file info
    //   at Namespace.ClassName.MethodName(params)
    private static readonly Regex StackFrameNoFileRegex = new(
        @"at\s+([\w\.<>]+)\.(\w+)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Represents a single frame in a stack trace.
    /// </summary>
    public class StackFrame
    {
        public string FullMethod { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string MethodName { get; set; } = "";
        public string? FilePath { get; set; }
        public string? FileName => FilePath != null ? System.IO.Path.GetFileName(FilePath) : null;
        public int? LineNumber { get; set; }
        public bool HasFileInfo => FilePath != null && LineNumber != null;
    }

    /// <summary>
    /// Parses a stack trace string and extracts all frames.
    /// Returns frames ordered from innermost (exception site) to outermost.
    /// </summary>
    public static List<StackFrame> ParseStackTrace(string stackTrace)
    {
        var frames = new List<StackFrame>();
        if (string.IsNullOrWhiteSpace(stackTrace)) return frames;

        var lines = stackTrace.Split('\n', '\r')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        foreach (var line in lines)
        {
            // Try to match with file info first
            var matchWithFile = StackFrameWithFileRegex.Match(line);
            if (matchWithFile.Success)
            {
                frames.Add(new StackFrame
                {
                    FullMethod = matchWithFile.Groups[1].Value,
                    MethodName = matchWithFile.Groups[2].Value,
                    FilePath = matchWithFile.Groups[3].Value,
                    LineNumber = int.Parse(matchWithFile.Groups[4].Value),
                    ClassName = ExtractClassName(matchWithFile.Groups[1].Value)
                });
                continue;
            }

            // Try to match without file info
            var matchNoFile = StackFrameNoFileRegex.Match(line);
            if (matchNoFile.Success)
            {
                frames.Add(new StackFrame
                {
                    FullMethod = matchNoFile.Groups[1].Value,
                    MethodName = matchNoFile.Groups[2].Value,
                    ClassName = ExtractClassName(matchNoFile.Groups[1].Value)
                });
            }
        }

        return frames;
    }

    /// <summary>
    /// Gets the first frame in the stack trace that has file and line information.
    /// This is typically the exact point where the exception occurred.
    /// </summary>
    public static StackFrame? GetFailingFrame(string stackTrace)
    {
        var frames = ParseStackTrace(stackTrace);
        return frames.FirstOrDefault(f => f.HasFileInfo);
    }

    /// <summary>
    /// Gets all frames that have file and line information, ordered from innermost to outermost.
    /// </summary>
    public static List<StackFrame> GetFramesWithFileInfo(string stackTrace)
    {
        var frames = ParseStackTrace(stackTrace);
        return frames.Where(f => f.HasFileInfo).ToList();
    }

    /// <summary>
    /// Extracts just the file names mentioned in the stack trace (without line numbers).
    /// Useful for filtering chunks by file name.
    /// </summary>
    public static HashSet<string> ExtractFileNames(string stackTrace)
    {
        var files = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var frames = ParseStackTrace(stackTrace);

        foreach (var frame in frames.Where(f => f.FilePath != null))
        {
            var fileName = System.IO.Path.GetFileName(frame.FilePath);
            if (!string.IsNullOrEmpty(fileName))
                files.Add(fileName);
        }

        return files;
    }

    /// <summary>
    /// Extracts the class name from a fully qualified method name.
    /// Example: "Aveva.Tests.Pages.LoginPage" (without method) -> "LoginPage"
    /// Handles constructors: "Aveva.Tests.Admin.AdminApplication" (before ..ctor) -> "AdminApplication"
    /// Note: The input should NOT include the method name (it's extracted separately by the regex).
    /// </summary>
    private static string ExtractClassName(string fullMethod)
    {
        // Handle constructor pattern: ClassName..ctor -> extract ClassName
        if (fullMethod.EndsWith("..ctor", StringComparison.Ordinal) || 
            fullMethod.EndsWith("..cctor", StringComparison.Ordinal))
        {
            var parts = fullMethod.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
            {
                var nameParts = parts[0].Split('.');
                return nameParts.Length > 0 ? nameParts[^1] : fullMethod;
            }
        }

        // Normal method: extract LAST part (the class name, since method was already extracted by regex)
        var normalParts = fullMethod.Split('.');
        return normalParts.Length >= 1 ? normalParts[^1] : fullMethod;
    }
}
