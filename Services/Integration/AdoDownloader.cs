using Newtonsoft.Json.Linq;

namespace FailureAnalyzer.Services;

/// <summary>
/// Orchestrates downloading TRX files and logs from Azure DevOps.
/// Handles finding the right test run, downloading files, and preparing paths for analysis.
/// </summary>
public class AdoDownloader
{
    private readonly AdoClient _client;
    private readonly string _tempDownloadPath;

    public AdoDownloader(AdoClient client, string tempDownloadPath = ".ado-downloads")
    {
        _client = client;
        _tempDownloadPath = tempDownloadPath;
    }

    /// <summary>
    /// Download files from the latest test run of a pipeline.
    /// </summary>
    public async Task<(string? trxPath, string? logsPath)> DownloadLatestAsync(int? pipelineId = null)
    {
        Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine("  Azure DevOps - Fetching Latest Test Run");
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        // Get latest build if pipeline specified
        int? buildId = null;
        if (pipelineId.HasValue)
        {
            var build = await _client.GetLatestBuildAsync(pipelineId.Value);
            if (build == null)
            {
                Console.WriteLine($"[ADO] ✗ No builds found for pipeline {pipelineId}");
                return (null, null);
            }

            buildId = build["id"]?.Value<int>();
            var buildNumber = build["buildNumber"]?.Value<string>();
            Console.WriteLine($"[ADO] Latest build: #{buildNumber} (ID: {buildId})");
        }

        // Get test runs
        var testRuns = await _client.GetTestRunsAsync(buildId, pipelineId, top: 5);
        if (testRuns.Count == 0)
        {
            Console.WriteLine("[ADO] ✗ No test runs found");
            return (null, null);
        }

        // Use the first (latest) run
        var latestRun = testRuns[0];
        var runId = latestRun["id"]?.Value<int>();
        var runName = latestRun["name"]?.Value<string>();

        if (!runId.HasValue)
        {
            Console.WriteLine("[ADO] ✗ Could not determine test run ID");
            return (null, null);
        }

        Console.WriteLine($"[ADO] Latest test run: {runName} (ID: {runId})");
        Console.WriteLine($"[ADO] Run details:");
        Console.WriteLine($"      Total Tests: {latestRun["totalTests"]}");
        Console.WriteLine($"      Passed: {latestRun["passedTests"]}");
        Console.WriteLine($"      Failed: {latestRun["failedTests"]}");
        Console.WriteLine($"      Incomplete: {latestRun["incompleteTests"]}");

        return await DownloadTestRunFilesAsync(runId.Value, buildId);
    }

    /// <summary>
    /// Download files from a specific build ID.
    /// </summary>
    public async Task<(string? trxPath, string? logsPath)> DownloadFromBuildAsync(int buildId)
    {
        Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine($"  Azure DevOps - Fetching Build #{buildId}");
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        // Get test runs for this build
        var testRuns = await _client.GetTestRunsAsync(buildId);
        if (testRuns.Count == 0)
        {
            Console.WriteLine($"[ADO] ✗ No test runs found for build {buildId}");
            return (null, null);
        }

        var latestRun = testRuns[0];
        var runId = latestRun["id"]?.Value<int>();

        if (!runId.HasValue)
        {
            Console.WriteLine("[ADO] ✗ Could not determine test run ID");
            return (null, null);
        }

        return await DownloadTestRunFilesAsync(runId.Value, buildId);
    }

