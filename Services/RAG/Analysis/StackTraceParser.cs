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
    /// Checks if the failing frame is called from a constructor.
    /// This helps identify initialization failures vs runtime failures.
    /// </summary>
    public static bool IsCalledFromConstructor(string stackTrace)
    {
        var frames = ParseStackTrace(stackTrace);
        // Check if any frame in the stack is a constructor (.ctor)
        return frames.Any(f => f.MethodName.Equals("ctor", StringComparison.OrdinalIgnoreCase) || 
                               f.MethodName.Equals("cctor", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the constructor call chain from innermost to outermost.
    /// Returns frames that are constructors or called directly from constructors.
    /// </summary>
    public static List<StackFrame> GetConstructorChain(string stackTrace)
    {
        var frames = ParseStackTrace(stackTrace);
        var constructorChain = new List<StackFrame>();

        bool inConstructorContext = false;
        foreach (var frame in frames)
        {
            // Mark when we enter constructor context
            if (frame.MethodName.Equals("ctor", StringComparison.OrdinalIgnoreCase) || 
                frame.MethodName.Equals("cctor", StringComparison.OrdinalIgnoreCase))
            {
                inConstructorContext = true;
                if (frame.HasFileInfo)
                    constructorChain.Add(frame);
            }
            // Include methods called from constructors (until we exit constructor context)
            else if (inConstructorContext && frame.HasFileInfo)
            {
                constructorChain.Add(frame);
                // Stop after we've collected 2-3 frames past the constructor
                if (constructorChain.Count >= 5)
                    break;
            }
        }

        return constructorChain;
    }

    /// <summary>
    /// Extracts helper/wrapper method calls from the stack trace.
    /// Returns frames that are NOT in test assemblies or framework code.
    /// Useful for finding window-matching logic, validation helpers, etc.
    /// </summary>
    public static List<StackFrame> GetHelperMethodCalls(string stackTrace, int maxCount = 5)
    {
        var frames = ParseStackTrace(stackTrace);
        var helpers = new List<StackFrame>();

        // Skip framework and test method frames
        var frameworkPrefixes = new[] { "System.", "Microsoft.", "UTA.Desktop.Waits", "FlaUI." };
        var testMethodPatterns = new[] { "TestMethod", "Test_", "_Test" };

        foreach (var frame in frames.Where(f => f.HasFileInfo))
        {
            // Skip framework code
            if (frameworkPrefixes.Any(prefix => frame.FullMethod.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Skip test methods (they're already in context)
            if (testMethodPatterns.Any(pattern => frame.MethodName.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Include helper methods
            helpers.Add(frame);

            if (helpers.Count >= maxCount)
                break;
        }

        return helpers;
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
