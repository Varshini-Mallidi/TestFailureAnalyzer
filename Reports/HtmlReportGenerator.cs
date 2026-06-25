using System.Text;
using FailureAnalyzer.Models;

namespace FailureAnalyzer.Reports;

public class HtmlReportGenerator
{
    public string Generate(RunAnalysis analysis)
    {
        var sb = new StringBuilder();
        var r = analysis.Run;
        var date = analysis.GeneratedAt.ToString("yyyy-MM-dd HH:mm UTC");

        sb.AppendLine($$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Test Failure Report — {{HE(r.RunName)}}</title>
<style>
  /* All CSS now uses normal single braces */
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0 }
  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; font-size: 14px; line-height: 1.6; background: #f8f8f7; color: #1a1a1a }
  .page { max-width: 900px; margin: 0 auto; padding: 2rem 1.5rem }
  .header { margin-bottom: 2rem; padding-bottom: 1rem; border-bottom: 1px solid #e5e5e5 }
  .header h1 { font-size: 22px; font-weight: 600; margin-bottom: 4px }
  .header .meta { font-size: 12px; color: #666; display: flex; gap: 16px; flex-wrap: wrap; margin-top: 8px }
  .summary-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 2rem }
  .sum-card { background: #fff; border: 1px solid #e5e5e5; border-radius: 10px; padding: 14px; text-align: center }
  .sum-card .num { font-size: 28px; font-weight: 600 }
  .sum-card .lbl { font-size: 11px; color: #888; margin-top: 2px; text-transform: uppercase; letter-spacing: 0.04em }
  .num.red { color: #c0392b }
  .num.green { color: #27ae60 }
  .section-title { font-size: 13px; font-weight: 600; color: #555; text-transform: uppercase; letter-spacing: 0.05em; margin: 1.5rem 0 10px }
  .pattern-box { background: #fff; border: 1px solid #e5e5e5; border-radius: 10px; padding: 14px 16px; margin-bottom: 1.5rem }
  .pattern-item { display: flex; gap: 10px; padding: 5px 0; font-size: 13px; color: #444; border-bottom: 1px solid #f0f0f0 }
  .pattern-item:last-child { border-bottom: none }
  .pattern-dot { color: #999; flex-shrink: 0; margin-top: 2px }
  .env-note { margin-top: 10px; padding: 8px 12px; background: #eef4fb; border-radius: 6px; font-size: 12px; color: #1a5fa0; border-left: 3px solid #3498db }
  .failure-card { background: #fff; border: 1px solid #e5e5e5; border-radius: 10px; padding: 16px; margin-bottom: 14px }
  .failure-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; margin-bottom: 12px }
  .failure-name { font-size: 15px; font-weight: 600 }
  .failure-fullname { font-size: 11px; color: #888; margin-top: 2px; word-break: break-all }
  .badges { display: flex; gap: 6px; flex-wrap: wrap; flex-shrink: 0 }
  .badge { padding: 2px 8px; border-radius: 20px; font-size: 11px; font-weight: 600 }
  .badge-critical { background: #fde8e8; color: #922b21 }
  .badge-high { background: #fef3e2; color: #784212 }
  .badge-medium { background: #e8f4fd; color: #1a5276 }
  .badge-low { background: #eafaf1; color: #1d6a39 }
  .badge-cat { background: #f0f0f0; color: #555 }
  .error-box { font-family: 'Courier New', monospace; font-size: 12px; background: #fafafa; border: 1px solid #e8e8e8; border-radius: 6px; padding: 10px 12px; margin-bottom: 12px; white-space: pre-wrap; word-break: break-word; color: #333; max-height: 120px; overflow-y: auto }
  .field-label { font-size: 11px; font-weight: 600; color: #888; text-transform: uppercase; letter-spacing: 0.04em; margin-bottom: 6px; margin-top: 12px }
  .cause-block { padding: 10px 12px; border-left: 3px solid #e74c3c; background: #fdf9f9; border-radius: 0 6px 6px 0; font-size: 13px; color: #333 }
  .factor-item { display: flex; gap: 8px; font-size: 13px; color: #555; padding: 3px 0 }
  .factor-dot { color: #bbb; flex-shrink: 0 }
  .suggestion-item { display: flex; gap: 10px; padding: 7px 0; border-bottom: 1px solid #f5f5f5; align-items: flex-start }
  .suggestion-item:last-child { border-bottom: none }
  .sug-icon { font-size: 15px; color: #888; flex-shrink: 0; margin-top: 1px }
  .sug-text { flex: 1; font-size: 13px; color: #333 }
  .sug-tags { display: flex; gap: 5px; flex-wrap: wrap; margin-top: 3px }
  .sug-tag { font-size: 10px; padding: 1px 5px; border: 1px solid #e0e0e0; border-radius: 3px; color: #777 }
  .sug-tag.immediate { border-color: #e74c3c; color: #c0392b }
  .code-block { font-family: 'Courier New', monospace; font-size: 12px; background: #1e1e1e; color: #d4d4d4; border-radius: 6px; padding: 12px 14px; margin-top: 8px; white-space: pre; overflow-x: auto; max-height: 220px }
  .attachments { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 6px }
  .attach-link { font-size: 12px; color: #2980b9; text-decoration: none; padding: 2px 7px; border: 1px solid #d5e8f7; border-radius: 4px }
  .attach-link:hover { background: #eef4fb }
  details > summary { cursor: pointer; list-style: none; user-select: none }
  details > summary::-webkit-details-marker { display: none }
  details > summary::before { content: '▶ '; font-size: 10px; color: #999 }
  details[open] > summary::before { content: '▼ '; }
  .footer { margin-top: 2.5rem; padding-top: 1rem; border-top: 1px solid #e5e5e5; font-size: 12px; color: #aaa; text-align: center }
  @media (max-width: 600px) { .summary-grid { grid-template-columns: repeat(2, 1fr) } .failure-header { flex-direction: column } }
  @media print { body { background: white } .failure-card { break-inside: avoid } }
</style>
</head>
<body>
<div class="page">

  <div class="header">
    <h1>Test failure report</h1>
    <div style="font-size:15px;color:#555;margin-top:2px">{{HE(r.RunName)}}</div>
    <div class="meta">
      <span>📅 {{date}}</span>
      <span>🌐 {{HE(analysis.Environment)}}</span>
      <span>⏱ {{HE(r.StartTime)}} → {{HE(r.FinishTime)}}</span>
      {{(analysis.ExtraContext != null ? $"<span>ℹ️ {HE(analysis.ExtraContext)}</span>" : "")}}
    </div>
  </div>

  <div class="summary-grid">
    <div class="sum-card"><div class="num">{{r.Total}}</div><div class="lbl">Total</div></div>
    <div class="sum-card"><div class="num red">{{r.Failed}}</div><div class="lbl">Failed</div></div>
    <div class="sum-card"><div class="num green">{{r.Passed}}</div><div class="lbl">Passed</div></div>
    <div class="sum-card"><div class="num">{{r.Skipped}}</div><div class="lbl">Skipped</div></div>
  </div>
""");

        // Patterns
        if (analysis.Patterns.Any() || analysis.EnvironmentNotes.Length > 0)
        {
            sb.AppendLine("""<div class="section-title">Cross-cutting patterns</div>""");
            sb.AppendLine("""<div class="pattern-box">""");
            foreach (var p in analysis.Patterns)
                sb.AppendLine($"""<div class="pattern-item"><span class="pattern-dot">—</span>{HE(p)}</div>""");
            if (analysis.EnvironmentNotes.Length > 0)
                sb.AppendLine($"""<div class="env-note">⚙️ {HE(analysis.EnvironmentNotes)}</div>""");
            sb.AppendLine("</div>");
        }

        // Failures
        sb.AppendLine("""<div class="section-title">Failed tests</div>""");

        foreach (var f in analysis.Failures)
        {
            var sevClass = $"badge-{f.Severity}";
            sb.AppendLine($$"""
  <div class="failure-card">
    <div class="failure-header">
      <div>
        <div class="failure-name">{{HE(f.ShortName)}}</div>
        <div class="failure-fullname">{{HE(f.TestName)}}</div>
      </div>
      <div class="badges">
        <span class="badge {{sevClass}}">{{HE(f.Severity)}}</span>
        <span class="badge badge-cat">{{HE(f.Category.Replace("_", " "))}}</span>
      </div>
    </div>

    <div class="error-box">{{HE(f.ErrorSummary)}}</div>

    <div class="field-label">Root cause</div>
    <div class="cause-block">{{HE(f.PrimaryCause)}}</div>
""");

            if (f.ContributingFactors.Any())
            {
                sb.AppendLine("""    <div class="field-label">Contributing factors</div>""");
                foreach (var cf in f.ContributingFactors)
                    sb.AppendLine($"""    <div class="factor-item"><span class="factor-dot">·</span>{HE(cf)}</div>""");
            }

            if (f.Suggestions.Any())
            {
                sb.AppendLine("""    <div class="field-label">Fix suggestions</div>""");
                foreach (var s in f.Suggestions)
                {
                    sb.AppendLine($$"""
    <div class="suggestion-item">
      <span class="sug-text">
        {{HE(s.Action)}}
        <div class="sug-tags">
          <span class="sug-tag">{{HE(s.Type)}}</span>
          <span class="sug-tag {{(s.Priority == "immediate" ? "immediate" : "")}}">{{HE(s.Priority)}}</span>
        </div>
      </span>
    </div>
""");
                }
            }

            if (!string.IsNullOrWhiteSpace(f.CodeSnippet))
            {
                sb.AppendLine($$"""
    <details style="margin-top:12px">
      <summary class="field-label" style="display:inline-block">Suggested code fix</summary>
      <div class="code-block">{{HE(f.CodeSnippet)}}</div>
    </details>
""");
            }

            if (f.AttachmentPaths.Any())
            {
                sb.AppendLine("""    <div class="field-label">Attachments</div><div class="attachments">""");
                foreach (var a in f.AttachmentPaths)
                    sb.AppendLine($"""    <a class="attach-link" href="{HE(a)}" target="_blank">📎 {HE(Path.GetFileName(a))}</a>""");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("  </div>"); // failure-card
        }

        sb.AppendLine($"""
  <div class="footer">Generated by FailureAnalyzer · Azure OpenAI · {date}</div>
</div>
</body>
</html>
""");

        return sb.ToString();
    }

    //a plain Markdown version
    public string GenerateMarkdown(RunAnalysis analysis)
    {
        var sb = new StringBuilder();
        var r = analysis.Run;

        sb.AppendLine($"# Test failure report — {r.RunName}");
        sb.AppendLine($"\n**Date:** {analysis.GeneratedAt:yyyy-MM-dd HH:mm} UTC  ");
        sb.AppendLine($"**Environment:** {analysis.Environment}  ");
        sb.AppendLine($"**Results:** {r.Failed} failed / {r.Passed} passed / {r.Total} total\n");
        sb.AppendLine("---\n");

        if (analysis.Patterns.Any())
        {
            sb.AppendLine("## Cross-cutting patterns\n");
            foreach (var p in analysis.Patterns) sb.AppendLine($"- {p}");
            if (analysis.EnvironmentNotes.Length > 0)
                sb.AppendLine($"\n> **Environment:** {analysis.EnvironmentNotes}");
            sb.AppendLine("\n---\n");
        }

        sb.AppendLine("## Failed tests\n");
        int i = 1;
        foreach (var f in analysis.Failures)
        {
            sb.AppendLine($"### {i++}. {f.ShortName}");
            sb.AppendLine($"**Severity:** {f.Severity} | **Category:** {f.Category.Replace("_", " ")}  ");
            sb.AppendLine($"**Full name:** `{f.TestName}`\n");
            sb.AppendLine($"**Error:** {f.ErrorSummary}\n");
            sb.AppendLine($"**Root cause:** {f.PrimaryCause}\n");
            if (f.ContributingFactors.Any())
            {
                sb.AppendLine("**Contributing factors:**");
                foreach (var cf in f.ContributingFactors) sb.AppendLine($"- {cf}");
                sb.AppendLine();
            }
            if (f.Suggestions.Any())
            {
                sb.AppendLine("**Fix suggestions:**");
                foreach (var s in f.Suggestions)
                    sb.AppendLine($"- [{s.Priority.ToUpper()}] `{s.Type}` — {s.Action}");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(f.CodeSnippet))
                sb.AppendLine($"**Suggested code:**\n```csharp\n{f.CodeSnippet}\n```\n");
            sb.AppendLine("---\n");
        }

        return sb.ToString();
    }

    private static string HE(string? s) =>
        System.Net.WebUtility.HtmlEncode(s ?? "");
}