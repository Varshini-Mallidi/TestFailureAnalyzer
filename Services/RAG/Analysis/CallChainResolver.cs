using FailureAnalyzer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FailureAnalyzer.Services;

/// <summary>
/// Reads your actual source files and resolves the full call chain from a stack trace.
/// Instead of telling the AI "go check ClickLoginButton()", it hands the AI
/// the actual source of every method in the chain so it can reason about exactly
/// what is broken and produce a precise fix.
/// </summary>
public class CallChainResolver
{
    private readonly string _sourceRoot;

    // Cache of filePath → all lines, so we don't re-read the same file repeatedly
    private readonly Dictionary<string, string[]> _fileCache = new();

    // All .cs files in the repo, indexed by filename for fast lookup
    private readonly Dictionary<string, List<string>> _fileIndex = new();

    // Regex: parses a stack trace line like:
    //   at LoginPage.ClickLoginButton() in C:\src\Tests\Pages\LoginPage.cs:line 47
    private static readonly Regex StackFrameRegex = new(
        @"at\s+([\w\.]+)\.(\w+)\s*\([^)]*\)\s+in\s+(.+\.cs):line\s+(\d+)",
        RegexOptions.Compiled);

    // Regex: parses a stack trace line WITHOUT file info:
    //   at FlaUI.Core.AutomationElement.FindFirstChild(...)
    private static readonly Regex StackFrameNoFileRegex = new(
        @"at\s+([\w\.]+)\.(\w+)\s*\(", RegexOptions.Compiled);

    // Regex: finds a method declaration by name in source
    // Note: MethodDeclRegex removed — method finding is done inline with IsMethodDeclaration()

    // Regex: extracts method calls made within a method body
    private static readonly Regex MethodCallRegex = new(
        @"\b([A-Z][a-zA-Z0-9]+)\s*\(", RegexOptions.Compiled);

    // Regex: extracts AutomationId strings
    private static readonly Regex AutomationIdRegex = new(
        @"(?:ByAutomationId|AutomationId)\s*[\(=""]+\s*[""']?([A-Za-z0-9_\-]{3,})[""']?|!!([A-Za-z0-9_]{3,})",
        RegexOptions.Compiled);

    public CallChainResolver(string sourceRoot)
    {
        _sourceRoot = sourceRoot;
        BuildFileIndex();
    }

