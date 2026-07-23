using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FailureAnalyzer.Models;

namespace FailureAnalyzer.Services;

/// <summary>
/// Extracts precise code snippets from source files for debugging purposes.
/// Given a file path and line number, returns a focused window of code around that line.
/// </summary>
public class DebugSnippetExtractor
{
    private readonly string _sourceRoot;
    private readonly Dictionary<string, string[]> _fileCache = new();

    public DebugSnippetExtractor(string sourceRoot)
    {
        _sourceRoot = sourceRoot;
    }

    /// <summary>
    /// Extracts a code snippet from a specific file and line number.
    /// Shows contextLines before and after the focus line.
    /// </summary>
    public DebugSnippet? ExtractSnippet(
        string filePath,
        int lineNumber,
        int contextLines = 5,
        string category = "Code Snippet",
        string reason = "")
    {
        var lines = GetFileLines(filePath);
        if (lines == null || lines.Length == 0) return null;

        // Validate that the requested line is within the file bounds
        if (lineNumber < 1 || lineNumber > lines.Length)
        {
            Console.WriteLine($"  [DebugSnippetExtractor] ⚠️  Requested line {lineNumber} is out of bounds for {Path.GetFileName(filePath)} (file has {lines.Length} lines)");
            return null;
        }

        // Clamp to valid range
        int startLine = Math.Max(1, lineNumber - contextLines);
        int endLine = Math.Min(lines.Length, lineNumber + contextLines);

        // Extract the snippet
        var snippetLines = lines[(startLine - 1)..endLine];  // Array is 0-based, line numbers are 1-based
        var content = string.Join("\n", snippetLines);

        // Try to find the method name
        var methodName = FindContainingMethod(lines, lineNumber);

        return new DebugSnippet
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            MethodName = methodName ?? "Unknown",
            StartLine = startLine,
            EndLine = endLine,
            FocusLine = lineNumber,
            Content = content,
            Category = category,
            Reason = reason
        };
    }

    /// <summary>
    /// Extracts a snippet showing a full method definition.
    /// Limits to maxLines to avoid huge test methods.
    /// If lineHint is provided, finds the method overload that contains that line.
    /// </summary>
    public DebugSnippet? ExtractMethod(
        string filePath,
        string methodName,
        int maxLines = 20,
        string category = "Method Definition",
        string reason = "",
        int? lineHint = null)
    {
        var lines = GetFileLines(filePath);
        if (lines == null || lines.Length == 0) return null;

        // Find the method declaration (with overload disambiguation if lineHint provided)
        int methodStartLine = lineHint.HasValue 
            ? FindMethodStartContainingLine(lines, methodName, lineHint.Value)
            : FindMethodStart(lines, methodName);

        if (methodStartLine == -1) return null;

        // Find the method end (simplified: look for closing brace at same indent level)
        int methodEndLine = FindMethodEnd(lines, methodStartLine, maxLines);

        var startLine = methodStartLine + 1;  // Convert to 1-based
        var endLine = Math.Min(methodEndLine + 1, lines.Length);
        var actualEndLine = Math.Min(endLine, startLine + maxLines - 1);

        var snippetLines = lines[(startLine - 1)..(actualEndLine)];
        var content = string.Join("\n", snippetLines);

        return new DebugSnippet
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            MethodName = methodName,
            StartLine = startLine,
            EndLine = actualEndLine,
            Content = content,
            Category = category,
            Reason = reason
        };
    }

    /// <summary>
    /// Searches for a property or field definition in a file.
    /// Example: "_contextPane =>", "private AutomationElement _contextPane"
    /// </summary>
    public DebugSnippet? FindPropertyDefinition(
        string filePath,
        string propertyName,
        string category = "Locator Definition",
        string reason = "")
    {
        var lines = GetFileLines(filePath);
        if (lines == null || lines.Length == 0) return null;

        // Search for the property/field definition
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Match patterns like:
            // - "private AutomationElement _contextPane"
            // - "_contextPane =>"
            // - "public AutomationElement _contextPane { get; set; }"
            if (line.Contains(propertyName) &&
                (line.Contains("=>") ||
                 line.Contains("private") ||
                 line.Contains("public") ||
                 line.Contains("protected") ||
                 line.Contains("internal")))
            {
                // Found it! Extract with context
                int lineNumber = i + 1;  // Convert to 1-based
                int contextLines = 3;
                int startLine = Math.Max(1, lineNumber - contextLines);
                int endLine = Math.Min(lines.Length, lineNumber + contextLines);

                // If it's a property with body, extend the end
                if (line.Contains("=>") || line.Contains("{"))
                {
                    // Look for closing brace or semicolon
                    for (int j = i + 1; j < Math.Min(i + 10, lines.Length); j++)
                    {
                        if (lines[j].Contains("}") || lines[j].Contains(";"))
                        {
                            endLine = j + 2;  // Include closing line + 1 more
                            break;
                        }
                    }
                }

                var snippetLines = lines[(startLine - 1)..endLine];
                var content = string.Join("\n", snippetLines);

                return new DebugSnippet
                {
                    FilePath = filePath,
                    FileName = Path.GetFileName(filePath),
                    MethodName = propertyName,
                    StartLine = startLine,
                    EndLine = endLine,
                    FocusLine = lineNumber,
                    Content = content,
                    Category = category,
                    Reason = reason
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the method that contains a given line number.
    /// Returns the method name or null if not found.
    /// </summary>
    private string? FindContainingMethod(string[] lines, int lineNumber)
    {
        // Search backwards from the line to find the method declaration
        for (int i = lineNumber - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();

            // Simple heuristic: method declaration usually has ( and )
            if (line.Contains("(") && line.Contains(")") &&
                (line.Contains("public") || line.Contains("private") ||
                 line.Contains("protected") || line.Contains("internal") ||
                 line.Contains("void") || line.Contains("async")))
            {
                // Extract method name (between whitespace and '(')
                var methodMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\w+)\s*\(");
                if (methodMatch.Success)
                    return methodMatch.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the starting line (0-based) of a method in a file.
    /// Returns -1 if not found.
    /// </summary>
    private int FindMethodStart(string[] lines, string methodName)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains(methodName) && line.Contains("(") &&
                (line.Contains("public") || line.Contains("private") ||
                 line.Contains("protected") || line.Contains("internal")))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the starting line (0-based) of a method that contains the given lineNumber.
    /// This disambiguates overloads by checking which method body contains the target line.
    /// </summary>
    private int FindMethodStartContainingLine(string[] lines, string methodName, int targetLine)
    {
        // Find all methods with this name
        var candidates = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains(methodName) && line.Contains("(") &&
                (line.Contains("public") || line.Contains("private") ||
                 line.Contains("protected") || line.Contains("internal")))
            {
                candidates.Add(i);
            }
        }

        // For each candidate, check if the target line falls within its body
        foreach (var candidateStart in candidates)
        {
            int methodEnd = FindMethodEnd(lines, candidateStart, maxLines: 100);
            // Convert to 1-based for comparison with targetLine
            if (targetLine >= (candidateStart + 1) && targetLine <= (methodEnd + 1))
            {
                return candidateStart;
            }
        }

        // Fallback: return first match if no range contains the line
        return candidates.FirstOrDefault(-1);
    }

    /// <summary>
    /// Finds the ending line (0-based) of a method, limited by maxLines.
    /// Simplified: looks for the closing brace.
    /// </summary>
    private int FindMethodEnd(string[] lines, int startLine, int maxLines)
    {
        int braceCount = 0;
        bool foundFirstBrace = false;

        for (int i = startLine; i < Math.Min(lines.Length, startLine + maxLines); i++)
        {
            var line = lines[i];

            foreach (var ch in line)
            {
                if (ch == '{')
                {
                    braceCount++;
                    foundFirstBrace = true;
                }
                else if (ch == '}')
                {
                    braceCount--;
                    if (foundFirstBrace && braceCount == 0)
                        return i;
                }
            }
        }

        // Didn't find the end, return max
        return Math.Min(lines.Length - 1, startLine + maxLines);
    }

    /// <summary>
    /// Gets file lines from cache or reads from disk.
    /// </summary>
    private string[]? GetFileLines(string filePath)
    {
        if (_fileCache.TryGetValue(filePath, out var cached))
            return cached;

        // Try to resolve the file path
        var resolvedPath = ResolveFilePath(filePath);
        if (resolvedPath == null || !File.Exists(resolvedPath))
            return null;

        try
        {
            var lines = File.ReadAllLines(resolvedPath);
            _fileCache[filePath] = lines;
            return lines;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a file path relative to the source root.
    /// Handles absolute paths and relative paths.
    /// </summary>
    private string? ResolveFilePath(string filePath)
    {
        // If absolute and exists, use it
        if (Path.IsPathRooted(filePath) && File.Exists(filePath))
            return filePath;

        // Try relative to source root
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName))
            return null;

        // Search for the file in source root
        try
        {
            var matches = Directory.GetFiles(_sourceRoot, fileName, SearchOption.AllDirectories);
            return matches.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
