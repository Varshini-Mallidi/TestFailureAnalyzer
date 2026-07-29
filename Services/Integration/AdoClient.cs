using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace FailureAnalyzer.Services;

/// <summary>
/// Azure DevOps REST API client for fetching test runs, results, and attachments.
/// Authenticates using Personal Access Token (PAT).
/// </summary>
public class AdoClient
{
    private readonly HttpClient _httpClient;
    private readonly string _organizationUrl;
    private readonly string _projectName;

    public AdoClient(string organizationUrl, string projectName, string personalAccessToken)
    {
        _organizationUrl = organizationUrl.TrimEnd('/');
        _projectName = projectName;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30) // Increase timeout for large artifacts (99GB TRX files!)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // ADO uses Basic auth with PAT as username, empty password
        var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{personalAccessToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
    }

    /// <summary>
    /// Get latest test runs, optionally filtered by build ID or pipeline ID.
    /// </summary>
    public async Task<List<JObject>> GetTestRunsAsync(int? buildId = null, int? pipelineId = null, int top = 10)
    {
        var url = $"{_organizationUrl}/{_projectName}/_apis/test/runs?api-version=7.1&$top={top}";

        if (buildId.HasValue)
            url += $"&buildIds={buildId.Value}";

        Console.WriteLine($"[ADO] Fetching test runs from: {_projectName}");

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var data = JObject.Parse(json);
        var runs = data["value"]?.ToObject<List<JObject>>() ?? new List<JObject>();

        // If pipelineId specified, filter client-side (ADO API doesn't have direct pipeline filter)
        if (pipelineId.HasValue)
        {
            runs = runs.Where(r =>
            {
                var buildDef = r["buildConfiguration"]?["id"]?.Value<int>();
                return buildDef == pipelineId.Value;
            }).ToList();
        }

        Console.WriteLine($"[ADO] Found {runs.Count} test run(s)");
        return runs;
    }

    /// <summary>
    /// Get test results for a specific test run.
    /// </summary>
    public async Task<List<JObject>> GetTestResultsAsync(int runId)
    {
        var url = $"{_organizationUrl}/{_projectName}/_apis/test/runs/{runId}/results?api-version=7.1";

        Console.WriteLine($"[ADO] Fetching test results for run {runId}");

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var data = JObject.Parse(json);
        return data["value"]?.ToObject<List<JObject>>() ?? new List<JObject>();
    }

    /// <summary>
    /// Download test run attachments (logs, TRX files, screenshots, etc.)
    /// </summary>
    public async Task<List<string>> DownloadTestRunAttachmentsAsync(int runId, string downloadDirectory)
    {
        var url = $"{_organizationUrl}/{_projectName}/_apis/test/runs/{runId}/attachments?api-version=7.1";

        Console.WriteLine($"[ADO] Fetching attachments for test run {runId}");

        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[ADO] No attachments found for run {runId} (or access denied)");
            return new List<string>();
        }

        var json = await response.Content.ReadAsStringAsync();
        var data = JObject.Parse(json);
        var attachments = data["value"]?.ToObject<List<JObject>>() ?? new List<JObject>();

        Console.WriteLine($"[ADO] Found {attachments.Count} attachment(s)");

        Directory.CreateDirectory(downloadDirectory);
        var downloadedFiles = new List<string>();

        foreach (var attachment in attachments)
        {
            var attachmentId = attachment["id"]?.Value<int>();
            var fileName = attachment["fileName"]?.Value<string>() ?? $"attachment_{attachmentId}.dat";

            if (attachmentId.HasValue)
            {
                var filePath = await DownloadAttachmentAsync(runId, attachmentId.Value, fileName, downloadDirectory);
                if (filePath != null)
                    downloadedFiles.Add(filePath);
            }
        }

