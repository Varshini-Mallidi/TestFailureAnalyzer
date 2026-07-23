using System.IO;
using System.Linq;
using System.Text;
using FailureAnalyzer.Models;

namespace FailureAnalyzer.Reports;

public class HtmlReportGenerator
{
    public string Generate(RunAnalysis analysis)
    {
        var r = analysis.Run;
        var date = analysis.GeneratedAt.ToString("yyyy-MM-dd HH:mm UTC");
        int total = analysis.Failures.Count;
        var passRate = r.Total > 0 ? (r.Passed * 100.0 / r.Total).ToString("F0") : "0";
        var sb = new StringBuilder();

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"UTF-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\n");
        sb.Append($"<title>AI Test Failure Analyzer \u2014 {HE(r.RunName)}</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");

        // ── Header (Centered Title, Right Meta) ───────────────────────
        sb.Append("<header class=\"g-hdr\">\n");
        sb.Append("  <div class=\"g-hdr-center\">\n");
        sb.Append("    <div class=\"g-hdr-dot\"></div>\n");
        sb.Append("    <h1 class=\"g-htitle\">AI-Powered Test Failure Analyzer</h1>\n");
        sb.Append("  </div>\n");
        sb.Append($"  <div class=\"g-hdr-right\">\n");
        sb.Append($"    {HE(analysis.Environment)} &middot; {HE(date)}\n");
        sb.Append("  </div>\n");
        sb.Append("</header>\n");

        // ── AI Disclaimer Banner ──────────────────────────────────────────
        sb.Append("<div style=\"background:#f9fafb;border:1px solid #d1d5db;padding:12px 24px;margin:0;\">\n");
        sb.Append("  <div style=\"font-size:13px;color:#4b5563;line-height:1.5;\">\n");
        sb.Append("    <strong>AI-Generated Analysis</strong> — This report uses AI to analyze test failures. ");
        sb.Append("    Confidence scores indicate reliability. Always verify findings before making changes.\n");
        sb.Append("  </div>\n");
        sb.Append("</div>\n");

        // Cross-cutting patterns removed per user request

        // ── Layout ────────────────────────────────────────────────────
        sb.Append("<div class=\"g-layout\">\n");

        // ── Sidebar ───────────────────────────────────────────────────
        sb.Append("  <aside class=\"g-side\">\n");

        // ── 2x2 Stats Grid ────────────────────────────────────────────
        sb.Append("    <div class=\"g-stats-grid\">\n");
        sb.Append($"      <div class=\"g-stat-card\"><div class=\"g-stat-num\">{r.Total}</div><div class=\"g-stat-lbl\">TOTAL</div></div>\n");
        sb.Append($"      <div class=\"g-stat-card\"><div class=\"g-stat-num stat-fail\">{r.Failed}</div><div class=\"g-stat-lbl\">FAILED</div></div>\n");
        sb.Append($"      <div class=\"g-stat-card\"><div class=\"g-stat-num stat-pass\">{r.Passed}</div><div class=\"g-stat-lbl\">PASSED</div></div>\n");
        sb.Append($"      <div class=\"g-stat-card\"><div class=\"g-stat-num\">{r.Skipped}</div><div class=\"g-stat-lbl\">SKIPPED</div></div>\n");
        sb.Append("    </div>\n");

        // Test list
        sb.Append("    <div class=\"g-tlist-wrap\">\n");
        sb.Append("      <div class=\"g-lbl\" style=\"margin:24px 0 12px\">FAILED TESTS</div>\n");
        sb.Append("      <ul class=\"g-tlist\">\n");
        int li = 0;
        foreach (var f in analysis.Failures)
        {
            li++;
            var act = li == 1 ? " g-ti-on" : "";
            sb.Append($"        <li class=\"g-ti{act}\" id=\"li-{li}\" onclick=\"showCard({li - 1})\">"
                    + "<span class=\"g-tdot\"></span>"
                    + $"<span class=\"g-tname\">{HE(f.ShortName)}</span>"
                    + $"<span class=\"g-tnum\">{li}</span></li>\n");
        }
        sb.Append("      </ul>\n    </div>\n  </aside>\n");

        // ── Resize handle ─────────────────────────────────────────────
        sb.Append("  <div class=\"g-resize\" id=\"g-resize\"></div>\n");

        // ── Main panel ────────────────────────────────────────────────
        var initW = total > 0 ? (100.0 / total).ToString("F1") : "100";
        sb.Append("  <div class=\"g-main\">\n");
        sb.Append("    <div class=\"g-topbar\">\n");
        sb.Append($"      <span id=\"g-sub\" class=\"g-topbar-title\">Test <strong id=\"g-sub-strong\">1</strong> of {total}</span>\n");
        sb.Append("      <div class=\"g-nav\">"
                + "<button class=\"g-abtn\" id=\"g-prev\" onclick=\"navigate(-1)\" disabled>&#8592;</button>"
                + $"<span class=\"g-frac\" id=\"g-frac\">1 / {total}</span>"
                + "<button class=\"g-abtn\" id=\"g-next\" onclick=\"navigate(1)\">&#8594;</button>"
                + "</div>\n    </div>\n");
        sb.Append($"    <div class=\"g-prog\"><div class=\"g-progf\" id=\"g-progf\" style=\"width:{initW}%\"></div></div>\n");
        sb.Append("    <div id=\"g-area\">\n");

        // ── Cards ─────────────────────────────────────────────────────
        int ci = 0;
        foreach (var f in analysis.Failures)
        {
            ci++;
            var hide = ci == 1 ? "" : " style=\"display:none\"";
            var sevCls = "sev-" + f.Severity.ToLowerInvariant();
            var catLabel = f.Category.Replace("_", " ");

            sb.Append($"      <div class=\"g-card\" id=\"card-{ci}\"{hide}>\n");

            // Header info
            sb.Append("        <div class=\"g-card-hd\">\n");
            sb.Append($"          <div class=\"g-card-kicker\">Test {ci} of {total} &middot; {HE(catLabel)}</div>\n");
            sb.Append($"          <h2 class=\"g-th\">{HE(f.ShortName)}</h2>\n");
            sb.Append($"          <div class=\"g-tfull\">{HE(f.TestName)}</div>\n");

            // Simplified badges
            sb.Append($"          <div class=\"g-bdgs\">");
            sb.Append($"<span class=\"g-bdg-simple\">{HE(f.Severity)}</span>");
            sb.Append($"<span class=\"g-bdg-simple\">{HE(catLabel)}</span>");

            // Add confidence badge based on evidence availability
            string confidenceLevel = DetermineConfidenceLevel(f);
            string confidenceColor = confidenceLevel.ToLowerInvariant() switch
            {
                "high" => "#16a34a",      // green
                "medium" => "#f59e0b",    // amber
                "low" => "#dc2626",       // red
                _ => "#6b7280"            // gray
            };
            sb.Append($"<span class=\"g-bdg-simple\" style=\"background-color:{confidenceColor};color:white;\">Confidence: {HE(confidenceLevel)}</span>");

            sb.Append($"</div>\n");
            sb.Append("        </div>\n");

            // ═══════════════════════════════════════════════════════════════
            // PROFESSIONAL TEST FAILURE INVESTIGATION REPORT
            // ═══════════════════════════════════════════════════════════════

            // 1. TEST SUMMARY
            sb.Append("        <div class=\"g-section-lbl\">TEST SUMMARY</div>\n");
            sb.Append("        <div class=\"g-alert\" style=\"border-left-color: #6b7280;\">\n");
            sb.Append("          <table class=\"g-locator-table\">\n");
            sb.Append($"            <tr><td><strong>Test Name:</strong></td><td>{HE(f.ShortName)}</td></tr>\n");
            sb.Append($"            <tr><td><strong>Status:</strong></td><td><span style=\"color:#dc2626;font-weight:600;\">Failed</span></td></tr>\n");

            // Extract exception type - use evidence bundle first, then parse bundle's ExceptionMessage as fallback
            string exceptionType = "Unknown";

            // Priority 1: Use evidence bundle's pre-extracted ExceptionType if available
            if (f.Bundle != null && !string.IsNullOrWhiteSpace(f.Bundle.ExceptionType))
            {
                exceptionType = f.Bundle.ExceptionType;
            }
            // Priority 2: Extract from evidence bundle's raw ExceptionMessage (from TRX)
            else if (f.Bundle != null && !string.IsNullOrWhiteSpace(f.Bundle.ExceptionMessage))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    f.Bundle.ExceptionMessage,
                    @"(?:^|\s)([A-Za-z0-9_\.]+(?:Exception|Error|Failed))(?:\s|:|$)",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                if (match.Success)
                {
                    // Extract just the exception name (strip namespace if present)
                    var fullName = match.Groups[1].Value;
                    exceptionType = fullName.Split('.').Last();
                }
            }
            // Priority 3: Fallback to parsing error_summary (LLM output) only as last resort
            else if (!string.IsNullOrWhiteSpace(f.ErrorSummary))
            {
                var exceptionMatch = System.Text.RegularExpressions.Regex.Match(
                    f.ErrorSummary, 
                    @"\b(\w+Exception)\b");

                if (exceptionMatch.Success)
                {
                    exceptionType = exceptionMatch.Groups[1].Value;
                }
            }

            sb.Append($"            <tr><td><strong>Exception:</strong></td><td>{HE(exceptionType)}</td></tr>\n");

            // Error message
            if (!string.IsNullOrWhiteSpace(f.ErrorSummary))
            {
                sb.Append($"            <tr><td><strong>Error Message:</strong></td><td style=\"max-width:600px;\">{HE(f.ErrorSummary)}</td></tr>\n");
            }

