using FailureAnalyzer.Models;
using Newtonsoft.Json;

namespace FailureAnalyzer.Services;

/// <summary>
/// Simple JSON file-based vector store for development/testing.
/// Not recommended for production use beyond 10k chunks.
/// </summary>
public class JsonVectorStore : IVectorStore
{
    private readonly string _storagePath;
    private VectorStore _store = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonVectorStore(string storagePath)
    {
        _storagePath = storagePath;
    }

    public async Task InitializeAsync(string collectionName, int vectorDimension)
    {
        if (File.Exists(_storagePath))
        {
            var json = await File.ReadAllTextAsync(_storagePath);
            _store = JsonConvert.DeserializeObject<VectorStore>(json) ?? new VectorStore();
            Console.WriteLine($"  [JSON] Loaded {_store.Chunks.Count} chunks from {_storagePath}");
        }
        else
        {
            _store = new VectorStore
            {
                EmbeddingModel = "unknown",
                EmbeddingDimensions = vectorDimension,
                Chunks = new List<DocumentChunk>()
            };
            Console.WriteLine($"  [JSON] Initialized new empty store");
        }
    }

    public Task<bool> HealthCheckAsync()
    {
        return Task.FromResult(true);  // Always healthy (local file)
    }

    public async Task UpsertChunksAsync(List<VectorChunk> chunks, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // Remove existing chunks with same IDs
            var existingIds = new HashSet<string>(chunks.Select(c => c.Id));
            _store.Chunks.RemoveAll(c => existingIds.Contains(c.Id));

            // Add new chunks (VectorChunk inherits from DocumentChunk so this is safe)
            _store.Chunks.AddRange(chunks.Cast<DocumentChunk>());

            // Persist to disk
            var json = JsonConvert.SerializeObject(_store, Formatting.Indented);
            await File.WriteAllTextAsync(_storagePath, json, cancellationToken);

            Console.WriteLine($"  [JSON] Upserted {chunks.Count} chunks ({_store.Chunks.Count} total)");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<List<VectorSearchResult>> SearchAsync(
        float[] queryEmbedding,
        int topK,
        Dictionary<string, object>? filters = null,
        CancellationToken cancellationToken = default)
    {
        var results = _store.Chunks.AsParallel()
            .Select(chunk => new VectorSearchResult
            {
                Chunk = ToVectorChunk(chunk),
                Score = CosineSimilarity(queryEmbedding, chunk.Embedding)
            })
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<VectorChunk?> GetChunkByIdAsync(string chunkId, CancellationToken cancellationToken = default)
    {
        var chunk = _store.Chunks.FirstOrDefault(c => c.Id == chunkId);
        return Task.FromResult(chunk != null ? ToVectorChunk(chunk) : null);
    }

    public Task<List<VectorChunk>> GetAllChunksAsync(int limit = 10000, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_store.Chunks.Take(limit).Select(ToVectorChunk).ToList());
    }

    public async Task DeleteChunksAsync(List<string> chunkIds, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var before = _store.Chunks.Count;
            _store.Chunks.RemoveAll(c => chunkIds.Contains(c.Id));
            var after = _store.Chunks.Count;

            // Persist
            var json = JsonConvert.SerializeObject(_store, Formatting.Indented);
            await File.WriteAllTextAsync(_storagePath, json, cancellationToken);

            Console.WriteLine($"  [JSON] Deleted {before - after} chunks");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<VectorStoreStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new VectorStoreStats
        {
            TotalChunks = _store.Chunks.Count,
            VectorDimension = _store.Chunks.FirstOrDefault()?.Embedding.Length ?? 0,
            StorageBackend = "JSON File",
            StorageSizeBytes = File.Exists(_storagePath) ? new FileInfo(_storagePath).Length : 0,
            ChunksByType = _store.Chunks
                .GroupBy(c => c.SourceType)
                .ToDictionary(g => g.Key, g => g.Count()),
            ChunksBySourceFile = _store.Chunks
                .GroupBy(c => Path.GetFileName(c.SourcePath))
                .OrderByDescending(g => g.Count())
                .Take(20)
                .ToDictionary(g => g.Key, g => g.Count())
        };

        return Task.FromResult(stats);
    }

    public Task DeleteCollectionAsync(string collectionName, CancellationToken cancellationToken = default)
    {
        if (File.Exists(_storagePath))
        {
            File.Delete(_storagePath);
            Console.WriteLine($"  [JSON] Deleted {_storagePath}");
        }
        _store = new VectorStore();
        return Task.CompletedTask;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var magnitude = (float)Math.Sqrt(magA) * (float)Math.Sqrt(magB);
        return magnitude == 0 ? 0 : dot / magnitude;
    }

    private static VectorChunk ToVectorChunk(DocumentChunk chunk)
    {
        return new VectorChunk
        {
            Id = chunk.Id,
            Content = chunk.Content,
            SourcePath = chunk.SourcePath,
            SourceType = chunk.SourceType,
            ClassName = chunk.ClassName,
            MethodName = chunk.MethodName,
            StartLine = chunk.StartLine,
            EndLine = chunk.EndLine,
            TokenCount = chunk.TokenCount,
            FileHash = chunk.FileHash,
            IndexedAt = chunk.IndexedAt,
            Embedding = chunk.Embedding
        };
    }
}
