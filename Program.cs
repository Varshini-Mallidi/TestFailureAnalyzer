using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CommandLine;
using FailureAnalyzer.Models;
using FailureAnalyzer.Services;
using FailureAnalyzer.Reports;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

await Parser.Default.ParseArguments<CliOptions>(args)
    .WithParsedAsync(async opts =>
    {
        Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine("  FailureAnalyzer — AI Test Analysis");
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        // ── Azure DevOps Integration mode ────────────────────────────────────
        if (opts.AdoLatest || opts.AdoBuildId.HasValue || opts.AdoPipelineId.HasValue)
        {
            var adoSection = config.GetSection("AzureDevOps");
            var orgUrl = adoSection["OrganizationUrl"];
            var projectName = adoSection["ProjectName"];
            var pat = adoSection["PersonalAccessToken"];
            var defaultPipelineId = int.TryParse(adoSection["DefaultPipelineId"], out var pid) ? pid : 0;
            var tempPath = adoSection["TempDownloadPath"] ?? ".ado-downloads";

            if (string.IsNullOrWhiteSpace(orgUrl) || string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(pat))
            {
                Console.WriteLine("❌ Error: Azure DevOps configuration missing in appsettings.json");
                Console.WriteLine("   Required fields: AzureDevOps.OrganizationUrl, ProjectName, PersonalAccessToken");
                Console.WriteLine("   Example configuration:");
                Console.WriteLine("   {");
                Console.WriteLine("     \"AzureDevOps\": {");
                Console.WriteLine("       \"OrganizationUrl\": \"https://dev.azure.com/your-org\",");
                Console.WriteLine("       \"ProjectName\": \"YourProject\",");
                Console.WriteLine("       \"PersonalAccessToken\": \"your-pat-token\",");
                Console.WriteLine("       \"DefaultPipelineId\": 123");
                Console.WriteLine("     }");
                Console.WriteLine("   }");
                Console.WriteLine("\n   Get PAT at: https://dev.azure.com/{org}/_usersSettings/tokens");
                Console.WriteLine("   Required scopes: Build (Read), Test Management (Read)");
                return;
            }

            var adoClient = new AdoClient(orgUrl, projectName, pat);
            var adoDownloader = new AdoDownloader(adoClient, tempPath);

            string? adoTrxPath = null;
            string? adoLogsPath = null;

            // Download from ADO based on the specified option
            if (opts.AdoBuildId.HasValue)
            {
                (adoTrxPath, adoLogsPath) = await adoDownloader.DownloadFromBuildAsync(opts.AdoBuildId.Value);
            }
            else if (opts.AdoPipelineId.HasValue)
            {
                (adoTrxPath, adoLogsPath) = await adoDownloader.DownloadLatestAsync(opts.AdoPipelineId.Value);
            }
            else // opts.AdoLatest
            {
                var pipelineId = defaultPipelineId > 0 ? defaultPipelineId : (int?)null;
                (adoTrxPath, adoLogsPath) = await adoDownloader.DownloadLatestAsync(pipelineId);
            }

            if (string.IsNullOrWhiteSpace(adoTrxPath))
            {
                Console.WriteLine("❌ Could not download TRX file from Azure DevOps. Analysis cannot proceed.");
                return;
            }

            // Override opts with downloaded paths
            opts.TrxPath = adoTrxPath;
            if (!string.IsNullOrWhiteSpace(adoLogsPath))
                opts.LogDirectory = adoLogsPath;

            Console.WriteLine($"[ADO] Using TRX: {adoTrxPath}");
            if (!string.IsNullOrWhiteSpace(adoLogsPath))
                Console.WriteLine($"[ADO] Using logs: {adoLogsPath}");
            Console.WriteLine();

            // Continue to normal analysis flow below
        }

        // ── Ingestion Pipeline mode: --index ────────────────────────────────────
        if (opts.Index)
        {
            if (opts.SourceDirectories == null || !opts.SourceDirectories.Any())
            {
                Console.WriteLine("❌ Error: --index requires --source-dir to be specified");
                Console.WriteLine("   Example: dotnet run -- --index --source-dir /path/to/repo");
                Console.WriteLine("   Or multiple: dotnet run -- --index --source-dir /path1 --source-dir /path2");
                return;
            }

            var indexer = new StandaloneIndexer(config);
            await indexer.IndexRepositoryAsync(opts.SourceDirectories, opts.ForceReindex);
            return;
        }

        // ── Audit Index mode: --audit-index ────────────────────────────────────
        if (opts.AuditIndex)
        {
            if (opts.SourceDirectories == null || !opts.SourceDirectories.Any())
            {
                Console.WriteLine("❌ Error: --audit-index requires --source-dir to be specified");
                Console.WriteLine("   Example: dotnet run -- --audit-index --source-dir /path/to/repo");
                return;
            }

            var auditor = new FailureAnalyzer.Utils.VectorIndexAuditor(
                "vector_store.json",  // hardcoded path matching StandaloneIndexer
                opts.SourceDirectories);
            await auditor.PrintAuditReportAsync();
            return;
        }

        // ── Test mode: --test-exception ────────────────────────────────────
        if (opts.TestException)
        {
            FailureAnalyzer.Tests.ExceptionExtractionTests.RunAllTests();
            return;
        }

        // ── Screenshot Test mode: --test-screenshots ────────────────────────
        if (opts.TestScreenshots)
        {
            var mockTypes = opts.MockTypes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                          .Select(t => t.Trim().ToLowerInvariant())
                                          .ToArray();

            await FailureAnalyzer.Commands.ScreenshotTestCommand.RunAsync(
                config, 
                mockTypes, 
                opts.ScreenshotOutput, 
                opts.VisionProvider
            );
            return;
        }

        // ── Analyze Real Screenshot mode: --analyze-image ────────────────────────
        if (!string.IsNullOrWhiteSpace(opts.AnalyzeImage))
        {
            await FailureAnalyzer.Commands.ScreenshotTestCommand.AnalyzeRealScreenshotAsync(
                config,
                opts.AnalyzeImage,
                opts.VisionProvider
            );
            return;
        }

        // ── Screenshot Inventory mode: --inventory-screenshots ────────────────────────
        if (opts.InventoryScreenshots)
        {
            if (string.IsNullOrWhiteSpace(opts.TrxPath))
            {
                Console.WriteLine("  ❌ --inventory-screenshots requires --trx to be specified.");
                Environment.Exit(1);
            }

            var inventoryTrxPath = ResolvePath(opts.TrxPath);
            await FailureAnalyzer.Commands.ScreenshotTestCommand.InventoryScreenshotsAsync(inventoryTrxPath);
            return;
        }

        // ── Diagnostic mode: --rag-query "some text" ────────────────────────
        // Sanity-checks retrieval in isolation, without needing a TRX file or running the
        // full pipeline. Point this at a real error message/method name from a failure
        // you're debugging and eyeball whether the top matches are actually relevant.
        if (!string.IsNullOrWhiteSpace(opts.RagQuery))
        {
            var diagAoai = config.GetSection("AzureOpenAI");
            bool useOllamaForDiag = opts.Ollama || opts.Gemini;

            var diagRag = new RagService(
                useOllamaForDiag ? "http://localhost:11434" : (diagAoai["Endpoint"] ?? ""),
                useOllamaForDiag ? "" : (diagAoai["ApiKey"] ?? ""),
                "vector_store.json",
                useOllamaForDiag ? "nomic-embed-text" : "text-embedding-3-small",
                useOllamaForDiag,
                opts.SourceDirectories
            );
            await diagRag.LoadAsync();

            var probe = new TestResult { ShortName = opts.RagQuery, ErrorMessage = opts.RagQuery, StackTrace = "" };
            var (result, _) = await diagRag.RetrieveContextAsync(probe);

            Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.WriteLine(string.IsNullOrWhiteSpace(result)
                ? "No context retrieved for this query — see [RAG] warnings above."
                : result);
            Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Environment.Exit(0);
        }

        if (string.IsNullOrWhiteSpace(opts.TrxPath))
        {
            Console.WriteLine("  ❌ --trx is required (unless using --rag-query for diagnostics).");
            Environment.Exit(1);
        }

        // ── 1. Parse TRX ──────────────────────────────────────────────────
        Console.WriteLine("▶ Step 1/4 — Parsing TRX file");
        var trxPath = ResolvePath(opts.TrxPath);
        var run = new TrxParser().Parse(trxPath);

        var failures = run.Results
            .Where(r => r.Outcome == "Failed")
            .Take(opts.MaxFailures)
            .ToList();
        var runDate = run.StartTime.Length >= 10 ? run.StartTime[..10] : null;

        if (failures.Count == 0)
        {
            Console.WriteLine("\n✅ No failures found — nothing to analyze!");
            Environment.Exit(0);
        }

        if (run.Failed > opts.MaxFailures)
            Console.WriteLine($"  ⚠ {run.Failed} failures — analyzing first {opts.MaxFailures} (use --max-failures to change)");

        // Display test categories if found
        var categoriesFound = failures.SelectMany(f => f.Categories).Distinct().ToList();
        if (categoriesFound.Any())
        {
            Console.WriteLine($"\n  🏷️  Test Categories Found: {string.Join(", ", categoriesFound)}");
        }

        // ── 2. Read logs ──────────────────────────────────────────────────
        Console.WriteLine("\n▶ Step 2/4 — Reading log files");
        var logReader = new LogReaderV2();
        var logDir = opts.LogDirectory ?? Path.GetDirectoryName(trxPath) ?? ".";

        // --- NEW RAG INITIALIZATION ---
        Console.WriteLine("\n▶ Initializing Knowledge Base (RAG)...");
        var ragAoai = config.GetSection("AzureOpenAI");

        // Use Ollama embeddings if: --ollama OR --gemini (since Gemini doesn't have embedding API)
        bool useOllamaEmbeddings = opts.Ollama || opts.Gemini;

        var rag = new RagService(
            useOllamaEmbeddings ? "http://localhost:11434" : (ragAoai["Endpoint"] ?? ""),
            useOllamaEmbeddings ? "" : (ragAoai["ApiKey"] ?? ""),
            "vector_store.json",
            useOllamaEmbeddings ? "nomic-embed-text" : "text-embedding-3-small",
            useOllamaEmbeddings,
            opts.SourceDirectories
        );

        // CallChainResolver reads your ACTUAL failing method (and its callers) straight off
        // disk using the stack trace — this is what gives the AI real code to fix, instead
        // of the loosely-related chunks RAG alone returns.
        CallChainResolver? chainResolver = null;

        if (opts.SourceDirectories != null && opts.SourceDirectories.Any())
        {
            // Use incremental indexing by default (only re-embeds changed files)
            // Force full re-index with --force-reindex flag
            if (opts.ForceReindex)
            {
                Console.WriteLine("  [CONFIG] --force-reindex specified, performing full re-index");
                await rag.IndexAsync(opts.SourceDirectories, force: true);
            }
            else
            {
                // Smart incremental: checks file hashes, only re-embeds new/changed files
                await rag.IndexIncrementalAsync(opts.SourceDirectories);
            }

            // Use first directory for call chain resolution
            chainResolver = new CallChainResolver(opts.SourceDirectories.First());
        }
        else
        {
            await rag.LoadAsync();
        }
        // ------------------------------

        // Builds the context string handed to the AI: real resolved source code first
        // (highest signal — exact failing method + call chain + AutomationId defs),
        // then semantic RAG chunks as supporting context, then any --context the user gave.
        async Task<(string? context, List<RetrievedChunk> chunks)> BuildEnrichedContextAsync(TestResult failure, string? logSnippet = null)
        {
            var parts = new List<string>();
            var retrievedChunks = new List<RetrievedChunk>();

            var chain = chainResolver?.Resolve(failure);
            if (chain != null && chain.Frames.Any(f => f.IsUserCode && f.SourceCode != null))
                parts.Add(chain.FormatForPrompt());

            var (ragContext, ragChunks) = await rag.RetrieveContextAsync(failure, logSnippet);
            if (!string.IsNullOrWhiteSpace(ragContext))
            {
                parts.Add(ragContext);
                retrievedChunks.AddRange(ragChunks);
            }

            if (opts.Context != null)
                parts.Add(opts.Context);

            return (parts.Any() ? string.Join("\n\n", parts) : null, retrievedChunks);
        }

        // Assembles an immutable evidence bundle for a single test failure.
        // This bundle is the single source of truth for all downstream analysis and reporting,
        // preventing run-to-run variance from re-gathering evidence.
        async Task<EvidenceBundle> AssembleEvidenceBundleAsync(
            TestResult failure,
            string testLog,
            string? appLog,
            List<ScreenshotAnalysis> screenshots,
            List<RetrievedChunk> sourceChunks)
        {
            var bundle = new EvidenceBundle
            {
                TestName = failure.TestName,
                ShortName = failure.ShortName,
                AssembledAt = DateTime.UtcNow,

                // TRX evidence
                ExceptionType = ExtractExceptionType(failure.ErrorMessage),
                ExceptionMessage = failure.ErrorMessage,
                StackTrace = failure.StackTrace,
                StartTime = failure.StartTime,
                EndTime = failure.EndTime,
                Duration = failure.Duration,
                TestCategories = failure.Categories,

                // Log evidence
                TestLog = testLog,
                ApplicationLog = appLog ?? "",
                HasApplicationLog = !string.IsNullOrWhiteSpace(appLog),

                // Screenshot evidence
                Screenshots = screenshots,
                HasScreenshots = screenshots.Any(),

                // Source code evidence
                SourceCodeChunks = sourceChunks,
                HasExactSymbolMatch = sourceChunks.Any(c => c.IsExactMatch),

                // Missing evidence detection
                MissingEvidence = DetermineMissingEvidence(testLog, appLog, screenshots, sourceChunks)
            };

            return bundle;
        }

        // Helper to extract exception type from error message
        string ExtractExceptionType(string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage)) return "";

            // Match patterns like "System.InvalidOperationException:" or "Assert.IsTrue failed"
            var match = System.Text.RegularExpressions.Regex.Match(
                errorMessage,
                @"^(?:Test method .+ threw exception:\s+)?([A-Za-z0-9_\.]+(?:Exception|Error|Failed))",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            return match.Success ? match.Groups[1].Value : "";
        }

        // Determine what evidence is missing
        List<string> DetermineMissingEvidence(string testLog, string? appLog, List<ScreenshotAnalysis> screenshots, List<RetrievedChunk> sourceChunks)
        {
            var missing = new List<string>();

            if (string.IsNullOrWhiteSpace(appLog))
                missing.Add("Application logs");

            if (!screenshots.Any())
                missing.Add("Screenshot at failure");

            if (!sourceChunks.Any(c => c.IsExactMatch))
                missing.Add("Exact crash-site source code match");

            // Note: Video is intentionally excluded from evidence gathering

            return missing;
        }


        // ── 3. AI analysis ────────────────────────────────────────────────
        List<FailureAnalysis> analyses;
        List<string> patterns;
        string envNotes;

        if (opts.Ollama)
        {
            Console.WriteLine("\n▶ Step 3/4 — Analyzing failures with [LOCAL OLLAMA] (Free & Private)");

            var ollamaSection = config.GetSection("Ollama");
            var modelName = ollamaSection["AnalysisModel"] ?? "llama3";
            var baseUrl = ollamaSection["BaseUrl"] ?? "http://localhost:11434";
            var maxOutputTokens = int.TryParse(ollamaSection["MaxOutputTokens"], out var ollamaMaxTokens) 
                ? ollamaMaxTokens : 4096;

            Console.WriteLine($"  Model: {modelName}");
            Console.WriteLine($"  Max output tokens: {maxOutputTokens:N0}");

            var ollama = new OllamaFailureAnalyzer(modelName, baseUrl, maxRetries: 3, maxOutputTokens);
            analyses = new List<FailureAnalysis>();

            // Initialize screenshot analyzer if needed
            FailureAnalyzer.Services.ScreenshotAnalyzer? screenshotAnalyzer = null;
            if (opts.AnalyzeScreenshots)
            {
                var visionProvider = config["Vision:Provider"] ?? "Gemini";
                screenshotAnalyzer = new FailureAnalyzer.Services.ScreenshotAnalyzer(config, visionProvider);
                Console.WriteLine($"  📸 Screenshot analysis enabled (provider: {visionProvider})");
            }

            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}/{failures.Count}] {failures[i].ShortName}");
                var log = logReader.ReadLogsForTestByTime(logDir, failures[i].ShortName, 
                    failures[i].StartTime, failures[i].EndTime, contextLines: 20);

                // Extract categories from logs and merge with TRX categories
                var logCategories = CategoryExtractor.ExtractCategoriesFromLogs(log);
                failures[i].Categories = failures[i].Categories.Union(logCategories).Distinct().ToList();

                var (enrichedContext, ragChunks) = await BuildEnrichedContextAsync(failures[i], log);

                // Find and analyze screenshots if enabled
                var screenshots = await FindAndAnalyzeScreenshotsAsync(
                    opts.ScreenshotOutput, 
                    failures[i],
                    screenshotAnalyzer);

                // Assemble immutable evidence bundle
                var evidenceBundle = await AssembleEvidenceBundleAsync(
                    failures[i],
                    log,
                    null,  // Ollama doesn't use separated evidence, so no app log
                    screenshots,
                    ragChunks);

                var analysis = await ollama.AnalyzeFailureAsync(failures[i], log, opts.Environment, enrichedContext);
                analysis.RetrievedChunks = ragChunks;
                analysis.Screenshots = screenshots;  // Attach screenshots to analysis
                analysis.Bundle = evidenceBundle;  // Attach evidence bundle

                // Apply evidence-tier-based confidence caps
                ApplyEvidenceTierConfidenceCaps(analysis);

                analyses.Add(analysis);
            }

            Console.WriteLine("\n  Detecting cross-cutting patterns...");
            (patterns, envNotes) = await ollama.DetectPatternsAsync(analyses, opts.Environment);
        }
        else if (opts.OpenAI)
        {
            Console.WriteLine("\n▶ Step 3/4 — Analyzing failures with [OPENAI API]");

            var apiKey = opts.OpenAIKey ?? config.GetSection("OpenAI")["ApiKey"] 
                ?? throw new Exception("OpenAI API key required. Use --openai-key or configure OpenAI:ApiKey in appsettings.json");
            var model = opts.OpenAIModel;
            var maxOutputTokens = int.TryParse(config.GetSection("OpenAI")["MaxOutputTokens"], out var openaiMaxTokens) 
                ? openaiMaxTokens : 16384;

            Console.WriteLine($"  Model: {model}");
            Console.WriteLine($"  Max output tokens: {maxOutputTokens:N0}");

            var openai = new OpenAIFailureAnalyzer(apiKey, model, maxRetries: 3, maxOutputTokens);
            analyses = new List<FailureAnalysis>();

            // Initialize screenshot analyzer if needed
            FailureAnalyzer.Services.ScreenshotAnalyzer? screenshotAnalyzer = null;
            if (opts.AnalyzeScreenshots && !opts.SkipScreenshotAnalysis)
            {
                var visionProvider = config["Vision:Provider"] ?? "Gemini";
                screenshotAnalyzer = new FailureAnalyzer.Services.ScreenshotAnalyzer(config, visionProvider);
                Console.WriteLine($"  📸 Screenshot analysis enabled (provider: {visionProvider})");
            }
            else
            {
                Console.WriteLine($"  ⏭️  Screenshot analysis disabled (use --analyze-screenshots to enable)");
            }

            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}/{failures.Count}] {failures[i].ShortName}");
                var log = logReader.ReadLogsForTestByTime(logDir, failures[i].ShortName, 
                    failures[i].StartTime, failures[i].EndTime, contextLines: 20);

                // Extract categories from logs and merge with TRX categories
                var logCategories = CategoryExtractor.ExtractCategoriesFromLogs(log);
                failures[i].Categories = failures[i].Categories.Union(logCategories).Distinct().ToList();

                var (enrichedContext, ragChunks) = await BuildEnrichedContextAsync(failures[i], log);

                // Find and analyze screenshots if enabled
                var screenshots = await FindAndAnalyzeScreenshotsAsync(
                    opts.ScreenshotOutput, 
                    failures[i],
                    screenshotAnalyzer);

                // Assemble immutable evidence bundle
                var evidenceBundle = await AssembleEvidenceBundleAsync(
                    failures[i],
                    log,
                    null,  // OpenAI doesn't use separated evidence, so no app log
                    screenshots,
                    ragChunks);

                var analysis = await openai.AnalyzeFailureAsync(failures[i], log, opts.Environment, enrichedContext);
                analysis.RetrievedChunks = ragChunks;
                analysis.Screenshots = screenshots;  // Attach screenshots to analysis
                analysis.Bundle = evidenceBundle;  // Attach evidence bundle

                // Apply evidence-tier-based confidence caps
                ApplyEvidenceTierConfidenceCaps(analysis);

                analyses.Add(analysis);
            }

            Console.WriteLine("\n  Detecting cross-cutting patterns...");
            (patterns, envNotes) = await openai.DetectPatternsAsync(analyses, opts.Environment);
        }
        else if (opts.Gemini)
        {
            Console.WriteLine("\n▶ Step 3/4 — Analyzing failures with [GOOGLE GEMINI] (FREE tier available!)");

            var apiKey = opts.GeminiKey ?? config.GetSection("Gemini")["ApiKey"] 
                ?? throw new Exception("Gemini API key required. Get free at https://aistudio.google.com/app/apikey - use --gemini-key");
            var model = opts.GeminiModel;
            var maxOutputTokens = int.TryParse(config.GetSection("Gemini")["MaxOutputTokens"], out var geminiMaxTokens) 
                ? geminiMaxTokens : 8192;

            Console.WriteLine($"  Model: {model}");
            Console.WriteLine($"  Max output tokens: {maxOutputTokens:N0}");
            Console.WriteLine($"  Get your FREE API key at: https://aistudio.google.com/app/apikey");

            var gemini = new GeminiFailureAnalyzer(apiKey, model, maxRetries: 3, maxOutputTokens);
            analyses = new List<FailureAnalysis>();

            // Initialize screenshot analyzer if needed
            FailureAnalyzer.Services.ScreenshotAnalyzer? screenshotAnalyzer = null;
            if (opts.AnalyzeScreenshots && !opts.SkipScreenshotAnalysis)
            {
                var visionProvider = config["Vision:Provider"] ?? "Gemini";
                screenshotAnalyzer = new FailureAnalyzer.Services.ScreenshotAnalyzer(config, visionProvider);
                Console.WriteLine($"  📸 Screenshot analysis enabled (provider: {visionProvider})");
            }
            else
            {
                Console.WriteLine($"  ⏭️  Screenshot analysis disabled (use --analyze-screenshots to enable)");
            }

            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}/{failures.Count}] {failures[i].ShortName}");

                // Use separated evidence for better classification
                var separatedEvidence = logReader.ReadLogsWithSeparatedEvidence(logDir, failures[i].ShortName,
                    failures[i].StartTime, failures[i].EndTime, contextLines: 20);

                // Extract categories from logs and merge with TRX categories
                var combinedLog = separatedEvidence.ApplicationEvidence + "\n" + separatedEvidence.TestEvidence;
                var logCategories = CategoryExtractor.ExtractCategoriesFromLogs(combinedLog);
                failures[i].Categories = failures[i].Categories.Union(logCategories).Distinct().ToList();

                // For RAG context, use the combined log text (we still need full log for code search)
                var (enrichedContext, ragChunks) = await BuildEnrichedContextAsync(failures[i], combinedLog);

                // Find and analyze screenshots if enabled
                var screenshots = await FindAndAnalyzeScreenshotsAsync(
                    opts.ScreenshotOutput, 
                    failures[i],
                    screenshotAnalyzer);

                // ══════════════════════════════════════════════════════════════
                // ASSEMBLE IMMUTABLE EVIDENCE BUNDLE
                // ══════════════════════════════════════════════════════════════
                var evidenceBundle = await AssembleEvidenceBundleAsync(
                    failures[i],
                    combinedLog,
                    separatedEvidence.HasActualApplicationLogFiles ? separatedEvidence.ApplicationEvidence : null,  // Only if we have real app logs
                    screenshots,
                    ragChunks);

                try
                {
                    var analysis = await gemini.AnalyzeFailureWithSeparatedEvidenceAsync(
                        failures[i], separatedEvidence, opts.Environment, enrichedContext, screenshots);
                    analysis.RetrievedChunks = ragChunks;
                    analysis.Screenshots = screenshots;  // Attach screenshots to analysis

                    // Attach immutable evidence bundle
                    analysis.Bundle = evidenceBundle;

                    // Apply evidence-tier-based confidence caps
                    ApplyEvidenceTierConfidenceCaps(analysis);

                    analyses.Add(analysis);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ Failed to analyze {failures[i].ShortName}: {ex.Message}");

                    // If quota is exceeded, stop processing remaining tests
                    if (ex.Message.Contains("quota") || ex.Message.Contains("Quota"))
                    {
                        Console.WriteLine($"  ⚠️  Quota exhausted. Generating report with {analyses.Count} successfully analyzed test(s)...");
                        break;
                    }

                    // For other errors, add a fallback analysis and continue
                    var fallback = FailureAnalysisParser.FallbackAnalysis(failures[i], $"Exception during analysis: {ex.Message}");
                    fallback.RetrievedChunks = ragChunks;
                    fallback.Bundle = evidenceBundle;  // Attach bundle even for fallback
                    analyses.Add(fallback);
                }
            }

            // Only attempt pattern detection if we have at least one successful analysis
            if (analyses.Count > 0)
            {
                Console.WriteLine("\n  Detecting cross-cutting patterns...");
                try
                {
                    (patterns, envNotes) = await gemini.DetectPatternsAsync(analyses, opts.Environment);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ⚠️  Pattern detection failed: {ex.Message}");
                    patterns = new List<string>();
                    envNotes = $"Pattern detection failed due to API error. {analyses.Count} failures were analyzed.";
                }
            }
            else
            {
                Console.WriteLine("  ⚠️  No analyses completed. Skipping pattern detection.");
                patterns = new List<string>();
                envNotes = "No analyses completed due to API errors.";
            }
        }
        else
        {
            Console.WriteLine("\n▶ Step 3/4 — Analyzing failures with [AZURE OPENAI]");

            var aoai = config.GetSection("AzureOpenAI");
            var endpoint = aoai["Endpoint"] ?? throw new Exception("AzureOpenAI:Endpoint not configured");
            var apiKey = aoai["ApiKey"] ?? throw new Exception("AzureOpenAI:ApiKey not configured");
            var deploy = aoai["DeploymentName"] ?? "gpt-4o";

            var ac = config.GetSection("Analyzer");
            int maxRetries = int.Parse(ac["MaxRetries"] ?? "3");
            int retryDelay = int.Parse(ac["RetryDelayMs"] ?? "1000");
            var maxOutputTokens = int.TryParse(aoai["MaxOutputTokens"], out var azureMaxTokens) 
                ? azureMaxTokens : 16384;

            Console.WriteLine($"  Deployment: {deploy}");
            Console.WriteLine($"  Max output tokens: {maxOutputTokens:N0}");

            var ai = new AzureOpenAIFailureAnalyzer(endpoint, apiKey, deploy, maxRetries, retryDelay, maxOutputTokens);
            analyses = new List<FailureAnalysis>();

            // Initialize screenshot analyzer if needed
            FailureAnalyzer.Services.ScreenshotAnalyzer? screenshotAnalyzer = null;
            if (opts.AnalyzeScreenshots)
            {
                var visionProvider = config["Vision:Provider"] ?? "Gemini";
                screenshotAnalyzer = new FailureAnalyzer.Services.ScreenshotAnalyzer(config, visionProvider);
                Console.WriteLine($"  📸 Screenshot analysis enabled (provider: {visionProvider})");
            }

            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}/{failures.Count}] {failures[i].ShortName}");
                var log = logReader.ReadLogsForTestByTime(logDir, failures[i].ShortName, 
                    failures[i].StartTime, failures[i].EndTime, contextLines: 20);

                // Extract categories from logs and merge with TRX categories
                var logCategories = CategoryExtractor.ExtractCategoriesFromLogs(log);
                failures[i].Categories = failures[i].Categories.Union(logCategories).Distinct().ToList();

                var (enrichedContext, ragChunks) = await BuildEnrichedContextAsync(failures[i], log);

                // Find and analyze screenshots if enabled
                var screenshots = await FindAndAnalyzeScreenshotsAsync(
                    opts.ScreenshotOutput, 
                    failures[i],
                    screenshotAnalyzer);

                // Assemble immutable evidence bundle
                var evidenceBundle = await AssembleEvidenceBundleAsync(
                    failures[i],
                    log,
                    null,  // Azure doesn't use separated evidence, so no app log
                    screenshots,
                    ragChunks);

                var analysis = await ai.AnalyzeFailureAsync(failures[i], log, opts.Environment, enrichedContext);
                analysis.RetrievedChunks = ragChunks;
                analysis.Screenshots = screenshots;  // Attach screenshots to analysis
                analysis.Bundle = evidenceBundle;  // Attach evidence bundle

                // Apply evidence-tier-based confidence caps
                ApplyEvidenceTierConfidenceCaps(analysis);

                analyses.Add(analysis);
                if (i < failures.Count - 1) await Task.Delay(200);
            }

            Console.WriteLine("\n  Detecting cross-cutting patterns...");
            (patterns, envNotes) = await ai.DetectPatternsAsync(analyses, opts.Environment);
        }

        // ── 4. Generate report ────────────────────────────────────────────
        Console.WriteLine("\n▶ Step 4/4 — Generating report");

        var runAnalysis = new RunAnalysis
        {
            Run = run,
            Failures = analyses,
            Patterns = patterns,
            EnvironmentNotes = envNotes,
            Environment = opts.Environment,
            ExtraContext = opts.Context
        };

        var generator = new HtmlReportGenerator();

        var htmlPath = opts.Output;
        File.WriteAllText(htmlPath, generator.Generate(runAnalysis));
        Console.WriteLine($"  ✅ HTML report : {Path.GetFullPath(htmlPath)}");

        var mdPath = Path.ChangeExtension(htmlPath, ".md");
        File.WriteAllText(mdPath, generator.GenerateMarkdown(runAnalysis));
        Console.WriteLine($"  ✅ Markdown    : {Path.GetFullPath(mdPath)}");

        // ── Console summary ───────────────────────────────────────────────
        Console.WriteLine($"\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine($"  {run.Failed} failed  |  {run.Passed} passed  |  {run.Skipped} skipped");

        var critical = analyses.Where(a => a.Severity == "critical").ToList();
        if (critical.Any())
        {
            Console.WriteLine($"\n  🔴 Critical ({critical.Count}):");
            foreach (var c in critical)
                Console.WriteLine($"     • {c.ShortName} [{c.Category}]");
        }

        if (patterns.Any())
        {
            Console.WriteLine("\n  Patterns:");
            foreach (var p in patterns)
                Console.WriteLine($"     → {p}");
        }

        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        if (opts.FailOnCritical && critical.Any())
        {
            Console.WriteLine("  Exiting with code 2 (critical failures found)");
            Environment.Exit(2);
        }
    });

