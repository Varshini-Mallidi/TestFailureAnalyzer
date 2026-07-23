using System.Drawing;
using System.Drawing.Imaging;

namespace FailureAnalyzer.Utils;

/// <summary>
/// Utility to create mock screenshots for testing screenshot analysis without real test failures.
/// </summary>
public static class MockScreenshotGenerator
{
    public static void CreateMockErrorScreenshot(string outputPath, string errorMessage, string testName)
    {
        using var bitmap = new Bitmap(1024, 768);
        using var graphics = Graphics.FromImage(bitmap);

        // Background
        graphics.Clear(Color.FromArgb(240, 240, 245));

        // Draw application chrome
        graphics.FillRectangle(new SolidBrush(Color.FromArgb(51, 51, 51)), 0, 0, 1024, 60);
        graphics.DrawString("Test Application", new Font("Segoe UI", 16, FontStyle.Bold), Brushes.White, 20, 18);

        // Draw main content area
        graphics.FillRectangle(Brushes.White, 50, 100, 924, 600);
        graphics.DrawRectangle(new Pen(Color.LightGray, 2), 50, 100, 924, 600);

        // Draw error dialog
        var errorDialogRect = new Rectangle(300, 250, 424, 200);
        graphics.FillRectangle(Brushes.White, errorDialogRect);
        graphics.DrawRectangle(new Pen(Color.Red, 3), errorDialogRect);

        // Error icon (red X)
        graphics.FillEllipse(Brushes.Red, 320, 270, 40, 40);
        graphics.DrawString("X", new Font("Arial", 24, FontStyle.Bold), Brushes.White, 330, 275);

        // Error title
        graphics.DrawString("Error", new Font("Segoe UI", 14, FontStyle.Bold), Brushes.Black, 380, 275);

        // Error message
        var errorFont = new Font("Segoe UI", 10);
        var errorBrush = Brushes.DarkRed;
        var errorRect = new RectangleF(320, 320, 380, 80);
        graphics.DrawString(errorMessage, errorFont, errorBrush, errorRect);

        // OK button
        var buttonRect = new Rectangle(550, 400, 100, 30);
        graphics.FillRectangle(new SolidBrush(Color.FromArgb(0, 120, 215)), buttonRect);
        graphics.DrawString("OK", new Font("Segoe UI", 10, FontStyle.Bold), Brushes.White, 580, 407);

        // Footer with test info
        graphics.DrawString($"Test: {testName}", new Font("Segoe UI", 8), Brushes.Gray, 55, 720);
        graphics.DrawString($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", new Font("Segoe UI", 8), Brushes.Gray, 55, 740);

        bitmap.Save(outputPath, ImageFormat.Png);
        Console.WriteLine($"[Mock] Created screenshot: {outputPath}");
    }

    public static void CreateMockTimeoutScreenshot(string outputPath, string elementName, string testName)
    {
        using var bitmap = new Bitmap(1024, 768);
        using var graphics = Graphics.FromImage(bitmap);

        // Background
        graphics.Clear(Color.FromArgb(245, 245, 250));

        // Draw application chrome
        graphics.FillRectangle(new SolidBrush(Color.FromArgb(41, 98, 255)), 0, 0, 1024, 60);
        graphics.DrawString("Test Application", new Font("Segoe UI", 16, FontStyle.Bold), Brushes.White, 20, 18);

        // Draw loading spinner area
        graphics.FillRectangle(Brushes.White, 50, 100, 924, 600);
        graphics.DrawRectangle(new Pen(Color.LightGray, 2), 50, 100, 924, 600);

        // Loading spinner
        DrawSpinner(graphics, 512, 350, 60);

        // Loading text
        graphics.DrawString("Loading...", new Font("Segoe UI", 16), Brushes.Gray, 460, 440);

        // Expected element that never appeared (grayed out)
        var ghostRect = new Rectangle(400, 520, 224, 40);
        graphics.FillRectangle(new SolidBrush(Color.FromArgb(50, 200, 200, 200)), ghostRect);
        graphics.DrawRectangle(new Pen(Color.LightGray, 2), ghostRect);
        graphics.DrawString($"[{elementName}]", new Font("Segoe UI", 10, FontStyle.Italic), Brushes.LightGray, 410, 530);

        // Footer
        graphics.DrawString($"Test: {testName}", new Font("Segoe UI", 8), Brushes.Gray, 55, 720);
        graphics.DrawString($"Element '{elementName}' never became visible", new Font("Segoe UI", 8, FontStyle.Italic), Brushes.DarkRed, 55, 740);

        bitmap.Save(outputPath, ImageFormat.Png);
        Console.WriteLine($"[Mock] Created timeout screenshot: {outputPath}");
    }

    private static void DrawSpinner(Graphics g, int centerX, int centerY, int radius)
    {
        for (int i = 0; i < 8; i++)
        {
            var angle = i * 45;
            var alpha = (byte)(255 - (i * 30));
            var color = Color.FromArgb(alpha, 41, 98, 255);

            var radians = angle * Math.PI / 180;
            var x1 = centerX + (int)(radius * 0.5 * Math.Cos(radians));
            var y1 = centerY + (int)(radius * 0.5 * Math.Sin(radians));
            var x2 = centerX + (int)(radius * Math.Cos(radians));
            var y2 = centerY + (int)(radius * Math.Sin(radians));

            g.DrawLine(new Pen(color, 6), x1, y1, x2, y2);
        }
    }

    public static void CreateMockLocatorFailureScreenshot(string outputPath, string locator, string testName)
    {
        using var bitmap = new Bitmap(1024, 768);
        using var graphics = Graphics.FromImage(bitmap);

        // Background
        graphics.Clear(Color.White);

        // App header
        graphics.FillRectangle(new SolidBrush(Color.FromArgb(33, 150, 83)), 0, 0, 1024, 60);
        graphics.DrawString("Dashboard", new Font("Segoe UI", 16, FontStyle.Bold), Brushes.White, 20, 18);

        // Content area with multiple elements
        graphics.FillRectangle(Brushes.White, 50, 100, 924, 600);

        // Draw several buttons/elements
        string[] elementIds = { "btnSubmit", "btnCancel", "btnSave", "txtUsername", "txtPassword" };
        for (int i = 0; i < elementIds.Length; i++)
        {
            var rect = new Rectangle(100, 150 + (i * 80), 200, 50);
            graphics.FillRectangle(new SolidBrush(Color.FromArgb(230, 230, 240)), rect);
            graphics.DrawRectangle(Pens.Black, rect);
            graphics.DrawString(elementIds[i], new Font("Segoe UI", 10), Brushes.Black, 110, 165 + (i * 80));
        }

        // Highlight that the locator is searching for something not present
        var searchRect = new Rectangle(500, 300, 400, 100);
        graphics.DrawRectangle(new Pen(Color.Red, 3), searchRect);
        graphics.DrawString($"Looking for: {locator}", new Font("Segoe UI", 12, FontStyle.Bold), Brushes.Red, 510, 310);
        graphics.DrawString("❌ NOT FOUND", new Font("Segoe UI", 14, FontStyle.Bold), Brushes.Red, 510, 340);

        // Footer
        graphics.DrawString($"Test: {testName}", new Font("Segoe UI", 8), Brushes.Gray, 55, 720);
        graphics.DrawString($"Locator: {locator}", new Font("Segoe UI", 8), Brushes.Gray, 55, 740);

        bitmap.Save(outputPath, ImageFormat.Png);
        Console.WriteLine($"[Mock] Created locator failure screenshot: {outputPath}");
    }
}
