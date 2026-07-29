using FailureAnalyzer.Models;
using Microsoft.Extensions.Configuration;

namespace FailureAnalyzer.Services;

/// <summary>
/// Standalone indexing service for the ingestion pipeline.
/// Provides decoupled indexing that runs separately from failure analysis,
/// enabling "index once, analyze many times" workflow for CI/CD efficiency.
/// </summary>
public class StandaloneIndexer
{
    private readonly IConfiguration _config;

    public StandaloneIndexer(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Run the ingestion pipeline to index a source code repository.
    /// This performs chunking, embedding, and storage operations.
    /// </summary>
    /// <param name="sourceDirectories">Path(s) to the repository/repositories to index</param>
    /// <param name="forceReindex">If true, forces full re-index even if index exists</param>
    public async Task IndexRepositoryAsync(IEnumerable<string> sourceDirectories, bool forceReindex = false)
    {
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine("  Ingestion Pipeline — Standalone Indexing");
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine();

        var paths = sourceDirectories.Select(Path.GetFullPath).ToList();

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"Source directory not found: {path}");
            }
        }

        Console.WriteLine($"📂 Repositories: {string.Join(", ", paths)}");
        Console.WriteLine($"🔄 Mode: {(forceReindex ? "Full Re-Index" : "Incremental")}");
        Console.WriteLine();

        // Initialize RAG service (reuses existing infrastructure)
        Console.WriteLine("▶ Initializing RAG service...");

        // Use Ollama embeddings (same as the main analysis flow)
        var rag = new RagService(
            "http://localhost:11434",
            "",
            "vector_store.json",
            "nomic-embed-text",
            true,  // useOllamaEmbeddings
            paths  // sourceDirectories
        );

        try
        {
            // Run indexing (chunking, embedding, storage)
            var startTime = DateTime.UtcNow;

            await rag.IndexAsync(paths, forceReindex);

            var duration = DateTime.UtcNow - startTime;

            Console.WriteLine();
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine("  ✅ Indexing Complete");
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine($"  Duration: {duration.TotalSeconds:F1}s");
            Console.WriteLine();
            Console.WriteLine("  The vector index is now ready for analysis.");
            Console.WriteLine("  Run analysis with:");
            Console.WriteLine("    dotnet run -- --trx <file>.trx --ollama");
            Console.WriteLine("    dotnet run -- --trx <file>.trx --gemini");
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine("  ❌ Indexing Failed");
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine($"  Error: {ex.Message}");
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            throw;
        }
    }
}