            // Failure location (extract from stack trace or evidence)
            string failureLocation = ExtractFailureLocation(f);
            if (!string.IsNullOrWhiteSpace(failureLocation))
            {
                sb.Append($"            <tr><td><strong>Failure Location:</strong></td><td>{HE(failureLocation)}</td></tr>\n");
            }

            sb.Append("          </table>\n");
            sb.Append("        </div>\n");

            // ═══════════════════════════════════════════════════════════════
            // 1b. CONFIDENCE REASONING (Evidence-Based Transparency)
            // ═══════════════════════════════════════════════════════════════

            if (f.Bundle != null)
            {
                var confidenceEvidence = EvidenceValidator.GetSummary(f.Bundle);

                // Determine if we should show detailed reasoning (when confidence was capped)
                bool showDetailedReasoning = f.Hypotheses.Any(h => h.OriginalConfidence.HasValue && h.OriginalConfidence > h.Confidence);

                sb.Append("        <details style=\"margin:16px 0;\">\n");
                sb.Append("          <summary style=\"cursor:pointer;font-weight:600;color:#374151;font-size:14px;padding:8px 12px;background:#f9fafb;border-radius:6px;user-select:none;\">📊 Confidence Calculation</summary>\n");
                sb.Append("          <div style=\"margin-top:12px;padding:16px;background:#f9fafb;border-radius:6px;border:1px solid #e5e7eb;\">\n");

                // Show primary hypothesis confidence if available
                if (f.Hypotheses.Any())
                {
                    var primaryHyp = f.PrimaryHypothesis < f.Hypotheses.Count 
                        ? f.Hypotheses[f.PrimaryHypothesis] 
                        : f.Hypotheses.First();

                    sb.Append("            <div style=\"margin-bottom:16px;\">\n");
                    sb.Append("              <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:8px;\">Primary Hypothesis</div>\n");

                    if (primaryHyp.OriginalConfidence.HasValue && primaryHyp.OriginalConfidence > primaryHyp.Confidence)
                    {
                        sb.Append($"              <div style=\"font-size:13px;color:#374151;line-height:1.6;\">\n");
                        sb.Append($"                <div style=\"margin-bottom:4px;\">• <strong>Original AI confidence:</strong> <span style=\"color:#6b7280;text-decoration:line-through;\">{primaryHyp.OriginalConfidence}%</span></div>\n");
                        sb.Append($"                <div style=\"margin-bottom:4px;\">• <strong>Evidence tier:</strong> {HE(confidenceEvidence.EvidenceTier)}</div>\n");
                        sb.Append($"                <div style=\"margin-bottom:4px;\">• <strong>Cap applied:</strong> {HE(primaryHyp.ConfidenceCapReason ?? "None")}</div>\n");
                        sb.Append($"                <div style=\"font-weight:600;color:#16a34a;\">• <strong>Final confidence:</strong> {primaryHyp.Confidence}%</div>\n");
                        sb.Append($"              </div>\n");
                    }
                    else
                    {
                        sb.Append($"              <div style=\"font-size:13px;color:#374151;\">• <strong>AI confidence:</strong> {primaryHyp.Confidence}% (no cap applied)</div>\n");
                    }

                    sb.Append("            </div>\n");
                }

                // Evidence checklist
                sb.Append("            <div style=\"margin-top:16px;border-top:1px solid #e5e7eb;padding-top:12px;\">\n");
                sb.Append("              <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:8px;\">Evidence Checklist</div>\n");
                sb.Append("              <ul style=\"margin:0;padding-left:20px;font-size:13px;color:#374151;line-height:1.8;\">\n");
                sb.Append($"                <li>{(confidenceEvidence.HasStackTrace ? "✅" : "❌")} Stack trace with file:line info</li>\n");
                sb.Append($"                <li>{(confidenceEvidence.HasScreenshots ? "✅" : "❌")} Screenshots analyzed ({confidenceEvidence.ScreenshotCount} image(s))</li>\n");
                sb.Append($"                <li>{(confidenceEvidence.HasDialogText ? "✅" : "❌")} Quoted error dialog text</li>\n");
                sb.Append($"                <li>{(confidenceEvidence.HasApplicationLog ? "✅" : "❌")} Application logs</li>\n");
                sb.Append($"                <li>{(confidenceEvidence.HasExactSymbolMatch ? "✅" : "❌")} Exact crash site match</li>\n");
                sb.Append("              </ul>\n");
                sb.Append("            </div>\n");

                // Available evidence summary
                sb.Append("            <div style=\"margin-top:12px;padding:10px;background:#eff6ff;border-radius:4px;\">\n");
                sb.Append($"              <div style=\"font-size:12px;color:#1e40af;\"><strong>Available:</strong> {HE(confidenceEvidence.GetAvailableEvidenceDescription())}</div>\n");
                if (confidenceEvidence.MissingCategories.Any())
                {
                    sb.Append($"              <div style=\"font-size:12px;color:#92400e;margin-top:4px;\"><strong>Missing:</strong> {HE(confidenceEvidence.GetMissingEvidenceDescription())}</div>\n");
                }
                sb.Append("            </div>\n");

                sb.Append("          </div>\n");
                sb.Append("        </details>\n");
            }

            // ═══════════════════════════════════════════════════════════════
            // 1a. FAULT ATTRIBUTION (Script vs. Application) — NEW
            // ═══════════════════════════════════════════════════════════════

            // Try to display fault attribution - either from structured field or synthesized from hypotheses
            bool displayedAttribution = false;

            if (f.Attribution != null)
            {
                // Use structured fault attribution from LLM
                sb.Append("        <div class=\"g-section-lbl\">FAULT ATTRIBUTION</div>\n");
                sb.Append("        <div class=\"g-alert\" style=\"border-left-color: #8b5cf6;\">\n");

                // Primary classification
                string primaryIcon = f.Attribution.Primary switch
                {
                    "SCRIPT" => "🔧",
                    "APPLICATION" => "🐛",
                    "ENVIRONMENT" => "🌐",
                    "DATA" => "💾",
                    _ => "❓"
                };

                string primaryColor = f.Attribution.Primary switch
                {
                    "SCRIPT" => "#8b5cf6",
                    "APPLICATION" => "#dc2626",
                    "ENVIRONMENT" => "#f59e0b",
                    "DATA" => "#3b82f6",
                    _ => "#6b7280"
                };

                sb.Append($"          <div style=\"margin-bottom:16px;\">\n");
                sb.Append($"            <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:6px;\">Primary Cause</div>\n");
                sb.Append($"            <div style=\"font-size:18px;font-weight:700;color:{primaryColor};\">{primaryIcon} {HE(f.Attribution.Primary)}</div>\n");

                string attrConfidenceText = GetConfidenceText(f.Attribution.Confidence);
                string attrConfidenceColor = GetConfidenceColor(f.Attribution.Confidence);
                sb.Append($"            <div style=\"margin-top:4px;font-size:13px;\"><span style=\"color:{attrConfidenceColor};font-weight:600;\">{attrConfidenceText} ({f.Attribution.Confidence}%)</span></div>\n");
                sb.Append($"          </div>\n");

                // Secondary contributing factors
                if (f.Attribution.SecondaryFactors.Any())
                {
                    sb.Append($"          <div style=\"margin-top:20px;\">\n");
                    sb.Append($"            <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:8px;\">Secondary Contributing Factors</div>\n");

                    foreach (var factor in f.Attribution.SecondaryFactors)
                    {
                        string factorIcon = factor.Type switch
                        {
                            "SCRIPT" => "🔧",
                            "APPLICATION" => "🐛",
                            "ENVIRONMENT" => "🌐",
                            "DATA" => "💾",
                            _ => "•"
                        };

                        sb.Append($"            <div style=\"margin-bottom:12px;padding:10px;background:#f9fafb;border-radius:4px;\">\n");
                        sb.Append($"              <div style=\"font-weight:600;color:#374151;margin-bottom:4px;\">{factorIcon} {HE(factor.Type)}: {HE(factor.Description)}</div>\n");
                        if (!string.IsNullOrWhiteSpace(factor.WhyItMatters))
                        {
                            sb.Append($"              <div style=\"font-size:13px;color:#6b7280;font-style:italic;\">→ {HE(factor.WhyItMatters)}</div>\n");
                        }
                        sb.Append($"            </div>\n");
                    }

                    sb.Append($"          </div>\n");
                }

                sb.Append("        </div>\n");
                displayedAttribution = true;
            }
            else if (f.Hypotheses.Any())
            {
                // Fallback: Synthesize fault attribution from hypotheses
                // Find primary (highest confidence or marked as root-cause)
                var primary = f.Hypotheses
                    .Where(h => !string.IsNullOrWhiteSpace(h.IssueOwner))
                    .OrderByDescending(h => h.Relationship == "root-cause" ? 1 : 0)
                    .ThenByDescending(h => h.Confidence)
                    .FirstOrDefault();

                if (primary != null)
                {
                    sb.Append("        <div class=\"g-section-lbl\">FAULT ATTRIBUTION</div>\n");
                    sb.Append("        <div class=\"g-alert\" style=\"border-left-color: #8b5cf6;\">\n");

                    // Map issue_owner to primary cause type
                    string primaryType = primary.IssueOwner.ToUpperInvariant() switch
                    {
                        "SCRIPT" => "SCRIPT",
                        "APPLICATION" => "APPLICATION",
                        "UNCERTAIN" => "INDETERMINATE",
                        _ => primary.IssueOwner.ToUpperInvariant()
                    };

                    string primaryIcon = primaryType switch
                    {
                        "SCRIPT" => "🔧",
                        "APPLICATION" => "🐛",
                        "ENVIRONMENT" => "🌐",
                        "DATA" => "💾",
                        _ => "❓"
                    };

                    string primaryColor = primaryType switch
                    {
                        "SCRIPT" => "#8b5cf6",
                        "APPLICATION" => "#dc2626",
                        "ENVIRONMENT" => "#f59e0b",
                        "DATA" => "#3b82f6",
                        _ => "#6b7280"
                    };

                    sb.Append($"          <div style=\"margin-bottom:16px;\">\n");
                    sb.Append($"            <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:6px;\">Primary Cause</div>\n");
                    sb.Append($"            <div style=\"font-size:18px;font-weight:700;color:{primaryColor};\">{primaryIcon} {HE(primaryType)}</div>\n");

                    string primaryConfidenceText = GetConfidenceText(primary.Confidence);
                    string primaryConfidenceColor = GetConfidenceColor(primary.Confidence);
                    sb.Append($"            <div style=\"margin-top:4px;font-size:13px;\"><span style=\"color:{primaryConfidenceColor};font-weight:600;\">{primaryConfidenceText} ({primary.Confidence}%)</span></div>\n");
                    sb.Append($"            <div style=\"margin-top:8px;font-size:14px;color:#374151;line-height:1.6;\">{HE(primary.Explanation)}</div>\n");
                    sb.Append($"          </div>\n");

                    // Find secondary/contributing factors (other hypotheses marked as contributing-factor)
                    var secondaryHypotheses = f.Hypotheses
                        .Where(h => h != primary && 
                                    (h.Relationship == "contributing-factor" || 
                                     (string.IsNullOrWhiteSpace(h.Relationship) && h.Confidence < primary.Confidence)))
                        .OrderByDescending(h => h.Confidence)
                        .ToList();

                    if (secondaryHypotheses.Any())
                    {
                        sb.Append($"          <div style=\"margin-top:20px;\">\n");
                        sb.Append($"            <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:8px;\">Secondary / Contributing Factors</div>\n");
                        sb.Append($"            <div style=\"font-size:13px;color:#6b7280;font-style:italic;margin-bottom:8px;\">These issues compound the primary cause and may cause the failure to recur even if the primary cause is fixed.</div>\n");

                        foreach (var secondary in secondaryHypotheses)
                        {
                            string secType = secondary.IssueOwner.ToUpperInvariant() switch
                            {
                                "SCRIPT" => "SCRIPT",
                                "APPLICATION" => "APPLICATION",
                                "UNCERTAIN" => "INDETERMINATE",
                                _ => secondary.IssueOwner.ToUpperInvariant()
                            };

                            string secIcon = secType switch
                            {
                                "SCRIPT" => "🔧",
                                "APPLICATION" => "🐛",
                                "ENVIRONMENT" => "🌐",
                                "DATA" => "💾",
                                _ => "•"
                            };

                            sb.Append($"            <div style=\"margin-bottom:12px;padding:10px;background:#f9fafb;border-radius:4px;\">\n");
                            sb.Append($"              <div style=\"font-weight:600;color:#374151;margin-bottom:4px;\">{secIcon} {HE(secType)} ({secondary.Confidence}%): {HE(secondary.Explanation)}</div>\n");
                            sb.Append($"            </div>\n");
                        }

                        sb.Append($"          </div>\n");
                    }

                    sb.Append("        </div>\n");
                    displayedAttribution = true;
                }
            }


