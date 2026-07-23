using FailureAnalyzer.Models;
using FailureAnalyzer.Services;
using Newtonsoft.Json;

namespace FailureAnalyzer.Utils;

/// <summary>
/// Audits the vector store to identify what files are indexed vs what's missing from the source repository.
/// </summary>
public class VectorIndexAuditor
{
    public class AuditReport
    {
        public int TotalChunks { get; set; }
        public int UniqueFiles { get; set; }
        public List<string> IndexedFiles { get; set; } = new();
        public Dictionary<string, int> FileChunkCounts { get; set; } = new();
        public List<string> MissingFromIndex { get; set; } = new();
        public DateTime IndexLastModified { get; set; }
        public string EmbeddingModel { get; set; } = "";
    }

    private readonly string _vectorStorePath;
    private readonly IEnumerable<string> _sourceDirectories;

    public VectorIndexAuditor(string vectorStorePath, IEnumerable<string> sourceDirectories)
    {
        _vectorStorePath = vectorStorePath;
        _sourceDirectories = sourceDirectories;
    }

    /// <summary>
    /// Loads the vector store and generates a comprehensive audit report.
    /// </summary>
    public async Task<AuditReport> GenerateAuditReportAsync()
    {
        var report = new AuditReport();

        // Load vector store
        if (!File.Exists(_vectorStorePath))
        {
            Console.WriteLine($"⚠️  Vector store not found at: {_vectorStorePath}");
            return report;
        }

        var json = await File.ReadAllTextAsync(_vectorStorePath);
        var store = JsonConvert.DeserializeObject<VectorStore>(json);

        if (store == null || store.Chunks == null)
        {
            Console.WriteLine($"⚠️  Failed to load vector store");
            return report;
        }

        report.TotalChunks = store.Chunks.Count;
        report.EmbeddingModel = store.EmbeddingModel;
        report.IndexLastModified = File.GetLastWriteTimeUtc(_vectorStorePath);

        // Group by source file
        var fileGroups = store.Chunks
            .Where(c => !string.IsNullOrEmpty(c.SourcePath))
            .GroupBy(c => c.SourcePath)
            .OrderBy(g => g.Key);

        foreach (var group in fileGroups)
        {
            report.IndexedFiles.Add(group.Key);
            report.FileChunkCounts[group.Key] = group.Count();
        }

        report.UniqueFiles = report.IndexedFiles.Count;

        // Find all C# files in source directories
        var allSourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in _sourceDirectories)
        {
            if (Directory.Exists(dir))
            {
                var csFiles = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
                foreach (var file in csFiles)
                {
                    allSourceFiles.Add(file);
                }
            }
        }

        // Find files referenced in stack traces (from common patterns)
        var commonStackTraceFiles = new[]
        {
            "DabaconProductApplication.cs",
            "AdminApplication.cs",
            "DBSyncService.cs",
            "BasePageObject.cs"
        };

        // Check which files are missing from the index
        foreach (var sourceFile in allSourceFiles)
        {
            var fileName = Path.GetFileName(sourceFile);
            bool isIndexed = report.IndexedFiles.Any(indexed => 
                indexed.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

            if (!isIndexed && commonStackTraceFiles.Contains(fileName))
            {
                report.MissingFromIndex.Add(sourceFile);
            }
        }

        return report;
    }

    /// <summary>
    /// Prints a detailed audit report to the console.
    /// </summary>
    public async Task PrintAuditReportAsync()
    {
        Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine("  VECTOR INDEX AUDIT REPORT");
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        var report = await GenerateAuditReportAsync();

        Console.WriteLine($"📊 Index Statistics:");
        Console.WriteLine($"   • Total chunks: {report.TotalChunks}");
        Console.WriteLine($"   • Unique files: {report.UniqueFiles}");
        Console.WriteLine($"   • Embedding model: {report.EmbeddingModel}");
        Console.WriteLine($"   • Last modified: {report.IndexLastModified:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"   • Storage path: {_vectorStorePath}\n");

        if (report.MissingFromIndex.Any())
        {
            Console.WriteLine($"❌ Critical Files Missing from Index ({report.MissingFromIndex.Count}):");
            foreach (var missing in report.MissingFromIndex)
            {
                Console.WriteLine($"   • {missing}");
            }
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine($"✅ All critical stack-trace files are indexed\n");
        }

        Console.WriteLine($"📁 Top 20 Indexed Files (by chunk count):");
        var top20 = report.FileChunkCounts
            .OrderByDescending(kvp => kvp.Value)
            .Take(20);

        foreach (var kvp in top20)
        {
            var fileName = Path.GetFileName(kvp.Key);
            Console.WriteLine($"   • {fileName,-50} ({kvp.Value} chunks)");
        }

        Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");
    }

    /// <summary>
    /// Checks if a specific file is in the index.
    /// </summary>
    public async Task<bool> IsFileIndexedAsync(string fileName)
    {
        var report = await GenerateAuditReportAsync();
        return report.IndexedFiles.Any(path => 
            path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
    }
}
