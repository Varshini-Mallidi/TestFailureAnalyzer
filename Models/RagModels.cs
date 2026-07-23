using System;
using System.Collections.Generic;
using System.Linq;

namespace FailureAnalyzer.Models;

public class DocumentChunk
{
    public string Id { get; set; } = "";  // Set in constructor to avoid regeneration on deserialization
    public string Content { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceType { get; set; } = ""; // "code" | "report" | "docs"
    public string ClassName { get; set; } = "";   // extracted from C# if available
    public string MethodName { get; set; } = "";  // method name if chunk is a single method
    public int StartLine { get; set; }            // starting line number in source file
    public int EndLine { get; set; }              // ending line number in source file
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
    public int TokenCount { get; set; }           // approximate token count for this chunk
    public string FileHash { get; set; } = "";    // SHA256 hash of source file for change detection

    public DocumentChunk()
    {
        Id = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Returns true if this chunk has a valid embedding that can be used for similarity search.
    /// </summary>
    public bool HasValidEmbedding => Embedding != null && Embedding.Length > 0;
}

public class VectorStore
{
    /// <summary>
    /// Schema version for detecting incompatible format changes. Increment when making
    /// breaking changes to DocumentChunk structure or storage format.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Name and version of the embedding model used (e.g., "text-embedding-3-small", "nomic-embed-text").
    /// If the model changes, embeddings are incompatible and the index must be rebuilt.
    /// </summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>
    /// Dimensionality of the embedding vectors (e.g., 1536 for text-embedding-3-small).
    /// Used to validate that all embeddings are consistent.
    /// </summary>
    public int EmbeddingDimensions { get; set; }

    /// <summary>
    /// Strategy used for chunking (e.g., "sliding-window-1500", "roslyn-method-aware").
    /// If chunking strategy changes significantly, consider re-indexing.
    /// </summary>
    public string ChunkingStrategy { get; set; } = "sliding-window-1500";

    /// <summary>
    /// UTC timestamp when this index was last built or updated.
    /// </summary>
    public DateTime LastIndexed { get; set; }

    /// <summary>
    /// All indexed document chunks with their embeddings.
    /// </summary>
    public List<DocumentChunk> Chunks { get; set; } = new();

    /// <summary>
    /// Validates the integrity of this vector store and returns a list of issues found.
    /// Empty list means the index is healthy.
    /// </summary>
    public List<string> Validate()
    {
        var issues = new List<string>();

        if (Chunks == null || Chunks.Count == 0)
        {
            issues.Add("Index contains no chunks");
            return issues;
        }

        // Check for embedding consistency
        var chunksWithEmbeddings = Chunks.Where(c => c.HasValidEmbedding).ToList();
        if (chunksWithEmbeddings.Count == 0)
        {
            issues.Add("No chunks have valid embeddings - index is unusable");
            return issues;
        }

        if (chunksWithEmbeddings.Count < Chunks.Count)
        {
            var missing = Chunks.Count - chunksWithEmbeddings.Count;
            issues.Add($"{missing}/{Chunks.Count} chunks have no embedding and cannot be retrieved");
        }

        // Check embedding dimension consistency
        var dimensions = chunksWithEmbeddings.Select(c => c.Embedding.Length).Distinct().ToList();
        if (dimensions.Count > 1)
        {
            issues.Add($"Inconsistent embedding dimensions detected: {string.Join(", ", dimensions)}");
        }
        else if (EmbeddingDimensions > 0 && dimensions[0] != EmbeddingDimensions)
        {
            issues.Add($"Embedding dimension mismatch: expected {EmbeddingDimensions}, found {dimensions[0]}");
        }

        // Check for duplicate chunks (exact content match)
        var duplicates = Chunks.GroupBy(c => c.Content).Where(g => g.Count() > 1).ToList();
        if (duplicates.Any())
        {
            var dupeCount = duplicates.Sum(g => g.Count() - 1);
            issues.Add($"{dupeCount} duplicate chunks detected (exact content matches) - consider deduplication");
        }

        // Check index age
        var age = DateTime.UtcNow - LastIndexed;
        if (age.TotalDays > 7)
        {
            issues.Add($"Index is {age.TotalDays:F0} days old - consider re-indexing if source code has changed");
        }

        return issues;
    }

    /// <summary>
    /// Returns true if this index is compatible with the specified embedding model.
    /// </summary>
    public bool IsCompatibleWith(string embeddingModel)
    {
        return string.Equals(EmbeddingModel, embeddingModel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a human-readable summary of this index for diagnostics.
    /// </summary>
    public string GetSummary()
    {
        var age = DateTime.UtcNow - LastIndexed;
        var ageStr = age.TotalHours < 1 ? $"{age.TotalMinutes:F0}m ago"
                   : age.TotalDays < 1 ? $"{age.TotalHours:F1}h ago"
                   : $"{age.TotalDays:F0}d ago";

        var byType = Chunks.GroupBy(c => c.SourceType)
            .Select(g => $"{g.Key}={g.Count()}")
            .OrderBy(s => s);

        var embeddable = Chunks.Count(c => c.HasValidEmbedding);

        return $"{Chunks.Count} chunks ({string.Join(", ", byType)}) | " +
               $"{embeddable}/{Chunks.Count} have embeddings | " +
               $"model: {EmbeddingModel} ({EmbeddingDimensions}D) | " +
               $"indexed {ageStr}";
    }
}

/// <summary>
/// Type alias for DocumentChunk when used in vector store context.
/// Makes it clear this is a chunk with embeddings ready for similarity search.
/// </summary>
public class VectorChunk : DocumentChunk
{
}