    // ── Main entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Given a test failure, resolves and returns the full annotated call chain
    /// with actual source code of each method — ready to inject into the AI prompt.
    /// </summary>
    public CallChainResult Resolve(TestResult failure)
    {
        var result = new CallChainResult
        {
            TestName = failure.TestName,
            ShortName = failure.ShortName,
            StackTrace = failure.StackTrace
        };

        // Step 1 — parse every frame from the stack trace
        var frames = ParseStackFrames(failure.StackTrace);
        result.Frames = frames;

        // Step 2 — for each frame that's in YOUR codebase (not FlaUI/MSTest internals),
        // read the actual method body
        foreach (var frame in frames.Where(f => f.IsUserCode))
        {
            var body = ReadMethodBody(frame);
            if (body != null)
            {
                frame.SourceCode = body.Code;
                frame.StartLine = body.StartLine;
                frame.EndLine = body.EndLine;
                frame.ResolvedFile = body.FilePath;

                // Extract AutomationIds used in this method
                frame.AutomationIdsUsed = ExtractAutomationIds(body.Code);

                // Extract method calls made — used for recursive resolution
                frame.MethodCallsMade = ExtractMethodCalls(body.Code);
            }
        }

        // Step 3 — find the exact failing line across all frames
        // NOTE: frames[] is in stack-trace order, i.e. innermost/closest-to-the-throw
        // frame FIRST, progressively outer callers after. FirstOrDefault() here is the
        // deepest user-code frame — the actual crash site. (This used to be LastOrDefault(),
        // which picked the outermost caller — e.g. the test method itself — instead of the
        // method that actually threw. It "worked" only for stack traces with a single
        // resolvable user-code frame, where First and Last are the same frame.)
        result.FailingFrame = frames
            .Where(f => f.IsUserCode && f.LineNumber > 0 && f.SourceCode != null)
            .FirstOrDefault();

        // Step 4 — look up AutomationId definitions if any IDs appear in error
        var idsInError = ExtractAutomationIds(failure.ErrorMessage + " " + failure.StackTrace);
        foreach (var id in idsInError)
        {
            var def = FindAutomationIdDefinition(id);
            if (def != null) result.AutomationIdDefinitions[id] = def;
        }

        // Step 5 — resolve fields/properties the crash-site method actually calls into.
        // A crash-site method is very often just "_someLocator.Value.Click()" — a thin
        // wrapper around a lazily-evaluated locator field/property defined ELSEWHERE in the
        // class (e.g. "private Lazy<AutomationElement> _someLocator => new(() =>
        // ...FindFirstDescendant(cf => cf.ByAutomationId(\"...\")))"). Without this, the AI
        // sees only the click and has no idea what element is actually being searched for or
        // why the search might time out — which is exactly what caused the crash-site method
        // above to look uninformative for a pure element-search-timeout failure.
        if (result.FailingFrame != null)
            result.ReferencedFieldDefinitions = ResolveReferencedFields(result.FailingFrame);

        // Step 6 — find the CALLER method (one hop above the topmost user-code frame).
        // The topmost frame is often the test method itself, and its CALLER is the test
        // framework setup/runner — not useful. But for Page Object failures, the topmost
        // frame might be a helper like OpenCreateDatabaseForm(), and seeing what TEST or
        // higher-level method called it (with what parameters/context) can reveal
        // missing preconditions or wrong sequencing that the stack trace alone doesn't show.
        var topmostUserFrame = frames.LastOrDefault(f => f.IsUserCode && f.SourceCode != null);
        if (topmostUserFrame != null)
            result.CallerMethod = FindCallerMethod(topmostUserFrame);

        return result;
    }

