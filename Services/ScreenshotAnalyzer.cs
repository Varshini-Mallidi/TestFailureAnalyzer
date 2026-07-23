using System.Text;
using Azure;
using Azure.AI.OpenAI;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;

namespace FailureAnalyzer.Services;

/// <summary>
/// Analyzes test failure screenshots using vision-capable AI models.
/// Supports Azure OpenAI (GPT-4 Vision, GPT-4o) and Google Gemini Vision.
/// </summary>
public class ScreenshotAnalyzer
{
    private readonly string _provider;
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;

    public ScreenshotAnalyzer(IConfiguration config, string provider = "Gemini")
    {
        _config = config;
        _provider = provider;
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Analyze a screenshot to extract visible UI elements, errors, and relevance to test failure.
    /// </summary>
    public async Task<Models.ScreenshotAnalysis> AnalyzeScreenshotAsync(
        string screenshotPath,
        string testName,
        string errorMessage,
        string? stackTrace = null)
    {
        if (!File.Exists(screenshotPath))
        {
            Console.WriteLine($"  [Screenshot] ⚠️ File not found: {screenshotPath}");
            return new Models.ScreenshotAnalysis
            {
                ScreenshotPath = screenshotPath,
                Description = "Screenshot file not found on disk",
                RelevanceToFailure = "Cannot analyze - file missing",
                ConfidenceScore = 0
            };
        }

        try
        {
            Console.WriteLine($"  [Screenshot] Analyzing with {_provider}: {Path.GetFileName(screenshotPath)}");

            return _provider.ToLowerInvariant() switch
            {
                "gemini" => await AnalyzeWithGeminiAsync(screenshotPath, testName, errorMessage, stackTrace),
                "azure" or "azureopenai" => await AnalyzeWithAzureOpenAIAsync(screenshotPath, testName, errorMessage, stackTrace),
                "openai" => await AnalyzeWithOpenAIAsync(screenshotPath, testName, errorMessage, stackTrace),
                "ollama" => await AnalyzeWithOllamaAsync(screenshotPath, testName, errorMessage, stackTrace),
                _ => throw new ArgumentException($"Unsupported vision provider: {_provider}")
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Screenshot] ❌ Analysis failed: {ex.Message}");
            return new Models.ScreenshotAnalysis
            {
                ScreenshotPath = screenshotPath,
                Description = $"Analysis failed: {ex.Message}",
                RelevanceToFailure = "Error during analysis",
                ConfidenceScore = 0
            };
        }
    }

    private async Task<Models.ScreenshotAnalysis> AnalyzeWithGeminiAsync(
        string screenshotPath,
        string testName,
        string errorMessage,
        string? stackTrace)
    {
        var apiKey = _config["Gemini:ApiKey"] 
            ?? throw new InvalidOperationException("Gemini:ApiKey not configured in appsettings.json");

        // Read and encode image
        var imageBytes = await File.ReadAllBytesAsync(screenshotPath);
        var base64Image = Convert.ToBase64String(imageBytes);

        var prompt = BuildVisionPrompt(testName, errorMessage, stackTrace);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = GetMimeType(screenshotPath),
                                data = base64Image
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.3,
                maxOutputTokens = 4000
            }
        };

