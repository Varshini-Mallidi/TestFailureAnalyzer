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
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FailureAnalyzer.Services;

public class RagService
{
    private readonly OpenAIClient? _azureClient;
    private readonly HttpClient? _ollamaClient;
    private readonly string _embeddingModel;
    private readonly string _vectorStorePath;
    private readonly bool _useOllama;
    private readonly List<string> _sourceDirectories;
    private VectorStore _store = new();

    // Cache for file location lookups to avoid repeated directory scans
    private readonly Dictionary<string, string> _fileLocationCache = new();

    // Exact symbol index for deterministic method->file:line lookup (prevents wrong file returns)
    private SymbolIndexer? _symbolIndexer;

    // Adaptive retrieval parameters - adjusted based on query confidence
    private const int MinTopK = 15;  // INCREASED: was 8, now 15 for better context coverage
    private const int MaxTopK = 30;  // INCREASED: was 15, now 30 for complex failures
    private const int MaxStoreChars = 18000; // INCREASED: was 12000, now 18000 to accommodate more chunks with line numbers

    // FIX 1: Minimum score threshold to filter irrelevant chunks
    private const float MinScoreThreshold = 0.15f;

    public RagService(string endpoint, string apiKey, string vectorStorePath,
        string embeddingModel = "text-embedding-3-small", bool useOllama = false,
        IEnumerable<string>? sourceDirectories = null)
    {
        _useOllama = useOllama;
        _embeddingModel = embeddingModel;
        _vectorStorePath = vectorStorePath;
        _sourceDirectories = sourceDirectories?.ToList() ?? new List<string>();

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

    public async Task IndexAsync(IEnumerable<string> knowledgePaths, bool force = false)
    {
        if (File.Exists(_vectorStorePath) && !force)
        {
            _store = JsonConvert.DeserializeObject<VectorStore>(
                await File.ReadAllTextAsync(_vectorStorePath)) ?? new();

            Console.WriteLine($"  [RAG] Loaded existing index: {_store.Chunks.Count} chunks");

            // Validate compatibility with current embedding model
            if (!string.IsNullOrEmpty(_store.EmbeddingModel) && !_store.IsCompatibleWith(_embeddingModel))
            {
                Console.WriteLine($"  [RAG] WARNING: Index was built with '{_store.EmbeddingModel}' but you're now using '{_embeddingModel}'");
                Console.WriteLine($"  [RAG] Embeddings are incompatible - forcing rebuild");
                force = true;
            }
            else
            {
                PrintIndexSummary();

                // Validate index health
                var issues = _store.Validate();
                if (issues.Any())
                {
                    Console.WriteLine("  [RAG] Index validation warnings:");
                    foreach (var issue in issues)
                        Console.WriteLine($"    - {issue}");
                }

                // Build symbol index for exact lookups
                await EnsureSymbolIndexAsync();

                return;
            }
        }

        var pathsList = knowledgePaths.ToList();
        Console.WriteLine($"  [RAG] Building index from: {string.Join(", ", pathsList)}");

        var chunker = new DocumentChunker();
        var allChunks = new List<DocumentChunk>();

        foreach (var path in pathsList)
        {
            var chunks = chunker.ChunkDirectory(path);
            allChunks.AddRange(chunks);
        }

        if (allChunks.Count == 0)
        {
            Console.WriteLine("  [RAG] WARNING: 0 chunks produced from these directories — check --source-dir " +
                               "points at your actual repo root and contains .cs files outside bin/obj/node_modules.");
            return;
        }

        Console.WriteLine($"  [RAG] Embedding {allChunks.Count} chunks using {(_useOllama ? "Ollama" : "Azure")} in batches...");

        // Batch embedding for better performance
        int batchSize = _useOllama ? 10 : 100;  // Ollama: smaller batches, Azure: larger batches
        int embedFailures = 0;
        int totalEmbedded = 0;

        for (int i = 0; i < allChunks.Count; i += batchSize)
        {
            var batch = allChunks.Skip(i).Take(batchSize).ToList();

            try
            {
                var embeddings = await GenerateBatchEmbeddingsAsync(batch.Select(c => c.Content).ToList());

                for (int j = 0; j < batch.Count && j < embeddings.Count; j++)
                {
                    batch[j].Embedding = embeddings[j];
                }

                totalEmbedded += batch.Count;
                Console.WriteLine($"  [RAG] Embedded {totalEmbedded}/{allChunks.Count} chunks...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [RAG] Batch embedding failed ({ex.Message}), falling back to individual embedding...");

                // Fall back to individual embedding for this batch
                foreach (var chunk in batch)
                {
                    try
                    {
                        chunk.Embedding = await GenerateEmbeddingAsync(chunk.Content);
                        totalEmbedded++;
                    }
                    catch
                    {
                        embedFailures++;
                        chunk.Embedding = Array.Empty<float>();
                    }
                }

                Console.WriteLine($"  [RAG] Recovered: {totalEmbedded}/{allChunks.Count} chunks embedded");
            }

            // Rate limiting: small delay between batches to avoid throttling
            if (i + batchSize < allChunks.Count)
                await Task.Delay(_useOllama ? 100 : 50);
        }

        if (embedFailures > 0)
            Console.WriteLine($"  [RAG] WARNING: {embedFailures}/{allChunks.Count} chunks failed to embed and will " +
                               "never be retrievable — check your embedding endpoint/model is reachable.");

        // Detect embedding dimensions from first successful embedding
        var firstValidChunk = allChunks.FirstOrDefault(c => c.HasValidEmbedding);
        int embeddingDims = firstValidChunk?.Embedding.Length ?? 0;

        _store = new VectorStore 
        { 
            LastIndexed = DateTime.UtcNow, 
            Chunks = allChunks,
            EmbeddingModel = _embeddingModel,
            EmbeddingDimensions = embeddingDims,
            ChunkingStrategy = "roslyn-method-aware-1500"
        };

        await SaveAsync();
        Console.WriteLine($"  [RAG] Index complete. Saved to {_vectorStorePath}");
        PrintIndexSummary();

        // Final validation
        var validationIssues = _store.Validate();
        if (validationIssues.Any())
        {
            Console.WriteLine("  [RAG] Post-indexing validation:");
            foreach (var issue in validationIssues)
                Console.WriteLine($"    - {issue}");
        }
    }

    public async Task LoadAsync()
    {
        if (File.Exists(_vectorStorePath))
        {
            _store = JsonConvert.DeserializeObject<VectorStore>(
                await File.ReadAllTextAsync(_vectorStorePath)) ?? new();
            PrintIndexSummary();
        }
        else
        {
            Console.WriteLine($"  [RAG] No index found at {_vectorStorePath} — RAG will return no " +
                               "context for every failure until you run with --source-dir at least once.");
        }
    }

    /// <summary>
    /// Incremental indexing: only re-embeds new or changed files based on SHA256 hash tracking.
    /// This is the recommended method for repeated analysis runs on the same codebase.
    /// </summary>
    /// <param name="knowledgePath">Root directory containing source files</param>
    /// <returns>Task</returns>
    public async Task IndexIncrementalAsync(IEnumerable<string> knowledgePaths)
    {
        var pathsList = knowledgePaths.ToList();
        Console.WriteLine($"  [RAG] Starting incremental index of: {string.Join(", ", pathsList)}");

        // If no existing index, do full index
        if (!File.Exists(_vectorStorePath))
        {
            Console.WriteLine($"  [RAG] No existing index found, performing full index...");
            await IndexAsync(pathsList, force: true);
            return;
        }

        // Load existing index
        _store = JsonConvert.DeserializeObject<VectorStore>(
            await File.ReadAllTextAsync(_vectorStorePath)) ?? new();

        Console.WriteLine($"  [RAG] Loaded existing index: {_store.Chunks.Count} chunks");

        // Check model compatibility
        if (!string.IsNullOrEmpty(_store.EmbeddingModel) && !_store.IsCompatibleWith(_embeddingModel))
        {
            Console.WriteLine($"  [RAG] WARNING: Index was built with '{_store.EmbeddingModel}' but you're now using '{_embeddingModel}'");
            Console.WriteLine($"  [RAG] Embeddings are incompatible - forcing full rebuild");
            await IndexAsync(pathsList, force: true);
            return;
        }

        Console.WriteLine($"  [RAG] Scanning for file changes...");

        // Group existing chunks by source file for quick lookup
        var existingChunksByFile = _store.Chunks
            .Where(c => !string.IsNullOrEmpty(c.SourcePath))
            .GroupBy(c => c.SourcePath)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Get all source files from all paths
        var allFiles = new List<string>();
        foreach (var knowledgePath in pathsList)
        {
            var files = Directory.GetFiles(knowledgePath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
                .ToList();
            allFiles.AddRange(files);
        }

        int skippedFiles = 0;
        int changedFiles = 0;
        int newFiles = 0;
        int deletedFiles = 0;
        var newChunks = new List<Models.DocumentChunk>();
        var filesToRemove = new HashSet<string>();

        // Track which existing files we've seen (for detecting deletions)
        var seenFiles = new HashSet<string>();

        foreach (var filePath in allFiles)
        {
            seenFiles.Add(filePath);
            var fileHash = ComputeFileHash(filePath);

            if (existingChunksByFile.TryGetValue(filePath, out var oldChunks))
            {
                var oldHash = oldChunks.FirstOrDefault()?.FileHash;

                // Check if file unchanged
                if (!string.IsNullOrEmpty(oldHash) && oldHash == fileHash)
                {
                    skippedFiles++;
                    continue;
                }

                // File changed, mark old chunks for removal
                filesToRemove.Add(filePath);
                changedFiles++;
            }
            else
            {
                newFiles++;
            }

            // Chunk and embed new/changed file
            var fileChunks = DocumentChunker.ChunkCSharpFileWithRoslyn(filePath);

            Console.WriteLine($"  [RAG] Embedding {Path.GetFileName(filePath)} ({fileChunks.Count} chunks)...");

            foreach (var chunk in fileChunks)
            {
                chunk.FileHash = fileHash;
                try
                {
                    chunk.Embedding = await GenerateEmbeddingAsync(chunk.Content);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [RAG] WARNING: Failed to embed chunk from {Path.GetFileName(filePath)}: {ex.Message}");
                    chunk.Embedding = Array.Empty<float>();
                }
            }

            newChunks.AddRange(fileChunks);
        }

        // Detect deleted files (chunks exist but file doesn't)
        foreach (var existingFile in existingChunksByFile.Keys)
        {
            if (!seenFiles.Contains(existingFile) && File.Exists(existingFile) == false)
            {
                filesToRemove.Add(existingFile);
                deletedFiles++;
            }
        }

        // Remove old chunks for changed/deleted files
        int removedChunks = _store.Chunks.RemoveAll(c => filesToRemove.Contains(c.SourcePath));

        // Add new chunks
        _store.Chunks.AddRange(newChunks);
        _store.LastIndexed = DateTime.UtcNow;

        // Update embedding model metadata if this is the first time we're tracking it
        if (string.IsNullOrEmpty(_store.EmbeddingModel))
        {
            _store.EmbeddingModel = _embeddingModel;
            var firstChunk = newChunks.FirstOrDefault(c => c.HasValidEmbedding);
            if (firstChunk != null)
            {
                _store.EmbeddingDimensions = firstChunk.Embedding.Length;
            }
        }

        await SaveAsync();

        Console.WriteLine($"  [RAG] Incremental index complete:");
        Console.WriteLine($"    ✅ Unchanged: {skippedFiles} files");
        Console.WriteLine($"    🔄 Re-indexed: {changedFiles} files");
        Console.WriteLine($"    ➕ New: {newFiles} files");
        Console.WriteLine($"    ➖ Deleted: {deletedFiles} files");
        Console.WriteLine($"    📊 Removed {removedChunks} old chunks, added {newChunks.Count} new chunks");
        Console.WriteLine($"    📚 Total chunks in index: {_store.Chunks.Count}");

        PrintIndexSummary();

        // Validate
        var issues = _store.Validate();
        if (issues.Any())
        {
            Console.WriteLine("  [RAG] Index validation warnings:");
            foreach (var issue in issues)
                Console.WriteLine($"    - {issue}");
        }
    }

    /// <summary>
    /// Computes SHA256 hash of a file for change detection.
    /// </summary>
    private string ComputeFileHash(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Prints a quick health check of the loaded index: how old it is, how many chunks per
    /// source type, and how many chunks have no usable embedding. Run this every time and
    /// glance at it — a suspiciously small/stale index explains "RAG isn't finding anything"
    /// far more often than the retrieval logic itself being wrong.
    /// </summary>
    /// <summary>
    /// Prints a quick health check of the loaded index for diagnostics.
    /// </summary>
    private void PrintIndexSummary()
    {
        if (_store.Chunks.Count == 0)
        {
            Console.WriteLine("  [RAG] Index has 0 chunks — RAG is effectively disabled until re-indexed.");
            return;
        }

        Console.WriteLine($"  [RAG] {_store.GetSummary()}");
    }

    /// <summary>
    /// Builds the exact symbol index if not already built.
    /// This enables deterministic class.method -> file:line lookups.
    /// </summary>
    private async Task EnsureSymbolIndexAsync()
    {
        if (_symbolIndexer != null)
            return;  // Already built

        if (!_sourceDirectories.Any())
        {
            Console.WriteLine("  [RAG] ⚠️  No source directories configured - symbol indexing disabled");
            return;
        }

        _symbolIndexer = new SymbolIndexer(_sourceDirectories);
        await _symbolIndexer.BuildIndexAsync();

        var (uniqueSymbols, totalLocations) = _symbolIndexer.GetStats();
        Console.WriteLine($"  [RAG] ✓ Symbol index ready: {uniqueSymbols} unique symbols, {totalLocations} total locations");
    }

    // ── Retrieve ────────────────────────────────────────────────────────────

    public async Task<(string context, List<Models.RetrievedChunk> chunks)> RetrieveContextAsync(Models.TestResult failure, string? logSnippet = null)
    {
        if (_store.Chunks.Count == 0) return ("", new List<Models.RetrievedChunk>());

        // Ensure symbol index is built before retrieval
        await EnsureSymbolIndexAsync();

        Console.WriteLine($"  [RAG] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine($"  [RAG] RETRIEVING CODE FOR TEST: {failure.ShortName}");
        Console.WriteLine($"  [RAG] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        // ═══════════════════════════════════════════════════════════════════════
        // NEW: Stack-Trace-First Retrieval (Debugging-Relevant Code)
        // ═══════════════════════════════════════════════════════════════════════
        Console.WriteLine($"  [RAG] Attempting stack-trace-first retrieval...");
        Console.WriteLine($"  [RAG] Stack trace preview: {Truncate(failure.StackTrace, 150)}");

        var (stackTraceSuccess, debugSnippets) = await TryStackTraceFirstRetrievalAsync(failure);

        if (stackTraceSuccess && debugSnippets.Any())
        {
            Console.WriteLine($"  [RAG] ✅ Stack-trace-first retrieval successful: {debugSnippets.Count} debug snippets");
            Console.WriteLine($"  [RAG] Retrieved files: {string.Join(", ", debugSnippets.Select(s => Path.GetFileName(s.FilePath)).Distinct())}");
            return FormatDebugSnippets(debugSnippets, failure);
        }

        Console.WriteLine($"  [RAG] ⚠️  Stack-trace-first retrieval incomplete, falling back to semantic search...");
        Console.WriteLine($"  [RAG] Reason: {(debugSnippets.Any() ? "Partial success - some files not found on disk" : "Source files not found on disk or not in indexed directory")}");

        // ═══════════════════════════════════════════════════════════════════════
        // FALLBACK: Semantic + Keyword Hybrid Search (existing logic)
        // ═══════════════════════════════════════════════════════════════════════

        var query = BuildFocusedQuery(failure);
        Console.WriteLine($"  [RAG] Semantic search query: \"{Truncate(query, 120)}\"");

        float[] queryVector;

        try
        {
            queryVector = await GenerateEmbeddingAsync(query);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [RAG] Query embedding failed: {ex.Message}");
            return ("", new List<Models.RetrievedChunk>());
        }

        // Extract key terms for keyword matching
        var keyTerms = ExtractKeyTerms(failure);
        Console.WriteLine($"  [RAG] Key terms for keyword boost: {string.Join(", ", keyTerms.Take(5))}");

        // Extract files mentioned in stack trace for boosting
        var stackTraceFiles = ExtractStackTraceFiles(failure.StackTrace);
        if (stackTraceFiles.Any())
        {
            Console.WriteLine($"  [RAG] Stack trace files for boost: {string.Join(", ", stackTraceFiles)}");
        }

        // Detect failure characteristics for adaptive weighting
        bool hasAutomationId = failure.ErrorMessage.Contains("AutomationId", StringComparison.OrdinalIgnoreCase);
        bool isTimingIssue = failure.ErrorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                             failure.ErrorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase);

        // Adaptive hybrid weights based on failure type
        float semanticWeight = hasAutomationId ? 0.5f : 0.75f;  // Keyword matters more for locator issues
        float keywordWeight = 1.0f - semanticWeight;

        Console.WriteLine($"  [RAG] Retrieval strategy: semantic={semanticWeight:F2}, keyword={keywordWeight:F2}");

        // Phase 1: Hybrid retrieval with adaptive scoring
        var scoredChunks = _store.Chunks
            .Where(c => c.HasValidEmbedding)
            .Select(c => new
            {
                Chunk = c,
                SemanticScore = CosineSimilarity(queryVector, c.Embedding),
                KeywordScore = CalculateKeywordScore(c, keyTerms),
                InStackTrace = stackTraceFiles.Any(f => c.SourcePath.Contains(f, StringComparison.OrdinalIgnoreCase))
            })
            .Select(x => new
            {
                x.Chunk,
                x.SemanticScore,
                x.KeywordScore,
                x.InStackTrace,
                // Base hybrid score
                BaseScore = (semanticWeight * x.SemanticScore) + (keywordWeight * x.KeywordScore),
            })
            .Select(x => new
            {
                x.Chunk,
                x.SemanticScore,
                x.KeywordScore,
                x.InStackTrace,
                x.BaseScore,
                // Apply stack-trace boost (2x multiplier if file is in stack trace)
                Score = x.InStackTrace ? x.BaseScore * 2.0f : x.BaseScore
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Chunk.SourcePath)  // Stable tie-breaker: alphabetical file path
            .ThenBy(x => x.Chunk.StartLine)   // Then by line number
            .ToList();

        // Adaptive TopK based on best score confidence
        int topK = MinTopK;
        if (scoredChunks.Any())
        {
            float bestScore = scoredChunks[0].Score;
            if (bestScore < 0.25f)
                topK = MaxTopK;  // Low confidence - cast wider net
            else if (bestScore > 0.6f)
                topK = MinTopK;  // High confidence - focus on top results
            else
                topK = (MinTopK + MaxTopK) / 2;  // Medium confidence
        }

        // FIX 1: Apply minimum score threshold to filter out irrelevant chunks
        var aboveThreshold = scoredChunks
            .Where(x => x.Score >= MinScoreThreshold)
            .ToList();

        if (!aboveThreshold.Any())
        {
            Console.WriteLine($"  [RAG] ❌ No chunks above minimum threshold ({MinScoreThreshold:F2})");
            Console.WriteLine($"  [RAG] Best score was {(scoredChunks.Any() ? scoredChunks[0].Score : 0):F3}");
            return ("No relevant code found in repository", new List<Models.RetrievedChunk>());
        }

        var ranked = aboveThreshold.Take(topK).ToList();

        // Enhanced diagnostic output
        if (ranked.Any())
        {
            var top = ranked.Take(3)
                .Select(r => $"{r.Score:F2}{(r.InStackTrace ? "⭐" : "")} {Path.GetFileName(r.Chunk.SourcePath)}");
            Console.WriteLine($"  [RAG] Query: \"{Truncate(query, 100)}\"");
            Console.WriteLine($"  [RAG] Top {topK} matches: {string.Join(" | ", top)}");
            Console.WriteLine($"  [RAG] Stack trace boost: {ranked.Count(r => r.InStackTrace)} chunks");
            Console.WriteLine($"  [RAG] Filtered: {ranked.Count}/{scoredChunks.Count} chunks (threshold: {MinScoreThreshold:F2})");

            if (ranked[0].Score < 0.2f)
                Console.WriteLine("  [RAG] ⚠️  WARNING: Best match score is low — retrieved context may not be relevant.");
            else if (ranked[0].Score > 0.6f)
                Console.WriteLine("  [RAG] ✅ High confidence retrieval");
        }
        else
        {
            Console.WriteLine($"  [RAG] Query: \"{Truncate(query, 100)}\" → no chunks with usable embeddings.");
        }

        if (!ranked.Any()) return ("", new List<Models.RetrievedChunk>());

        // Phase 2: Diversification - avoid too many chunks from same file
        var diversified = new List<(float Score, Models.DocumentChunk Chunk, float SemanticScore, float KeywordScore, bool InStackTrace)>();
        var fileCount = new Dictionary<string, int>();

        foreach (var r in ranked)
        {
            var fileName = Path.GetFileName(r.Chunk.SourcePath);
            fileCount.TryGetValue(fileName, out int count);

            // Allow max 3 chunks from same file (unless it's in stack trace)
            if (r.InStackTrace || count < 3)
            {
                diversified.Add((r.Score, r.Chunk, r.SemanticScore, r.KeywordScore, r.InStackTrace));
                fileCount[fileName] = count + 1;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // FIX 2, 3, 4: Enhanced retrieval for Page Object Model failures
        // ═══════════════════════════════════════════════════════════════════════
        Console.WriteLine($"  [RAG] Applying Page Object Model enhancements...");

        var enhancedChunks = await EnhancePageObjectContextAsync(diversified, failure, stackTraceFiles);
        diversified = enhancedChunks;

        Console.WriteLine($"  [RAG] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine($"  [RAG] FINAL RESULTS FOR TEST: {failure.ShortName}");
        Console.WriteLine($"  [RAG] Total chunks: {diversified.Count}");
        Console.WriteLine($"  [RAG] Files returned: {string.Join(", ", diversified.Select(c => Path.GetFileName(c.Chunk.SourcePath)).Distinct().Take(5))}");
        Console.WriteLine($"  [RAG] Top methods: {string.Join(", ", diversified.Select(c => c.Chunk.MethodName).Where(m => !string.IsNullOrEmpty(m)).Distinct().Take(3))}");
        Console.WriteLine($"  [RAG] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        // Build execution flow narrative
        var executionFlow = BuildExecutionFlowNarrative(diversified, failure, logSnippet);

        // Build context string
        var sb = new System.Text.StringBuilder();

        // START WITH EXECUTION FLOW
        if (!string.IsNullOrEmpty(executionFlow))
        {
            sb.AppendLine("=== EXECUTION FLOW TO FAILURE ===");
            sb.AppendLine(executionFlow);
            sb.AppendLine();
        }

        sb.AppendLine("=== RELEVANT CONTEXT FROM YOUR REPOSITORY ===");
        sb.AppendLine($"Retrieved {diversified.Count} most relevant code chunks using hybrid search (semantic + keyword).");
        sb.AppendLine("Files from stack trace are marked with priority.\n");

        var retrievedChunks = new List<Models.RetrievedChunk>();
        int totalChars = 0;

        foreach (var (score, chunk, semanticScore, keywordScore, inStackTrace) in diversified)
        {
            if (totalChars >= MaxStoreChars) break;

            var priority = inStackTrace ? "[STACK TRACE FILE]" : "";
            sb.AppendLine($"\n--- {chunk.SourceType}: {TryMakeRelative(chunk.SourcePath)} {priority}");
            sb.AppendLine($"    Relevance: {score:F3} (semantic: {semanticScore:F2}, keyword: {keywordScore:F2})");

            if (!string.IsNullOrEmpty(chunk.ClassName) && !string.IsNullOrEmpty(chunk.MethodName))
                sb.AppendLine($"    Location: {chunk.ClassName}.{chunk.MethodName}() lines {chunk.StartLine}-{chunk.EndLine}");

            sb.AppendLine("---");

            // IMPROVEMENT: Add line numbers to code for precise citation
            var contentWithLineNumbers = AddLineNumbersToCode(chunk.Content, chunk.StartLine);
            sb.AppendLine(contentWithLineNumbers);
            totalChars += chunk.Content.Length;

            // Store chunk info for HTML report
            retrievedChunks.Add(new Models.RetrievedChunk
            {
                SourcePath = chunk.SourcePath,
                MethodName = chunk.MethodName,
                ClassName = chunk.ClassName,
                StartLine = chunk.StartLine,
                EndLine = chunk.EndLine,
                RelevanceScore = score,
                SemanticScore = semanticScore,
                KeywordScore = keywordScore,
                Content = chunk.Content,
                IsExactMatch = false,  // This is from semantic fallback search
                RetrievalMethod = semanticScore > keywordScore ? "semantic" : "keyword"
            });
        }

        sb.AppendLine("\n=== END OF CONTEXT ===");
        return (sb.ToString(), retrievedChunks);
    }

    /// <summary>
    /// NEW: Stack-trace-first retrieval - extracts debugging-relevant code snippets.
    /// Priority: Failing statement → Locator definitions → Calling test method.
    /// Returns (success, snippets) where success indicates if we found useful debug context.
    /// </summary>
    private async Task<(bool success, List<Models.DebugSnippet> snippets)> TryStackTraceFirstRetrievalAsync(Models.TestResult failure)
    {
        var snippets = new List<Models.DebugSnippet>();

        // Parse stack trace
        var frames = StackTraceParser.ParseStackTrace(failure.StackTrace);
        var failingFrame = StackTraceParser.GetFailingFrame(failure.StackTrace);

        if (failingFrame == null || failingFrame.FilePath == null)
        {
            Console.WriteLine($"  [RAG] No file:line info in stack trace - cannot use stack-trace-first retrieval");
            return (false, snippets);
        }

        Console.WriteLine($"  [RAG] Failing frame: {failingFrame.FileName}:line {failingFrame.LineNumber} in {failingFrame.MethodName}()");

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 1: Try exact symbol lookup FIRST (prevents wrong file returns)
        // ═══════════════════════════════════════════════════════════════════════
        DebugSnippetExtractor? extractor = null;
        string? foundSourceRoot = null;
        var cacheKey = failingFrame.FileName ?? Path.GetFileName(failingFrame.FilePath ?? "");

        if (_symbolIndexer != null && !string.IsNullOrEmpty(failingFrame.ClassName))
        {
            Console.WriteLine($"  [RAG]   Trying exact symbol lookup: {failingFrame.ClassName}.{failingFrame.MethodName}");
            var symbolMatches = _symbolIndexer.Lookup(failingFrame.ClassName, failingFrame.MethodName);

            // Filter to matches in the correct file if we have filename
            if (failingFrame.FileName != null)
            {
                symbolMatches = symbolMatches
                    .Where(s => s.FilePath.EndsWith(failingFrame.FileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (symbolMatches.Any())
            {
                var match = symbolMatches.First();
                Console.WriteLine($"  [RAG]   ✓ EXACT MATCH found via symbol index: {match.FilePath}:{match.LineNumber}");

                // Use the symbol index result to extract the snippet
                var symbolSourceDir = Path.GetDirectoryName(Path.GetDirectoryName(match.FilePath));
                if (symbolSourceDir != null)
                {
                    // Find the appropriate source root
                    foreach (var sourceDir in _sourceDirectories)
                    {
                        if (match.FilePath.StartsWith(sourceDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var symbolExtractor = new DebugSnippetExtractor(sourceDir);

                            // Extract the failing statement with the exact line from stack trace
                            var symbolSnippet = symbolExtractor.ExtractSnippet(
                                match.FilePath,
                                failingFrame.LineNumber.Value,  // Use stack trace line, not symbol line
                                contextLines: 7,
                                category: "Failing Statement (Exact Symbol Match)",
                                reason: "Found via exact symbol index lookup");

                            if (symbolSnippet != null)
                            {
                                Console.WriteLine($"  [RAG]   ✓ Extracted snippet from exact symbol match");
                                snippets.Add(symbolSnippet);

                                // Continue to collect additional context (locators, full method, etc.)
                                // rather than returning early — this ensures deterministic chunk count
                                // Set up extractor for the additional retrieval steps below
                                extractor = symbolExtractor;
                                foundSourceRoot = sourceDir;
                                _fileLocationCache[cacheKey] = sourceDir;
                                break;
                            }
                            break;
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine($"  [RAG]   ⚠️  No exact symbol match found - falling back to file scanning");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 2: File scanning fallback (if symbol lookup didn't find it)
        // ═══════════════════════════════════════════════════════════════════════
        // Try each source directory to find the file (with caching for performance)

        // Check cache first (only if extractor not already set by symbol lookup)
        if (extractor == null && _fileLocationCache.TryGetValue(cacheKey, out var cachedDir) && _sourceDirectories.Contains(cachedDir))
        {
            var cachedExtractor = new DebugSnippetExtractor(cachedDir);
            var testSnippet = cachedExtractor.ExtractSnippet(
                failingFrame.FilePath,
                failingFrame.LineNumber.Value,
                contextLines: 1,
                category: "Test",
                reason: "Testing cached directory");

            if (testSnippet != null)
            {
                extractor = cachedExtractor;
                foundSourceRoot = cachedDir;
                Console.WriteLine($"  [RAG]   ✓ Found source file in cached location: {cachedDir}");
            }
        }

        // If not in cache or cache miss, search through directories
        if (extractor == null)
        {
            foreach (var sourceDir in _sourceDirectories)
            {
                var testExtractor = new DebugSnippetExtractor(sourceDir);
                // Test if this source directory contains the failing file
                var testSnippet = testExtractor.ExtractSnippet(
                    failingFrame.FilePath,
                    failingFrame.LineNumber.Value,
                    contextLines: 1,
                    category: "Test",
                    reason: "Testing source directory");

                if (testSnippet != null)
                {
                    extractor = testExtractor;
                    foundSourceRoot = sourceDir;
                    _fileLocationCache[cacheKey] = sourceDir; // Cache the result
                    Console.WriteLine($"  [RAG]   ✓ Found source file in: {sourceDir}");
                    break;
                }
            }
        }

        // Fallback to vector store directory if source directories don't contain the file
        if (extractor == null)
        {
            var fallbackRoot = Path.GetDirectoryName(_vectorStorePath) ?? Directory.GetCurrentDirectory();
            Console.WriteLine($"  [RAG]   ⚠ File not found in source directories, trying fallback: {fallbackRoot}");
            extractor = new DebugSnippetExtractor(fallbackRoot);
            foundSourceRoot = fallbackRoot;
        }

        var locatorFinder = new LocatorDefinitionFinder(_store.Chunks);

        // ──────────────────────────────────────────────────────────────────────
        // 1. FAILING STATEMENT (Highest Priority)
        // ──────────────────────────────────────────────────────────────────────
        // Only extract if not already added by symbol lookup
        var failingSnippet = snippets.FirstOrDefault(s => s.Category.Contains("Failing Statement"));
        if (failingSnippet == null)
        {
            failingSnippet = extractor.ExtractSnippet(
                failingFrame.FilePath,
                failingFrame.LineNumber.Value,
                contextLines: 7,
                category: "Failing Statement",
                reason: "Exception occurred at this line");

            if (failingSnippet != null)
            {
                Console.WriteLine($"  [RAG]   ✓ Extracted failing statement from {failingSnippet.FileName}:{failingSnippet.FocusLine}");
                snippets.Add(failingSnippet);
            }
        }

        if (failingSnippet != null)
        {
            // 2. LOCATOR DEFINITIONS (if referenced in failing statement)
            // ──────────────────────────────────────────────────────────────────
            var potentialLocators = LocatorDefinitionFinder.ExtractPotentialLocators(failingSnippet.Content);
            Console.WriteLine($"  [RAG]   Found {potentialLocators.Count} potential locators in failing statement: {string.Join(", ", potentialLocators.Take(3))}");

            foreach (var locatorName in potentialLocators.Take(3))  // Limit to top 3
            {
                // Try to find the locator definition in the same file first
                var locatorSnippet = extractor.FindPropertyDefinition(
                    failingFrame.FilePath,
                    locatorName,
                    category: "Locator Definition",
                    reason: $"Referenced by failing statement: {locatorName}");

                if (locatorSnippet != null)
                {
                    Console.WriteLine($"  [RAG]   ✓ Found locator definition: {locatorName}");
                    snippets.Add(locatorSnippet);
                }
                else
                {
                    // Search in indexed chunks if not in same file
                    var matchingChunks = locatorFinder.FindDefinitions(locatorName);
                    if (matchingChunks.Any())
                    {
                        var chunk = matchingChunks.First();
                        Console.WriteLine($"  [RAG]   ✓ Found locator definition in indexed chunks: {locatorName} ({Path.GetFileName(chunk.SourcePath)})");

                        snippets.Add(new Models.DebugSnippet
                        {
                            FilePath = chunk.SourcePath,
                            FileName = Path.GetFileName(chunk.SourcePath),
                            MethodName = chunk.MethodName,
                            StartLine = chunk.StartLine,
                            EndLine = chunk.EndLine,
                            Content = chunk.Content,
                            Category = "Locator Definition",
                            Reason = $"Referenced by failing statement: {locatorName}"
                        });
                    }
                }
            }
        }
        else
        {
            Console.WriteLine($"  [RAG]   ❌ Could not extract failing statement - file not found on disk: {failingFrame.FileName}");
            Console.WriteLine($"  [RAG]   Expected path: {failingFrame.FilePath}");
        }

        // ──────────────────────────────────────────────────────────────────────
        // 3. METHOD CONTAINING FAILURE (show relevant portion only)
        // ──────────────────────────────────────────────────────────────────────
        if (failingFrame.MethodName != null && !failingFrame.MethodName.StartsWith("get_") && !failingFrame.MethodName.StartsWith("set_"))
        {
            var methodSnippet = extractor.ExtractMethod(
                failingFrame.FilePath,
                failingFrame.MethodName,
                maxLines: 20,
                category: "Method Containing Failure",
                reason: $"Contains the failing line {failingFrame.LineNumber}",
                lineHint: failingFrame.LineNumber);  // Disambiguate overloads

            if (methodSnippet != null)
            {
                Console.WriteLine($"  [RAG]   ✓ Extracted method: {methodSnippet.MethodName}()");
                snippets.Add(methodSnippet);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 4. CALLING TEST METHOD (if different from failing method)
        // ──────────────────────────────────────────────────────────────────────
        var testFrames = frames.Where(f => f.HasFileInfo && f.MethodName != failingFrame.MethodName).ToList();
        if (testFrames.Any())
        {
            var testFrame = testFrames.First();
            if (testFrame.FilePath != null && testFrame.LineNumber != null)
            {
                var testSnippet = extractor.ExtractSnippet(
                    testFrame.FilePath,
                    testFrame.LineNumber.Value,
                    contextLines: 5,
                    category: "Calling Test",
                    reason: $"Invokes {failingFrame.MethodName}()");

                if (testSnippet != null)
                {
                    Console.WriteLine($"  [RAG]   ✓ Extracted calling test context from {testSnippet.FileName}:{testSnippet.FocusLine}");
                    snippets.Add(testSnippet);
                }
            }
        }

        // Consider success if we have at least the failing statement
        // Use Contains() instead of == to match both "Failing Statement" and "Failing Statement (Exact Symbol Match)"
        bool success = snippets.Any(s => s.Category.Contains("Failing Statement"));
        return (success, snippets);
    }

    /// <summary>
    /// NEW: Formats debug snippets into a structured, labeled context string.
    /// </summary>
    private (string context, List<Models.RetrievedChunk> chunks) FormatDebugSnippets(
        List<Models.DebugSnippet> snippets,
        Models.TestResult failure)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("=== DEBUGGING-RELEVANT CODE (Stack-Trace-First Retrieval) ===");
        sb.AppendLine($"Retrieved {snippets.Count} debug snippets prioritized for debugging relevance.\n");

        var retrievedChunks = new List<Models.RetrievedChunk>();

        foreach (var snippet in snippets)
        {
            sb.AppendLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"Category: {snippet.Category}");
            sb.AppendLine($"File: {snippet.FileName}");
            if (!string.IsNullOrEmpty(snippet.MethodName))
                sb.AppendLine($"Method: {snippet.MethodName}()");
            sb.AppendLine($"Lines: {snippet.StartLine}-{snippet.EndLine}");
            if (snippet.FocusLine.HasValue)
                sb.AppendLine($"Focus Line: {snippet.FocusLine.Value} ← Exception occurred here");
            if (!string.IsNullOrEmpty(snippet.Reason))
                sb.AppendLine($"Reason: {snippet.Reason}");
            sb.AppendLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

            // Add line numbers to code
            var contentWithLineNumbers = AddLineNumbersToCode(snippet.Content, snippet.StartLine);
            sb.AppendLine(contentWithLineNumbers);
            sb.AppendLine();

            // Store for HTML report
            retrievedChunks.Add(new Models.RetrievedChunk
            {
                SourcePath = snippet.FilePath,
                MethodName = snippet.MethodName,
                ClassName = "",  // Not tracked in debug snippets
                StartLine = snippet.StartLine,
                EndLine = snippet.EndLine,
                RelevanceScore = 1.0f,  // Stack-trace snippets are always highly relevant
                SemanticScore = 1.0f,
                KeywordScore = 1.0f,
                Content = snippet.Content,
                IsExactMatch = true,  // This is an exact stack-trace match
                RetrievalMethod = "exact"
            });
        }

        sb.AppendLine("=== END OF DEBUG CONTEXT ===");
        return (sb.ToString(), retrievedChunks);
    }

    /// <summary>
    /// Adds line numbers to code for precise citation by LLM
    /// </summary>
    private static string AddLineNumbersToCode(string code, int startLine)
    {
        if (string.IsNullOrEmpty(code)) return code;

        var lines = code.Split('\n');
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            var lineNum = startLine + i;
            sb.AppendLine($"{lineNum,4}: {lines[i]}");
        }

        return sb.ToString();
    }

    // ── Engine Wrapper ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates embeddings for a batch of texts. Much more efficient than individual calls.
    /// Automatically handles rate limiting and retries with exponential backoff.
    /// </summary>
    private async Task<List<float[]>> GenerateBatchEmbeddingsAsync(List<string> texts, int maxRetries = 3)
    {
        if (texts.Count == 0)
            return new List<float[]>();

        if (_useOllama)
        {
            // Ollama doesn't support batch embedding in a single call, so we still call individually
            // but with better error handling and progress tracking
            var embeddings = new List<float[]>();
            foreach (var text in texts)
            {
                embeddings.Add(await GenerateEmbeddingAsync(text));
            }
            return embeddings;
        }
        else
        {
            // Azure OpenAI supports batch embedding (up to 2048 inputs, but we limit to 100 for safety)
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var embedResp = await _azureClient!.GetEmbeddingsAsync(
                        new EmbeddingsOptions(_embeddingModel, texts));

                    return embedResp.Value.Data
                        .OrderBy(d => d.Index)  // Ensure correct order
                        .Select(d => d.Embedding.ToArray())
                        .ToList();
                }
                catch (RequestFailedException ex) when (ex.Status == 429 || ex.Status >= 500)
                {
                    // Rate limit or server error - retry with exponential backoff
                    if (attempt == maxRetries)
                        throw;

                    var delayMs = 1000 * (int)Math.Pow(2, attempt - 1);  // 1s, 2s, 4s
                    Console.WriteLine($"  [RAG] Rate limited (HTTP {ex.Status}), retrying in {delayMs}ms... (attempt {attempt}/{maxRetries})");
                    await Task.Delay(delayMs);
                }
                catch (Exception ex) when (attempt < maxRetries && IsTransientError(ex))
                {
                    // Network or timeout error - retry
                    var delayMs = 1000 * attempt;
                    Console.WriteLine($"  [RAG] Transient error ({ex.Message}), retrying in {delayMs}ms...");
                    await Task.Delay(delayMs);
                }
            }

            throw new Exception($"Failed to generate batch embeddings after {maxRetries} retries");
        }
    }

    /// <summary>
    /// Generates embedding for a single text. Used for query embedding and as fallback
    /// when batch embedding fails.
    /// </summary>
    private async Task<float[]> GenerateEmbeddingAsync(string text, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
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
            catch (RequestFailedException ex) when (ex.Status == 429 || ex.Status >= 500)
            {
                if (attempt == maxRetries)
                    throw;

                var delayMs = 1000 * (int)Math.Pow(2, attempt - 1);
                await Task.Delay(delayMs);
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                var delayMs = 1000 * attempt;
                await Task.Delay(delayMs);
            }
        }

        throw new Exception($"Failed to generate embedding after {maxRetries} retries");
    }

    /// <summary>
    /// Determines if an exception is transient and worth retrying.
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        return ex is HttpRequestException ||
               ex is TaskCanceledException ||
               ex is TimeoutException ||
               (ex.InnerException != null && IsTransientError(ex.InnerException));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts key terms from the failure for keyword-based matching.
    /// Focuses on AutomationIds, method names, class names, and element names.
    /// </summary>
    private static HashSet<string> ExtractKeyTerms(Models.TestResult failure)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // AutomationIds (highest priority - these are unique identifiers)
        var idMatches = Regex.Matches(failure.ErrorMessage + " " + failure.StackTrace,
            @"(?:AutomationId|ByAutomationId)\s*[=:(""']+\s*([A-Za-z0-9_!]+)", RegexOptions.IgnoreCase);
        foreach (Match m in idMatches)
            if (m.Groups[1].Value.Length > 2)
                terms.Add(m.Groups[1].Value);

        // Method names from stack trace
        var methodMatches = Regex.Matches(failure.StackTrace,
            @"at\s+[\w\.]+\.([\w<>]+)\s*\(", RegexOptions.IgnoreCase);
        foreach (Match m in methodMatches)
            if (m.Groups[1].Value.Length > 3 && !m.Groups[1].Value.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
                terms.Add(m.Groups[1].Value);

        // Class names from stack trace
        var classMatches = Regex.Matches(failure.StackTrace,
            @"at\s+([\w\.]+)\.\w+\s*\(", RegexOptions.IgnoreCase);
        foreach (Match m in classMatches)
        {
            var fullName = m.Groups[1].Value;
            var className = fullName.Split('.').LastOrDefault();
            if (!string.IsNullOrEmpty(className) && className.Length > 3)
                terms.Add(className);
        }

        // Element/Window names
        var elementMatches = Regex.Matches(failure.ErrorMessage,
            @"(?:Element Name|Window|Dialog|Name)\s*:\s*[""']([^""']{3,})[""']", RegexOptions.IgnoreCase);
        foreach (Match m in elementMatches)
            terms.Add(m.Groups[1].Value);

        // Test name keywords (split CamelCase)
        var testNameParts = Regex.Split(failure.ShortName, @"(?<!^)(?=[A-Z])");
        foreach (var part in testNameParts)
            if (part.Length > 3)
                terms.Add(part);

        return terms;
    }

    /// <summary>
    /// Extracts file names mentioned in the stack trace.
    /// These files are given priority boosting during retrieval.
    /// </summary>
    private static HashSet<string> ExtractStackTraceFiles(string stackTrace)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pattern: "in C:\path\to\File.cs:line 123" or "File.cs:line 123"
        var fileMatches = Regex.Matches(stackTrace, @"(?:in\s+)?(?:[A-Z]:[\\\/])?(?:[\w\\\/]+[\\\/])?([\w\.]+\.cs)(?::line\s+\d+)?", RegexOptions.IgnoreCase);
        foreach (Match m in fileMatches)
        {
            var fileName = m.Groups[1].Value;
            if (!string.IsNullOrEmpty(fileName))
                files.Add(fileName);
        }

        return files;
    }

    /// <summary>
    /// Calculates keyword match score based on term frequency in chunk content.
    /// Returns normalized score between 0 and 1.
    /// </summary>
    private static float CalculateKeywordScore(Models.DocumentChunk chunk, HashSet<string> keyTerms)
    {
        if (keyTerms.Count == 0) return 0f;

        var contentLower = chunk.Content.ToLower();
        int matchCount = 0;
        int totalTerms = keyTerms.Count;

        foreach (var term in keyTerms)
        {
            // Check if term appears in content (case-insensitive)
            if (contentLower.Contains(term.ToLower()))
            {
                matchCount++;

                // Bonus points for exact match in method name or file path
                if (!string.IsNullOrEmpty(chunk.MethodName) &&
                    chunk.MethodName.Contains(term, StringComparison.OrdinalIgnoreCase))
                    matchCount++;

                if (chunk.SourcePath.Contains(term, StringComparison.OrdinalIgnoreCase))
                    matchCount++;
            }
        }

        // Normalize: divide by total terms, capped at 1.0
        return Math.Min(1.0f, (float)matchCount / totalTerms);
    }

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

    // Common FlaUI/MSTest boilerplate that appears near-identically across almost every
    // locator/timing failure. Left in the query, it dominates the embedding and makes
    // completely unrelated files score just as high as the actually relevant one, because
    // the vector ends up representing "generic FlaUI exception text" rather than anything
    // about THIS specific failure. Strip it before embedding so what's left is the stuff
    // that's actually distinctive: the AutomationId, the method names, the element/window
    // names involved.
    private static readonly Regex[] BoilerplatePatterns =
    {
        new(@"Element search timeout\.?\s*Element not found\.?", RegexOptions.IgnoreCase),
        new(@"Searched with scope:\s*\w+", RegexOptions.IgnoreCase),
        new(@"--->\s*System\.\w*Exception:\s*Unable to find[^\r\n]*", RegexOptions.IgnoreCase),
        new(@"at\s+System\.\w+(\.\w+)*\([^)]*\)", RegexOptions.IgnoreCase),          // framework stack frames
        new(@"at\s+FlaUI\.\w+(\.\w+)*\([^)]*\)", RegexOptions.IgnoreCase),           // FlaUI internals
        new(@"Test method [\w\.]+ threw exception:", RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// Builds the embedding query for retrieval. Pulls out the distinctive identifiers
    /// (AutomationId, element/window name, innermost user-code stack frame, class/method names) 
    /// and puts them FIRST and un-diluted, then appends a boilerplate-stripped version of the error
    /// message/stack trace as supporting context — rather than just concatenating the raw
    /// error text, which is mostly framework noise that's nearly identical across failures.
    /// Also expands key terms with common synonyms for better retrieval.
    /// </summary>
    private static string BuildFocusedQuery(Models.TestResult failure)
    {
        var identifiers = new List<string>();

        // 1. Test name itself (often contains valuable keywords)
        identifiers.Add(failure.ShortName);

        // 2. AutomationId the search was actually looking for (most important signal)
        var idMatches = Regex.Matches(failure.ErrorMessage + " " + failure.StackTrace,
            @"(?:AutomationId|ByAutomationId)\s*[=:(""']+\s*([A-Za-z0-9_!]+)", RegexOptions.IgnoreCase);
        foreach (Match m in idMatches)
        {
            if (m.Groups[1].Value.Length > 2)
            {
                var automationId = m.Groups[1].Value;
                identifiers.Add(automationId);

                // Query expansion: add variations of AutomationId
                // e.g., "!!ADMINELEMDB" -> also add "AdminElement", "Database"
                var expanded = ExpandAutomationId(automationId);
                identifiers.AddRange(expanded);
            }
        }

        // 3. Parent/target element or window Name — often carries the real context
        foreach (Match m in Regex.Matches(failure.ErrorMessage,
            @"(?:Parent Element Name|Element Name|Window|Dialog|Name)\s*:\s*[""']([^""']{3,60})[""']", RegexOptions.IgnoreCase))
            identifiers.Add(m.Groups[1].Value);

        // 4. Extract class and method names from user code frames
        var userFrames = failure.StackTrace.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("at ", StringComparison.OrdinalIgnoreCase)
                       && !l.Contains("System.", StringComparison.OrdinalIgnoreCase)
                       && !l.Contains("FlaUI.", StringComparison.OrdinalIgnoreCase)
                       && !l.Contains("Microsoft.", StringComparison.OrdinalIgnoreCase)
                       && !l.Contains("MSTest.", StringComparison.OrdinalIgnoreCase))
            .Take(5); // Increased from 3 to 5 for more context

        foreach (var frame in userFrames)
        {
            // Extract "at ClassName.MethodName(...)" pattern
            var frameMatch = Regex.Match(frame, @"at\s+([\w\.]+)\.([\w<>]+)\s*\(");
            if (frameMatch.Success)
            {
                var className = frameMatch.Groups[1].Value.Split('.').LastOrDefault();
                var methodName = frameMatch.Groups[2].Value;
                if (!string.IsNullOrEmpty(className)) identifiers.Add(className);
                if (!string.IsNullOrEmpty(methodName)) identifiers.Add(methodName);
            }
            else
            {
                // Fallback: add the whole frame
                identifiers.Add(frame);
            }
        }

        // 5. Extract action keywords (Click, Select, Navigate, etc.)
        var actionMatches = Regex.Matches(failure.ErrorMessage + " " + failure.ShortName,
            @"\b(Click|Select|Navigate|Open|Close|Enter|Type|Wait|Find|Search|Verify|Assert|Check|Create|Delete|Replicate|Apply)\w*\b",
            RegexOptions.IgnoreCase);
        foreach (Match m in actionMatches.Cast<Match>().Take(5))
            identifiers.Add(m.Value);

        // 6. Clean the error message by removing boilerplate
        var cleanedError = failure.ErrorMessage;
        foreach (var pattern in BoilerplatePatterns)
            cleanedError = pattern.Replace(cleanedError, " ");
        cleanedError = Regex.Replace(cleanedError, @"\s+", " ").Trim();

        // 7. Clean the stack trace
        var cleanedStack = TrimStackTrace(failure.StackTrace);
        foreach (var pattern in BoilerplatePatterns)
            cleanedStack = pattern.Replace(cleanedStack, " ");

        // Distinctive identifiers first (repeated for emphasis in embedding),
        // followed by cleaned error context
        var uniqueIdentifiers = identifiers.Distinct().Where(id => !string.IsNullOrWhiteSpace(id));
        return $"{string.Join(" ", uniqueIdentifiers)} {string.Join(" ", uniqueIdentifiers)} {cleanedError} {cleanedStack}".Trim();
    }

    /// <summary>
    /// Expands AutomationId into component terms for better retrieval.
    /// e.g., "!!ADMINELEMDB" -> ["Admin", "Element", "Database", "DB"]
    /// </summary>
    private static List<string> ExpandAutomationId(string automationId)
    {
        var expanded = new List<string>();

        // Remove special characters and split by common patterns
        var cleaned = Regex.Replace(automationId, @"[!@#$%^&*()_+=\-\[\]{}|\\:;""'<>,.?/~`]", " ");

        // Split by uppercase (camel case), numbers, and spaces
        var parts = Regex.Split(cleaned, @"(?<!^)(?=[A-Z])|(?<=\D)(?=\d)|(?<=\d)(?=\D)|\s+")
            .Where(p => p.Length > 2)
            .ToList();

        expanded.AddRange(parts);

        // Add common abbreviation expansions
        var abbreviations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "DB", "Database" },
            { "ELEM", "Element" },
            { "BTN", "Button" },
            { "DLG", "Dialog" },
            { "WIN", "Window" },
            { "ADMIN", "Administration" },
            { "MGR", "Manager" },
            { "CMD", "Command" },
            { "PROJ", "Project" },
            { "REG", "Registry" }
        };

        foreach (var part in parts)
        {
            if (abbreviations.TryGetValue(part, out var fullForm))
                expanded.Add(fullForm);
        }

        return expanded.Distinct().ToList();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string TryMakeRelative(string path)
    {
        try { return Path.GetRelativePath(Directory.GetCurrentDirectory(), path); }
        catch { return Path.GetFileName(path); }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FIXES 2, 3, 4: Page Object Model Enhancement
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enhances retrieved chunks with Page Object Model-specific context:
    /// FIX 2: Explicit constructor retrieval for Page Objects
    /// FIX 3: Guaranteed stack trace method retrieval
    /// FIX 4: Explicit AutomationId locator search
    /// </summary>
    private async Task<List<(float Score, Models.DocumentChunk Chunk, float SemanticScore, float KeywordScore, bool InStackTrace)>> 
        EnhancePageObjectContextAsync(
            List<(float Score, Models.DocumentChunk Chunk, float SemanticScore, float KeywordScore, bool InStackTrace)> initialChunks,
            Models.TestResult failure,
            HashSet<string> stackTraceFiles)
    {
        var enhanced = new List<(float Score, Models.DocumentChunk Chunk, float SemanticScore, float KeywordScore, bool InStackTrace)>(initialChunks);
        var addedChunkIds = new HashSet<string>(initialChunks.Select(c => c.Chunk.Id));

        Console.WriteLine($"\n  ══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  [RAG ENHANCEMENT DIAGNOSTICS]");
        Console.WriteLine($"  ══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Initial chunks: {initialChunks.Count}");
        Console.WriteLine($"  Classes in initial chunks:");
        foreach (var c in initialChunks.Take(5))
        {
            Console.WriteLine($"    - {c.Chunk.ClassName ?? "NO_CLASS"}.{c.Chunk.MethodName ?? "NO_METHOD"} (score: {c.Score:F2})");
        }

        // FIX 3: Guarantee stack trace method retrieval
        var stackMethods = ExtractStackTraceMethods(failure.StackTrace);
        var stackClasses = new HashSet<string>(stackMethods.Select(m => m.ClassName));

        Console.WriteLine($"\n  [FIX 3] Stack trace analysis:");
        Console.WriteLine($"    Extracted {stackMethods.Count} project methods from stack trace:");
        foreach (var (className, methodName) in stackMethods.Take(5))
        {
            Console.WriteLine($"      - {className}.{methodName}()");
        }

        int stackAdded = 0;
        foreach (var (className, methodName) in stackMethods.Take(5))  // Increased from 3 to 5
        {
            Console.WriteLine($"\n    Searching for: {className}.{methodName}()");

            // Debug: Show what we're searching against
            var candidateChunks = _store.Chunks
                .Where(c => !string.IsNullOrEmpty(c.ClassName) && !string.IsNullOrEmpty(c.MethodName))
                .Where(c => c.ClassName.Contains(className, StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToList();

            if (candidateChunks.Any())
            {
                Console.WriteLine($"    Found {candidateChunks.Count} chunks with class name containing '{className}':");
                foreach (var cc in candidateChunks)
                {
                    Console.WriteLine($"      - {cc.ClassName}.{cc.MethodName}() — Match: {cc.MethodName.Equals(methodName, StringComparison.OrdinalIgnoreCase)}");
                }
            }
            else
            {
                Console.WriteLine($"    ❌ No chunks found with class name containing '{className}'");
            }

            var stackChunk = _store.Chunks.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.ClassName) &&
                !string.IsNullOrEmpty(c.MethodName) &&
                c.ClassName.Contains(className, StringComparison.OrdinalIgnoreCase) &&
                c.MethodName.Equals(methodName, StringComparison.OrdinalIgnoreCase));

            if (stackChunk != null && !addedChunkIds.Contains(stackChunk.Id))
            {
                enhanced.Insert(0, (2.5f, stackChunk, 1.0f, 1.0f, true));
                addedChunkIds.Add(stackChunk.Id);
                stackAdded++;
                Console.WriteLine($"    ✅ ADDED: {stackChunk.ClassName}.{stackChunk.MethodName}() from {Path.GetFileName(stackChunk.SourcePath)}");
            }
            else if (stackChunk != null)
            {
                Console.WriteLine($"    ⚠️  Already in context: {stackChunk.ClassName}.{stackChunk.MethodName}()");
            }
            else
            {
                Console.WriteLine($"    ❌ NOT FOUND in vector store");
            }
        }

        // NEW: Also retrieve constructors for all classes in stack trace
        Console.WriteLine($"\n  [FIX 3+] Retrieving constructors for {stackClasses.Count} classes in stack trace...");

        int ctorFromStackAdded = 0;
        foreach (var className in stackClasses.Take(5))  // Top 5 classes
        {
            // Defense: Skip empty class names (prevent matching everything via .Contains(""))
            if (string.IsNullOrWhiteSpace(className))
            {
                Console.WriteLine($"    ⚠️  Skipping empty class name from stack trace");
                continue;
            }

            var constructor = _store.Chunks.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.ClassName) &&
                c.ClassName.Contains(className, StringComparison.OrdinalIgnoreCase) &&
                c.MethodName == ".ctor");

            if (constructor != null && !addedChunkIds.Contains(constructor.Id))
            {
                // Insert after the failing method from this class
                var insertIndex = enhanced.FindIndex(c => 
                    c.Chunk.ClassName?.Contains(className, StringComparison.OrdinalIgnoreCase) == true) + 1;

                if (insertIndex > 0 && insertIndex <= enhanced.Count)
                {
                    enhanced.Insert(insertIndex, (2.3f, constructor, 0.9f, 0.9f, false));
                    addedChunkIds.Add(constructor.Id);
                    ctorFromStackAdded++;
                    Console.WriteLine($"    ✅ Added constructor for {className} (from stack trace)");
                }
            }
        }

        Console.WriteLine($"  [FIX 3] Result: Added {stackAdded} stack trace methods + {ctorFromStackAdded} constructors");

        // FIX 2: Explicit constructor retrieval for Page Objects
        Console.WriteLine($"\n  [FIX 2] Page Object constructor analysis:");

        // DEBUG: Show all unique class names in enhanced chunks
        var allClasses = enhanced.Select(c => c.Chunk.ClassName).Where(cn => !string.IsNullOrEmpty(cn)).Distinct().ToList();
        Console.WriteLine($"    All unique classes in context: {allClasses.Count}");
        foreach (var cn in allClasses.Take(10))
        {
            var isPageObject = IsPageObjectOrUIClass(cn);
            Console.WriteLine($"      - {cn} (Page Object: {isPageObject})");
        }

        var pageObjectClasses = enhanced
            .Where(c => !string.IsNullOrEmpty(c.Chunk.ClassName))
            .Where(c => IsPageObjectOrUIClass(c.Chunk.ClassName))
            .Select(c => c.Chunk.ClassName)
            .Distinct()
            .ToList();

        Console.WriteLine($"    Detected {pageObjectClasses.Count} Page Object classes: {string.Join(", ", pageObjectClasses)}");

        // DEBUG: Check if constructors exist at all
        var allConstructors = _store.Chunks.Where(c => c.MethodName == ".ctor").Take(5).ToList();
        Console.WriteLine($"    Total constructors in vector store: {_store.Chunks.Count(c => c.MethodName == ".ctor")}");
        if (allConstructors.Any())
        {
            Console.WriteLine($"    Example constructors:");
            foreach (var ctor in allConstructors)
            {
                Console.WriteLine($"      - {ctor.ClassName}.{ctor.MethodName}");
            }
        }

        int ctorAdded = 0;
        foreach (var className in pageObjectClasses)
        {
            Console.WriteLine($"\n    Searching for constructor: {className}..ctor()");

            var constructor = _store.Chunks.FirstOrDefault(c =>
                c.ClassName?.Equals(className, StringComparison.OrdinalIgnoreCase) == true &&
                c.MethodName == ".ctor");

            if (constructor != null && !addedChunkIds.Contains(constructor.Id))
            {
                var insertIndex = enhanced.FindIndex(c => 
                    c.Chunk.ClassName?.Equals(className, StringComparison.OrdinalIgnoreCase) == true) + 1;

                if (insertIndex > 0 && insertIndex <= enhanced.Count)
                {
                    enhanced.Insert(insertIndex, (2.3f, constructor, 0.9f, 0.9f, false));
                    addedChunkIds.Add(constructor.Id);
                    ctorAdded++;
                    Console.WriteLine($"    ✅ ADDED: {className}..ctor() at index {insertIndex}");
                    Console.WriteLine($"       Preview: {constructor.Content.Substring(0, Math.Min(100, constructor.Content.Length))}...");
                }
            }
            else if (constructor != null)
            {
                Console.WriteLine($"    ⚠️  Already in context: {className}..ctor()");
            }
            else
            {
                Console.WriteLine($"    ❌ NOT FOUND: Constructor for {className} doesn't exist in vector store");
            }
        }
        Console.WriteLine($"  [FIX 2] Result: Added {ctorAdded} constructors");

        // FIX 4: Explicit AutomationId locator search
        Console.WriteLine($"\n  [FIX 4] AutomationId locator analysis:");

        var automationIds = ExtractAutomationIdsFromFailure(failure);
        Console.WriteLine($"    Error message: {failure.ErrorMessage.Substring(0, Math.Min(200, failure.ErrorMessage.Length))}...");
        Console.WriteLine($"    Extracted AutomationIds: {automationIds.Count}");

        if (automationIds.Any())
        {
            foreach (var id in automationIds)
            {
                Console.WriteLine($"      - '{id}'");
            }

            int locatorAdded = 0;
            foreach (var id in automationIds.Take(3))  // Top 3 AutomationIds
            {
                Console.WriteLine($"\n    Searching for locator definition: '{id}'");

                var candidateChunks = _store.Chunks
                    .Where(c => c.Content.Contains(id, StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList();

                Console.WriteLine($"    Found {candidateChunks.Count} chunks containing '{id}'");

                var locatorChunks = _store.Chunks
                    .Where(c => c.Content.Contains(id, StringComparison.OrdinalIgnoreCase))
                    .Where(c => c.Content.Contains("ByAutomationId", StringComparison.OrdinalIgnoreCase) ||
                               c.Content.Contains("FindFirstDescendant", StringComparison.OrdinalIgnoreCase) ||
                               c.Content.Contains("FindAll", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.MethodName == ".ctor" ? 100 :
                                           c.MethodName?.StartsWith("Get") == true ? 50 :
                                           c.Content.Contains("public ") ? 25 : 0)
                    .Take(2)
                    .ToList();

                Console.WriteLine($"    Filtered to {locatorChunks.Count} locator-defining chunks:");
                foreach (var lc in locatorChunks)
                {
                    Console.WriteLine($"      - {lc.ClassName}.{lc.MethodName}() — Priority: {(lc.MethodName == ".ctor" ? "HIGH (ctor)" : "MEDIUM")}");
                }

                foreach (var locatorChunk in locatorChunks)
                {
                    if (!addedChunkIds.Contains(locatorChunk.Id))
                    {
                        enhanced.Insert(0, (2.4f, locatorChunk, 1.0f, 1.0f, false));
                        addedChunkIds.Add(locatorChunk.Id);
                        locatorAdded++;
                        Console.WriteLine($"    ✅ ADDED: {locatorChunk.ClassName}.{locatorChunk.MethodName}() containing '{id}'");

                        // Show snippet
                        var snippet = locatorChunk.Content;
                        var idIndex = snippet.IndexOf(id, StringComparison.OrdinalIgnoreCase);
                        if (idIndex >= 0)
                        {
                            var start = Math.Max(0, idIndex - 50);
                            var length = Math.Min(150, snippet.Length - start);
                            Console.WriteLine($"       Snippet: ...{snippet.Substring(start, length)}...");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"    ⚠️  Already in context: {locatorChunk.ClassName}.{locatorChunk.MethodName}()");
                    }
                }

                if (locatorChunks.Count == 0)
                {
                    Console.WriteLine($"    ❌ NOT FOUND: No chunks define locator for '{id}'");
                }
            }
            Console.WriteLine($"  [FIX 4] Result: Added {locatorAdded} locator chunks");
        }
        else
        {
            Console.WriteLine($"    No AutomationIds detected in error message");
        }

        // Deduplicate chunks by ID before final summary
        Console.WriteLine($"\n  [DEDUPLICATION] Before: {enhanced.Count} chunks");
        enhanced = enhanced
            .GroupBy(e => e.Chunk.Id)
            .Select(g => g.First())
            .ToList();
        Console.WriteLine($"  [DEDUPLICATION] After: {enhanced.Count} chunks (removed {addedChunkIds.Count - enhanced.Count} duplicates)");

        // CRITICAL: Sort by priority so LLM sees the most important code first
        // Priority order:
        // 1. Stack trace methods (2.5) - The actual failing code path
        // 2. Locator definitions (2.4) - Element definitions for failed locators
        // 3. Original semantic matches (1.5-0.5) - Related code
        Console.WriteLine($"\n  [PRIORITY SORTING] Reordering chunks by relevance...");
        enhanced = enhanced.OrderByDescending(e => e.Score).ToList();
        Console.WriteLine($"    Top 3 after sort:");
        foreach (var item in enhanced.Take(3))
        {
            Console.WriteLine($"      {item.Score:F2} - {item.Chunk.ClassName}.{item.Chunk.MethodName}()");
        }

        // Final summary
        Console.WriteLine($"\n  ══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  [FINAL CONTEXT SUMMARY]");
        Console.WriteLine($"  ══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Total chunks being sent to LLM: {enhanced.Count}");
        Console.WriteLine($"  Breakdown:");
        int chunkNum = 1;
        foreach (var (score, chunk, _, _, inStack) in enhanced.Take(15))
        {
            var marker = inStack ? "[STACK]" : "";
            var ctorMarker = chunk.MethodName == ".ctor" ? "[CTOR]" : "";
            Console.WriteLine($"    {chunkNum}. {chunk.ClassName}.{chunk.MethodName}() {marker}{ctorMarker}");
            Console.WriteLine($"       File: {Path.GetFileName(chunk.SourcePath)} (lines {chunk.StartLine}-{chunk.EndLine})");
            Console.WriteLine($"       Score: {score:F2}");
            chunkNum++;
        }
        if (enhanced.Count > 15)
        {
            Console.WriteLine($"    ... and {enhanced.Count - 15} more chunks");
        }
        Console.WriteLine($"  ══════════════════════════════════════════════════════════════\n");

        return enhanced;
    }

    /// <summary>
    /// Builds an execution flow narrative that reconstructs the path from test start to failure.
    /// Combines stack trace call chain with log timeline to create a chronological story.
    /// </summary>
    private static string BuildExecutionFlowNarrative(
        List<(float Score, Models.DocumentChunk Chunk, float SemanticScore, float KeywordScore, bool InStackTrace)> chunks,
        Models.TestResult failure,
        string? logSnippet)
    {
        var sb = new System.Text.StringBuilder();

        // Extract call chain from stack trace (project methods only, in order)
        var stackMethods = ExtractStackTraceMethods(failure.StackTrace);
        if (!stackMethods.Any())
            return string.Empty;

        // Reverse to get execution order (deepest call first in stack = earliest in execution)
        stackMethods.Reverse();

        sb.AppendLine("This failure occurred during the following execution sequence:\n");

        // Step 1: Test method start
        var testMethod = stackMethods.FirstOrDefault(m => m.MethodName.Contains(failure.ShortName) || 
                                                            m.MethodName.EndsWith("Test") ||
                                                            failure.StackTrace.Contains($"{m.ClassName}.{m.MethodName}"));

        if (testMethod != default)
        {
            sb.AppendLine($"1. TEST STARTED: {testMethod.ClassName}.{testMethod.MethodName}()");
            sb.AppendLine($"   Purpose: {InferMethodPurpose(testMethod.MethodName, failure.ShortName)}");
            sb.AppendLine();
        }

        // Step 2: Extract log timeline
        var logSteps = ExtractLogTimeline(logSnippet);
        if (logSteps.Any())
        {
            sb.AppendLine("2. LOG TIMELINE (successful operations before failure):");
            foreach (var (timestamp, operation) in logSteps.Take(5))
            {
                sb.AppendLine($"   {timestamp} → {operation}");
            }
            var lastSuccessful = logSteps.Last();
            sb.AppendLine($"   ✓ Last successful: {lastSuccessful.Operation}");
            sb.AppendLine();
        }

        // Step 3: Call chain leading to failure
        var projectMethods = stackMethods.Where(m => !m.MethodName.Contains(failure.ShortName)).Take(5).ToList();
        if (projectMethods.Any())
        {
            sb.AppendLine("3. CALL CHAIN TO FAILURE:");
            for (int i = 0; i < projectMethods.Count; i++)
            {
                var method = projectMethods[i];
                var indent = new string(' ', i * 3);
                var arrow = i > 0 ? "↓ " : "  ";

                // Try to find this method in retrieved chunks
                var methodChunk = chunks.FirstOrDefault(c => 
                    c.Chunk.ClassName?.Equals(method.ClassName, StringComparison.OrdinalIgnoreCase) == true &&
                    c.Chunk.MethodName?.Equals(method.MethodName, StringComparison.OrdinalIgnoreCase) == true).Chunk;

                if (methodChunk != null)
                {
                    sb.AppendLine($"   {indent}{arrow}{method.ClassName}.{method.MethodName}()");
                    sb.AppendLine($"   {indent}   └─ {Path.GetFileName(methodChunk.SourcePath)}:{methodChunk.StartLine}");

                    // Extract key line that likely failed
                    var keyLine = ExtractLikelyFailingLine(methodChunk.Content, failure.ErrorMessage);
                    if (!string.IsNullOrEmpty(keyLine))
                    {
                        sb.AppendLine($"   {indent}   └─ Key operation: {keyLine.Trim()}");
                    }
                }
                else
                {
                    sb.AppendLine($"   {indent}{arrow}{method.ClassName}.{method.MethodName}()");
                }
            }
            sb.AppendLine();
        }

        // Step 4: Failure point
        sb.AppendLine("4. FAILURE POINT:");
        var exceptionType = ExtractExceptionType(failure.ErrorMessage);
        sb.AppendLine($"   ✗ Exception: {exceptionType}");
        sb.AppendLine($"   ✗ Message: {failure.ErrorMessage.Split('\n').FirstOrDefault() ?? failure.ErrorMessage}");

        if (projectMethods.Any())
        {
            var failingMethod = projectMethods.Last();
            sb.AppendLine($"   ✗ Location: {failingMethod.ClassName}.{failingMethod.MethodName}()");
        }

        sb.AppendLine();
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        sb.AppendLine("Use this execution flow to understand the sequence of events.");
        sb.AppendLine("The retrieved code chunks below provide implementation details for each step.");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

        return sb.ToString();
    }

    /// <summary>
    /// Extracts timestamp and operation pairs from log snippet.
    /// </summary>
    private static List<(string Timestamp, string Operation)> ExtractLogTimeline(string? logSnippet)
    {
        var timeline = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(logSnippet))
            return timeline;

        var lines = logSnippet.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            // Special handling for gap descriptions: "X.Xs gap between HH:MM:SS and HH:MM:SS"
            var gapMatch = Regex.Match(line, @"(\d+\.?\d*)s gap between (\d{1,2}:\d{2}:\d{2}) and (\d{1,2}:\d{2}:\d{2})");
            if (gapMatch.Success)
            {
                var duration = gapMatch.Groups[1].Value;
                var startTime = gapMatch.Groups[2].Value;
                var endTime = gapMatch.Groups[3].Value;

                // Extract description after the timestamps (if any)
                var afterPattern = Regex.Match(line, @"\d{1,2}:\d{2}:\d{2}\)?\s*(.*)$");
                var description = afterPattern.Success ? afterPattern.Groups[1].Value.Trim() : "silence";

                // Create a readable gap entry
                var gapDescription = !string.IsNullOrWhiteSpace(description) && description != ")" 
                    ? $"{duration}s silence — {description}" 
                    : $"{duration}s silence";

                timeline.Add((startTime, $"{endTime} ({gapDescription})"));
                continue;
            }

            // Pattern: "HH:MM:SS ... operation text"
            var match = Regex.Match(line, @"(\d{1,2}:\d{2}:\d{2}(?:\.\d+)?)\s+(.+)");
            if (match.Success)
            {
                var timestamp = match.Groups[1].Value;
                var operation = match.Groups[2].Value.Trim();

                // Skip noise
                if (operation.Length > 10 && 
                    !operation.Contains("DEBUG") && 
                    !operation.Contains("Trace"))
                {
                    timeline.Add((timestamp, operation));
                }
            }
        }

        return timeline;
    }

    /// <summary>
    /// Tries to extract the specific line in the method that likely failed.
    /// </summary>
    private static string ExtractLikelyFailingLine(string methodContent, string errorMessage)
    {
        // Look for keywords from error message in method content
        var errorKeywords = new[] { "Click", "FindFirst", "WaitFor", "GetElement", "AutomationId", "ByName", "Select", "Assert" };

        foreach (var keyword in errorKeywords)
        {
            if (errorMessage.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                // Find line containing this keyword
                var lines = methodContent.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase) && 
                        !line.TrimStart().StartsWith("//"))
                    {
                        return line.Length > 80 ? line.Substring(0, 77) + "..." : line;
                    }
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Extracts exception type from error message.
    /// </summary>
    private static string ExtractExceptionType(string errorMessage)
    {
        var match = Regex.Match(errorMessage, @"(\w+(?:\.\w+)*Exception)");
        return match.Success ? match.Groups[1].Value : "Exception";
    }

    /// <summary>
    /// Infers the purpose of a test method from its name.
    /// </summary>
    private static string InferMethodPurpose(string methodName, string testName)
    {
        // CamelCase to words
        var words = Regex.Split(methodName, @"(?<!^)(?=[A-Z])");
        var purpose = string.Join(" ", words).ToLower();

        if (purpose.Contains("verify") || purpose.Contains("check"))
            return $"Verify {purpose.Replace("verify", "").Replace("check", "").Trim()}";
        if (purpose.Contains("test"))
            return $"Test {purpose.Replace("test", "").Trim()}";
        if (purpose.Contains("ensure"))
            return $"Ensure {purpose.Replace("ensure", "").Trim()}";

        return purpose;
    }

    /// <summary>
    /// Extracts (ClassName, MethodName) tuples from stack trace.
    /// Filters out framework methods (System, FlaUI, Microsoft, MSTest).
    /// </summary>
    private static List<(string ClassName, string MethodName)> ExtractStackTraceMethods(string stackTrace)
    {
        var methods = new List<(string, string)>();

        var lines = stackTrace.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Contains("System.", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Contains("FlaUI.", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Contains("Microsoft.", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Contains("MSTest.", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Contains("UTA.", StringComparison.OrdinalIgnoreCase))  // Filter utility framework
            .Where(l => !l.Contains("Aveva.Test.Automation.", StringComparison.OrdinalIgnoreCase));  // Filter test automation utilities

        foreach (var line in lines)
        {
            // Parse: "at Namespace.ClassName.MethodName(params) in File.cs:line 123"
            var match = Regex.Match(line, @"at\s+([\w\.]+)\.([\w<>]+)\s*\(");
            if (match.Success)
            {
                var fullName = match.Groups[1].Value;  // "Namespace.SubNamespace.ClassName"
                var methodName = match.Groups[2].Value;

                // FIX: Handle constructors properly (fullName may end with dot: "Namespace.ClassName.")
                // Split with RemoveEmptyEntries to avoid empty strings from trailing dots
                var parts = fullName.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var className = parts.Length > 0 ? parts.Last() : "";

                // Only add if we have a valid class name (prevent empty string matching everything)
                if (!string.IsNullOrWhiteSpace(className))
                {
                    methods.Add((className, methodName));
                }
            }
        }

        return methods;
    }

    /// <summary>
    /// Extracts AutomationIds from error message and stack trace.
    /// Looks for patterns like: ByAutomationId("!!ADMIN"), AutomationId="!!DB", "Condition: AutomationId = !!ID"
    /// </summary>
    private static List<string> ExtractAutomationIdsFromFailure(Models.TestResult failure)
    {
        var ids = new List<string>();
        var text = failure.ErrorMessage + " " + failure.StackTrace;

        // Pattern 1: ByAutomationId("!!ID") or ByAutomationId('!!ID')
        var pattern1 = Regex.Matches(text, @"ByAutomationId\s*\(\s*[""']([^""']+)[""']\s*\)", RegexOptions.IgnoreCase);
        foreach (Match m in pattern1)
            if (m.Groups[1].Value.Length > 2)
                ids.Add(m.Groups[1].Value);

        // Pattern 2: AutomationId = "!!ID" or AutomationId="!!ID"
        var pattern2 = Regex.Matches(text, @"AutomationId\s*[=:]\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        foreach (Match m in pattern2)
            if (m.Groups[1].Value.Length > 2)
                ids.Add(m.Groups[1].Value);

        // NEW Pattern 4: "Condition: AutomationId = !!ID" (FlaUI exception format)
        var pattern4 = Regex.Matches(text, @"Condition:\s*AutomationId\s*=\s*([^\s\r\n]+)", RegexOptions.IgnoreCase);
        foreach (Match m in pattern4)
        {
            var id = m.Groups[1].Value.Trim();
            if (id.Length > 2)
                ids.Add(id);
        }

        // Pattern 3: Just "!!ID" (common in error messages) - Keep this last as it's most generic
        var pattern3 = Regex.Matches(text, @"!+([A-Z0-9_]{3,})");
        foreach (Match m in pattern3)
            ids.Add("!!" + m.Groups[1].Value);

        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Detects if a class is a Page Object or UI automation class using multiple criteria.
    /// Expands beyond simple "*Page" name suffix to catch classes like CreateDatabasesandExtracts.
    /// </summary>
    private bool IsPageObjectOrUIClass(string className)
    {
        // Criteria 1: Class name ends with "Page"
        if (className.EndsWith("Page", StringComparison.OrdinalIgnoreCase))
            return true;

        // Criteria 2: Class name contains UI-related keywords
        var uiKeywords = new[] { "Dialog", "Window", "Screen", "Panel", "Form", "Extract", "Wizard", "View" };
        if (uiKeywords.Any(k => className.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Criteria 3: Class is in a "PageObjects" directory
        var classChunks = _store.Chunks.Where(c => c.ClassName == className).ToList();
        if (classChunks.Any(c => c.SourcePath.Contains("\\PageObjects\\", StringComparison.OrdinalIgnoreCase) ||
                                 c.SourcePath.Contains("/PageObjects/", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Criteria 4: Constructor uses FlaUI locators
        var constructor = classChunks.FirstOrDefault(c => c.MethodName == ".ctor");
        if (constructor != null)
        {
            var content = constructor.Content;
            bool hasAutomationElement = content.Contains("AutomationElement", StringComparison.OrdinalIgnoreCase);
            bool hasFlaUILocator = content.Contains("FindFirstDescendant", StringComparison.OrdinalIgnoreCase) ||
                                    content.Contains("ByAutomationId", StringComparison.OrdinalIgnoreCase) ||
                                    content.Contains("ByName", StringComparison.OrdinalIgnoreCase) ||
                                    content.Contains("ByClassName", StringComparison.OrdinalIgnoreCase);

            if (hasAutomationElement && hasFlaUILocator)
                return true;
        }

        // Criteria 5: Class has AutomationElement fields
        bool hasUIFields = classChunks.Any(c =>
            c.Content.Contains("AutomationElement ", StringComparison.OrdinalIgnoreCase) &&
            (c.Content.Contains("private ", StringComparison.OrdinalIgnoreCase) ||
             c.Content.Contains("protected ", StringComparison.OrdinalIgnoreCase) ||
             c.Content.Contains("public ", StringComparison.OrdinalIgnoreCase)));

        if (hasUIFields)
            return true;

        return false;
    }
}