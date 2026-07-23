using FailureAnalyzer.Services;

namespace FailureAnalyzer.Configuration;

/// <summary>
/// Enterprise configuration for RAG infrastructure.
/// Loaded from appsettings.json and environment variables.
/// </summary>
public class RagConfiguration
{
    public VectorStoreConfig VectorStore { get; set; } = new();
    public RagConfig RAG { get; set; } = new();
    public OllamaConfig Ollama { get; set; } = new();
    public MonitoringConfig Monitoring { get; set; } = new();
}

/// <summary>
/// Vector store provider selection and connection settings.
/// </summary>
public class VectorStoreConfig
{
    /// <summary>
    /// Vector store provider: "Json" (development) or "Qdrant" (production).
    /// </summary>
    public string Provider { get; set; } = "Json";

    public QdrantConfig Qdrant { get; set; } = new();
    public JsonVectorConfig Json { get; set; } = new();
}

/// <summary>
/// Qdrant-specific configuration.
/// </summary>
public class QdrantConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6334;
    public string CollectionName { get; set; } = "test_failure_chunks";
    public int MaxConcurrentWrites { get; set; } = 10;
    public string? ApiKey { get; set; }

    /// <summary>
    /// For Qdrant Cloud deployments, set this to the full HTTPS URL.
    /// </summary>
    public string? CloudUrl { get; set; }
}

/// <summary>
/// JSON file vector store configuration.
/// </summary>
public class JsonVectorConfig
{
    public string StoragePath { get; set; } = ".rag/vectors.json";
}

/// <summary>
/// RAG retrieval and chunking configuration.
/// </summary>
public class RagConfig
{
    /// <summary>
    /// Chunking strategy: "MethodAware" (Roslyn-based) or "SlidingWindow" (fallback).
    /// </summary>
    public string ChunkingStrategy { get; set; } = "MethodAware";

    public int MaxChunkTokens { get; set; } = 1000;
    public int ChunkOverlapTokens { get; set; } = 50;
    public bool EnableIncrementalIndexing { get; set; } = true;
    public int EmbeddingBatchSize { get; set; } = 100;
    public int TopKResults { get; set; } = 8;
    public float MinSimilarityScore { get; set; } = 0.5f;
    public bool EnableHybridSearch { get; set; } = true;
    public float KeywordWeight { get; set; } = 0.3f;
    public float SemanticWeight { get; set; } = 0.7f;
    public float StackTraceFileBoost { get; set; } = 1.5f;
}

/// <summary>
/// Ollama local LLM configuration.
/// </summary>
public class OllamaConfig
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public string AnalysisModel { get; set; } = "llama3.1:latest";
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Monitoring and health check configuration.
/// </summary>
public class MonitoringConfig
{
    public bool EnableHealthChecks { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public int MetricRetentionMinutes { get; set; } = 60;
    public int HealthCheckIntervalMinutes { get; set; } = 5;
}

/// <summary>
/// Factory for creating vector store instances based on configuration.
/// </summary>
public static class VectorStoreFactory
{
    public static IVectorStore Create(VectorStoreConfig config)
    {
        return config.Provider.ToLowerInvariant() switch
        {
            "qdrant" => throw new NotImplementedException("Qdrant implementation requires API compatibility update. Use 'Json' provider for now."),
            "json" => CreateJsonStore(config.Json),
            _ => throw new ArgumentException($"Unknown vector store provider: {config.Provider}. Use 'Json' for now (Qdrant coming soon).")
        };
    }

    private static IVectorStore CreateJsonStore(JsonVectorConfig config)
    {
        Console.WriteLine($"[VectorStore] Using JSON file: {config.StoragePath}");
        return new Services.JsonVectorStore(config.StoragePath);
    }
}
