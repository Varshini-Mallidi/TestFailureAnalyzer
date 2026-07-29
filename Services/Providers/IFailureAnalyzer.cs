using FailureAnalyzer.Models;

namespace FailureAnalyzer.Services;

/// <summary>
/// Contract for AI-powered test failure analyzers.
/// Implementations can use different providers (Ollama, Azure OpenAI, OpenAI, etc.)
/// but must provide consistent analysis capabilities.
/// </summary>
public interface IFailureAnalyzer
{
    /// <summary>
    /// Analyze a single test failure using AI.
    /// </summary>
    /// <param name="failure">Test result with error details</param>
    /// <param name="logSnippet">Relevant log entries for this test</param>
    /// <param name="environment">Test environment (e.g., "Azure DevOps CI")</param>
    /// <param name="extraContext">Additional context (e.g., RAG-retrieved source code)</param>
    /// <returns>Structured failure analysis</returns>
    Task<FailureAnalysis> AnalyzeFailureAsync(
        TestResult failure,
        string logSnippet,
        string environment,
        string? extraContext);

    /// <summary>
    /// Detect cross-cutting patterns across multiple failures.
    /// </summary>
    /// <param name="failures">List of analyzed failures</param>
    /// <param name="environment">Test environment</param>
    /// <returns>List of detected patterns and environment-specific notes</returns>
    Task<(List<string> Patterns, string EnvNotes)> DetectPatternsAsync(
        List<FailureAnalysis> failures,
        string environment);
}