        return downloadedFiles;
    }

    /// <summary>
    /// Download a specific attachment by ID.
    /// </summary>
    private async Task<string?> DownloadAttachmentAsync(int runId, int attachmentId, string fileName, string downloadDirectory)
    {
        try
        {
            var url = $"{_organizationUrl}/{_projectName}/_apis/test/runs/{runId}/attachments/{attachmentId}?api-version=7.1";

            Console.WriteLine($"[ADO] Downloading: {fileName}");

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsByteArrayAsync();
            var filePath = Path.Combine(downloadDirectory, fileName);

            await File.WriteAllBytesAsync(filePath, content);
            Console.WriteLine($"[ADO] ✓ Saved: {filePath}");

            return filePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ADO] ✗ Failed to download {fileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get build artifacts (alternative source for logs and TRX files).
    /// </summary>
    public async Task<List<JObject>> GetBuildArtifactsAsync(int buildId)
    {
        var url = $"{_organizationUrl}/{_projectName}/_apis/build/builds/{buildId}/artifacts?api-version=7.1";

        Console.WriteLine($"[ADO] Fetching build artifacts for build {buildId}");

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var data = JObject.Parse(json);
        return data["value"]?.ToObject<List<JObject>>() ?? new List<JObject>();
    }

    /// <summary>
    /// Download a build artifact by name.
    /// </summary>
    public async Task<string?> DownloadBuildArtifactAsync(int buildId, string artifactName, string downloadDirectory)
    {
        try
        {
            // Get artifact download URL
            var artifactsUrl = $"{_organizationUrl}/{_projectName}/_apis/build/builds/{buildId}/artifacts?artifactName={Uri.EscapeDataString(artifactName)}&api-version=7.1";
            var artifactsResponse = await _httpClient.GetAsync(artifactsUrl);
            artifactsResponse.EnsureSuccessStatusCode();

            var artifactsJson = await artifactsResponse.Content.ReadAsStringAsync();
            var artifact = JObject.Parse(artifactsJson);

            // Check if this is a single artifact response (not a 'value' array)
            var downloadUrl = artifact["resource"]?["downloadUrl"]?.Value<string>();

            if (string.IsNullOrEmpty(downloadUrl))
            {
                Console.WriteLine($"[ADO] Artifact '{artifactName}' not found in build {buildId}");
                return null;
            }

            return await DownloadAndExtractArtifactAsync(downloadUrl, artifactName, downloadDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ADO] ✗ Failed to download artifact '{artifactName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download and extract an artifact from a URL.
    /// </summary>
    private async Task<string?> DownloadAndExtractArtifactAsync(string downloadUrl, string artifactName, string downloadDirectory)
    {
        Console.WriteLine($"[ADO] Downloading artifact: {artifactName}");
        Console.WriteLine($"[ADO] This may take several minutes for large artifacts...");

        using var downloadResponse = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        downloadResponse.EnsureSuccessStatusCode();

        var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
        Console.WriteLine($"[ADO] Size: {totalBytes / (1024.0 * 1024.0 * 1024.0):F1} GB");

        Directory.CreateDirectory(downloadDirectory);
        var zipPath = Path.Combine(downloadDirectory, $"{artifactName}.zip");

        using (var contentStream = await downloadResponse.Content.ReadAsStreamAsync())
        using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
        {
            var buffer = new byte[8192];
            var totalRead = 0L;
            int read;
            var lastProgressPercent = 0;

            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read);
                totalRead += read;

                if (totalBytes > 0)
                {
                    var progressPercent = (int)((totalRead * 100) / totalBytes);
                    if (progressPercent >= lastProgressPercent + 10) // Report every 10%
                    {
                        Console.WriteLine($"[ADO] Progress: {progressPercent}% ({totalRead / (1024.0 * 1024.0 * 1024.0):F1} GB / {totalBytes / (1024.0 * 1024.0 * 1024.0):F1} GB)");
                        lastProgressPercent = progressPercent;
                    }
                }
            }
        }

        Console.WriteLine($"[ADO] ✓ Downloaded: {zipPath}");

        // Extract zip
        Console.WriteLine($"[ADO] Extracting... (this may take a few minutes for large files)");
        var extractPath = Path.Combine(downloadDirectory, artifactName);
        if (Directory.Exists(extractPath))
            Directory.Delete(extractPath, true);

        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractPath);
        Console.WriteLine($"[ADO] ✓ Extracted to: {extractPath}");

        return extractPath;
    }

    /// <summary>
    /// Get latest build for a pipeline.
    /// </summary>
    public async Task<JObject?> GetLatestBuildAsync(int pipelineId, string? branchName = null)
    {
        var url = $"{_organizationUrl}/{_projectName}/_apis/build/builds?definitions={pipelineId}&$top=1&api-version=7.1";

        if (!string.IsNullOrEmpty(branchName))
            url += $"&branchName={branchName}";

        Console.WriteLine($"[ADO] Fetching latest build for pipeline {pipelineId}");

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var data = JObject.Parse(json);
        var builds = data["value"]?.ToObject<List<JObject>>() ?? new List<JObject>();

        return builds.FirstOrDefault();
    }
}
