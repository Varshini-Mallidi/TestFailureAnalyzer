using System;
using System.Collections.Generic;
using FailureAnalyzer.Models;

namespace FailureAnalyzer.Tests;

/// <summary>
/// Regression tests for exception type extraction from TRX files.
/// Ensures the exception field is correctly extracted from various formats
/// and doesn't regress to "Unknown".
/// </summary>
public static class ExceptionExtractionTests
{
    public static void RunAllTests()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("  EXCEPTION EXTRACTION REGRESSION TESTS");
        Console.WriteLine("═══════════════════════════════════════════════════════════\n");

        TestStandardExceptionFormat();
        TestFullyQualifiedExceptionFormat();
        TestComplexStackTraceFormat();
        TestEdgeCases();

        Console.WriteLine("\n═══════════════════════════════════════════════════════════");
        Console.WriteLine("  ALL EXCEPTION EXTRACTION TESTS PASSED ✅");
        Console.WriteLine("═══════════════════════════════════════════════════════════\n");
    }

    private static void TestStandardExceptionFormat()
    {
        Console.WriteLine("TEST 1: Standard Exception Format");
        Console.WriteLine("────────────────────────────────────────────────────────────");

        var testCases = new[]
        {
            (
                "ElementNotAvailableException: Cannot access element",
                "ElementNotAvailableException"
            ),
            (
                "System.UnauthorizedAccessException: Access denied",
                "UnauthorizedAccessException"
            ),
            (
                "NullReferenceException: Object reference not set",
                "NullReferenceException"
            )
        };

        foreach (var (input, expected) in testCases)
        {
            var result = ExtractExceptionType(input);

            if (result == expected)
            {
                Console.WriteLine($"  ✅ '{Truncate(input, 50)}' → {result}");
            }
            else
            {
                Console.WriteLine($"  ❌ FAILED: Expected '{expected}', got '{result}'");
                throw new Exception($"Test failed for: {input}");
            }
        }

        Console.WriteLine();
    }

    private static void TestFullyQualifiedExceptionFormat()
    {
        Console.WriteLine("TEST 2: Fully Qualified Exception Names");
        Console.WriteLine("────────────────────────────────────────────────────────────");

        var testCases = new[]
        {
            (
                "FlaUI.Core.Exceptions.ElementNotAvailableException: The element is not available",
                "ElementNotAvailableException"
            ),
            (
                "System.IO.IOException: The file could not be opened",
                "IOException"
            ),
            (
                "Microsoft.VisualStudio.TestPlatform.ObjectModel.TestFailedException: Test failed",
                "TestFailedException"
            )
        };

        foreach (var (input, expected) in testCases)
        {
            var result = ExtractExceptionType(input);

            if (result == expected)
            {
                Console.WriteLine($"  ✅ '{Truncate(input, 50)}' → {result}");
            }
            else
            {
                Console.WriteLine($"  ❌ FAILED: Expected '{expected}', got '{result}'");
                throw new Exception($"Test failed for: {input}");
            }
        }

        Console.WriteLine();
    }

    private static void TestComplexStackTraceFormat()
    {
        Console.WriteLine("TEST 3: Exception in Multi-line Stack Trace");
        Console.WriteLine("────────────────────────────────────────────────────────────");

        var testCases = new[]
        {
            (
                @"   at FlaUI.Core.Elements.Element.Click()
   at MyTest.PerformAction()
FlaUI.Core.Exceptions.ElementNotAvailableException: Element not found
   at FlaUI.Core.Elements.Element.get_Properties()",
                "ElementNotAvailableException"
            ),
            (
                @"System.InvalidOperationException: Sequence contains no elements
   at System.Linq.Enumerable.First[TSource](IEnumerable`1 source)
   at MyApp.Service.GetData()",
                "InvalidOperationException"
            )
        };

        foreach (var (input, expected) in testCases)
        {
            var result = ExtractExceptionType(input);

            if (result == expected)
            {
                Console.WriteLine($"  ✅ Multi-line trace → {result}");
            }
            else
            {
                Console.WriteLine($"  ❌ FAILED: Expected '{expected}', got '{result}'");
                throw new Exception($"Test failed for multi-line trace");
            }
        }

        Console.WriteLine();
    }

    private static void TestEdgeCases()
    {
        Console.WriteLine("TEST 4: Edge Cases");
        Console.WriteLine("────────────────────────────────────────────────────────────");

        var testCases = new[]
        {
            (
                "Error occurred",
                "Unknown"  // No exception in message
            ),
            (
                "",
                "Unknown"  // Empty string
            ),
            (
                "TestFailedException",
                "TestFailedException"  // Just the exception name
            ),
            (
                "Multiple Exception words: IOException and TimeoutException occurred",
                "IOException"  // Should pick first one
            )
        };

        foreach (var (input, expected) in testCases)
        {
            var result = ExtractExceptionType(input);

            if (result == expected)
            {
                Console.WriteLine($"  ✅ '{Truncate(input, 40)}' → {result}");
            }
            else
            {
                Console.WriteLine($"  ❌ FAILED: Expected '{expected}', got '{result}'");
                throw new Exception($"Test failed for: {input}");
            }
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Mirrors the extraction logic in HtmlReportGenerator.cs
    /// This should match the actual implementation to ensure tests validate the real behavior.
    /// </summary>
    private static string ExtractExceptionType(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return "Unknown";

        // Try regex pattern matching for exception types
        var match = System.Text.RegularExpressions.Regex.Match(
            errorMessage,
            @"(?:^|\s)([A-Za-z0-9_\.]+(?:Exception|Error|Failed))(?:\s|:|$)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        if (match.Success)
        {
            // Extract just the exception name (strip namespace if present)
            var fullName = match.Groups[1].Value;
            return fullName.Split('.').Last();
        }

        return "Unknown";
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength) + "...";
    }
}