    /// <summary>
    /// Finds the declaration of any private field/property the given frame's source code
    /// references (typical "_camelCase" naming), by searching the same file for a line with
    /// an access modifier where that identifier is immediately followed by "=>" or "=" — i.e.
    /// an actual declaration/initializer, not just a use like "_field.Value.Click()".
    /// </summary>
    private Dictionary<string, string> ResolveReferencedFields(StackFrame frame)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(frame.SourceCode)) return result;

        var filePath = frame.ResolvedFile ?? frame.FilePath;
        var lines = TryLoadFile(filePath)
                    ?? (string.IsNullOrEmpty(frame.ClassName) ? null : FindFileByClassName(frame.ClassName));
        if (lines == null) return result;

        var usedFields = Regex.Matches(frame.SourceCode, @"\b(_[a-z][A-Za-z0-9]*)\b")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        foreach (var field in usedFields)
        {
            var declRegex = new Regex(
                $@"\b(?:private|protected|internal|public)\b[^;{{]*\b{Regex.Escape(field)}\b\s*(=>|=(?!=))",
                RegexOptions.IgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                if (!declRegex.IsMatch(lines[i])) continue;

                // Grab a few lines of context — locator declarations (especially
                // expression-bodied lazy properties with a lambda) commonly span several
                // lines, and a single line is rarely the full picture.
                int end = Math.Min(lines.Length - 1, i + 6);
                result[field] = string.Join("\n", lines.Skip(i).Take(end - i + 1));
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the method that CALLS the given frame's method (one hop above in the actual
    /// execution flow, not just the stack trace). This searches the same source file for
    /// any method that contains a call to frame.MethodName and returns its full body.
    /// Useful for understanding the context/parameters/preconditions that led to the call.
    /// </summary>
    private CallerMethodInfo? FindCallerMethod(StackFrame frame)
    {
        if (string.IsNullOrEmpty(frame.MethodName)) return null;

        var filePath = frame.ResolvedFile ?? frame.FilePath;
        var lines = TryLoadFile(filePath)
                    ?? (string.IsNullOrEmpty(frame.ClassName) ? null : FindFileByClassName(frame.ClassName));
        if (lines == null) return null;

        // Look for lines that call this method: "MethodName(" or "MethodName ("
        var callPattern = new Regex($@"\b{Regex.Escape(frame.MethodName)}\s*\(", RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            // Skip the method's own declaration line
            if (IsMethodDeclaration(lines[i], frame.MethodName))
                continue;

            if (callPattern.IsMatch(lines[i]))
            {
                // Found a call — now find which method contains this call
                int callerStart = FindContainingMethodStart(lines, i);
                if (callerStart < 0) continue;

                string callerName = ExtractMethodName(lines[callerStart]);
                if (string.IsNullOrEmpty(callerName)) continue;

                int callerEnd = FindMethodEnd(lines, callerStart);

                // Extract up to ~25 lines of the caller method
                int linesToTake = Math.Min(25, callerEnd - callerStart + 1);
                var callerCode = string.Join("\n", lines.Skip(callerStart).Take(linesToTake));

                // Annotate the line that makes the call
                var relativeCallLine = i - callerStart;
                var codeLines = callerCode.Split('\n').ToList();
                if (relativeCallLine >= 0 && relativeCallLine < codeLines.Count)
                {
                    codeLines[relativeCallLine] = codeLines[relativeCallLine] + 
                        $"  // ← CALLS {frame.MethodName}()";
                    callerCode = string.Join("\n", codeLines);
                }

                return new CallerMethodInfo
                {
                    MethodName = callerName,
                    SourceCode = callerCode,
                    StartLine = callerStart + 1,
                    EndLine = Math.Min(callerStart + linesToTake, callerEnd + 1),
                    FilePath = filePath,
                    CallLine = i + 1
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Given a line index, walks backwards to find the start of the method that contains it.
    /// </summary>
    private int FindContainingMethodStart(string[] lines, int lineIndex)
    {
        // Walk backwards looking for a method declaration
        for (int i = lineIndex; i >= 0; i--)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("public") || line.StartsWith("private") || 
                line.StartsWith("protected") || line.StartsWith("internal") ||
                line.StartsWith("static") || line.StartsWith("async") ||
                line.StartsWith("override") || line.StartsWith("virtual"))
            {
                // Check if it's a method declaration (contains method name followed by '(')
                if (line.Contains("(") && !line.TrimStart().StartsWith("//"))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// Extracts the method name from a method declaration line.
    /// </summary>
    private string ExtractMethodName(string declarationLine)
    {
        // Pattern: return_type MethodName( or MethodName(
        var match = Regex.Match(declarationLine, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(");
        if (match.Success)
        {
            var candidate = match.Groups[1].Value;
            // Filter out common keywords that might match the pattern
            var keywords = new[] { "if", "for", "while", "switch", "catch", "using", "lock", "fixed" };
            if (!keywords.Contains(candidate.ToLowerInvariant()))
                return candidate;
        }
        return "";
    }

    // ── Stack trace parser ──────────────────────────────────────────────────

    private List<StackFrame> ParseStackFrames(string stackTrace)
    {
        var frames = new List<StackFrame>();
        var lines = stackTrace.Split('\n');

        foreach (var line in lines)
        {
            // Try with file path first (most informative)
            var matchWithFile = StackFrameRegex.Match(line);
            if (matchWithFile.Success)
            {
                var filePath = matchWithFile.Groups[3].Value.Trim();
                frames.Add(new StackFrame
                {
                    ClassName = matchWithFile.Groups[1].Value,
                    MethodName = matchWithFile.Groups[2].Value,
                    FilePath = filePath,
                    LineNumber = int.Parse(matchWithFile.Groups[4].Value),
                    IsUserCode = IsUserCode(filePath, matchWithFile.Groups[1].Value)
                });
                continue;
            }

            // Try without file path — external library frames
            var matchNoFile = StackFrameNoFileRegex.Match(line);
            if (matchNoFile.Success)
            {
                var className = matchNoFile.Groups[1].Value;
                frames.Add(new StackFrame
                {
                    ClassName = className,
                    MethodName = matchNoFile.Groups[2].Value,
                    IsUserCode = false,  // no file path = external library
                    FilePath = ""
                });
            }
        }

        return frames;
    }

    // ── Method body reader ──────────────────────────────────────────────────

    private MethodBody? ReadMethodBody(StackFrame frame)
    {
        // Try the exact file path from the stack trace first
        var lines = TryLoadFile(frame.FilePath);

        // Fall back to searching the file index by filename
        if (lines == null)
        {
            var fileName = Path.GetFileName(frame.FilePath);
            if (!string.IsNullOrEmpty(fileName))
                lines = FindFileByName(fileName);
        }

        // Fall back to searching by class name
        if (lines == null)
            lines = FindFileByClassName(frame.ClassName);

        if (lines == null) return null;

        // Find the method declaration line
        int methodStart = FindMethodStart(lines, frame.MethodName, frame.LineNumber);
        if (methodStart < 0) return null;

        // Extract the full method body using brace matching
        int methodEnd = FindMethodEnd(lines, methodStart);

        var code = string.Join("\n",
            lines.Skip(methodStart).Take(methodEnd - methodStart + 1));

        // Annotate the specific failing line if we know it
        if (frame.LineNumber > 0 && frame.LineNumber <= lines.Length)
        {
            var relLine = frame.LineNumber - methodStart;
            var codeLines = code.Split('\n').ToList();
            if (relLine >= 0 && relLine < codeLines.Count)
                codeLines[relLine] = codeLines[relLine] + "  // ← FAILS HERE (line " + frame.LineNumber + ")";
            code = string.Join("\n", codeLines);
        }

        return new MethodBody
        {
            Code = code,
            StartLine = methodStart + 1,  // convert to 1-indexed
            EndLine = methodEnd + 1,
            FilePath = frame.FilePath.Length > 0 ? frame.FilePath
                      : FindActualFilePath(frame.ClassName) ?? frame.FilePath
        };
    }

    private int FindMethodStart(string[] lines, string methodName, int hintLine)
    {
        // If we have a line hint, search near it first (most accurate)
        if (hintLine > 0)
        {
            int searchStart = Math.Max(0, hintLine - 30);
            int searchEnd = Math.Min(lines.Length - 1, hintLine + 5);

            for (int i = searchStart; i >= 0; i--)
            {
                if (IsMethodDeclaration(lines[i], methodName))
                    return i;
            }
        }

        // Full file search
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsMethodDeclaration(lines[i], methodName))
                return i;
        }

        return -1;
    }

    private static bool IsMethodDeclaration(string line, string methodName)
    {
        // Must contain the method name followed by ( and be a declaration not a call
        if (!line.Contains(methodName + "(") && !line.Contains(methodName + " ("))
            return false;

        return line.TrimStart().StartsWith("public") ||
               line.TrimStart().StartsWith("private") ||
               line.TrimStart().StartsWith("protected") ||
               line.TrimStart().StartsWith("internal") ||
               line.TrimStart().StartsWith("static") ||
               line.TrimStart().StartsWith("async") ||
               line.TrimStart().StartsWith("override") ||
               line.TrimStart().StartsWith("virtual");
    }

    private static int FindMethodEnd(string[] lines, int methodStart)
    {
        int depth = 0;
        bool started = false;

        for (int i = methodStart; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{') { depth++; started = true; }
                if (c == '}') { depth--; }
            }
            if (started && depth == 0) return i;
        }

        // Fallback: 60 lines max if brace matching fails
        return Math.Min(methodStart + 60, lines.Length - 1);
    }

    // ── AutomationId definition finder ─────────────────────────────────────

    /// <summary>
    /// Searches the entire codebase for where an AutomationId constant is defined.
    /// e.g. finds: const string AdminElem = "ADMINELEMDB"
    ///         or: public static string AdminDb => "ADMINELEMDB"
    /// </summary>
    private AutomationIdDefinition? FindAutomationIdDefinition(string automationId)
    {
        // ExtractAutomationIds() strips a leading "!!" when it captures an ID (e.g. the error
        // text "AutomationId = !!ADMINELEMDB" yields the bare id "ADMINELEMDB"). But the
        // constant as actually WRITTEN in source may include that prefix as part of the
        // literal string itself (a "!!ElementName" naming convention is common in this
        // codebase). Searching only for the bare id misses that case entirely and silently
        // returns no definition — try the bare id and both "!"/"!!"-prefixed variants.
        var candidates = new[] { automationId, "!" + automationId, "!!" + automationId };

        var patterns = candidates
            .Select(id => new Regex($@"[""']{Regex.Escape(id)}[""']", RegexOptions.IgnoreCase))
            .ToArray();

        foreach (var kvp in _fileCache)
        {
            var lines = kvp.Value;
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var pattern in patterns)
                {
                    if (pattern.IsMatch(lines[i]))
                    {
                        // Get a few lines of context around the definition
                        int contextStart = Math.Max(0, i - 2);
                        int contextEnd = Math.Min(lines.Length - 1, i + 2);
                        var context = string.Join("\n",
                            lines.Skip(contextStart).Take(contextEnd - contextStart + 1));

                        return new AutomationIdDefinition
                        {
                            AutomationId = automationId,
                            FilePath = kvp.Key,
                            LineNumber = i + 1,
                            Context = context
                        };
                    }
                }
            }
        }

        return null;
    }

    // ── File index + loading ────────────────────────────────────────────────

    private void BuildFileIndex()
    {
        if (!Directory.Exists(_sourceRoot)) return;

        var csFiles = Directory.GetFiles(_sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("/obj/")
                     && !f.Contains("\\bin\\") && !f.Contains("/bin/"))
            .ToList();

        foreach (var file in csFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            if (!_fileIndex.ContainsKey(name)) _fileIndex[name] = new();
            _fileIndex[name].Add(file);

            // Pre-load into cache
            TryLoadFile(file);
        }

        Console.WriteLine($"  [CallChain] Indexed {csFiles.Count} source files");
    }

    private string[]? TryLoadFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_fileCache.TryGetValue(path, out var cached)) return cached;

        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            _fileCache[path] = lines;
            return lines;
        }
        catch { return null; }
    }

    private string[]? FindFileByName(string fileName)
    {
        var key = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (_fileIndex.TryGetValue(key, out var paths) && paths.Count > 0)
            return TryLoadFile(paths[0]);
        return null;
    }

    private string[]? FindFileByClassName(string className)
    {
        // Strip namespace — "LoginPage.ClickLoginButton" → "LoginPage"
        var simpleName = className.Split('.').LastOrDefault() ?? className;
        return FindFileByName(simpleName + ".cs");
    }

    private string? FindActualFilePath(string className)
    {
        var simpleName = className.Split('.').LastOrDefault() ?? className;
        var key = simpleName.ToLowerInvariant();
        return _fileIndex.TryGetValue(key, out var paths) ? paths.FirstOrDefault() : null;
    }

    // ── Helper extractors ───────────────────────────────────────────────────

    private static List<string> ExtractAutomationIds(string code)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AutomationIdRegex.Matches(code))
        {
            if (m.Groups[1].Value.Length > 2) ids.Add(m.Groups[1].Value);
            if (m.Groups[2].Value.Length > 2) ids.Add(m.Groups[2].Value);
        }
        return ids.ToList();
    }

    private static List<string> ExtractMethodCalls(string code)
        => MethodCallRegex.Matches(code)
            .Select(m => m.Groups[1].Value)
            .Where(n => n.Length > 3 && n is not ("True" or "False" or "Null"))
            .Distinct().Take(15).ToList();

    private static bool IsUserCode(string filePath, string className)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        // Exclude FlaUI, MSTest, System, Microsoft internals
        return !className.StartsWith("FlaUI.")
            && !className.StartsWith("Microsoft.")
            && !className.StartsWith("System.")
            && !className.StartsWith("MSTest.")
            && filePath.EndsWith(".cs");
    }
}

