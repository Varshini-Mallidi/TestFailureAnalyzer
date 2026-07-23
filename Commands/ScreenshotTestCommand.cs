using FailureAnalyzer.Services;
using FailureAnalyzer.Utils;
using Microsoft.Extensions.Configuration;

namespace FailureAnalyzer.Commands;

/// <summary>
/// Command-line tool for testing screenshot analysis without needing real test failures.
/// </summary>
public static class ScreenshotTestCommand
{
    public static async Task AnalyzeRealScreenshotAsync(IConfiguration config, string screenshotPath, string? provider)
    {
        Console.WriteLine("=== REAL SCREENSHOT ANALYSIS ===\n");

        if (!File.Exists(screenshotPath))
        {
            Console.WriteLine($"❌ Screenshot not found: {screenshotPath}");
            return;
        }

        var fileInfo = new FileInfo(screenshotPath);
        Console.WriteLine($"📁 Screenshot: {Path.GetFileName(screenshotPath)}");
        Console.WriteLine($"📏 Size: {fileInfo.Length / 1024} KB");
        Console.WriteLine($"📅 Modified: {fileInfo.LastWriteTime}\n");

        var visionProvider = provider ?? config["Vision:Provider"] ?? "Gemini";
        Console.WriteLine($"🔍 Using Vision Provider: {visionProvider}\n");
        Console.WriteLine("─────────────────────────────────────────────────────────────\n");

        var analyzer = new ScreenshotAnalyzer(config, visionProvider);

        // Extract test name from filename
        var testName = Path.GetFileNameWithoutExtension(screenshotPath).Split('_')[0];

        var analysis = await analyzer.AnalyzeScreenshotAsync(
            screenshotPath,
            testName,
            "Database service test - analyzing test execution state",
            stackTrace: null
        );

        Console.WriteLine("📸 ANALYSIS RESULTS:");
        Console.WriteLine("═══════════════════════════════════════════════════════════\n");

        Console.WriteLine($"📝 Description:\n   {analysis.Description}\n");

        if (analysis.ObservedElements.Any())
        {
            Console.WriteLine($"🔍 Observed UI Elements:");
            foreach (var element in analysis.ObservedElements)
            {
                Console.WriteLine($"   • {element}");
            }
            Console.WriteLine();
        }

        if (analysis.ErrorsVisible.Any())
        {
            Console.WriteLine($"⚠️  Errors Visible:");
            foreach (var error in analysis.ErrorsVisible)
            {
                Console.WriteLine($"   • {error}");
            }
            Console.WriteLine();
        }

        if (analysis.CategoriesVisible.Any())
        {
            Console.WriteLine($"🏷️  Categories Detected:");
            foreach (var category in analysis.CategoriesVisible)
            {
                Console.WriteLine($"   • {category}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"💡 Relevance to Failure:\n   {analysis.RelevanceToFailure}\n");
        Console.WriteLine($"📊 Confidence Score: {analysis.ConfidenceScore}%\n");

        Console.WriteLine("═══════════════════════════════════════════════════════════");
    }

    public static async Task RunAsync(IConfiguration config, string[] mockTypes, string outputDir, string? provider)
    {
        Console.WriteLine("=== SCREENSHOT ANALYSIS TEST MODE ===\n");

        // Create output directory if it doesn't exist
        Directory.CreateDirectory(outputDir);
        Console.WriteLine($"Output Directory: {outputDir}\n");

        // Generate mock screenshots
        var mockScreenshots = new List<(string path, string testName, string error)>();

        if (mockTypes.Contains("error") || mockTypes.Contains("all"))
        {
            var path = Path.Combine(outputDir, "mock_error_dialog.png");
            MockScreenshotGenerator.CreateMockErrorScreenshot(
                path,
                "Element 'btnSubmit' was not found in the current context.\nApplication may not have loaded completely.",
                "LoginTests.TestSuccessfulLogin"
            );
            mockScreenshots.Add((path, "LoginTests.TestSuccessfulLogin", "Element 'btnSubmit' not found"));
        }

        if (mockTypes.Contains("timeout") || mockTypes.Contains("all"))
        {
            var path = Path.Combine(outputDir, "mock_timeout.png");
            MockScreenshotGenerator.CreateMockTimeoutScreenshot(
                path,
                "btnSubmit",
                "DashboardTests.TestDashboardLoad"
            );
            mockScreenshots.Add((path, "DashboardTests.TestDashboardLoad", "Timeout waiting for 'btnSubmit' to be visible"));
        }

        if (mockTypes.Contains("locator") || mockTypes.Contains("all"))
        {
            var path = Path.Combine(outputDir, "mock_locator_failure.png");
            MockScreenshotGenerator.CreateMockLocatorFailureScreenshot(
                path,
                "//button[@id='submit-final']",
                "CheckoutTests.TestCheckoutFlow"
            );
            mockScreenshots.Add((path, "CheckoutTests.TestCheckoutFlow", "Locator '//button[@id='submit-final']' not found"));
        }

        Console.WriteLine($"✅ Generated {mockScreenshots.Count} mock screenshot(s)\n");
        Console.WriteLine("─────────────────────────────────────────────────────────────\n");

        // Analyze each screenshot
        var visionProvider = provider ?? config["Vision:Provider"] ?? "Gemini";
        Console.WriteLine($"Using Vision Provider: {visionProvider}\n");

        var analyzer = new ScreenshotAnalyzer(config, visionProvider);

        foreach (var (path, testName, error) in mockScreenshots)
        {
            Console.WriteLine($"Analyzing: {Path.GetFileName(path)}");
            Console.WriteLine($"  Test: {testName}");
            Console.WriteLine($"  Error: {error}");

            var analysis = await analyzer.AnalyzeScreenshotAsync(path, testName, error);

            Console.WriteLine($"\n  📸 ANALYSIS RESULTS:");
            Console.WriteLine($"  Description: {analysis.Description}");

            if (analysis.ObservedElements.Any())
            {
                Console.WriteLine($"  Observed Elements: {string.Join(", ", analysis.ObservedElements)}");
            }

            if (analysis.ErrorsVisible.Any())
            {
                Console.WriteLine($"  ⚠️ Errors Visible: {string.Join(", ", analysis.ErrorsVisible)}");
            }

            if (analysis.CategoriesVisible.Any())
            {
                Console.WriteLine($"  🏷️  Categories: {string.Join(", ", analysis.CategoriesVisible)}");
            }

            Console.WriteLine($"  Relevance: {analysis.RelevanceToFailure}");

            var confidenceColor = analysis.ConfidenceScore >= 70 ? ConsoleColor.Green 
                                : analysis.ConfidenceScore >= 40 ? ConsoleColor.Yellow 
                                : ConsoleColor.Red;

            Console.ForegroundColor = confidenceColor;
            Console.WriteLine($"  Confidence: {analysis.ConfidenceScore}%");
            Console.ResetColor();

            Console.WriteLine("\n─────────────────────────────────────────────────────────────\n");
        }

        Console.WriteLine("=== TEST COMPLETE ===");
        Console.WriteLine($"\n💡 Next Steps:");
        Console.WriteLine($"  1. Review the generated screenshots in: {outputDir}");
        Console.WriteLine($"  2. Check if the AI correctly identified UI elements and errors");
        Console.WriteLine($"  3. Adjust prompts in ScreenshotAnalyzer.cs if needed");
        Console.WriteLine($"  4. Test with your own real failure screenshots");
    }

    public static async Task InventoryScreenshotsAsync(string trxPath)
    {
        Console.WriteLine("=== SCREENSHOT INVENTORY ===\n");

        var parser = new TrxParser();
        var run = parser.Parse(trxPath);

        int totalScreenshots = 0;
        int totalFailuresWithScreenshots = 0;
        int totalScreenshotsExist = 0;

        foreach (var result in run.Results)
        {
            var screenshots = result.AttachmentPaths
                .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                           p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                           p.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (screenshots.Any())
            {
                totalFailuresWithScreenshots++;
                totalScreenshots += screenshots.Count;

                Console.WriteLine($"Test: {result.ShortName}");
                Console.WriteLine($"  Outcome: {result.Outcome}");
                Console.WriteLine($"  Screenshots: {screenshots.Count}");

                foreach (var screenshot in screenshots)
                {
                    var exists = File.Exists(screenshot);
                    if (exists) totalScreenshotsExist++;

                    var statusIcon = exists ? "✅" : "❌";
                    Console.WriteLine($"    {statusIcon} {screenshot}");

                    if (exists)
                    {
                        var fileInfo = new FileInfo(screenshot);
                        Console.WriteLine($"        Size: {fileInfo.Length / 1024}KB, Modified: {fileInfo.LastWriteTime}");
                    }
                }
                Console.WriteLine();
            }
        }

        Console.WriteLine("=== SUMMARY ===");
        Console.WriteLine($"Total Tests: {run.Results.Count}");
        Console.WriteLine($"Tests with Screenshot Attachments: {totalFailuresWithScreenshots}");
        Console.WriteLine($"Total Screenshot References: {totalScreenshots}");
        Console.WriteLine($"Screenshots Found on Disk: {totalScreenshotsExist}");
        Console.WriteLine($"Screenshots Missing: {totalScreenshots - totalScreenshotsExist}");

        if (totalScreenshots == 0)
        {
            Console.WriteLine("\n⚠️ No screenshots found in TRX file.");
            Console.WriteLine("   This could mean:");
            Console.WriteLine("   - Your tests don't capture screenshots on failure");
            Console.WriteLine("   - Screenshots are not being attached to test results");
            Console.WriteLine("   - TRX file was generated without attachments");
        }
        else if (totalScreenshotsExist < totalScreenshots)
        {
            Console.WriteLine($"\n⚠️ {totalScreenshots - totalScreenshotsExist} screenshot(s) are referenced but not found on disk.");
            Console.WriteLine("   This could mean:");
            Console.WriteLine("   - TRX contains absolute paths from another machine");
            Console.WriteLine("   - Screenshots were deleted or moved");
            Console.WriteLine("   - You need to download attachments from Azure DevOps first");
        }
        else
        {
            Console.WriteLine("\n✅ All screenshots are accessible!");
        }
    }
}
