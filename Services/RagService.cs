using Azure;
using Azure.AI.OpenAI;
using FailureAnalyzer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace FailureAnalyzer.Services;

public class RagService
{
    private readonly OpenAIClient? _azureClient;
    private readonly HttpClient? _ollamaClient;
    private readonly string _embeddingModel;
    private readonly string _vectorStorePath;
    private readonly bool _useOllama;
    private VectorStore _store = new();

    private const int TopK = 5;    // chunks to retrieve per query
    private const int MaxStoreChars = 8000; // max total context injected into prompt

    public RagService(string endpoint, string apiKey, string vectorStorePath,
        string embeddingModel = "text-embedding-3-small", bool useOllama = false)
    {
        _useOllama = useOllama;
        _embeddingModel = embeddingModel;
        _vectorStorePath = vectorStorePath;

        if (_useOllama)
        {
            _ollamaClient = new HttpClient { BaseAddress = new Uri(endpoint) };
        }
        else
        {
            _azureClient = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        }
    }

    // ── Index ───────────────────────────────────────────────────────────────

    public async Task IndexAsync(string knowledgePath, bool force = false)
    {
        if (File.Exists(_vectorStorePath) && !force)
        {
            _store = JsonConvert.DeserializeObject<VectorStore>(
                await File.ReadAllTextAsync(_vectorStorePath)) ?? new();

            Console.WriteLine($"  [RAG] Loaded existing index: {_store.Chunks.Count} chunks");
            return;
        }

        Console.WriteLine($"  [RAG] Building index from: {knowledgePath}");
        var chunker = new DocumentChunker();
        var chunks = chunker.ChunkDirectory(knowledgePath);

        if (chunks.Count == 0) return;

        Console.WriteLine($"  [RAG] Embedding {chunks.Count} chunks using {(_useOllama ? "Ollama" : "Azure")}...");

        int done = 0;
        foreach (var chunk in chunks)
        {
            try
            {
                chunk.Embedding = await GenerateEmbeddingAsync(chunk.Content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [RAG] Warning: Embedding failed for a chunk: {ex.Message}");
                chunk.Embedding = new float[768]; // fallback empty vector
            }
            done++;
            if (done % 50 == 0) Console.WriteLine($"  [RAG] Embedded {done}/{chunks.Count} chunks...");
        }

        _store = new VectorStore { LastIndexed = DateTime.UtcNow, Chunks = chunks };
        await SaveAsync();
        Console.WriteLine($"  [RAG] Index complete. Saved to {_vectorStorePath}");
    }

    public async Task LoadAsync()
    {
        if (File.Exists(_vectorStorePath))
        {
            _store = JsonConvert.DeserializeObject<VectorStore>(
                await File.ReadAllTextAsync(_vectorStorePath)) ?? new();
        }
    }

    // ── Retrieve ────────────────────────────────────────────────────────────

    public async Task<string> RetrieveContextAsync(Models.TestResult failure)
    {
        if (_store.Chunks.Count == 0) return "";

        var query = $"{failure.ShortName} {failure.ErrorMessage} {TrimStackTrace(failure.StackTrace)}";
        float[] queryVector;

        try
        {
            queryVector = await GenerateEmbeddingAsync(query);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [RAG] Query embedding failed: {ex.Message}");
            return "";
        }

        var ranked = _store.Chunks
            .Where(c => c.Embedding.Length > 0)
            .Select(c => new { Chunk = c, Score = CosineSimilarity(queryVector, c.Embedding) })
            .OrderByDescending(x => x.Score)
            .Take(TopK)
            .ToList();

        if (!ranked.Any()) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== RELEVANT CONTEXT FROM YOUR REPOSITORY ===");

        int totalChars = 0;
        foreach (var r in ranked)
        {
            if (totalChars >= MaxStoreChars) break;
            var c = r.Chunk;
            sb.AppendLine($"\n--- {c.SourceType}: {TryMakeRelative(c.SourcePath)} | relevance: {r.Score:F2} ---");
            sb.AppendLine(c.Content);
            totalChars += c.Content.Length;
        }

        sb.AppendLine("\n=== END OF CONTEXT ===");
        return sb.ToString();
    }

    // ── Engine Wrapper ──────────────────────────────────────────────────────

    private async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        if (_useOllama)
        {
            var payload = new { model = _embeddingModel, prompt = text };
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
            var response = await _ollamaClient!.PostAsync("/api/embeddings", content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var obj = JObject.Parse(json);
            return obj["embedding"]?.ToObject<float[]>() ?? Array.Empty<float>();
        }
        else
        {
            var embedResp = await _azureClient!.GetEmbeddingsAsync(
                new EmbeddingsOptions(_embeddingModel, new[] { text }));
            return embedResp.Value.Data[0].Embedding.ToArray();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task SaveAsync()
    {
        var dir = Path.GetDirectoryName(_vectorStorePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(_vectorStorePath,
            JsonConvert.SerializeObject(_store, Formatting.None));
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom < 1e-10 ? 0f : (float)(dot / denom);
    }

    private static string TrimStackTrace(string trace)
        => string.Join("\n", trace.Split('\n').Take(20));

    private static string TryMakeRelative(string path)
    {
        try { return Path.GetRelativePath(Directory.GetCurrentDirectory(), path); }
        catch { return Path.GetFileName(path); }
    }
}