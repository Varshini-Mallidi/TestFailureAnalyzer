using FailureAnalyzer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FailureAnalyzer.Services;

/// <summary>
/// Searches for locator and control definitions in indexed code chunks.
/// Helps find property/field definitions like "_contextPane =>..." or "commandTextBox =>..."
/// that are referenced in failing statements.
/// </summary>
public class LocatorDefinitionFinder
{
    private readonly List<DocumentChunk> _chunks;

    public LocatorDefinitionFinder(List<DocumentChunk> chunks)
    {
        _chunks = chunks;
    }

    /// <summary>
    /// Finds property or field definitions that match the given name.
    /// Example: Find "_contextPane" definition when the failing statement references it.
    /// </summary>
    public List<DocumentChunk> FindDefinitions(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return new List<DocumentChunk>();

        var results = new List<DocumentChunk>();

        foreach (var chunk in _chunks)
        {
            // Look for patterns like:
            // - "_contextPane =>"
            // - "private AutomationElement _contextPane"
            // - "public AutomationElement _contextPane { get; set; }"
            // - "commandTextBox => ..."

            if (chunk.Content.Contains(propertyName))
            {
                // Check if it's actually a definition, not just a usage
                var lines = chunk.Content.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains(propertyName) && IsDefinitionLine(line, propertyName))
                    {
                        results.Add(chunk);
                        break;  // Don't add the same chunk multiple times
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts potential locator/property names from a failing statement.
    /// Example: "_contextPane.FindFirstDescendant(...)" -> "_contextPane"
    /// Example: "cmdWindow.EnterText(...)" -> "cmdWindow"
    /// </summary>
    public static List<string> ExtractPotentialLocators(string failingStatement)
    {
        var locators = new List<string>();
        if (string.IsNullOrWhiteSpace(failingStatement))
            return locators;

        // Pattern 1: _variableName. or variableName.
        var dotAccessMatches = Regex.Matches(failingStatement, @"(\b_?\w+)\.");
        foreach (Match match in dotAccessMatches)
        {
            var name = match.Groups[1].Value;
            // Skip common keywords
            if (!IsCommonKeyword(name))
                locators.Add(name);
        }

        // Pattern 2: Variables in method calls
        var methodCallMatches = Regex.Matches(failingStatement, @"(\b_?\w+)\s*\(");
        foreach (Match match in methodCallMatches)
        {
            var name = match.Groups[1].Value;
            if (!IsCommonKeyword(name) && !locators.Contains(name))
                locators.Add(name);
        }

        return locators.Distinct().ToList();
    }

    /// <summary>
    /// Checks if a line is a property/field definition rather than just a usage.
    /// </summary>
    private static bool IsDefinitionLine(string line, string propertyName)
    {
        // Property with expression body: "PropName =>"
        if (line.Contains("=>") && line.Contains(propertyName))
            return true;

        // Field or property with access modifier
        if ((line.Contains("private") || line.Contains("public") ||
             line.Contains("protected") || line.Contains("internal")) &&
            line.Contains(propertyName))
            return true;

        // Auto-property: "{ get; set; }" or "{ get; }"
        if (line.Contains(propertyName) && line.Contains("{") && line.Contains("get"))
            return true;

        return false;
    }

    /// <summary>
    /// Filters out common C# keywords and framework types that aren't locators.
    /// </summary>
    private static bool IsCommonKeyword(string name)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "var", "new", "this", "base", "string", "int", "bool", "void",
            "public", "private", "protected", "internal", "static", "async",
            "await", "return", "if", "else", "for", "foreach", "while",
            "System", "Task", "Assert", "Console", "File", "Path", "String"
        };

        return keywords.Contains(name);
    }

    /// <summary>
    /// Searches for chunks containing locator patterns commonly used in UI automation.
    /// Example: "By.AutomationId", "FindFirstDescendant", "ByAutomationId"
    /// </summary>
    public List<DocumentChunk> FindLocatorPatterns(string? contextHint = null)
    {
        var results = new List<DocumentChunk>();
        var locatorKeywords = new[] { "AutomationId", "FindFirstDescendant", "FindFirst", "ByName", "ByClassName" };

        foreach (var chunk in _chunks)
        {
            // If we have a context hint (like a property name), prioritize chunks with that name
            if (!string.IsNullOrWhiteSpace(contextHint) && chunk.Content.Contains(contextHint))
            {
                // Check if this chunk also has locator keywords
                if (locatorKeywords.Any(kw => chunk.Content.Contains(kw)))
                {
                    results.Add(chunk);
                    continue;
                }
            }

            // Otherwise, just look for locator keywords
            if (locatorKeywords.Any(kw => chunk.Content.Contains(kw)))
            {
                results.Add(chunk);
            }
        }

        return results;
    }
}