        // Use gemini-3.5-flash (latest available multimodal model)
        var url = "https://generativelanguage.googleapis.com/v1/models/gemini-3.5-flash:generateContent?key=" + apiKey;

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Gemini API error: {response.StatusCode} - {responseText}");
        }

        var data = JObject.Parse(responseText);
        var aiText = data["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.Value<string>() ?? "{}";

        return ParseVisionResponse(aiText, screenshotPath);
    }

    private async Task<Models.ScreenshotAnalysis> AnalyzeWithAzureOpenAIAsync(
        string screenshotPath,
        string testName,
        string errorMessage,
        string? stackTrace)
    {
        var endpoint = _config["AzureOpenAI:Endpoint"] 
            ?? throw new InvalidOperationException("AzureOpenAI:Endpoint not configured");
        var apiKey = _config["AzureOpenAI:ApiKey"] 
            ?? throw new InvalidOperationException("AzureOpenAI:ApiKey not configured");
        var deploymentName = _config["Vision:AzureDeploymentName"] ?? "gpt-4o";

        var client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

        // Read and encode image
        var imageBytes = await File.ReadAllBytesAsync(screenshotPath);
        var base64Image = Convert.ToBase64String(imageBytes);

        var prompt = BuildVisionPrompt(testName, errorMessage, stackTrace);

        var chatOptions = new ChatCompletionsOptions
        {
            DeploymentName = deploymentName,
            Messages =
            {
                new ChatRequestSystemMessage("You are a test automation expert analyzing UI test failure screenshots. Always respond with valid JSON."),
                new ChatRequestUserMessage(
                    new ChatMessageContentItem[]
                    {
                        new ChatMessageTextContentItem(prompt),
                        new ChatMessageImageContentItem(
                            new Uri($"data:image/png;base64,{base64Image}"),
                            ChatMessageImageDetailLevel.High
                        )
                    }
                )
            },
            MaxTokens = 2000,
            Temperature = 0.3f,
            ResponseFormat = ChatCompletionsResponseFormat.JsonObject
        };

        var response = await client.GetChatCompletionsAsync(chatOptions);
        var aiText = response.Value.Choices[0].Message.Content;

        return ParseVisionResponse(aiText, screenshotPath);
    }

    private async Task<Models.ScreenshotAnalysis> AnalyzeWithOpenAIAsync(
        string screenshotPath,
        string testName,
        string errorMessage,
        string? stackTrace)
    {
        var apiKey = _config["OpenAI:ApiKey"] 
            ?? throw new InvalidOperationException("OpenAI:ApiKey not configured");

        // Read and encode image
        var imageBytes = await File.ReadAllBytesAsync(screenshotPath);
        var base64Image = Convert.ToBase64String(imageBytes);

        var prompt = BuildVisionPrompt(testName, errorMessage, stackTrace);

        // Build request using OpenAI's vision API format (explicit JObject for complex structure)
        var requestBody = new JObject
        {
            ["model"] = "gpt-4o",
            ["messages"] = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] = "You are a test automation expert analyzing UI test failure screenshots. Always respond with valid JSON."
                },
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = prompt
                        },
                        new JObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JObject
                            {
                                ["url"] = $"data:image/png;base64,{base64Image}",
                                ["detail"] = "high"
                            }
                        }
                    }
                }
            },
            ["max_tokens"] = 2000,
            ["temperature"] = 0.3,
            ["response_format"] = new JObject { ["type"] = "json_object" }
        };

        var json = requestBody.ToString();
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var response = await _httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"OpenAI API error: {response.StatusCode} - {responseText}");
        }

        var data = JObject.Parse(responseText);
        var aiText = data["choices"]?[0]?["message"]?["content"]?.Value<string>() ?? "{}";

        return ParseVisionResponse(aiText, screenshotPath);
    }

    private async Task<Models.ScreenshotAnalysis> AnalyzeWithOllamaAsync(
        string screenshotPath,
        string testName,
        string errorMessage,
        string? stackTrace)
    {
        var baseUrl = _config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var model = _config["Vision:OllamaModel"] ?? "llava";  // Default vision model

        // Read and encode image
        var imageBytes = await File.ReadAllBytesAsync(screenshotPath);
        var base64Image = Convert.ToBase64String(imageBytes);

        var prompt = BuildVisionPrompt(testName, errorMessage, stackTrace);

        // Ollama vision API format
        var requestBody = new JObject
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["images"] = new JArray { base64Image },
            ["stream"] = false,
            ["format"] = "json",
            ["options"] = new JObject
            {
                ["temperature"] = 0.3,
                ["num_predict"] = 2000
            }
        };

        var json = requestBody.ToString();
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var url = $"{baseUrl}/api/generate";

        Console.WriteLine($"  [Screenshot] Using Ollama model: {model}");

        var response = await _httpClient.PostAsync(url, content);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Ollama API error: {response.StatusCode} - {responseText}");
        }

        var data = JObject.Parse(responseText);
        var aiText = data["response"]?.Value<string>() ?? "{}";

        return ParseVisionResponse(aiText, screenshotPath);
    }

    private string BuildVisionPrompt(string testName, string errorMessage, string? stackTrace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing a screenshot from a failed automated UI test.");
        sb.AppendLine();
        sb.AppendLine($"Test Name: {testName}");
        sb.AppendLine($"Error Message: {errorMessage}");

        if (!string.IsNullOrWhiteSpace(stackTrace))
        {
            var shortStack = string.Join("\n", stackTrace.Split('\n').Take(3));
            sb.AppendLine($"Stack Trace: {shortStack}");
        }

        sb.AppendLine();
        sb.AppendLine("Analyze the screenshot and provide:");
        sb.AppendLine("1. What UI elements are visible (buttons, text fields, dialogs, etc.)");
        sb.AppendLine("2. Any error dialogs, messages, or warnings visible in the screenshot");
        sb.AppendLine("3. Whether the expected element from the error message appears to be present or absent");
        sb.AppendLine("4. Any test category information visible (e.g., \"Integration\", \"UI\", \"Smoke\", \"Regression\")");
        sb.AppendLine("5. How this screenshot helps diagnose the test failure");
        sb.AppendLine("6. Your confidence (0-100) in the analysis");
        sb.AppendLine();
        sb.AppendLine("Return ONLY valid JSON in this exact format:");
        sb.AppendLine("{");
        sb.AppendLine("  \"description\": \"<Clear description of what you see in the screenshot>\",");
        sb.AppendLine("  \"observed_elements\": [\"element1\", \"element2\", \"element3\"],");
        sb.AppendLine("  \"errors_visible\": [\"error dialog text if any\", \"warning messages\"],");
        sb.AppendLine("  \"categories_visible\": [\"category1\", \"category2\"],");
        sb.AppendLine("  \"relevance_to_failure\": \"<How this screenshot helps diagnose the failure>\",");
        sb.AppendLine("  \"confidence_score\": <0-100>");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private Models.ScreenshotAnalysis ParseVisionResponse(string jsonResponse, string screenshotPath)
    {
        try
        {
            // Clean up potential markdown code blocks
            jsonResponse = jsonResponse.Trim();
            if (jsonResponse.StartsWith("```json"))
                jsonResponse = jsonResponse.Substring(7);
            if (jsonResponse.StartsWith("```"))
                jsonResponse = jsonResponse.Substring(3);
            if (jsonResponse.EndsWith("```"))
                jsonResponse = jsonResponse.Substring(0, jsonResponse.Length - 3);
            jsonResponse = jsonResponse.Trim();

            var data = JObject.Parse(jsonResponse);

            return new Models.ScreenshotAnalysis
            {
                ScreenshotPath = screenshotPath,
                Description = data["description"]?.Value<string>() ?? "No description provided",
                ObservedElements = data["observed_elements"]?.ToObject<List<string>>() ?? new List<string>(),
                ErrorsVisible = data["errors_visible"]?.ToObject<List<string>>() ?? new List<string>(),
                CategoriesVisible = data["categories_visible"]?.ToObject<List<string>>() ?? new List<string>(),
                RelevanceToFailure = data["relevance_to_failure"]?.Value<string>() ?? "Unknown relevance",
                ConfidenceScore = data["confidence_score"]?.Value<int>() ?? 50
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [Screenshot] ⚠️ Failed to parse vision response: {ex.Message}");

            // Try to extract useful info from malformed JSON
            string description = "Failed to parse AI response";
            string relevance = jsonResponse;

            try
            {
                // Try to extract description even from incomplete JSON
                var descMatch = System.Text.RegularExpressions.Regex.Match(jsonResponse, @"""description"":\s*""([^""]+)""");
                if (descMatch.Success)
                {
                    description = descMatch.Groups[1].Value;
                }

                // Show the full raw response for debugging
                Console.WriteLine($"  [Screenshot] Full raw response:\n{jsonResponse}");
            }
            catch { }

            return new Models.ScreenshotAnalysis
            {
                ScreenshotPath = screenshotPath,
                Description = description,
                RelevanceToFailure = relevance.Substring(0, Math.Min(1000, relevance.Length)),
                ConfidenceScore = 0
            };
        }
    }

    private string GetMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }
}
