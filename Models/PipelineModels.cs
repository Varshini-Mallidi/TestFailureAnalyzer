using System;
using System.Collections.Generic;

namespace FailureAnalyzer.Models;

// ============================================================================
// PIPELINE JOB MODELS
// ============================================================================

/// <summary>
/// Represents an ingestion job that flows through the pipeline stages.
/// </summary>
public class IngestionJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public string RepositoryPath { get; set; } = string.Empty;
    public IngestionMode Mode { get; set; } = IngestionMode.Incremental;
    public JobPriority Priority { get; set; } = JobPriority.Normal;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public PipelineProgress Progress { get; set; } = new();
}

public enum IngestionMode
{
    /// <summary>Only index changed files (using file hash tracking)</summary>
    Incremental,
    /// <summary>Re-index all files from scratch</summary>
    Full
}

public enum JobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

// ============================================================================
// PIPELINE PROGRESS TRACKING
// ============================================================================

public class PipelineProgress
{
    public PipelineStage CurrentStage { get; set; } = PipelineStage.Discovery;
    public Dictionary<PipelineStage, StageResult> StageResults { get; set; } = new();
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int TotalChunks { get; set; }
    public int EmbeddedChunks { get; set; }
    public int StoredChunks { get; set; }
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, long> StageTimingsMs { get; set; } = new();
}

public enum PipelineStage
{
    Discovery = 1,
    Chunking = 2,
    Embedding = 3,
    Validation = 4,
    Storage = 5,
    Indexing = 6,
    Completed = 7
}

// ============================================================================
// STAGE RESULTS
// ============================================================================

public class StageResult
{
    public PipelineStage Stage { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public TimeSpan Duration => CompletedAt - StartedAt;
    public Dictionary<string, object> Metrics { get; set; } = new();
}

public class DiscoveryResult : StageResult
{
    public List<string> DiscoveredFiles { get; set; } = new();
    public List<string> SkippedFiles { get; set; } = new();
    public Dictionary<string, string> FileHashes { get; set; } = new();
}

public class ChunkingResult : StageResult
{
    public List<DocumentChunk> Chunks { get; set; } = new();
    public int MethodChunks { get; set; }
    public int TextChunks { get; set; }
    public int SkippedFiles { get; set; }
}

public class EmbeddingResult : StageResult
{
    public List<DocumentChunk> EmbeddedChunks { get; set; } = new();
    public int SuccessfulEmbeddings { get; set; }
    public int FailedEmbeddings { get; set; }
    public List<string> FailedChunkIds { get; set; } = new();
}

public class ValidationResult : StageResult
{
    public int ValidChunks { get; set; }
    public int InvalidChunks { get; set; }
    public List<ValidationIssue> Issues { get; set; } = new();
}

public class StorageResult : StageResult
{
    public int StoredChunks { get; set; }
    public int FailedChunks { get; set; }
    public long TotalSizeBytes { get; set; }
}

// ============================================================================
// VALIDATION MODELS
// ============================================================================

public class ValidationIssue
{
    public string ChunkId { get; set; } = string.Empty;
    public ValidationSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

// ============================================================================
// PIPELINE CONFIGURATION
// ============================================================================

public class PipelineConfiguration
{
    public int MaxConcurrentJobs { get; set; } = 1;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 1000;
    public bool EnableCheckpointing { get; set; } = true;
    public string CheckpointDirectory { get; set; } = ".pipeline/checkpoints";
    public string QueueDirectory { get; set; } = ".pipeline/queue";
    public int JobTimeoutMinutes { get; set; } = 60;
    public int EmbeddingBatchSize { get; set; } = 100;
    public int EmbeddingRateLimitPerMinute { get; set; } = 1000;
    public bool ValidateChunks { get; set; } = true;
    public int MinChunkSize { get; set; } = 50;
    public int MaxChunkSize { get; set; } = 4000;
}

// ============================================================================
// PIPELINE METRICS
// ============================================================================

public class PipelineMetrics
{
    public string JobId { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan TotalDuration => EndTime.HasValue ? EndTime.Value - StartTime : TimeSpan.Zero;
    public int TotalFiles { get; set; }
    public int TotalChunks { get; set; }
    public int TotalEmbeddings { get; set; }
    public long TotalBytesProcessed { get; set; }
    public double ChunksPerSecond => TotalDuration.TotalSeconds > 0 
        ? TotalChunks / TotalDuration.TotalSeconds 
        : 0;
    public double EmbeddingsPerSecond => TotalDuration.TotalSeconds > 0 
        ? TotalEmbeddings / TotalDuration.TotalSeconds 
        : 0;
    public Dictionary<PipelineStage, TimeSpan> StageTimings { get; set; } = new();
    public Dictionary<string, int> ErrorCounts { get; set; } = new();
}