// ── Data models ─────────────────────────────────────────────────────────────

public class CallChainResult
{
    public string TestName { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public List<StackFrame> Frames { get; set; } = new();
    public StackFrame? FailingFrame { get; set; }
    public Dictionary<string, AutomationIdDefinition> AutomationIdDefinitions { get; set; } = new();
    // Declarations of fields/properties (typically locators) that the crash-site method
    // itself references, e.g. "_masterDatabaseRadioButton" -> its Lazy<AutomationElement>
    // definition. See ResolveReferencedFields().
    public Dictionary<string, string> ReferencedFieldDefinitions { get; set; } = new();
    // The method that calls the topmost user-code frame (one hop above the stack trace).
    // Provides context about HOW the failing method was invoked and with what preconditions.
    public CallerMethodInfo? CallerMethod { get; set; }

    /// <summary>Formats the full resolved chain for injection into the AI prompt.</summary>
    public string FormatForPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== FULL CALL CHAIN (resolved from your source code) ===");
        sb.AppendLine();

        var userFrames = Frames.Where(f => f.IsUserCode && f.SourceCode != null).ToList();

        if (!userFrames.Any())
        {
            sb.AppendLine("No user code frames could be resolved from the source directory.");
            return sb.ToString();
        }

        // ── CALLER CONTEXT (one hop above the stack trace) ──
        // Show this FIRST because it provides critical context about HOW the failing
        // method was invoked, what parameters were passed, and what preconditions
        // might have been missing. Without this, the AI sees the crash but not the
        // sequence of operations that led to it.
        if (CallerMethod != null)
        {
            sb.AppendLine("─── CALLER CONTEXT (one hop above crash — shows how the failing method was invoked) ───");
            sb.AppendLine($"{CallerMethod.MethodName}()");
            sb.AppendLine($"   File: {TryRelative(CallerMethod.FilePath)}");
            sb.AppendLine($"   Lines: {CallerMethod.StartLine}–{CallerMethod.EndLine}");
            sb.AppendLine($"   This method calls the failing code at line {CallerMethod.CallLine}");
            sb.AppendLine();
            sb.AppendLine("   SOURCE CODE:");
            sb.AppendLine("   " + string.Join("\n   ", CallerMethod.SourceCode.Split('\n')));
            sb.AppendLine();
            sb.AppendLine("   ANALYSIS NOTE: Look at what happens BEFORE the call marked above.");
            sb.AppendLine("   Missing initialization? Wrong order? Async timing issue?");
            sb.AppendLine();
        }

        // Lead with the actual crash site. Prompts get truncated downstream by character
        // budget, and this used to print the outer TEST wrapper first with the deepest
        // (most useful) frame last — meaning the exact method/line that threw was the
        // first thing to get cut off under truncation. Guarantee it survives by printing
        // it first, clearly labeled, then the rest of the call chain as supporting context.
        if (FailingFrame != null)
        {
            sb.AppendLine("─── CRASH SITE (this is where the exception was thrown — start here) ───");
            sb.AppendLine($"{FailingFrame.ClassName}.{FailingFrame.MethodName}()");
            sb.AppendLine($"   File: {TryRelative(FailingFrame.ResolvedFile ?? FailingFrame.FilePath)}");
            sb.AppendLine($"   Lines: {FailingFrame.StartLine}–{FailingFrame.EndLine}" +
                          (FailingFrame.LineNumber > 0 ? $" (fails at line {FailingFrame.LineNumber})" : ""));
            if (FailingFrame.AutomationIdsUsed.Any())
                sb.AppendLine($"   AutomationIds used: {string.Join(", ", FailingFrame.AutomationIdsUsed)}");
            sb.AppendLine();
            sb.AppendLine("   SOURCE CODE:");
            sb.AppendLine("   " + string.Join("\n   ", (FailingFrame.SourceCode ?? "").Split('\n')));
            sb.AppendLine();

            // The crash-site method itself is very often a thin wrapper (e.g. just
            // "_someLocator.Value.Click()") around a locator field/property defined
            // elsewhere in the class. Without this, the AI can see WHAT was clicked/searched
            // but not WHAT ELEMENT that actually resolves to or why finding it might time
            // out — it's the difference between seeing a symptom and seeing the cause.
            if (ReferencedFieldDefinitions.Any())
            {
                sb.AppendLine("   REFERENCED FIELD/PROPERTY DEFINITIONS (what the crash-site code above actually calls into):");
                foreach (var kvp in ReferencedFieldDefinitions)
                {
                    sb.AppendLine($"   --- {kvp.Key} ---");
                    sb.AppendLine("   " + string.Join("\n   ", kvp.Value.Split('\n')));
                }
                sb.AppendLine();
            }

            sb.AppendLine("─── FULL CALL CHAIN (for context — crash site above is the priority) ───");
            sb.AppendLine();
        }

        // Print each frame in call order (reverse stack — test method first)
        for (int i = userFrames.Count - 1; i >= 0; i--)
        {
            var frame = userFrames[i];
            var depth = userFrames.Count - 1 - i;
            var arrow = depth == 0 ? "▶ TEST" : $"  {"└─".PadLeft(depth * 2)} CALLS";

            sb.AppendLine($"{arrow} {frame.ClassName}.{frame.MethodName}()");
            sb.AppendLine($"   File: {TryRelative(frame.ResolvedFile ?? frame.FilePath)}");
            sb.AppendLine($"   Lines: {frame.StartLine}–{frame.EndLine}" +
                          (frame.LineNumber > 0 ? $" (fails at line {frame.LineNumber})" : ""));

            if (frame.AutomationIdsUsed.Any())
                sb.AppendLine($"   AutomationIds used: {string.Join(", ", frame.AutomationIdsUsed)}");

            sb.AppendLine();
            sb.AppendLine("   SOURCE CODE:");
            sb.AppendLine("   " + string.Join("\n   ", (frame.SourceCode ?? "").Split('\n')));
            sb.AppendLine();
        }

        // AutomationId definitions — highest value for locator failures
        if (AutomationIdDefinitions.Any())
        {
            sb.AppendLine("=== AUTOMATIONID DEFINITIONS FOUND IN CODEBASE ===");
            foreach (var kvp in AutomationIdDefinitions)
            {
                var def = kvp.Value;
                sb.AppendLine($"AutomationId \"{kvp.Key}\" is defined at:");
                sb.AppendLine($"   File: {TryRelative(def.FilePath)}  Line: {def.LineNumber}");
                sb.AppendLine($"   {def.Context}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("=== END OF CALL CHAIN ===");
        sb.AppendLine();
        sb.AppendLine("ANALYSIS INSTRUCTIONS:");
        sb.AppendLine("- The call chain above is the ACTUAL source code from the repo — not pseudocode");
        sb.AppendLine("- The line marked '// ← FAILS HERE' is where the exception is thrown");
        sb.AppendLine("- primary_cause must say: 'In [exact file] at line [N], [method]() does X which fails because Y'");
        sb.AppendLine("- code_snippet must show the FIXED version of the failing method using the real code above as base");
        sb.AppendLine("- Do NOT say 'check X' or 'consider Y' — give the exact change to make");

        return sb.ToString();
    }

    private static string TryRelative(string path)
    {
        try { return Path.GetRelativePath(Directory.GetCurrentDirectory(), path); }
        catch { return Path.GetFileName(path); }
    }
}

public class StackFrame
{
    public string ClassName { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? ResolvedFile { get; set; }
    public int LineNumber { get; set; }
    public bool IsUserCode { get; set; }
    public string? SourceCode { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public List<string> AutomationIdsUsed { get; set; } = new();
    public List<string> MethodCallsMade { get; set; } = new();
}

public class MethodBody
{
    public string Code { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string FilePath { get; set; } = "";
}

public class AutomationIdDefinition
{
    public string AutomationId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string Context { get; set; } = "";
}

public class CallerMethodInfo
{
    public string MethodName { get; set; } = "";
    public string SourceCode { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string FilePath { get; set; } = "";
    public int CallLine { get; set; }
}