static string ResolvePath(string pattern)
{
    if (File.Exists(pattern)) return pattern;
    var dir = Path.GetDirectoryName(pattern) ?? ".";
    var fileName = Path.GetFileName(pattern);
    if (fileName.Contains('*'))
    {
        var found = Directory.GetFiles(dir, fileName).FirstOrDefault();
        if (found != null) return found;
    }
    throw new FileNotFoundException($"TRX not found: {pattern}");
}

static string? FindVideoForTest(string videoDirectory, string testShortName)
{
    if (string.IsNullOrEmpty(videoDirectory) || !Directory.Exists(videoDirectory))
        return null;

    return Directory.GetFiles(videoDirectory, "*.mp4")
        .FirstOrDefault(f => Path.GetFileName(f)
            .StartsWith(testShortName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Find and analyze screenshots for a given test
/// </summary>
static async Task<List<ScreenshotAnalysis>> FindAndAnalyzeScreenshotsAsync(
    string screenshotDirectory, 
    TestResult testFailure,
    FailureAnalyzer.Services.ScreenshotAnalyzer? screenshotAnalyzer)
{
    var screenshots = new List<ScreenshotAnalysis>();

    if (string.IsNullOrEmpty(screenshotDirectory) || !Directory.Exists(screenshotDirectory))
        return screenshots;

    if (screenshotAnalyzer == null)
        return screenshots;

    // Find screenshots matching pattern: Testcase_{TestName}_*.png
    var pattern = $"Testcase_{testFailure.ShortName}_*.png";
    var files = Directory.GetFiles(screenshotDirectory, pattern);

    foreach (var file in files)
    {
        try
        {
            Console.WriteLine($"      📸 Analyzing screenshot: {Path.GetFileName(file)}");
            var analysis = await screenshotAnalyzer.AnalyzeScreenshotAsync(
                file,
                testFailure.ShortName,
                testFailure.ErrorMessage ?? "Test failed",
                testFailure.StackTrace);
            if (analysis != null)
            {
                screenshots.Add(analysis);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      ⚠️  Failed to analyze {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    return screenshots;
}

/// <summary>
/// Apply evidence-tier-based confidence caps to hypotheses
/// </summary>
static void ApplyEvidenceTierConfidenceCaps(FailureAnalysis analysis)
{
    // Define confidence caps based on ACTUAL evidence availability from the bundle
    // Use centralized EvidenceValidator as single source of truth
    int maxConfidence = 100;
    var capReasons = new List<string>();

    // Check the actual evidence bundle (single source of truth)
    if (analysis.Bundle == null)
    {
        // No bundle means no evidence-based analysis happened - this shouldn't occur in normal flow
        maxConfidence = 50;
        capReasons.Add("no evidence bundle available");
    }
    else
    {
        // Use EvidenceValidator to get consistent evidence summary
        var evidenceSummary = EvidenceValidator.GetSummary(analysis.Bundle);

        // No screenshots → cap at 80
        if (!evidenceSummary.HasScreenshots)
        {
            maxConfidence = Math.Min(maxConfidence, 80);
            capReasons.Add("no UI screenshots");
        }

        // No exact code match → cap at 70
        if (!evidenceSummary.HasExactSymbolMatch)
        {
            maxConfidence = Math.Min(maxConfidence, 70);
            capReasons.Add("no exact code match");
        }

        // No quoted dialog text → cap at 65
        if (!evidenceSummary.HasQuotedErrorText)
        {
            maxConfidence = Math.Min(maxConfidence, 65);
            capReasons.Add("no quoted error dialog text");
        }
    }

    // Inference-based only (contains "likely", "probably", "may", "might") → cap at 60
    // This checks the LLM output quality, not evidence availability
    bool isInferenceOnly = analysis.Hypotheses.Any(h =>
        h.Explanation.Contains("likely", StringComparison.OrdinalIgnoreCase) ||
        h.Explanation.Contains("probably", StringComparison.OrdinalIgnoreCase) ||
        h.Explanation.Contains("may", StringComparison.OrdinalIgnoreCase) ||
        h.Explanation.Contains("might", StringComparison.OrdinalIgnoreCase) ||
        h.Explanation.Contains("infer", StringComparison.OrdinalIgnoreCase));

    if (isInferenceOnly)
    {
        maxConfidence = Math.Min(maxConfidence, 60);
        capReasons.Add("inference-based reasoning");
    }

    var capReasonText = capReasons.Any() ? $"missing: {string.Join(", ", capReasons)}" : "";

    // Apply cap to all hypotheses
    foreach (var hypothesis in analysis.Hypotheses)
    {
        if (hypothesis.Confidence > maxConfidence)
        {
            Console.WriteLine($"  [Confidence Cap] Hypothesis confidence reduced from {hypothesis.Confidence}% to {maxConfidence}% due to evidence limitations");
            hypothesis.OriginalConfidence = hypothesis.Confidence;
            hypothesis.ConfidenceCapReason = capReasonText;
            hypothesis.Confidence = maxConfidence;
        }
    }

    // Update overall confidence if it exceeds the cap
    if (!string.IsNullOrEmpty(analysis.OverallConfidence))
    {
        string cappedOverallConfidence = maxConfidence switch
        {
            >= 80 => "high",
            >= 60 => "medium",
            _ => "low"
        };

        if (analysis.OverallConfidence == "high" && maxConfidence < 80)
        {
            Console.WriteLine($"  [Confidence Cap] Overall confidence reduced from high to {cappedOverallConfidence} due to evidence limitations");
            analysis.OverallConfidence = cappedOverallConfidence;
        }
    }
}