            // 2. OBSERVED FACTS (Bullet points from investigation notes)
            if (!string.IsNullOrWhiteSpace(f.InvestigationNotes))
            {
                sb.Append("        <div class=\"g-section-lbl\">OBSERVED FACTS</div>\n");
                sb.Append("        <div class=\"g-alert\" style=\"border-left-color: #3b82f6;\">\n");
                sb.Append("          <div style=\"font-size:13px;color:#6b7280;margin-bottom:8px;font-style:italic;\">Objective facts gathered from TRX, logs, and retrieved source code</div>\n");

                // Extract facts from investigation notes (sections 1-3)
                var facts = ExtractObservedFacts(f.InvestigationNotes);
                if (facts.Any())
                {
                    sb.Append("          <ul style=\"margin:0;padding-left:20px;\">\n");
                    foreach (var fact in facts)
                    {
                        sb.Append($"            <li style=\"margin:6px 0;font-size:14px;line-height:1.6;color:#374151;\">{HE(fact)}</li>\n");
                    }
                    sb.Append("          </ul>\n");
                }
                sb.Append("        </div>\n");
            }

            // 3. POSSIBLE EXPLANATIONS (Hypotheses)
            // Only show this section if we haven't already displayed fault attribution from hypotheses
            if (f.Hypotheses.Any() && !displayedAttribution)
            {
                sb.Append("        <div class=\"g-section-lbl\">POSSIBLE EXPLANATIONS</div>\n");

                // Disclaimer
                sb.Append("        <div style=\"margin:12px 0;padding:12px;background:#fef3c7;border:1px solid #f59e0b;border-radius:4px;\">\n");
                sb.Append("          <div style=\"font-size:13px;color:#92400e;font-weight:600;\">⚠️ These are possible explanations based on available evidence. They are not confirmed root causes and require verification.</div>\n");
                sb.Append("        </div>\n");

                for (int h = 0; h < Math.Min(4, f.Hypotheses.Count); h++)
                {
                    var hyp = f.Hypotheses[h];

                    // Determine border color based on relationship type
                    string borderColor = "#d1d5db"; // default gray
                    string relationshipLabel = "";
                    string relationshipIcon = "";

                    if (!string.IsNullOrWhiteSpace(hyp.Relationship))
                    {
                        switch (hyp.Relationship.ToLowerInvariant())
                        {
                            case "root-cause":
                            case "root cause":
                                borderColor = "#dc2626"; // red
                                relationshipLabel = "ROOT CAUSE";
                                relationshipIcon = "🎯";
                                break;
                            case "contributing-factor":
                            case "contributing factor":
                                borderColor = "#f59e0b"; // orange
                                relationshipLabel = "CONTRIBUTING FACTOR";
                                relationshipIcon = "⚡";
                                break;
                            case "consequence":
                                borderColor = "#3b82f6"; // blue
                                relationshipLabel = "CONSEQUENCE";
                                relationshipIcon = "📉";
                                break;
                            case "alternative":
                                borderColor = "#6b7280"; // gray
                                relationshipLabel = "ALTERNATIVE EXPLANATION";
                                relationshipIcon = "🔄";
                                break;
                        }
                    }

                    sb.Append($"        <div style=\"margin:12px 0;padding:16px;background:#ffffff;border-left:4px solid {borderColor};border-top:1px solid #d1d5db;border-right:1px solid #d1d5db;border-bottom:1px solid #d1d5db;border-radius:6px;\">\n");

                    // Relationship badge (if present)
                    if (!string.IsNullOrWhiteSpace(relationshipLabel))
                    {
                        sb.Append($"          <div style=\"display:inline-block;padding:4px 10px;background:{borderColor};color:white;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:0.05em;border-radius:4px;margin-bottom:10px;\">{relationshipIcon} {relationshipLabel}</div>\n");
                    }

                    // Hypothesis
                    sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:6px;\">Hypothesis</div>\n");
                    sb.Append($"          <div style=\"font-size:14px;color:#111827;margin-bottom:12px;line-height:1.6;\">{HE(hyp.Explanation)}</div>\n");

                    // Issue Owner (if present)
                    if (!string.IsNullOrWhiteSpace(hyp.IssueOwner))
                    {
                        string ownerIcon = hyp.IssueOwner.ToLowerInvariant() switch
                        {
                            "script" => "🔧",
                            "application" => "🐛",
                            "insufficient_evidence" => "⚠️",
                            "uncertain" => "❓",
                            _ => "❓"
                        };

                        string displayText = hyp.IssueOwner.ToLowerInvariant() == "insufficient_evidence"
                            ? "Insufficient Evidence"
                            : hyp.IssueOwner;

                        sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:4px;\">Issue Owner</div>\n");
                        sb.Append($"          <div style=\"margin-bottom:12px;font-size:13px;\">{ownerIcon} <span style=\"font-weight:600;\">{HE(displayText)}</span></div>\n");
                    }

