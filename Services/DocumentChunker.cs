using FailureAnalyzer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FailureAnalyzer.Services;

public class DocumentChunker
{
    private const int ChunkSize = 400;  // tokens approx (~1600 chars)
    private const int ChunkOverlap = 80;   // overlap to preserve context across boundaries
    private const int ChunkChars = 1500;
    private const int OverlapChars = 300;

    // ── Public entry point ──────────────────────────────────────────────────

    public List<DocumentChunk> ChunkDirectory(string rootPath)
    {
        var chunks = new List<DocumentChunk>();

        if (!Directory.Exists(rootPath))
        {
            Console.WriteLine($"  [RAG] Directory not found: {rootPath}");
            return chunks;
        }

        // --- UPDATED: Strict exclusions to prevent indexing 600k+ chunks ---
        var exclusions = new[] { "\\obj\\", "/obj/", "\\bin\\", "/bin/", "\\.git\\", "/.git/", "\\node_modules\\", "/node_modules/", "\\packages\\", "/packages/", "\\TestResults\\", "/TestResults/" };

        // 1. C# source files — test codebase (filtered)
        var csFiles = Directory.GetFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !exclusions.Any(ex => f.Contains(ex)))
            .ToList();

        Console.WriteLine($"  [RAG] Indexing {csFiles.Count} C# files...");
        foreach (var file in csFiles)
            chunks.AddRange(ChunkCSharpFile(file));

        // 2. Past failure reports (Markdown - filtered)
        var mdFiles = Directory.GetFiles(rootPath, "*.md", SearchOption.AllDirectories)
            .Where(f => !exclusions.Any(ex => f.Contains(ex)) && (f.Contains("failure") || f.Contains("report") || f.Contains("analysis")))
            .ToList();

        Console.WriteLine($"  [RAG] Indexing {mdFiles.Count} past failure report(s)...");
        foreach (var file in mdFiles)
            chunks.AddRange(ChunkTextFile(file, "report"));

        // 3. Docs: txt files (filtered)
        var docFiles = Directory.GetFiles(rootPath, "*.txt", SearchOption.AllDirectories)
            .Where(f => !exclusions.Any(ex => f.Contains(ex)))
            .ToList();

        Console.WriteLine($"  [RAG] Indexing {docFiles.Count} doc file(s)...");
        foreach (var file in docFiles)
            chunks.AddRange(ChunkTextFile(file, "docs"));

        Console.WriteLine($"  [RAG] Total chunks created: {chunks.Count}");
        return chunks;
    }

    // ── C# file chunker — class-aware ───────────────────────────────────────

    private static List<DocumentChunk> ChunkCSharpFile(string filePath)
    {
        var chunks = new List<DocumentChunk>();
        string content;

        try { content = File.ReadAllText(filePath); }
        catch { return chunks; }

        if (string.IsNullOrWhiteSpace(content)) return chunks;

        // Extract the class name from the file
        var classMatch = Regex.Match(content, @"(?:public|internal)\s+class\s+(\w+)");
        var className = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(filePath);

        // Split on method boundaries to keep methods together where possible
        var sections = SplitOnMethods(content);

        foreach (var section in sections)
        {
            var subChunks = SlideIntoChunks(section, ChunkChars, OverlapChars);
            foreach (var chunk in subChunks)
            {
                if (string.IsNullOrWhiteSpace(chunk)) continue;
                chunks.Add(new DocumentChunk
                {
                    Content = chunk.Trim(),
                    SourcePath = filePath,
                    SourceType = "code",
                    ClassName = className
                });
            }
        }

        return chunks;
    }

    // Split C# content on method-level boundaries so chunks don't cut mid-method
    private static List<string> SplitOnMethods(string content)
    {
        // Match method declarations as split points
        var methodPattern = new Regex(
            @"(?:public|private|protected|internal|static|override|async)\s+[\w<>\[\]?]+\s+\w+\s*\(",
            RegexOptions.Compiled);

        var matches = methodPattern.Matches(content);
        if (matches.Count == 0) return new List<string> { content };

        var sections = new List<string>();
        int prev = 0;

        foreach (Match m in matches)
        {
            if (m.Index > prev + 50) // avoid tiny slivers
                sections.Add(content[prev..m.Index]);
            prev = m.Index;
        }
        sections.Add(content[prev..]);

        return sections;
    }

    // ── Generic text file chunker ───────────────────────────────────────────

    private static List<DocumentChunk> ChunkTextFile(string filePath, string sourceType)
    {
        var chunks = new List<DocumentChunk>();
        string content;

        try { content = File.ReadAllText(filePath); }
        catch { return chunks; }

        if (string.IsNullOrWhiteSpace(content)) return chunks;

        foreach (var chunk in SlideIntoChunks(content, ChunkChars, OverlapChars))
        {
            if (string.IsNullOrWhiteSpace(chunk)) continue;
            chunks.Add(new DocumentChunk
            {
                Content = chunk.Trim(),
                SourcePath = filePath,
                SourceType = sourceType,
                ClassName = ""
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

            // Try to break at a newline rather than mid-word
            if (end < text.Length)
            {
                int nl = text.LastIndexOf('\n', end, Math.Min(end - start, 200));
                if (nl > start) end = nl;
            }

            chunks.Add(text[start..end]);
            start = end - overlap;
            if (start >= text.Length - overlap) break;
        }

        return chunks;
    }
}