    /// <summary>
    /// Download TRX and logs for a specific test run.
    /// </summary>
    private async Task<(string? trxPath, string? logsPath)> DownloadTestRunFilesAsync(int runId, int? buildId)
    {
        // Create download directory
        var runDownloadDir = Path.Combine(_tempDownloadPath, $"run_{runId}");
        if (Directory.Exists(runDownloadDir))
            Directory.Delete(runDownloadDir, true);
        Directory.CreateDirectory(runDownloadDir);

        Console.WriteLine($"\n[ADO] Download directory: {Path.GetFullPath(runDownloadDir)}");

        // Download test run attachments (may include TRX)
        var attachments = await _client.DownloadTestRunAttachmentsAsync(runId, runDownloadDir);

        string? trxPath = null;
        string? logsPath = null;

        // Look for TRX file in attachments
        foreach (var file in attachments)
        {
            if (file.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            {
                trxPath = file;
                Console.WriteLine($"[ADO] ✓ TRX file: {Path.GetFileName(file)}");
            }
        }

        // If no TRX in attachments and we have a buildId, try build artifacts
        if (trxPath == null && buildId.HasValue)
        {
            Console.WriteLine("\n[ADO] TRX not found in test attachments, checking build artifacts...");

            var artifacts = await _client.GetBuildArtifactsAsync(buildId.Value);
            Console.WriteLine($"[ADO] Found {artifacts.Count} build artifact(s)");

            // PRIORITY 1: Look for optimized FailureAnalyzer artifact (small, fast)
            var failureAnalyzerArtifact = artifacts.FirstOrDefault(a =>
                a["name"]?.Value<string>()?.StartsWith("FailureAnalyzer", StringComparison.OrdinalIgnoreCase) == true);

            if (failureAnalyzerArtifact != null)
            {
                var artifactName = failureAnalyzerArtifact["name"]?.Value<string>();
                Console.WriteLine($"[ADO] ✓ Found optimized artifact: {artifactName} (fast download)");

                var extractedPath = await _client.DownloadBuildArtifactAsync(buildId.Value, artifactName!, runDownloadDir);

                if (extractedPath != null)
                {
                    trxPath = FindLargestTrxFile(extractedPath);
                    logsPath = FindOldestLogFolder(extractedPath);

                    if (trxPath != null)
                    {
                        Console.WriteLine($"[ADO] ✓ Using optimized artifact (downloaded in seconds)");
                    }
                }
            }

            // PRIORITY 2: Fall back to large test results artifact if optimized one not found
            if (trxPath == null)
            {
                Console.WriteLine("[ADO] ⚠ Optimized artifact not found, falling back to full test results artifact");
                Console.WriteLine("[ADO] ⚠ This may take 3-4 hours to download (~94 GB)");
                Console.WriteLine("[ADO] ℹ See ADO_PIPELINE_RECOMMENDATION.md for pipeline optimization guidance");

                foreach (var artifact in artifacts)
                {
                    var artifactName = artifact["name"]?.Value<string>();
                    Console.WriteLine($"[ADO] Artifact: {artifactName}");

                    // Common artifact names that might contain test results
                    if (artifactName != null && (
                        artifactName.Contains("TestResults", StringComparison.OrdinalIgnoreCase) ||
                        artifactName.Contains("Test_Results", StringComparison.OrdinalIgnoreCase) ||
                        artifactName.Contains("Administration-Regression", StringComparison.OrdinalIgnoreCase) ||
                        artifactName.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                        artifactName.Contains("logs", StringComparison.OrdinalIgnoreCase)))
                    {
                        var extractedPath = await _client.DownloadBuildArtifactAsync(buildId.Value, artifactName, runDownloadDir);

                        if (extractedPath != null)
                        {
                            trxPath = FindLargestTrxFile(extractedPath);
                            logsPath = FindOldestLogFolder(extractedPath);

                            // Fallback: generic log detection if above strategies fail
                            if (logsPath == null)
                            {
                                var logDirs = Directory.GetDirectories(extractedPath, "logs", SearchOption.AllDirectories);
                                if (logDirs.Length > 0)
                                {
                                    logsPath = logDirs[0];
                                    Console.WriteLine($"[ADO] ✓ Logs directory found: {logsPath}");
                                }
                                else
                                {
                                    var logFiles = Directory.GetFiles(extractedPath, "*.log", SearchOption.AllDirectories);
                                    if (logFiles.Length > 0)
                                    {
                                        logsPath = Path.GetDirectoryName(logFiles[0]) ?? extractedPath;
                                        Console.WriteLine($"[ADO] ✓ Log files found in: {logsPath}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Summary
        Console.WriteLine("\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine("  Download Summary");
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine($"  TRX File:  {(trxPath != null ? "✓ Found" : "✗ Not found")}");
        Console.WriteLine($"  Log Files: {(logsPath != null ? "✓ Found" : "✗ Not found")}");
        Console.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n");

        if (trxPath == null)
        {
            Console.WriteLine("⚠️  WARNING: No TRX file found. Analysis may not be possible.");
            Console.WriteLine("   Make sure test results are published to Azure DevOps.");
        }

        return (trxPath, logsPath);
    }

    /// <summary>
    /// Find the LARGEST TRX file in the directory tree (by file size).
    /// This helps identify the main test results file vs. smaller partial results.
    /// </summary>
    private string? FindLargestTrxFile(string rootPath)
    {
        try
        {
            var trxFiles = Directory.GetFiles(rootPath, "*.trx", SearchOption.AllDirectories);
            if (trxFiles.Length == 0)
                return null;

            var largest = trxFiles
                .Select(f => new FileInfo(f))
                .OrderByDescending(fi => fi.Length)
                .First();

            var sizeMB = largest.Length / (1024.0 * 1024.0);
            Console.WriteLine($"[ADO] ✓ TRX file found (largest): {largest.Name} ({sizeMB:F1} MB)");

            return largest.FullName;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ADO] Error finding TRX file: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Find the oldest date folder containing .txt log files.
    /// Looks for folder names that appear to be dates (e.g., "13JUNE26", "2026-07-11", etc.)
    /// </summary>
    private string? FindOldestLogFolder(string rootPath)
    {
        try
        {
            // Find all directories that contain .txt files
            var dirsWithTxtFiles = Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                .Where(dir => Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly).Length > 0)
                .ToList();

            if (dirsWithTxtFiles.Count == 0)
                return null;

            // Sort by directory creation time (oldest first)
            var oldestByCreationTime = dirsWithTxtFiles
                .Select(dir => new DirectoryInfo(dir))
                .OrderBy(di => di.CreationTime)
                .FirstOrDefault();

            if (oldestByCreationTime != null)
            {
                Console.WriteLine($"[ADO] Found {dirsWithTxtFiles.Count} log folder(s), selecting oldest: {oldestByCreationTime.Name} (created: {oldestByCreationTime.CreationTime:yyyy-MM-dd HH:mm})");
                return oldestByCreationTime.FullName;
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ADO] Error finding oldest log folder: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Export test results as TRX format (fallback if TRX not available as attachment).
    /// ADO stores results in database, this method reconstructs a minimal TRX.
    /// </summary>
    public async Task<string?> ExportTestResultsAsTrxAsync(int runId, string outputDirectory)
    {
        try
        {
            Console.WriteLine($"[ADO] Exporting test results to TRX format...");

            var results = await _client.GetTestResultsAsync(runId);
            if (results.Count == 0)
            {
                Console.WriteLine("[ADO] No test results to export");
                return null;
            }

            // Create minimal TRX XML
            var trxContent = GenerateTrxFromResults(results);

            Directory.CreateDirectory(outputDirectory);
            var trxPath = Path.Combine(outputDirectory, $"TestResults_Run{runId}.trx");

            await File.WriteAllTextAsync(trxPath, trxContent);
            Console.WriteLine($"[ADO] ✓ Exported TRX: {trxPath}");

            return trxPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ADO] ✗ Failed to export TRX: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Generate a minimal TRX XML from ADO test results.
    /// </summary>
    private string GenerateTrxFromResults(List<JObject> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">");
        sb.AppendLine("  <Results>");

        foreach (var result in results)
        {
            var testName = result["testCaseTitle"]?.Value<string>() ?? "Unknown";
            var outcome = result["outcome"]?.Value<string>() ?? "NotExecuted";
            var errorMessage = result["errorMessage"]?.Value<string>() ?? "";
            var stackTrace = result["stackTrace"]?.Value<string>() ?? "";
            var duration = result["durationInMs"]?.Value<int>() ?? 0;

            var executionId = Guid.NewGuid().ToString();
            var testId = Guid.NewGuid().ToString();

            sb.AppendLine($"    <UnitTestResult executionId=\"{executionId}\" testId=\"{testId}\" testName=\"{System.Security.SecurityElement.Escape(testName)}\" outcome=\"{outcome}\" duration=\"{TimeSpan.FromMilliseconds(duration)}\">");

            if (!string.IsNullOrEmpty(errorMessage) || !string.IsNullOrEmpty(stackTrace))
            {
                sb.AppendLine("      <Output>");
                sb.AppendLine("        <ErrorInfo>");
                sb.AppendLine($"          <Message>{System.Security.SecurityElement.Escape(errorMessage)}</Message>");
                if (!string.IsNullOrEmpty(stackTrace))
                    sb.AppendLine($"          <StackTrace>{System.Security.SecurityElement.Escape(stackTrace)}</StackTrace>");
                sb.AppendLine("        </ErrorInfo>");
                sb.AppendLine("      </Output>");
            }

            sb.AppendLine("    </UnitTestResult>");
        }

        sb.AppendLine("  </Results>");
        sb.AppendLine("</TestRun>");

        return sb.ToString();
    }
}