                    // Confidence
                    sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:4px;\">Confidence</div>\n");
                    string hypConfidenceText = GetConfidenceText(hyp.Confidence);
                    string hypConfidenceColor = GetConfidenceColor(hyp.Confidence);

                    if (hyp.OriginalConfidence.HasValue && hyp.OriginalConfidence > hyp.Confidence)
                    {
                        // Show both original and capped confidence
                        sb.Append($"          <div style=\"margin-bottom:12px;font-size:13px;\">");
                        sb.Append($"<span style=\"color:{hypConfidenceColor};font-weight:600;\">{hypConfidenceText} ({hyp.Confidence}%)</span>");
                        sb.Append($"<br/><span style=\"font-size:11px;color:#6b7280;font-style:italic;\">Model confidence: {hyp.OriginalConfidence}% — capped by policy ({hyp.ConfidenceCapReason})</span>");
                        sb.Append($"</div>\n");
                    }
                    else
                    {
                        sb.Append($"          <div style=\"margin-bottom:12px;font-size:13px;\"><span style=\"color:{hypConfidenceColor};font-weight:600;\">{hypConfidenceText} ({hyp.Confidence}%)</span></div>\n");
                    }

                    // Evidence
                    if (!string.IsNullOrWhiteSpace(hyp.Evidence))
                    {
                        sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:6px;\">Evidence</div>\n");

                        // Parse evidence into bullet points if it contains line breaks or bullets
                        var evidenceLines = hyp.Evidence
                            .Split(new[] { '\n', '•' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => line.Trim())
                            .Where(line => !string.IsNullOrWhiteSpace(line))
                            .ToList();

                        if (evidenceLines.Count > 1)
                        {
                            sb.Append("          <ul style=\"margin:0 0 12px 0;padding-left:20px;\">\n");
                            foreach (var evidenceLine in evidenceLines)
                            {
                                sb.Append($"            <li style=\"font-size:13px;color:#4b5563;line-height:1.7;margin:4px 0;\">{HE(evidenceLine)}</li>\n");
                            }
                            sb.Append("          </ul>\n");
                        }
                        else
                        {
                            sb.Append($"          <div style=\"font-size:13px;color:#4b5563;line-height:1.7;margin-bottom:12px;\">{HE(hyp.Evidence)}</div>\n");
                        }
                    }

                    // Needs Confirmation
                    if (!string.IsNullOrWhiteSpace(hyp.RequiredToConfirm))
                    {
                        sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:4px;\">Needs Confirmation</div>\n");
                        sb.Append($"          <div style=\"font-size:13px;color:#4b5563;line-height:1.7;\">{HE(hyp.RequiredToConfirm)}</div>\n");
                    }

                    sb.Append("        </div>\n");
                }
            }

            // 4. MISSING EVIDENCE (Use centralized EvidenceValidator as single source of truth)
            var evidenceSummary = f.Bundle != null 
                ? EvidenceValidator.GetSummary(f.Bundle)
                : null;

            var missingEvidence = evidenceSummary?.MissingCategories ?? new List<string>();

            // Only show Missing Evidence section if something is actually missing
            if (missingEvidence.Any())
            {

            sb.Append("        <div class=\"g-section-lbl\">MISSING EVIDENCE</div>\n");
            sb.Append("        <div style=\"margin:12px 0;padding:16px;background:#fef3c7;border:2px solid #f59e0b;border-radius:6px;\">\n");
            sb.Append("          <div style=\"font-size:13px;color:#92400e;margin-bottom:10px;\">The following evidence was unavailable during analysis:</div>\n");
            sb.Append("          <ul style=\"margin:0 0 12px 0;padding-left:20px;font-size:13px;color:#78350f;line-height:1.8;\">\n");
            foreach (var missing in missingEvidence)
            {
                sb.Append($"            <li>{HE(missing)}</li>\n");
            }
            sb.Append("          </ul>\n");
            sb.Append("          <div style=\"font-weight:600;color:#92400e;font-size:13px;border-top:1px solid #f59e0b;padding-top:10px;\">⚠️ Because this evidence is unavailable, the analyzer cannot conclusively determine the exact root cause.</div>\n");
            sb.Append("        </div>\n");
            }  // End missing evidence section

            // 5. RELEVANT SOURCE CODE (Only show 1-2 most relevant snippets, 5-15 lines each)
            if (f.RetrievedChunks.Any())
            {
                sb.Append("        <div class=\"g-section-lbl\">RELEVANT SOURCE CODE</div>\n");

                // Check if any chunks are from fallback search (not exact matches)
                bool hasFallbackChunks = f.RetrievedChunks.Any(c => !c.IsExactMatch);
                if (hasFallbackChunks)
                {
                    sb.Append("        <div style=\"margin:12px 0;padding:12px;background:#fef3c7;border-left:3px solid #f59e0b;\">\n");
                    sb.Append("          <div style=\"font-size:12px;color:#92400e;font-weight:600;\">⚠️ Fallback Code Match</div>\n");
                    sb.Append("          <div style=\"font-size:11px;color:#78350f;margin-top:4px;\">Exact crash site not found in indexed code. Showing similar code based on semantic/keyword search. This may not be the actual failing method.</div>\n");
                    sb.Append("        </div>\n");
                }

                // Detect if this is stack-trace-first retrieval (RelevanceScore = 1.0)
                bool isDebugFocused = f.RetrievedChunks.Any() && f.RetrievedChunks[0].RelevanceScore >= 0.99f;

                if (isDebugFocused)
                {
                    // NEW: Debug-focused display with category labels
                    sb.Append("        <div style=\"margin:12px 0;padding:12px;background:#eff6ff;border-left:3px solid #3b82f6;\">\n");
                    sb.Append("          <div style=\"font-size:12px;color:#1e40af;font-style:italic;\">⚡ Stack-trace-first retrieval: showing crash site and locator definition only</div>\n");
                    sb.Append("        </div>\n");

                    // Priority order: Crash Site → Locator Definition → Helper Methods → Calling Method → Test Method
                    // Group chunks by category
                    var crashSites = new List<RetrievedChunk>();
                    var locatorDefs = new List<RetrievedChunk>();
                    var helperMethods = new List<RetrievedChunk>();
                    var callingMethods = new List<RetrievedChunk>();
                    var testMethods = new List<RetrievedChunk>();

                    foreach (var chunk in f.RetrievedChunks)
                    {
                        string category = InferChunkCategory(chunk, f.RetrievedChunks.IndexOf(chunk));

                        if (category == "Failing Statement" || category == "Crash Site")
                            crashSites.Add(chunk);
                        else if (category == "Locator Definition")
                            locatorDefs.Add(chunk);
                        else if (category == "Method Definition" || category == "Helper Method")
                            helperMethods.Add(chunk);
                        else if (category == "Calling Method")
                            callingMethods.Add(chunk);
                        else if (category == "Calling Test" || category == "Test Method")
                            testMethods.Add(chunk);
                        else
                            helperMethods.Add(chunk); // Default to helper
                    }

                    // Display in priority order, limit to 5-15 lines per snippet
                    var chunksToDisplay = new List<(RetrievedChunk chunk, string category)>();

                    // 1. Crash Site (ALWAYS show if available)
                    if (crashSites.Any())
                        chunksToDisplay.Add((crashSites.First(), "Crash Site"));

                    // 2. Locator Definition (ALWAYS show if available)
                    if (locatorDefs.Any())
                        chunksToDisplay.Add((locatorDefs.First(), "Locator Definition"));

                    // 3. Helper Methods (show up to 2, but only if we have crash site or locator)
                    if (crashSites.Any() || locatorDefs.Any())
                    {
                        foreach (var h in helperMethods.Take(2))
                            chunksToDisplay.Add((h, "Helper Method"));
                    }

                    // 4. Calling Method (show 1 if we have some crash/locator but need more context)
                    if (callingMethods.Any() && chunksToDisplay.Count > 0 && chunksToDisplay.Count < 3)
                        chunksToDisplay.Add((callingMethods.First(), "Calling Method"));

                    // 5. Test Method (ONLY if absolutely nothing else is available - NO production code at all)
                    // STRICT RULE: If we have ANY crash site OR locator, NEVER show test method
                    if (!crashSites.Any() && !locatorDefs.Any() && !helperMethods.Any() && !callingMethods.Any() && testMethods.Any())
                        chunksToDisplay.Add((testMethods.First(), "Test Method"));

                    // Render prioritized chunks
                    foreach (var (chunk, displayCategory) in chunksToDisplay)
                    {
                        var fileName = System.IO.Path.GetFileName(chunk.SourcePath);
                        string categoryColor = GetCategoryColor(displayCategory);
                        string categoryIcon = GetCategoryIcon(displayCategory);

                        sb.Append("        <div style=\"margin:12px 0;padding:14px;background:#f9fafb;border-left:4px solid " + categoryColor + ";border-radius:6px;\">\n");

                        // Category badge
                        sb.Append($"          <div style=\"display:inline-block;background:{categoryColor};color:#ffffff;padding:4px 10px;border-radius:4px;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:10px;\">{categoryIcon} {HE(displayCategory)}</div>\n");

                        // File and line info
                        sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;margin-bottom:4px;\">File: <span style=\"color:#111827;font-weight:700;\">" + HE(fileName) + "</span>");
                        if (chunk.StartLine > 0)
                            sb.Append($" (lines {chunk.StartLine}-{chunk.EndLine})");
                        sb.Append("</div>\n");

                        // Method
                        if (!string.IsNullOrWhiteSpace(chunk.MethodName))
                        {
                            sb.Append($"          <div style=\"font-size:12px;color:#6b7280;margin-bottom:10px;\">Method: <code style=\"background:#ffffff;padding:2px 6px;border:1px solid #e5e7eb;border-radius:3px;color:#111827;font-weight:600;\">{HE(chunk.MethodName)}</code></div>\n");
                        }

                        // Why it is relevant
                        string reason = f.Evidence.RagRetrievalReasons.ContainsKey(f.RetrievedChunks.IndexOf(chunk))
                            ? f.Evidence.RagRetrievalReasons[f.RetrievedChunks.IndexOf(chunk)]
                            : GetDefaultReason(displayCategory);

                        if (!string.IsNullOrWhiteSpace(reason))
                        {
                            sb.Append($"          <div style=\"font-size:12px;color:#6b7280;margin-bottom:10px;font-style:italic;\">→ {HE(reason)}</div>\n");
                        }

                        // Code snippet - LIMIT to 5-15 lines for crash site/locator, slightly more for helpers
                        var lines = chunk.Content.Split('\n');
                        int maxLines = displayCategory == "Crash Site" || displayCategory == "Locator Definition" ? 15 : 20;
                        int lineCount = Math.Min(maxLines, lines.Length);
                        var condensed = string.Join("\n", lines.Take(lineCount));

                        // If we're truncating a large chunk, add indicator
                        if (lines.Length > lineCount)
                            condensed += $"\n... ({lines.Length - lineCount} more lines omitted)";

                        sb.Append($"          <pre style=\"margin:0;background:#ffffff;border:1px solid #e5e7eb;padding:12px;font-size:11px;overflow-x:auto;line-height:1.6;font-family:'Consolas','Monaco','Courier New',monospace;\">{HE(condensed)}</pre>\n");
                        sb.Append("        </div>\n");
                    }
                }
                else
                {
                    // EXISTING: Semantic search display (fallback)
                    // Show only first 2 chunks
                    int maxChunks = Math.Min(2, f.RetrievedChunks.Count);
                    for (int i = 0; i < maxChunks; i++)
                    {
                        var chunk = f.RetrievedChunks[i];
                        var fileName = System.IO.Path.GetFileName(chunk.SourcePath);

                        sb.Append("        <div style=\"margin:12px 0;padding:14px;background:#f9fafb;border:1px solid #e5e7eb;border-radius:6px;\">\n");

                        // File
                        sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:4px;\">File</div>\n");
                        sb.Append($"          <div style=\"font-weight:700;color:#111827;margin-bottom:10px;\">{HE(fileName)}</div>\n");

                        // Method
                        if (!string.IsNullOrWhiteSpace(chunk.MethodName))
                        {
                            sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:4px;\">Method</div>\n");
                            sb.Append($"          <div style=\"font-size:13px;color:#111827;margin-bottom:10px;\"><code style=\"background:#ffffff;padding:2px 6px;border:1px solid #e5e7eb;border-radius:3px;\">{HE(chunk.MethodName)}</code></div>\n");
                        }

                        // Why it is relevant
                        string reason = f.Evidence.RagRetrievalReasons.ContainsKey(i)
                            ? f.Evidence.RagRetrievalReasons[i]
                            : (i == 0 ? "Contains failing method" : "Referenced by failing code");

                        sb.Append("          <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:4px;\">Why it is relevant</div>\n");
                        sb.Append($"          <div style=\"font-size:13px;color:#6b7280;margin-bottom:10px;\">{HE(reason)}</div>\n");

                        // Show only 5-15 lines
                        var lines = chunk.Content.Split('\n');
                        int lineCount = Math.Min(15, Math.Max(5, lines.Length));
                        var condensed = string.Join("\n", lines.Take(lineCount));
                        if (lines.Length > lineCount)
                            condensed += "\n... (truncated)";

                        sb.Append($"          <pre style=\"margin:0;background:#ffffff;border:1px solid #e5e7eb;padding:12px;font-size:12px;overflow-x:auto;line-height:1.5;\">{HE(condensed)}</pre>\n");
                        sb.Append("        </div>\n");
                    }
                }
            }

            // ═══════════════════════════════════════════════════════════════
            // 5a. SUGGESTED CODE FIX (Gated on exact match + script attribution) — NEW
            // ═══════════════════════════════════════════════════════════════
            if (f.Fix != null && !string.IsNullOrWhiteSpace(f.Fix.Explanation))
            {
                sb.Append("        <div class=\"g-section-lbl\">SUGGESTED CODE FIX</div>\n");

                // Show fix if proposed, or show gating reason if not
                if (!string.IsNullOrWhiteSpace(f.Fix.ProposedCode))
                {
                    // Proposed fix exists
                    sb.Append("        <div style=\"margin:12px 0;padding:16px;background:#ecfdf5;border:2px solid #10b981;border-radius:6px;\">\n");
                    sb.Append($"          <div style=\"font-size:13px;color:#065f46;margin-bottom:8px;font-weight:600;\">✨ Proposed Fix ({HE(f.Fix.ConfidenceLevel)} confidence)</div>\n");

                    if (!string.IsNullOrWhiteSpace(f.Fix.FilePath))
                    {
                        sb.Append($"          <div style=\"font-size:12px;color:#6b7280;margin-bottom:12px;\"><strong>File:</strong> {HE(f.Fix.FilePath)}</div>\n");
                    }

                    // Explanation
                    sb.Append($"          <div style=\"margin-bottom:12px;font-size:14px;color:#065f46;line-height:1.6;\">{HE(f.Fix.Explanation)}</div>\n");

                    // Current code
                    if (!string.IsNullOrWhiteSpace(f.Fix.CurrentCode))
                    {
                        sb.Append("          <div style=\"margin-bottom:12px;\">\n");
                        sb.Append("            <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:6px;\">Current Code</div>\n");
                        sb.Append("            <pre style=\"margin:0;padding:12px;background:#f9fafb;border:1px solid #d1d5db;border-radius:4px;font-size:12px;overflow-x:auto;\">");
                        sb.Append(HE(f.Fix.CurrentCode));
                        sb.Append("</pre>\n");
                        sb.Append("          </div>\n");
                    }

                    // Proposed code
                    sb.Append("          <div style=\"margin-bottom:12px;\">\n");
                    sb.Append("            <div style=\"font-size:12px;font-weight:600;color:#6b7280;text-transform:uppercase;letter-spacing:0.05em;margin-bottom:6px;\">Proposed Code</div>\n");
                    sb.Append("            <pre style=\"margin:0;padding:12px;background:#ecfdf5;border:2px solid #10b981;border-radius:4px;font-size:12px;overflow-x:auto;\">");
                    sb.Append(HE(f.Fix.ProposedCode));
                    sb.Append("</pre>\n");
                    sb.Append("          </div>\n");

                    // Gating reason (safety check)
                    if (!string.IsNullOrWhiteSpace(f.Fix.GatingReason))
                    {
                        sb.Append($"          <div style=\"font-size:12px;color:#6b7280;font-style:italic;\">🔒 {HE(f.Fix.GatingReason)}</div>\n");
                    }

                    sb.Append("        </div>\n");
                }
                else
                {
                    // No fix proposed - show why
                    sb.Append("        <div style=\"margin:12px 0;padding:16px;background:#fef3c7;border:2px solid #f59e0b;border-radius:6px;\">\n");
                    sb.Append("          <div style=\"font-size:13px;color:#92400e;margin-bottom:6px;font-weight:600;\">⚠️ No Code Fix Proposed</div>\n");
                    sb.Append($"          <div style=\"font-size:14px;color:#92400e;line-height:1.6;\">{HE(f.Fix.Explanation)}</div>\n");

                    if (!string.IsNullOrWhiteSpace(f.Fix.GatingReason))
                    {
                        sb.Append($"          <div style=\"margin-top:8px;font-size:12px;color:#78350f;font-style:italic;\">🔒 {HE(f.Fix.GatingReason)}</div>\n");
                    }

                    sb.Append("        </div>\n");
                }
            }

            // 6. RECOMMENDED INVESTIGATION (Investigation steps only, not code fixes)
            if (f.RecommendedInvestigation.Any())
            {
                sb.Append("        <div class=\"g-section-lbl\">RECOMMENDED INVESTIGATION</div>\n");
                sb.Append("        <div style=\"margin:12px 0;padding:16px;background:#eff6ff;border:2px solid #3b82f6;border-radius:6px;\">\n");
                sb.Append("          <div style=\"font-size:13px;color:#1e3a8a;margin-bottom:6px;font-weight:600;\">🔍 Investigation Steps</div>\n");
                sb.Append("          <div style=\"font-size:12px;color:#1e40af;margin-bottom:10px;font-style:italic;\">Verify the hypothesis before implementing code changes:</div>\n");
                sb.Append("          <ol style=\"margin:0;padding-left:20px;font-size:13px;line-height:1.8;color:#1e40af;\">\n");
                foreach (var step in f.RecommendedInvestigation)
                {
                    sb.Append($"            <li>{HE(step)}</li>\n");
                }
                sb.Append("          </ol>\n");
                sb.Append("        </div>\n");
            }

            sb.Append("      </div>\n"); // End Card
        }

        // Down arrow scroll indicator
        sb.Append("      <div class=\"g-scroll-btn\">&#8595;</div>\n");

        sb.Append("    </div>\n  </div>\n</div>\n"); // End Area, Main, Layout

        // ── JavaScript ────────────────────────────────────────────────
        var jsCats = "[" + string.Join(",", analysis.Failures.Select(f =>
            "\"" + f.Category.Replace("_", " ").Replace("\"", "\\\"") + "\"")) + "]";

        sb.Append("<script>\n");
        sb.Append($"var total={total}, cats={jsCats}, cur=0;\n");
        sb.Append(@"var cards=[], lis=[];
for(var i=1;i<=total;i++){
  cards.push(document.getElementById('card-'+i));
  lis.push(document.getElementById('li-'+i));
}
var P=document.getElementById('g-prev'),
    N=document.getElementById('g-next'),
    F=document.getElementById('g-frac'),
    S=document.getElementById('g-sub-strong'),
    B=document.getElementById('g-progf');

function showCard(idx){
  if(total===0) return;
  cards[cur].style.display='none';
  lis[cur].classList.remove('g-ti-on');
  cur=idx;
  cards[cur].style.display='block';
  lis[cur].classList.add('g-ti-on');
  F.textContent=(cur+1)+' / '+total;
  S.textContent=(cur+1);
  B.style.width=((cur+1)/total*100)+'%';
  P.disabled=(cur===0);
  N.disabled=(cur===total-1);
  document.querySelector('.g-main').scrollTop=0;
}
function navigate(d){ var n=cur+d; if(n>=0&&n<total) showCard(n); }
document.addEventListener('keydown',function(e){
  if(e.key==='ArrowRight') navigate(1);
  if(e.key==='ArrowLeft')  navigate(-1);
});
showCard(0);

var handle=document.getElementById('g-resize');
var sidebar=document.querySelector('.g-side');
var dragging=false,startX=0,startW=0;
handle.addEventListener('mousedown',function(e){
  dragging=true; startX=e.clientX; startW=sidebar.offsetWidth;
  handle.classList.add('is-dragging');
  document.body.style.cursor='col-resize';
  document.body.style.userSelect='none';
  e.preventDefault();
});
document.addEventListener('mousemove',function(e){
  if(!dragging) return;
  var w=startW+(e.clientX-startX);
  if(w>=200&&w<=600) sidebar.style.width=w+'px';
});
document.addEventListener('mouseup',function(){
  if(!dragging) return;
  dragging=false;
  handle.classList.remove('is-dragging');
  document.body.style.cursor='';
  document.body.style.userSelect='';
});
");
        sb.Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string HE(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    /// <summary>
    /// Infers the category of a code chunk for debug-focused display.
    /// Looks at position and content to determine: Failing Statement, Locator Definition, etc.
    /// </summary>
    private static string InferChunkCategory(Models.RetrievedChunk chunk, int index)
    {
        var content = chunk.Content.ToLower();
        var methodName = chunk.MethodName?.ToLower() ?? "";

        // 1. Crash Site / Failing Statement (first chunk or contains exception line)
        if (index == 0 || content.Contains("// line") || content.Contains("exception"))
            return "Crash Site";

        // 2. Locator Definition (property with AutomationElement, FindElement, etc.)
        if (content.Contains("=>") && (content.Contains("automationid") || content.Contains("findfirst") 
            || content.Contains("byclassname") || content.Contains("findelement")))
            return "Locator Definition";

        if (content.Contains("private") && content.Contains("automationelement"))
            return "Locator Definition";

        if (content.Contains("public") && content.Contains("automationelement") && content.Contains("=>"))
            return "Locator Definition";

        // 3. Test Method (contains [TestMethod], [Test], [Fact])
        if (content.Contains("[testmethod]") || content.Contains("[test]") || content.Contains("[fact]"))
            return "Test Method";

        // 4. Helper Method (anything else that's not a test)
        if (methodName.Contains("helper") || methodName.Contains("create") || methodName.Contains("click"))
            return "Helper Method";

        // 5. Calling Method (default for methods in call chain)
        return "Calling Method";
    }

    private static string GetCategoryColor(string category)
    {
        return category switch
        {
            "Crash Site" => "#dc2626",          // Red
            "Failing Statement" => "#dc2626",   // Red (alias)
            "Locator Definition" => "#ea580c",  // Orange
            "Helper Method" => "#0284c7",       // Blue
            "Method Definition" => "#0284c7",   // Blue (alias)
            "Calling Method" => "#8b5cf6",      // Purple
            "Test Method" => "#7c3aed",         // Dark Purple
            "Calling Test" => "#7c3aed",        // Dark Purple (alias)
            _ => "#6b7280"                      // Gray
        };
    }

    private static string GetCategoryIcon(string category)
    {
        return category switch
        {
            "Crash Site" => "💥",
            "Failing Statement" => "⚠️",
            "Locator Definition" => "🎯",
            "Helper Method" => "🔧",
            "Method Definition" => "📦",
            "Calling Method" => "📞",
            "Test Method" => "🧪",
            "Calling Test" => "🧪",
            _ => "📄"
        };
    }

    private static string GetDefaultReason(string category)
    {
        return category switch
        {
            "Crash Site" => "Exception occurred at this line",
            "Failing Statement" => "Exception occurred at this line",
            "Locator Definition" => "Element locator used by failing statement",
            "Helper Method" => "Helper method called by failing code",
            "Method Definition" => "Contains the failing line",
            "Calling Method" => "Calls the method that failed",
            "Test Method" => "Test method that invokes the failure",
            "Calling Test" => "Test method that invokes the failure",
            _ => "Related to the failure"
        };
    }

    private static List<string> ExtractObservedFacts(string investigationNotes)
    {
        var facts = new List<string>();

        if (string.IsNullOrWhiteSpace(investigationNotes))
            return facts;

        // Parse investigation notes structure (sections 1-3 are facts, section 4 is inference)
        var lines = investigationNotes.Split('\n');
        bool inFactSection = false;
        int sectionCount = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Detect section headers (1., 2., 3., 4.)
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+\.\s"))
            {
                sectionCount++;
                inFactSection = sectionCount <= 3; // Sections 1-3 are facts
                continue;
            }

            // If in fact section and line starts with bullet or dash, extract it
            if (inFactSection && (trimmed.StartsWith("•") || trimmed.StartsWith("-") || trimmed.StartsWith("*")))
            {
                var fact = trimmed.TrimStart('•', '-', '*').Trim();
                if (!string.IsNullOrWhiteSpace(fact) && fact.Length > 10)
                {
                    facts.Add(fact);
                }
            }
            // Also capture lines that look like facts (e.g., "Exception: ...", "Stack trace: ...")
            else if (inFactSection && trimmed.Contains(":") && trimmed.Length > 15 && trimmed.Length < 200)
            {
                facts.Add(trimmed);
            }
        }

        // If no facts were extracted, try to extract key sentences
        if (!facts.Any())
        {
            // Use regex to split on sentence boundaries (period followed by space and capital letter)
            // This avoids splitting on periods in exception names like "System.IO.FileNotFoundException"
            var sentencePattern = new System.Text.RegularExpressions.Regex(@"(?<=[.!?])\s+(?=[A-Z])");
            var sentences = sentencePattern.Split(investigationNotes)
                .Select(s => s.Trim())
                .Where(s => s.Length > 20 && s.Length < 300 && !s.StartsWith("This suggests") && !s.StartsWith("One possible"))
                .Take(8)
                .ToList();

            facts.AddRange(sentences);
        }

        return facts.Take(10).ToList(); // Limit to 10 facts
    }

    public string GenerateMarkdown(RunAnalysis analysis)
    {
        var sb = new StringBuilder();
        var r = analysis.Run;
        sb.AppendLine($"# AI-Powered Test Failure Analyzer \u2014 {r.RunName}");
        sb.AppendLine($"\n**Date:** {analysis.GeneratedAt:yyyy-MM-dd HH:mm} UTC  ");
        sb.AppendLine($"**Environment:** {analysis.Environment}  ");
        sb.AppendLine($"**Results:** {r.Failed} failed / {r.Passed} passed / {r.Total} total\n");
        sb.AppendLine("---\n");
        if (analysis.Patterns.Any())
        {
            sb.AppendLine("## Cross-Cutting Patterns\n");
            foreach (var p in analysis.Patterns) sb.AppendLine($"- {p}");
            if (!string.IsNullOrWhiteSpace(analysis.EnvironmentNotes))
                sb.AppendLine($"\n> **Environment:** {analysis.EnvironmentNotes}");
            sb.AppendLine("\n---\n");
        }
        sb.AppendLine("## Failed Tests\n");
        int i = 1;
        foreach (var f in analysis.Failures)
        {
            sb.AppendLine($"### {i++}. {f.ShortName}");
            sb.AppendLine($"**Severity:** {f.Severity} | **Category:** {f.Category.Replace("_", " ")}  ");
            sb.AppendLine($"**Full name:** `{f.TestName}`\n");
            sb.AppendLine($"**Error:** {f.ErrorSummary}\n");

            // Fault Attribution (NEW)
            if (f.Attribution != null)
            {
                string primaryIcon = f.Attribution.Primary switch
                {
                    "SCRIPT" => "🔧",
                    "APPLICATION" => "🐛",
                    "ENVIRONMENT" => "🌐",
                    "DATA" => "💾",
                    _ => "❓"
                };

                string attrConfidenceText = GetConfidenceText(f.Attribution.Confidence);
                sb.AppendLine($"**Fault Attribution:** {primaryIcon} **{f.Attribution.Primary}** ({attrConfidenceText}, {f.Attribution.Confidence}%)\n");

                if (f.Attribution.SecondaryFactors.Any())
                {
                    sb.AppendLine("**Secondary Contributing Factors:**");
                    foreach (var factor in f.Attribution.SecondaryFactors)
                    {
                        string factorIcon = factor.Type switch
                        {
                            "SCRIPT" => "🔧",
                            "APPLICATION" => "🐛",
                            "ENVIRONMENT" => "🌐",
                            "DATA" => "💾",
                            _ => "•"
                        };
                        sb.AppendLine($"- {factorIcon} **{factor.Type}:** {factor.Description}");
                        if (!string.IsNullOrWhiteSpace(factor.WhyItMatters))
                        {
                            sb.AppendLine($"  - *{factor.WhyItMatters}*");
                        }
                    }
                    sb.AppendLine();
                }
            }


            // Add detailed investigation notes if available
            if (!string.IsNullOrWhiteSpace(f.InvestigationNotes))
            {
                sb.AppendLine($"**Detailed Investigation:**");
                sb.AppendLine($"> {f.InvestigationNotes.Replace("\n", "\n> ")}\n");
            }

            if (!string.IsNullOrWhiteSpace(f.PrimaryCause))
                sb.AppendLine($"**Root Cause:** {f.PrimaryCause}\n");
            if (!string.IsNullOrWhiteSpace(f.IssueOwner))
            {
                string ownerIcon = f.IssueOwner.ToLowerInvariant() switch
                {
                    "script" => "🔧",
                    "application" => "🐛",
                    "insufficient_evidence" => "⚠️",
                    "uncertain" => "❓",
                    _ => "❓"
                };

                string displayText = f.IssueOwner.ToLowerInvariant() == "insufficient_evidence"
                    ? "INSUFFICIENT EVIDENCE"
                    : f.IssueOwner.ToUpper();

                sb.AppendLine($"**Issue Owner:** {ownerIcon} **{displayText}**");
                if (!string.IsNullOrWhiteSpace(f.IssueOwnerRationale))
                    sb.AppendLine($"- {f.IssueOwnerRationale}");
                sb.AppendLine();
            }
            if (f.ContributingFactors.Any())
            {
                sb.AppendLine("**Contributing Factors:**");
                int n = 1;
                foreach (var cf in f.ContributingFactors) sb.AppendLine($"{n++}. {cf}");
                sb.AppendLine();
            }

            // Suggested Code Fix (NEW - gated)
            if (f.Fix != null && !string.IsNullOrWhiteSpace(f.Fix.Explanation))
            {
                if (!string.IsNullOrWhiteSpace(f.Fix.ProposedCode))
                {
                    // Proposed fix exists
                    sb.AppendLine($"**✨ Suggested Code Fix ({f.Fix.ConfidenceLevel} confidence):**");
                    if (!string.IsNullOrWhiteSpace(f.Fix.FilePath))
                    {
                        sb.AppendLine($"- **File:** `{f.Fix.FilePath}`");
                    }
                    sb.AppendLine($"- **Explanation:** {f.Fix.Explanation}");

                    if (!string.IsNullOrWhiteSpace(f.Fix.CurrentCode))
                    {
                        sb.AppendLine("\n**Current Code:**");
                        sb.AppendLine("```csharp");
                        sb.AppendLine(f.Fix.CurrentCode);
                        sb.AppendLine("```");
                    }

                    sb.AppendLine("\n**Proposed Code:**");
                    sb.AppendLine("```csharp");
                    sb.AppendLine(f.Fix.ProposedCode);
                    sb.AppendLine("```");

                    if (!string.IsNullOrWhiteSpace(f.Fix.GatingReason))
                    {
                        sb.AppendLine($"\n🔒 *{f.Fix.GatingReason}*");
                    }
                    sb.AppendLine();
                }
                else
                {
                    // No fix proposed - show why
                    sb.AppendLine($"**⚠️ No Code Fix Proposed:**");
                    sb.AppendLine($"- {f.Fix.Explanation}");
                    if (!string.IsNullOrWhiteSpace(f.Fix.GatingReason))
                    {
                        sb.AppendLine($"- 🔒 *{f.Fix.GatingReason}*");
                    }
                    sb.AppendLine();
                }
            }

            if (f.Suggestions.Any())
            {
                sb.AppendLine("**Fix Suggestions:**");
                foreach (var s in f.Suggestions)
                    sb.AppendLine($"- [{s.Priority.ToUpper()}] `{s.Type}` \u2014 {s.Action}");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(f.CodeSnippet))
                sb.AppendLine($"**Suggested Code:**\n```csharp\n{f.CodeSnippet}\n```\n");
            sb.AppendLine("---\n");
        }
        return sb.ToString();
    }

    private const string Css = @"
@import url('https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800&family=JetBrains+Mono:wght@400;500&display=swap');

*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

:root {
  --bg:        #fafafa;
  --surface:   #ffffff;
  --border:    #e5e7eb;
  --border-lt: #f3f4f6;
  --text-1:    #111827;
  --text-2:    #374151;
  --text-3:    #6b7280;
  --text-4:    #9ca3af;
  --blue:      #2563eb;
  --blue-lt:   #eff6ff;
  --red:       #dc2626;
  --red-bd:    #fca5a5;
  --green:     #16a34a;
}

body {
  font-family: 'Inter', sans-serif;
  font-size: 14px; line-height: 1.5;
  background: var(--bg); color: var(--text-2);
  height: 100vh; display: flex; flex-direction: column;
  overflow: hidden; -webkit-font-smoothing: antialiased;
}

/* ─────────────────────────────────────────
   HEADER
───────────────────────────────────────── */
.g-hdr {
  position: relative;
  background: var(--surface);
  padding: 16px 24px;
  flex-shrink: 0;
  border-bottom: 1px solid var(--border);
  display: flex;
  justify-content: center; /* Centered */
  align-items: center;
}
.g-hdr-center {
  display: flex; align-items: center; gap: 8px;
}
.g-hdr-dot {
  width: 10px; height: 10px; background: #93c5fd; border-radius: 50%;
}
.g-htitle {
  font-size: 16px; font-weight: 600; color: var(--text-1);
}
.g-hdr-right {
  position: absolute; right: 24px;
  font-size: 13px; color: var(--text-4); font-weight: 400;
}

/* ─────────────────────────────────────────
   PATTERNS BAR
───────────────────────────────────────── */
.g-patbar {
  background: var(--surface);
  border-bottom: 1px solid var(--border);
  padding: 20px 32px; flex-shrink: 0;
}
.g-lbl {
  font-size: 11px; font-weight: 600; color: var(--text-4);
  text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 12px;
}
.g-pat {
  display: flex; gap: 12px; font-size: 14px;
  color: var(--text-2); padding: 4px 0; align-items: center;
}
.g-arr { color: var(--blue); font-weight: 400; }

/* ─────────────────────────────────────────
   LAYOUT & SIDEBAR
───────────────────────────────────────── */
.g-layout { display: flex; flex: 1; overflow: hidden; }

.g-side {
  width: 340px; min-width: 240px; max-width: 500px;
  background: var(--bg);
  border-right: 1px solid var(--border);
  padding: 24px;
  display: flex; flex-direction: column;
  flex-shrink: 0; overflow: hidden;
}

/* ─────────────────────────────────────────
   2x2 STATS GRID
───────────────────────────────────────── */
.g-stats-grid {
  display: grid; grid-template-columns: 1fr 1fr; gap: 12px;
  margin-bottom: 32px;
}
.g-stat-card {
  background: var(--surface); border: 1px solid var(--border);
  border-radius: 8px; padding: 16px 12px; text-align: center;
}
.g-stat-num {
  font-size: 24px; font-weight: 500; color: var(--text-1); line-height: 1;
}
.stat-fail { color: var(--red); }
.stat-pass { color: var(--green); }
.g-stat-lbl {
  font-size: 11px; font-weight: 500; color: var(--text-4);
  text-transform: uppercase; letter-spacing: 0.05em; margin-top: 8px;
}

/* ─────────────────────────────────────────
   TEST LIST
───────────────────────────────────────── */
.g-tlist-wrap {
  flex: 1; overflow-y: auto; display: flex; flex-direction: column;
}
.g-tlist { list-style: none; display: flex; flex-direction: column; gap: 4px; }
.g-ti {
  display: flex; align-items: center; gap: 12px;
  padding: 10px 12px; border-radius: 6px; cursor: pointer; 
  transition: background 0.15s; border: 1px solid transparent;
}
.g-ti:hover:not(.g-ti-on) { background: #f3f4f6; }
.g-ti-on { background: var(--blue-lt); border-color: #bfdbfe; }
.g-tdot { width: 6px; height: 6px; border-radius: 50%; background: #d1d5db; flex-shrink: 0; }
.g-ti-on .g-tdot { background: var(--blue); }
.g-tname {
  flex: 1; font-size: 13px; font-weight: 400; color: var(--text-2);
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}
.g-ti-on .g-tname { color: var(--blue); font-weight: 500; }
.g-tnum { font-size: 12px; color: var(--text-4); flex-shrink: 0; }

/* ─────────────────────────────────────────
   RESIZE HANDLE
───────────────────────────────────────── */
.g-resize {
  width: 1px; background: var(--border);
  cursor: col-resize; flex-shrink: 0; position: relative; z-index: 10;
}
.g-resize::after {
  content: ''; position: absolute; inset: 0 -4px;
}
.g-resize:hover, .g-resize.is-dragging { background: var(--blue); }

/* ─────────────────────────────────────────
   MAIN PANEL
───────────────────────────────────────── */
.g-main { flex: 1; overflow-y: auto; padding: 40px 60px; background: var(--surface); }

.g-topbar {
  display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px;
}
.g-topbar-title { font-size: 14px; color: var(--text-3); }
.g-topbar-title strong { color: var(--text-1); font-weight: 600; }

.g-nav { display: flex; align-items: center; gap: 12px; }
.g-abtn {
  background: var(--surface); border: 1px solid var(--border);
  border-radius: 50%; width: 32px; height: 32px; cursor: pointer;
  font-size: 14px; color: var(--text-2); display: flex; align-items: center; justify-content: center;
}
.g-abtn:hover:not([disabled]) { background: var(--bg); border-color: #d1d5db; }
.g-abtn[disabled] { opacity: 0.3; cursor: not-allowed; }
.g-frac { font-size: 14px; color: var(--text-3); }

.g-prog { height: 2px; background: var(--border-lt); margin-bottom: 32px; }
.g-progf { height: 100%; background: #93c5fd; transition: width 0.3s ease; }

/* ─────────────────────────────────────────
   CARD & CONTENT
───────────────────────────────────────── */
.g-card { max-width: 800px; }

.g-card-kicker { font-size: 13px; color: var(--text-3); margin-bottom: 8px; }
.g-th { font-size: 20px; font-weight: 500; color: var(--text-1); line-height: 1.3; margin-bottom: 4px; }
.g-tfull { font-family: 'JetBrains Mono', monospace; font-size: 12px; color: var(--text-4); margin-bottom: 16px; word-break: break-all; }

.g-bdgs { display: flex; gap: 8px; margin-bottom: 32px; }
.g-bdg-simple { 
  padding: 4px 12px; 
  border-radius: 4px; 
  border: 1px solid #d1d5db; 
  font-size: 12px; 
  font-weight: 400; 
  background: #f9fafb; 
  color: #4b5563; 
}

.g-section-lbl {
  font-size: 12px; font-weight: 500; color: var(--text-4);
  text-transform: uppercase; letter-spacing: 0.05em; margin: 32px 0 16px;
  display: flex; align-items: center;
}
.g-section-lbl::after {
  content: ''; flex: 1; height: 1px; background: var(--border-lt); margin-left: 16px;
}

/* ─── ALERT BLOCKS ─── */
.g-alert {
  padding: 16px 20px; background: var(--surface);
  border: 1px solid var(--border); border-radius: 6px;
}
.g-alert-error { border-left: 3px solid var(--red-bd); }
.g-alert-info { border-left: 3px solid #93c5fd; }

.g-alert-msg { font-family: 'JetBrains Mono', monospace; font-size: 13px; color: var(--text-2); white-space: pre-wrap; word-break: break-word; }
.g-alert-msg-sans { font-size: 14px; color: var(--text-1); line-height: 1.6; }

/* ─── ISSUE OWNER CARD ─── */
.g-owner-card {
  padding: 16px 20px; background: var(--surface);
  border: 1px solid var(--border); border-radius: 6px;
  border-left: 3px solid var(--border);
}
.g-owner-script {
  border-left-color: #f59e0b; /* amber - test/script issue */
  background: #fffbeb;
}
.g-owner-app {
  border-left-color: #dc2626; /* red - application bug */
  background: #fef2f2;
}
.g-owner-uncertain {
  border-left-color: #6b7280; /* gray - uncertain */
  background: #f9fafb;
}
.g-owner-label {
  font-size: 15px; font-weight: 600; color: var(--text-1);
  margin-bottom: 8px;
}
.g-owner-rationale {
  font-size: 14px; color: var(--text-2); line-height: 1.6;
}

/* ─── CONTRIBUTING FACTORS ─── */
.g-facts { display: flex; flex-direction: column; gap: 12px; }
.g-frow { display: flex; gap: 16px; align-items: flex-start; }
.g-fnum {
  background: var(--blue-lt); color: var(--blue); border-radius: 4px;
  min-width: 24px; height: 24px; display: flex; align-items: center; justify-content: center;
  font-size: 12px; font-weight: 500; flex-shrink: 0;
}
.g-ftxt { font-size: 14px; color: var(--text-2); line-height: 1.6; padding-top: 2px;}

/* ─── FIX SUGGESTIONS ─── */
.g-suggs { display: flex; flex-direction: column; gap: 12px; }
.g-srow {
  display: flex; gap: 16px; padding: 16px;
  border: 1px solid var(--border); border-radius: 8px; align-items: flex-start;
}
.g-sico {
  min-width: 32px; height: 32px; border-radius: 6px;
  display: flex; align-items: center; justify-content: center;
  font-size: 16px; flex-shrink: 0;
}
.g-sbdy { flex: 1; }
.g-sact { font-size: 14px; color: var(--text-1); line-height: 1.6; margin-bottom: 12px; }
.g-stags { display: flex; gap: 8px; }
.g-stag {
  font-size: 11px; padding: 2px 8px; border: 1px solid var(--border); border-radius: 4px;
  color: var(--text-3); background: var(--surface);
}
.g-stag-imm { background: #fee2e2; color: #991b1b; border-color: #fecaca; }

/* ─── EVIDENCE SECTIONS (NEW) ─── */
.g-timeline {
  display: flex; flex-direction: column; gap: 8px;
}
.g-timeline-event {
  padding: 8px 12px; background: #f9fafb; border-left: 3px solid #3b82f6;
  font-family: 'JetBrains Mono', monospace; font-size: 12px; color: #374151;
}

.g-evidence-section {
  margin: 12px 0; border: 1px solid #d1d5db; border-radius: 6px; padding: 0;
}
.g-evidence-section summary {
  padding: 12px 16px; background: #f9fafb; cursor: pointer;
  font-weight: 600; color: #374151; border-radius: 6px;
  user-select: none; list-style: none;
}
.g-evidence-section summary::-webkit-details-marker {
  display: none;
}
.g-evidence-section summary::before {
  content: '▶'; display: inline-block; margin-right: 8px;
  transition: transform 0.2s; font-size: 10px;
}
.g-evidence-section[open] summary::before {
  transform: rotate(90deg);
}
.g-evidence-section summary:hover {
  background: #f3f4f6;
}
.g-evidence-section[open] summary {
  border-bottom: 1px solid #d1d5db; border-radius: 6px 6px 0 0;
}
.g-evidence-section > div {
  padding: 16px;
}

.g-locator-table {
  width: 100%; font-size: 13px; border-collapse: collapse;
}
.g-locator-table td {
  padding: 6px 12px; border-bottom: 1px solid #e5e7eb;
}
.g-locator-table tr:last-child td {
  border-bottom: none;
}
.g-locator-table td:first-child {
  font-weight: 600; color: #6b7280; width: 180px;
}
.g-locator-table td:last-child {
  color: #111827; font-family: 'JetBrains Mono', monospace;
}

.g-rag-chunks {
  display: flex; flex-direction: column; gap: 12px;
}
.g-rag-chunk {
  background: white; border: 1px solid #e5e7eb; border-radius: 4px; padding: 12px;
}
.g-rag-header {
  font-weight: 600; color: #111827; margin-bottom: 4px; font-size: 13px;
}
.g-rag-reason {
  font-size: 12px; color: #6b7280; margin-bottom: 8px; font-style: italic;
}

/* ─── SCROLL ARROW ─── */
.g-scroll-btn {
  width: 40px; height: 40px; border-radius: 50%; border: 1px solid var(--border);
  display: flex; align-items: center; justify-content: center;
  margin: 48px auto 0; color: var(--text-3); background: var(--surface);
  box-shadow: 0 2px 4px rgba(0,0,0,0.05);
}

/* ─── CODE BLOCK ─── */
.g-details { margin-top: 24px; border: 1px solid var(--border); border-radius: 6px; overflow: hidden; }
.g-details summary {
  padding: 12px 16px; font-size: 13px; font-weight: 500; color: var(--text-2);
  cursor: pointer; background: var(--bg); border-bottom: 1px solid var(--border);
}
.g-code { 
  font-family: 'JetBrains Mono', monospace; 
  font-size: 13px; 
  background: var(--surface); 
  padding: 16px; 
  overflow-x: auto; 
  white-space: pre-wrap;
  word-wrap: break-word;
  line-height: 1.5;
}

/* ─── RAG CHUNKS VISUALIZATION ─── */
.g-rag-details summary { background: linear-gradient(135deg, #f0f9ff 0%, #e0f2fe 100%); }
.g-rag-info { padding: 16px; background: var(--surface); }
.g-rag-chunk {
  background: var(--bg); border: 1px solid var(--border);
  border-radius: 6px; padding: 12px; margin-bottom: 12px;
}
.g-rag-chunk:last-child { margin-bottom: 0; }
.g-rag-header {
  display: flex; justify-content: space-between; align-items: center;
  margin-bottom: 8px; padding-bottom: 8px; border-bottom: 1px solid var(--border);
}
.g-rag-file {
  font-family: 'JetBrains Mono', monospace; font-size: 12px;
  font-weight: 600; color: var(--text-1);
}
.g-rag-lines {
  font-size: 11px; color: var(--text-3);
  background: var(--surface); padding: 2px 8px; border-radius: 4px;
}
.g-rag-scores {
  display: flex; gap: 12px; margin-bottom: 8px; flex-wrap: wrap;
}
.g-rag-score {
  font-size: 11px; padding: 4px 8px; background: var(--surface);
  border-radius: 4px; color: var(--text-2); border: 1px solid var(--border);
}
.g-rag-content {
  font-family: 'JetBrains Mono', monospace; font-size: 11px;
  background: var(--surface); padding: 12px; border-radius: 4px;
  overflow-x: auto; margin: 0; color: var(--text-2); line-height: 1.5;
  max-height: 200px; overflow-y: auto;
}
";

    // Helper to explain confidence scores
    private static string GetConfidenceReason(int confidence)
    {
        return confidence switch
        {
            >= 90 => "Very confident, clear evidence",
            >= 70 => "Reasonably confident, good evidence",
            >= 50 => "Moderate confidence, some ambiguity",
            _ => "Low confidence, limited or conflicting evidence"
        };
    }

    private static string ExtractFailureLocation(FailureAnalysis f)
    {
        // Try to extract file:line from stack trace or evidence
        if (!string.IsNullOrWhiteSpace(f.Evidence.TestFrameworkEvidence))
        {
            // Look for common stack trace patterns: "at Method() in File.cs:line 123"
            var match = System.Text.RegularExpressions.Regex.Match(
                f.Evidence.TestFrameworkEvidence, 
                @"in\s+(.+?\.cs):line\s+(\d+)", 
                System.Text.RegularExpressions.RegexOptions.Multiline);

            if (match.Success)
            {
                var file = System.IO.Path.GetFileName(match.Groups[1].Value);
                var line = match.Groups[2].Value;
                return $"{file}:line {line}";
            }
        }

        // Fallback: try from error summary
        if (!string.IsNullOrWhiteSpace(f.ErrorSummary))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                f.ErrorSummary, 
                @"at\s+(\w+\.\w+)\s+in\s+(.+?\.cs):line\s+(\d+)");

            if (match.Success)
            {
                var method = match.Groups[1].Value;
                var file = System.IO.Path.GetFileName(match.Groups[2].Value);
                var line = match.Groups[3].Value;
                return $"{file}:line {line} ({method})";
            }
        }

        return string.Empty;
    }

    private static string GetConfidenceText(int confidence)
    {
        // Explicit thresholds: ≥85 = High, 60-84 = Medium, <60 = Low
        return confidence switch
        {
            >= 85 => "High",
            >= 60 => "Medium",
            _ => "Low"
        };
    }

    private static string GetConfidenceColor(int confidence)
    {
        // Match color to the same thresholds
        return confidence switch
        {
            >= 85 => "#059669", // Green - High
            >= 60 => "#d97706", // Orange - Medium
            _ => "#dc2626"      // Red - Low
        };
    }

    /// <summary>
    /// Determine confidence level based on available evidence
    /// </summary>
    private static string DetermineConfidenceLevel(FailureAnalysis f)
    {
        // Use the actual hypothesis confidence values (which have been capped by evidence-tier logic)
        // to determine the display level using explicit thresholds:
        // ≥85 = High, 60-84 = Medium, <60 = Low

        if (!f.Hypotheses.Any())
        {
            return "low";  // No hypotheses → low confidence
        }

        // Use the highest hypothesis confidence (primary/leading hypothesis)
        int maxConfidence = f.Hypotheses.Max(h => h.Confidence);

        // Apply explicit thresholds - label must match number
        if (maxConfidence >= 85)
            return "high";
        else if (maxConfidence >= 60)
            return "medium";
        else
            return "low";
    }
}
