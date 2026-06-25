using System;
using System.Collections.Generic;

namespace FailureAnalyzer.Models;

public class DocumentChunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Content { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string SourceType { get; set; } = ""; // "code" | "report" | "docs"
    public string ClassName { get; set; } = "";   // extracted from C# if available
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
}

public class VectorStore
{
    public DateTime LastIndexed { get; set; }
    public List<DocumentChunk> Chunks { get; set; } = new();
}