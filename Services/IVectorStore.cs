using FailureAnalyzer.Models;

namespace FailureAnalyzer.Services;

/// <summary>
/// Abstraction for vector database operations, allowing different implementations
/// (in-memory JSON, Qdrant, Pinecone, etc.) without changing dependent code.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Initialize connection and create collection/index if needed.
    /// </summary>
    Task InitializeAsync(string collectionName, int vectorDimension);

    /// <summary>
    /// Check if the vector store is healthy and responding.
    /// </summary>
    Task<bool> HealthCheckAsync();

    /// <summary>
    /// Upsert (insert or update) chunks in batch for better performance.
    /// </summary>
    Task UpsertChunksAsync(List<VectorChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for similar chunks using vector similarity.
    /// </summary>
    Task<List<VectorSearchResult>> SearchAsync(
        float[] queryEmbedding, 
        int topK, 
        Dictionary<string, object>? filters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get chunk by ID.
    /// </summary>
    Task<VectorChunk?> GetChunkByIdAsync(string chunkId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all chunks (for migration or full scan - use with caution).
    /// </summary>
    Task<List<VectorChunk>> GetAllChunksAsync(int limit = 10000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete chunks by IDs.
    /// </summary>
    Task DeleteChunksAsync(List<string> chunkIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get statistics about the vector store.
    /// </summary>
    Task<VectorStoreStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete entire collection (use with extreme caution).
    /// </summary>
    Task DeleteCollectionAsync(string collectionName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result from vector similarity search with score.
/// </summary>
public class VectorSearchResult
{
    public VectorChunk Chunk { get; set; } = new();
    public float Score { get; set; }
}

/// <summary>
/// Statistics about the vector store.
/// </summary>
public class VectorStoreStats
{
    public int TotalChunks { get; set; }
    public int VectorDimension { get; set; }
    public string? StorageBackend { get; set; }
    public long StorageSizeBytes { get; set; }
    public Dictionary<string, int> ChunksByType { get; set; } = new();
    public Dictionary<string, int> ChunksBySourceFile { get; set; } = new();
}
