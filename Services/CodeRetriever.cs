using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FailureAnalyzer.Services;

public class CodeRetriever
{
    /// <summary>
    /// Extracts the relevant C# code snippet by cross-referencing the stack trace 
    /// with the local source code repository.
    /// </summary>
    public string ExtractCodeSnippet(string stackTrace, string? sourceDirectory)
    {
        // If there's no stack trace or the user didn't provide a valid source directory, skip RAG.
        if (string.IsNullOrWhiteSpace(stackTrace) || string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return "";

        try
        {
            // 1. Regex to find the file path and line number in the stack trace.
            // Example match: "in C:\azagent\_work\1\s\MyTests\LoginPage.cs:line 45"
            var match = Regex.Match(stackTrace, @"in\s+(?<path>.*\.cs)\s*:\s*line\s+(?<line>\d+)", RegexOptions.IgnoreCase);

            if (!match.Success)
                return ""; // No line number / file path found in the stack trace

            string cloudPath = match.Groups["path"].Value.Trim();
            if (!int.TryParse(match.Groups["line"].Value, out int targetLine))
                return "";

            // 2. Extract just the file name (e.g., "LoginPage.cs")
            string fileName = Path.GetFileName(cloudPath);

            // 3. Search the local repository for that file
            var possibleFiles = Directory.GetFiles(sourceDirectory, fileName, SearchOption.AllDirectories);

            if (!possibleFiles.Any())
                return ""; // The file wasn't found in your local directory

            // 4. If multiple files share the same name, pick the one whose folder structure matches the cloud path closest
            string bestMatchPath = possibleFiles.OrderByDescending(f => ScorePathMatch(cloudPath, f)).First();

            // 5. Read the 7 lines before and after the crash
            return ReadLinesAround(bestMatchPath, targetLine, contextLines: 7);
        }
        catch (Exception ex)
        {
            // Fail silently so we don't crash the pipeline, but log a warning
            Console.WriteLine($"  [CodeRetriever] Warning: Could not extract code snippet. {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Scores how well a local file path matches the CI/CD cloud file path by comparing them backwards.
    /// Helpful if you have multiple files named "Constants.cs" in different folders.
    /// </summary>
    private int ScorePathMatch(string cloudPath, string localPath)
    {
        int score = 0;
        int cLen = cloudPath.Length;
        int lLen = localPath.Length;

        // Compare character-by-character from the end of the string moving backwards
        while (score < cLen && score < lLen && cloudPath[cLen - 1 - score] == localPath[lLen - 1 - score])
        {
            score++;
        }

        return score;
    }

    /// <summary>
    /// Reads the specific lines of code from the file and adds a visual indicator (>>) 
    /// to the exact line that caused the crash.
    /// </summary>
    private string ReadLinesAround(string filePath, int centerLine, int contextLines)
    {
        var lines = File.ReadAllLines(filePath);
        if (lines.Length == 0) return "";

        int targetIdx = centerLine - 1; // Array is 0-indexed, lines are 1-indexed

        // Ensure we don't go out of bounds of the file
        int startIdx = Math.Max(0, targetIdx - contextLines);
        int endIdx = Math.Min(lines.Length - 1, targetIdx + contextLines);

        var snippet = new StringBuilder();
        snippet.AppendLine($"File: {Path.GetFileName(filePath)}");

        for (int i = startIdx; i <= endIdx; i++)
        {
            // Put a ">>" arrow pointing directly at the line where it failed
            string marker = (i == targetIdx) ? ">> " : "   ";
            snippet.AppendLine($"{marker}{i + 1}: {lines[i]}");
        }

        return snippet.ToString();
    }
}