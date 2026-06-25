using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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

        // ── 1. Parse TRX ──────────────────────────────────────────────────
        Console.WriteLine("▶ Step 1/4 — Parsing TRX file");
        var trxPath = ResolvePath(opts.TrxPath);
        var run = new TrxParser().Parse(trxPath);

        var failures = run.Results
            .Where(r => r.Outcome == "Failed")
            .Take(opts.MaxFailures)
            .ToList();

        if (failures.Count == 0)
        {
            Console.WriteLine("\n✅ No failures found — nothing to analyze!");
            Environment.Exit(0);
        }

        if (run.Failed > opts.MaxFailures)
            Console.WriteLine($"  ⚠ {run.Failed} failures — analyzing first {opts.MaxFailures} (use --max-failures to change)");

        // ── 2. Read logs ──────────────────────────────────────────────────
        Console.WriteLine("\n▶ Step 2/4 — Reading log files");
        var logReader = new LogReader();
        var logDir = opts.LogDirectory ?? Path.GetDirectoryName(trxPath) ?? ".";

        // --- NEW RAG INITIALIZATION ---
        Console.WriteLine("\n▶ Initializing Knowledge Base (RAG)...");
        var ragAoai = config.GetSection("AzureOpenAI");
        var rag = new RagService(
            opts.Ollama ? "http://localhost:11434" : (ragAoai["Endpoint"] ?? ""),
            opts.Ollama ? "" : (ragAoai["ApiKey"] ?? ""),
            "vector_store.json",
            opts.Ollama ? "nomic-embed-text" : "text-embedding-3-small",
            opts.Ollama
        );

        if (!string.IsNullOrWhiteSpace(opts.SourceDirectory))
        {
            await rag.IndexAsync(opts.SourceDirectory);
        }
        else
        {
            await rag.LoadAsync();
        }
        // ------------------------------

        // ── 3. AI analysis ────────────────────────────────────────────────
        List<FailureAnalysis> analyses;
        List<string> patterns;
        string envNotes;

        if (opts.Mock)
        {
            Console.WriteLine("\n▶ Step 3/4 — Analyzing failures [MOCK MODE — no API key needed]");
            Console.WriteLine("  Tip: remove --mock and configure appsettings.json to use real Azure OpenAI\n");

            var mock = new MockAnalyzer();
            analyses = new List<FailureAnalysis>();

            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}/{failures.Count}] {failures[i].ShortName}");
                var log = logReader.ReadLogsForTest(logDir, failures[i].ShortName);
                analyses.Add(await mock.AnalyzeFailureAsync(failures[i], log, opts.Environment, opts.Context));
            }

            Console.WriteLine("\n  Detecting cross-cutting patterns...");
            (patterns, envNotes) = await mock.DetectPatternsAsync(analyses, opts.Environment);
        }
        else if (opts.Ollama)
        {
            Console.WriteLine("\n▶ Step 3/4 — Analyzing failures with [LOCAL OLLAMA] (Free & Private)");

            var ollama = new OllamaAnalyzer("llama3");
            analyses = new List<FailureAnalysis>();

            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}/{failures.Count}] {failures[i].ShortName}");
                var log = logReader.ReadLogsForTest(logDir, failures[i].ShortName);

                // --- GET CONTEXT FROM RAG ---
                var ragContext = await rag.RetrieveContextAsync(failures[i]);
                var enrichedContext = string.IsNullOrWhiteSpace(ragContext) ? opts.Context :
                    $"{(opts.Context == null ? "" : opts.Context + "\n")}{ragContext}";
                // ----------------------------

                analyses.Add(await ollama.AnalyzeFailureAsync(failures[i], log, opts.Environment, enrichedContext));
            }

            Console.WriteLine("\n  Detecting cross-cutting patterns...");
            (patterns, envNotes) = await ollama.DetectPatternsAsync(analyses, opts.Environment);
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

            var ai = new AzureOpenAIAnalyzer(endpoint, apiKey, deploy, maxRetries, retryDelay);
            analyses = new List<FailureAnalysis>();

            for (int i = 0; i < failures.Count; i++)
            {
                Console.WriteLine($"  [{i + 1}/{failures.Count}] {failures[i].ShortName}");
                var log = logReader.ReadLogsForTest(logDir, failures[i].ShortName);

                // --- GET CONTEXT FROM RAG ---
                var ragContext = await rag.RetrieveContextAsync(failures[i]);
                var enrichedContext = string.IsNullOrWhiteSpace(ragContext) ? opts.Context :
                    $"{(opts.Context == null ? "" : opts.Context + "\n")}{ragContext}";
                // ----------------------------

                analyses.Add(await ai.AnalyzeFailureAsync(failures[i], log, opts.Environment, enrichedContext));
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