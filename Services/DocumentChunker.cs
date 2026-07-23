using FailureAnalyzer.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FailureAnalyzer.Services;

/// <summary>
/// Chunks documents into semantically meaningful pieces for RAG indexing.
/// Uses Roslyn for C# to understand code structure (methods, classes) and creates
/// chunks that respect logical boundaries rather than arbitrary character limits.
/// </summary>
public class DocumentChunker
{
    private const int ChunkChars = 1500;       // Target chunk size in characters
    private const int OverlapChars = 300;      // Overlap to preserve context
    private const int MaxMethodChars = 3000;   // Max size for a single method before forced split

    // Files/folders to exclude from indexing
    private static readonly string[] Exclusions = 
    {
        "\\obj\\", "/obj/",
        "\\bin\\", "/bin/",
        "\\.git\\", "/.git/",
        "\\.vs\\", "/.vs/",
        "\\node_modules\\", "/node_modules/",
        "\\packages\\", "/packages/",
        "\\TestResults\\", "/TestResults/",
        ".Designer.cs",  // Auto-generated
        ".g.cs",         // Generated files
        ".g.i.cs",
        "AssemblyInfo.cs",
        "AssemblyAttributes.cs"
    };

    public List<DocumentChunk> ChunkDirectory(string rootPath)
    {
        var chunks = new List<DocumentChunk>();

        if (!Directory.Exists(rootPath))
        {
            Console.WriteLine($"  [RAG] Directory not found: {rootPath}");
            return chunks;
        }

        // 1. C# source files with Roslyn-based parsing
        var csFiles = Directory.GetFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Exclusions.Any(ex => f.Contains(ex, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Console.WriteLine($"  [RAG] Indexing {csFiles.Count} C# files with Roslyn...");
        int methodsIndexed = 0, classesIndexed = 0;
        int roslynSuccess = 0, fallbackUsed = 0;

        foreach (var file in csFiles)
        {
            var fileChunks = ChunkCSharpFileWithRoslyn(file);
            chunks.AddRange(fileChunks);

            // Count how many are methods vs other chunks
            var methodChunks = fileChunks.Where(c => !string.IsNullOrEmpty(c.MethodName)).ToList();
            methodsIndexed += methodChunks.Count;
            classesIndexed += fileChunks.Select(c => c.ClassName).Distinct().Count();

            // Track Roslyn success vs fallback
            if (methodChunks.Any())
                roslynSuccess++;
            else if (fileChunks.Any())
                fallbackUsed++;
        }

        Console.WriteLine($"  [RAG] Indexed {methodsIndexed} methods from {classesIndexed} classes");
        if (fallbackUsed > 0)
            Console.WriteLine($"  [RAG] Roslyn parsing: {roslynSuccess} successful, {fallbackUsed} used regex fallback");

        // 2. Past failure reports (Markdown)
        var mdFiles = Directory.GetFiles(rootPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !Exclusions.Any(ex => f.Contains(ex, StringComparison.OrdinalIgnoreCase)))
            .Where(f => f.Contains("failure", StringComparison.OrdinalIgnoreCase) || 
                       f.Contains("report", StringComparison.OrdinalIgnoreCase) || 
                       f.Contains("analysis", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"  [RAG] Indexing {mdFiles.Count} past failure report(s)...");
        foreach (var file in mdFiles)
            chunks.AddRange(ChunkTextFile(file, "report"));

        // 3. Documentation files
        var docFiles = Directory.GetFiles(rootPath, "*.txt", SearchOption.AllDirectories)
            .Where(f => !Exclusions.Any(ex => f.Contains(ex, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Console.WriteLine($"  [RAG] Indexing {docFiles.Count} doc file(s)...");
        foreach (var file in docFiles)
            chunks.AddRange(ChunkTextFile(file, "docs"));

        Console.WriteLine($"  [RAG] Total chunks created: {chunks.Count}");

        // Chunk quality metrics
        PrintChunkQualityReport(chunks);

        return chunks;
    }

    /// <summary>
    /// Prints detailed chunk quality metrics to help verify Roslyn-based chunking is working well.
    /// </summary>
    private static void PrintChunkQualityReport(List<DocumentChunk> chunks)
    {
        if (chunks.Count == 0) return;

        var codeChunks = chunks.Where(c => c.SourceType == "code").ToList();
        if (codeChunks.Count == 0) return;

        var withMethods = codeChunks.Where(c => !string.IsNullOrEmpty(c.MethodName)).ToList();
        var withLines = codeChunks.Where(c => c.StartLine > 0 && c.EndLine > 0).ToList();

        var tokenCounts = codeChunks.Where(c => c.TokenCount > 0).Select(c => c.TokenCount).ToList();
        var avgTokens = tokenCounts.Any() ? (int)tokenCounts.Average() : 0;
        var minTokens = tokenCounts.Any() ? tokenCounts.Min() : 0;
        var maxTokens = tokenCounts.Any() ? tokenCounts.Max() : 0;

        Console.WriteLine($"  [RAG] Chunk Quality Report:");
        Console.WriteLine($"    • Methods with proper boundaries: {withMethods.Count}/{codeChunks.Count} ({100.0 * withMethods.Count / codeChunks.Count:F0}%)");
        Console.WriteLine($"    • Chunks with line numbers: {withLines.Count}/{codeChunks.Count} ({100.0 * withLines.Count / codeChunks.Count:F0}%)");

        if (tokenCounts.Any())
        {
            Console.WriteLine($"    • Avg chunk size: {avgTokens} tokens (range: {minTokens}-{maxTokens})");

            // Show distribution
            var small = tokenCounts.Count(t => t < 100);
            var medium = tokenCounts.Count(t => t >= 100 && t < 300);
            var large = tokenCounts.Count(t => t >= 300 && t < 500);
            var xlarge = tokenCounts.Count(t => t >= 500);
            Console.WriteLine($"    • Size distribution: <100tok={small}, 100-300={medium}, 300-500={large}, 500+={xlarge}");
        }

        // Find largest chunk for inspection
        var largest = codeChunks.OrderByDescending(c => c.TokenCount).FirstOrDefault();
        if (largest != null && largest.TokenCount > 0)
        {
            var fileName = Path.GetFileName(largest.SourcePath);
            var method = !string.IsNullOrEmpty(largest.MethodName) ? $":{largest.MethodName}" : "";
            Console.WriteLine($"    • Largest chunk: {fileName}{method} ({largest.TokenCount} tokens, lines {largest.StartLine}-{largest.EndLine})");
        }
    }

    // ── Roslyn-based C# chunking ────────────────────────────────────────────

    /// <summary>
    /// Uses Roslyn to parse C# files and create chunks at method/class boundaries.
    /// This produces much higher quality chunks than regex-based splitting.
    /// </summary>
    public static List<DocumentChunk> ChunkCSharpFileWithRoslyn(string filePath)
    {
        var chunks = new List<DocumentChunk>();

        string content;
        try 
        { 
            content = File.ReadAllText(filePath, Encoding.UTF8); 
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [RAG] Warning: Could not read {Path.GetFileName(filePath)}: {ex.Message}");
            return chunks;
        }

        if (string.IsNullOrWhiteSpace(content)) 
            return chunks;

        try
        {
            // Parse with Roslyn
            var tree = CSharpSyntaxTree.ParseText(content);
            var root = tree.GetRoot();
            var lines = content.Split('\n');

            // Find all classes and their members
            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classes)
            {
                var className = classDecl.Identifier.Text;

                // Get methods in this class
                var methods = classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>();

                foreach (var method in methods)
                {
                    var methodName = method.Identifier.Text;
                    var span = method.Span;
                    var startLine = tree.GetLineSpan(span).StartLinePosition.Line + 1;
                    var endLine = tree.GetLineSpan(span).EndLinePosition.Line + 1;

                    var methodText = content.Substring(span.Start, span.Length);

                    // Keep method as a single chunk - don't split methods!
                    // A method is a semantic unit and should stay together.
                    chunks.Add(new DocumentChunk
                    {
                        Content = methodText.Trim(),
                        SourcePath = filePath,
                        SourceType = "code",
                        ClassName = className,
                        MethodName = methodName,
                        StartLine = startLine,
                        EndLine = endLine,
                        TokenCount = EstimateTokens(methodText)
                    });
                }

                // CRITICAL FIX: Index constructors separately with MethodName = ".ctor"
                var constructors = classDecl.DescendantNodes().OfType<ConstructorDeclarationSyntax>();

                foreach (var ctor in constructors)
                {
                    var span = ctor.Span;
                    var startLine = tree.GetLineSpan(span).StartLinePosition.Line + 1;
                    var endLine = tree.GetLineSpan(span).EndLinePosition.Line + 1;
                    var ctorText = content.Substring(span.Start, span.Length);

                    chunks.Add(new DocumentChunk
                    {
                        Content = ctorText.Trim(),
                        SourcePath = filePath,
                        SourceType = "code",
                        ClassName = className,
                        MethodName = ".ctor",  // FIXED: Mark as constructor!
                        StartLine = startLine,
                        EndLine = endLine,
                        TokenCount = EstimateTokens(ctorText)
                    });
                }

                // Also capture properties - INDEX INDIVIDUALLY when they have bodies/expressions
                var properties = classDecl.DescendantNodes().OfType<PropertyDeclarationSyntax>();

                foreach (var prop in properties)
                {
                    // Expression-bodied properties (=> syntax) and properties with getters are callable
                    // and can be crash sites - index them individually like methods
                    var propName = prop.Identifier.Text;
                    var span = prop.Span;
                    var startLine = tree.GetLineSpan(span).StartLinePosition.Line + 1;
                    var endLine = tree.GetLineSpan(span).EndLinePosition.Line + 1;
                    var propText = content.Substring(span.Start, span.Length);

                    chunks.Add(new DocumentChunk
                    {
                        Content = propText.Trim(),
                        SourcePath = filePath,
                        SourceType = "code",
                        ClassName = className,
                        MethodName = propName,  // Store property name so it's searchable!
                        StartLine = startLine,
                        EndLine = endLine,
                        TokenCount = EstimateTokens(propText)
                    });
                }

                // Capture fields (grouped together since they're usually simple declarations)
                var fields = classDecl.DescendantNodes().OfType<FieldDeclarationSyntax>();

                var members = new List<MemberDeclarationSyntax>();
                members.AddRange(fields);
                // REMOVED: properties are now indexed individually above

                if (members.Any())
                {
                    // Group smaller members together
                    var memberTexts = new List<string>();
                    var currentSize = 0;

                    foreach (var member in members)
                    {
                        var memberText = content.Substring(member.Span.Start, member.Span.Length);

                        if (currentSize + memberText.Length > ChunkChars && memberTexts.Any())
                        {
                            // Flush current group
                            chunks.Add(new DocumentChunk
                            {
                                Content = string.Join("\n\n", memberTexts).Trim(),
                                SourcePath = filePath,
                                SourceType = "code",
                                ClassName = className,
                                MethodName = "", // Mixed fields only (no methods, constructors, or properties)
                                TokenCount = EstimateTokens(string.Join("\n\n", memberTexts))
                            });

                            memberTexts.Clear();
                            currentSize = 0;
                        }

                        memberTexts.Add(memberText);
                        currentSize += memberText.Length;
                    }

                    // Flush remaining
                    if (memberTexts.Any())
                    {
                        chunks.Add(new DocumentChunk
                        {
                            Content = string.Join("\n\n", memberTexts).Trim(),
                            SourcePath = filePath,
                            SourceType = "code",
                            ClassName = className,
                            MethodName = "", // Mixed members
                            TokenCount = EstimateTokens(string.Join("\n\n", memberTexts))
                        });
                    }
                }
            }

            // If no classes found or parsing failed, fall back to regex-based chunking
            if (!chunks.Any())
            {
                return ChunkCSharpFileFallback(filePath, content);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [RAG] Roslyn parsing failed for {Path.GetFileName(filePath)}, using fallback: {ex.Message}");
            return ChunkCSharpFileFallback(filePath, content);
        }

        return chunks;
    }

    /// <summary>
    /// Fallback chunking strategy when Roslyn fails. Uses simple regex-based method detection.
    /// </summary>
    private static List<DocumentChunk> ChunkCSharpFileFallback(string filePath, string content)
    {
        var chunks = new List<DocumentChunk>();

        // Extract class name
        var classMatch = Regex.Match(content, @"(?:public|internal|private)\s+(?:sealed\s+|abstract\s+|static\s+)?class\s+(\w+)", RegexOptions.IgnoreCase);
        var className = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(filePath);

        // Split on method-like patterns
        var methodPattern = new Regex(
            @"^\s*(?:public|private|protected|internal|static|override|async|virtual)+\s+[\w<>\[\]?]+\s+\w+\s*\(",
            RegexOptions.Compiled | RegexOptions.Multiline);

        var matches = methodPattern.Matches(content).Cast<Match>().ToList();

        if (matches.Count == 0)
        {
            // No methods found, chunk the whole file
            foreach (var chunk in SlideIntoChunks(content, ChunkChars, OverlapChars))
            {
                chunks.Add(new DocumentChunk
                {
                    Content = chunk.Trim(),
                    SourcePath = filePath,
                    SourceType = "code",
                    ClassName = className,
                    TokenCount = EstimateTokens(chunk)
                });
            }
            return chunks;
        }

        // Split on method boundaries
        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = (i + 1 < matches.Count) ? matches[i + 1].Index : content.Length;

            var section = content.Substring(start, end - start);

            // Keep methods whole - don't split even large methods
            chunks.Add(new DocumentChunk
            {
                Content = section.Trim(),
                SourcePath = filePath,
                SourceType = "code",
                ClassName = className,
                TokenCount = EstimateTokens(section)
            });
        }

        return chunks;
    }

    // ── Generic text file chunker ───────────────────────────────────────────

    public static List<DocumentChunk> ChunkTextFile(string filePath, string sourceType)
    {
        var chunks = new List<DocumentChunk>();

        string content;
        try 
        { 
            content = File.ReadAllText(filePath, Encoding.UTF8); 
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [RAG] Warning: Could not read {Path.GetFileName(filePath)}: {ex.Message}");
            return chunks;
        }

        if (string.IsNullOrWhiteSpace(content)) 
            return chunks;

        foreach (var chunk in SlideIntoChunks(content, ChunkChars, OverlapChars))
        {
            if (string.IsNullOrWhiteSpace(chunk)) 
                continue;

            chunks.Add(new DocumentChunk
            {
                Content = chunk.Trim(),
                SourcePath = filePath,
                SourceType = sourceType,
                TokenCount = EstimateTokens(chunk)
            });
        }

        return chunks;
    }

    // ── Sliding window splitter ─────────────────────────────────────────────

    private static List<string> SlideIntoChunks(string text, int size, int overlap)
    {
        var chunks = new List<string>();

        if (text.Length <= size)
        {
            chunks.Add(text);
            return chunks;
        }

        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + size, text.Length);

            // Try to break at logical code boundaries for cleaner chunks
            if (end < text.Length)
            {
                // Look for statement boundaries within the last 300 chars
                int lookback = Math.Min(300, end - start);
                int searchStart = end - lookback;

                // Priority 1: Find closing brace (end of block)
                int lastBrace = text.LastIndexOf('}', end - 1, lookback);
                if (lastBrace > start && lastBrace < end - 50) // Don't break too close to end
                {
                    end = lastBrace + 1;
                }
                // Priority 2: Find semicolon (end of statement)
                else
                {
                    int lastSemi = text.LastIndexOf(';', end - 1, lookback);
                    if (lastSemi > start)
                    {
                        // Find the newline after the semicolon
                        int nlAfterSemi = text.IndexOf('\n', lastSemi);
                        if (nlAfterSemi > start && nlAfterSemi < end)
                            end = nlAfterSemi + 1;
                        else
                            end = lastSemi + 1;
                    }
                    // Priority 3: Fall back to newline
                    else
                    {
                        int nl = text.LastIndexOf('\n', end - 1, lookback);
                        if (nl > start) 
                            end = nl + 1;
                    }
                }
            }

            chunks.Add(text[start..end]);
            start = end - overlap;

            if (start >= text.Length - overlap) 
                break;
        }

        return chunks;
    }

    /// <summary>
    /// Estimates token count using rough heuristic: ~4 chars per token for code.
    /// This is approximate but good enough for chunk size monitoring.
    /// </summary>
    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) 
            return 0;

        // Rough estimation: 1 token ≈ 4 characters for English text
        // Code tends to be denser, so we use 3.5
        return (int)(text.Length / 3.5);
    }
}