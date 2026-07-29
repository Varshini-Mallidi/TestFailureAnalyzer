using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Concurrent;

namespace FailureAnalyzer.Services;

/// <summary>
/// Builds an exact symbol index from C# source files using Roslyn.
/// Maps ClassName.MethodName -> (FilePath, LineNumber) for fast, deterministic lookup.
/// This prevents embedding fallback from returning wrong files when the correct file is indexed.
/// </summary>
public class SymbolIndexer
{
    public class SymbolLocation
    {
        public string FilePath { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string MethodName { get; set; } = "";
        public int LineNumber { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
    }

    private readonly ConcurrentDictionary<string, List<SymbolLocation>> _symbolIndex = new();
    private readonly List<string> _sourceDirectories;

    public SymbolIndexer(IEnumerable<string> sourceDirectories)
    {
        _sourceDirectories = sourceDirectories.ToList();
    }

    /// <summary>
    /// Builds the symbol index by parsing all C# files in the source directories.
    /// </summary>
    public async Task BuildIndexAsync()
    {
        var startTime = DateTime.UtcNow;
        int fileCount = 0;
        int symbolCount = 0;

        Console.WriteLine($"  [SymbolIndexer] Building exact symbol index from {_sourceDirectories.Count} source directories...");

        foreach (var dir in _sourceDirectories)
        {
            if (!Directory.Exists(dir))
            {
                Console.WriteLine($"  [SymbolIndexer] ⚠️  Directory not found: {dir}");
                continue;
            }

            var csFiles = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))  // Skip build artifacts
                .ToList();

            foreach (var filePath in csFiles)
            {
                try
                {
                    var symbols = await ParseFileAsync(filePath);
                    symbolCount += symbols.Count;
                    fileCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [SymbolIndexer] ⚠️  Failed to parse {Path.GetFileName(filePath)}: {ex.Message}");
                }
            }
        }

        var duration = DateTime.UtcNow - startTime;
        Console.WriteLine($"  [SymbolIndexer] ✓ Indexed {symbolCount} symbols from {fileCount} files in {duration.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Parses a single C# file and extracts all class/method symbols.
    /// </summary>
    private async Task<List<SymbolLocation>> ParseFileAsync(string filePath)
    {
        var symbols = new List<SymbolLocation>();

        var code = await File.ReadAllTextAsync(filePath);
        var tree = CSharpSyntaxTree.ParseText(code, path: filePath);
        var root = await tree.GetRootAsync();

        // Find all class declarations
        var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classDeclarations)
        {
            var className = classDecl.Identifier.Text;

            // Find all method declarations in this class
            var methodDeclarations = classDecl.DescendantNodes().OfType<MethodDeclarationSyntax>();

            foreach (var methodDecl in methodDeclarations)
            {
                var methodName = methodDecl.Identifier.Text;
                var location = methodDecl.GetLocation();
                var lineSpan = location.GetLineSpan();

                var symbol = new SymbolLocation
                {
                    FilePath = filePath,
                    ClassName = className,
                    MethodName = methodName,
                    LineNumber = lineSpan.StartLinePosition.Line + 1,  // Convert to 1-based
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1
                };

                // Add to index with multiple keys for flexible lookup
                AddToIndex($"{className}.{methodName}", symbol);
                AddToIndex(methodName, symbol);  // Allow lookup by method name alone
                symbols.Add(symbol);
            }

            // Find all constructors
            var constructors = classDecl.DescendantNodes().OfType<ConstructorDeclarationSyntax>();

            foreach (var ctorDecl in constructors)
            {
                var location = ctorDecl.GetLocation();
                var lineSpan = location.GetLineSpan();

                var symbol = new SymbolLocation
                {
                    FilePath = filePath,
                    ClassName = className,
                    MethodName = ".ctor",
                    LineNumber = lineSpan.StartLinePosition.Line + 1,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1
                };

                // Add constructor with standard keys
                AddToIndex($"{className}..ctor", symbol);
                AddToIndex($"{className}.ctor", symbol);  // Also support single dot
                symbols.Add(symbol);
            }

            // Find all properties (for locator definitions)
            var properties = classDecl.DescendantNodes().OfType<PropertyDeclarationSyntax>();

            foreach (var propDecl in properties)
            {
                var propName = propDecl.Identifier.Text;
                var location = propDecl.GetLocation();
                var lineSpan = location.GetLineSpan();

                var symbol = new SymbolLocation
                {
                    FilePath = filePath,
                    ClassName = className,
                    MethodName = propName,
                    LineNumber = lineSpan.StartLinePosition.Line + 1,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1
                };

                AddToIndex($"{className}.{propName}", symbol);
                AddToIndex(propName, symbol);
                symbols.Add(symbol);
            }
        }

        return symbols;
    }

    /// <summary>
    /// Adds a symbol to the index under a specific key.
    /// </summary>
    private void AddToIndex(string key, SymbolLocation symbol)
    {
        _symbolIndex.AddOrUpdate(
            key,
            new List<SymbolLocation> { symbol },
            (_, existing) =>
            {
                existing.Add(symbol);
                return existing;
            });
    }

    /// <summary>
    /// Looks up a symbol by class.method or just method name.
    /// Returns all matching symbols (there may be overloads or multiple classes with same method).
    /// </summary>
    public List<SymbolLocation> Lookup(string className, string methodName)
    {
        var results = new List<SymbolLocation>();

        // Try exact class.method match first
        var fullKey = $"{className}.{methodName}";
        if (_symbolIndex.TryGetValue(fullKey, out var exactMatches))
        {
            results.AddRange(exactMatches);
        }

        // If no exact match and method is .ctor, try double-dot syntax
        if (!results.Any() && methodName == ".ctor")
        {
            var ctorKey = $"{className}..ctor";
            if (_symbolIndex.TryGetValue(ctorKey, out var ctorMatches))
            {
                results.AddRange(ctorMatches);
            }
        }

        return results;
    }

    /// <summary>
    /// Looks up a symbol by method name alone (returns all classes with that method).
    /// </summary>
    public List<SymbolLocation> LookupByMethod(string methodName)
    {
        if (_symbolIndex.TryGetValue(methodName, out var matches))
        {
            return matches;
        }
        return new List<SymbolLocation>();
    }

    /// <summary>
    /// Looks up a symbol by filename and method name.
    /// Useful when stack trace provides filename but class resolution is ambiguous.
    /// </summary>
    public List<SymbolLocation> LookupByFile(string fileName, string methodName)
    {
        var allMatches = LookupByMethod(methodName);
        return allMatches
            .Where(s => s.FilePath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Gets statistics about the symbol index.
    /// </summary>
    public (int UniqueSymbols, int TotalLocations) GetStats()
    {
        var uniqueSymbols = _symbolIndex.Count;
        var totalLocations = _symbolIndex.Values.Sum(list => list.Count);
        return (uniqueSymbols, totalLocations);
    }
}
