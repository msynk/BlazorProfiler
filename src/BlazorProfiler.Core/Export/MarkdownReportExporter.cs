using System.Globalization;
using System.Text;
using BlazorProfiler.Analysis;
using BlazorProfiler.Events;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Export;

/// <summary>
/// Writes a human-readable Markdown report of a trace: session facts, key metrics, every finding with its evidence and
/// suggested fix, and the most expensive components, handlers, endpoints, queries, and interop calls. Suited to pull
/// requests, issues, and pasting into an AI assistant. It is a report, not a trace: it cannot be imported.
/// </summary>
public sealed class MarkdownReportExporter : IProfilerExporter
{
    private const int TopRows = 10;

    /// <inheritdoc />
    public string Name => "markdown";

    /// <inheritdoc />
    public string ContentType => "text/markdown; charset=utf-8";

    /// <inheritdoc />
    public string FileExtension => ".md";

    /// <inheritdoc />
    public async Task ExportAsync(TraceDocument document, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        var text = Render(document);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renders the report.</summary>
    public static string Render(TraceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var md = new StringBuilder();
        var meta = document.Metadata;
        var summary = document.Summary ?? new SessionSummary();
        var findings = document.Findings ?? [];
        var active = findings.Where(f => f.Suppression is null).ToList();

        md.Append("# Blazor Profiler report: ").AppendLine(Escape(meta.SessionName ?? "session")).AppendLine();
        md.Append("- **Source:** ").Append(meta.Source ?? "unknown").Append(" · ").Append(meta.Runtime ?? string.Empty).Append(" · profiler ").Append(meta.ProfilerVersion).Append(" · profile ").AppendLine(meta.Profile.ToString());
        md.Append("- **Recorded:** ").Append(meta.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC for ").Append(Ms(meta.DurationMs)).Append(" · ").Append(Num(document.Events.Count)).AppendLine(" events");
        if (meta.DroppedEvents > 0 || meta.EvictedEvents > 0)
        {
            md.Append("- **Incomplete data:** ").Append(Num(meta.DroppedEvents)).Append(" events dropped, ").Append(Num(meta.EvictedEvents)).AppendLine(" evicted by retention limits");
        }

        if (summary.ContainsSampledData)
        {
            md.AppendLine("- **Sampled:** some event kinds were sampled; counts and totals are weighted estimates");
        }

        md.AppendLine();
        md.AppendLine("## Key metrics").AppendLine();
        md.AppendLine("| Metric | Value |").AppendLine("|---|---|");
        Row(md, "Render batches", $"{Num(summary.RenderBatches.Durations.Count)} (p95 {Ms(summary.RenderBatches.Durations.P95Ms)}, max {Ms(summary.RenderBatches.Durations.MaxMs)})");
        Row(md, "Components updated", $"{Num(summary.RenderBatches.TotalUpdatedComponents)} (largest batch {Num(summary.RenderBatches.MaxUpdatedComponents)})");
        Row(md, "Component renders (instrumented)", Num(summary.Components.Sum(c => c.Renders.Count)));
        Row(md, "Event handlers", $"{Num(summary.Handlers.Sum(h => h.Durations.Count))} (slowest p95 {Ms(summary.Handlers.Select(h => h.Durations.P95Ms).DefaultIfEmpty().Max())})");
        Row(md, "HTTP requests", $"{Num(summary.Endpoints.Sum(e => e.Durations.Count))} ({Num(summary.Endpoints.Sum(e => e.Failures))} failed)");
        Row(md, "Database commands", $"{Num(summary.Queries.Sum(q => q.Durations.Count))} ({Num(summary.Queries.Count)} distinct, {Ms(summary.Queries.Sum(q => q.Durations.TotalMs))} total)");
        Row(md, "JS interop calls", Num(summary.Interop.Sum(i => i.Durations.Count)));
        Row(md, "Browser interactions", summary.BrowserInteractions.Count > 0 ? $"{Num(summary.BrowserInteractions.Count)} (p95 {Ms(summary.BrowserInteractions.P95Ms)}, max {Ms(summary.BrowserInteractions.MaxMs)})" : "not collected");
        Row(md, "Allocated", summary.Runtime.Samples > 0 ? $"{Bytes(summary.Runtime.AllocatedBytes)} · GCs {summary.Runtime.Gen0Collections}/{summary.Runtime.Gen1Collections}/{summary.Runtime.Gen2Collections} (gen0/1/2)" : "not collected");
        Row(md, "Retained components (possible leaks)", Num(summary.Components.Sum(c => c.Retained)));
        md.AppendLine();

        md.Append("## Findings (").Append(active.Count).AppendLine(")").AppendLine();
        if (active.Count == 0)
        {
            md.AppendLine("No rule reported a problem. Rules only report when the evidence is sufficient.").AppendLine();
        }

        foreach (var f in active)
        {
            md.Append("### ").Append(f.Severity).Append(": ").AppendLine(Escape(f.Title)).AppendLine();
            md.Append('`').Append(f.RuleId).Append("` · ").Append(f.Category).Append(" · ").Append(f.Confidence).AppendLine(" confidence").AppendLine();
            md.AppendLine(Escape(f.Summary)).AppendLine();
            md.Append("**Why it matters:** ").AppendLine(Escape(f.Explanation)).AppendLine();
            md.Append("**Suggested fix:** ").AppendLine(Escape(f.Recommendation)).AppendLine();
            if (!string.IsNullOrEmpty(f.CodeHint))
            {
                md.Append("**Where to look:** `").Append(f.CodeHint.Replace("`", "'", StringComparison.Ordinal)).AppendLine("`").AppendLine();
            }

            if (f.Evidence.Count > 0)
            {
                md.AppendLine("**Evidence:**").AppendLine();
                foreach (var e in f.Evidence)
                {
                    md.Append("- ").Append(Escape(e.Label)).Append(": ").Append(Escape(e.Value)).Append(" _(").Append(e.Quality.ToString().ToLowerInvariant()).AppendLine(")_");
                }

                md.AppendLine();
            }

            if (f.Impact is { } impact)
            {
                md.Append("**Impact:** ").Append(impact.TotalMs is { } total ? Ms(total) + ", " : string.Empty).Append(Num(impact.Occurrences)).Append(" occurrence(s). ").AppendLine(Escape(impact.Method)).AppendLine();
            }

            if (f.Caveats.Count > 0)
            {
                md.AppendLine("**May not apply when:**").AppendLine();
                foreach (var caveat in f.Caveats)
                {
                    md.Append("- ").AppendLine(Escape(caveat));
                }

                md.AppendLine();
            }

            if (DiagnosticEngine.IsSafeDocumentationUrl(f.DocumentationUrl))
            {
                md.Append("[Rule documentation](").Append(f.DocumentationUrl).AppendLine(")").AppendLine();
            }
        }

        var suppressed = findings.Where(f => f.Suppression is not null).ToList();
        if (suppressed.Count > 0)
        {
            md.Append("<details><summary>").Append(suppressed.Count).AppendLine(" dismissed or expected finding(s)</summary>").AppendLine();
            foreach (var f in suppressed)
            {
                md.Append("- `").Append(f.RuleId).Append("` ").Append(Escape(f.Title)).Append(" (").Append(f.Suppression!.Kind).AppendLine(")");
            }

            md.AppendLine().AppendLine("</details>").AppendLine();
        }

        var components = summary.Components.Select(c => (Component: c, Cost: c.Renders.TotalMs > 0 ? c.Renders.TotalMs : c.ParametersSets.TotalMs))
            .Where(x => x.Cost > 0).OrderByDescending(x => x.Cost).Take(TopRows).ToList();
        if (components.Count > 0)
        {
            md.AppendLine("## Most expensive components").AppendLine();
            md.AppendLine("| Component | Renders | Render time | Render p95 | Parameter updates | Main render cause |").AppendLine("|---|---:|---:|---:|---:|---|");
            foreach (var (c, _) in components)
            {
                var cause = c.RenderReasons.Where(r => r.Key != nameof(RenderReason.FirstRender)).OrderByDescending(r => r.Value).Select(r => r.Key).FirstOrDefault() ?? "—";
                md.Append("| ").Append(Cell(DiagnosticContext.ShortType(c.ComponentType))).Append(" | ")
                    .Append(c.Instrumented ? Num(c.Renders.Count) : "n/a").Append(" | ")
                    .Append(c.Instrumented ? Ms(c.Renders.TotalMs) : "n/a").Append(" | ")
                    .Append(c.Instrumented ? Ms(c.Renders.P95Ms) : "n/a").Append(" | ")
                    .Append(Num(c.ParametersSets.Count)).Append(" | ")
                    .Append(cause).AppendLine(" |");
            }

            md.AppendLine();
        }

        Table(md, "Slowest event handlers", ["Handler", "Calls", "p95", "Total"], summary.Handlers.OrderByDescending(h => h.Durations.P95Ms).Take(TopRows)
            .Select(h => new[] { $"{h.AttributeName ?? "event"} → {DiagnosticContext.ShortType(h.ComponentType)}.{h.MethodName}", Num(h.Durations.Count), Ms(h.Durations.P95Ms), Ms(h.Durations.TotalMs) }));
        Table(md, "HTTP endpoints", ["Endpoint", "Requests", "Failures", "p95", "Total"], summary.Endpoints.OrderByDescending(e => e.Durations.TotalMs).Take(TopRows)
            .Select(e => new[] { e.Endpoint, Num(e.Durations.Count), Num(e.Failures), Ms(e.Durations.P95Ms), Ms(e.Durations.TotalMs) }));
        Table(md, "Database queries", ["Query", "Executions", "p95", "Total", "Rows read"], summary.Queries.OrderByDescending(q => q.Durations.TotalMs).Take(TopRows)
            .Select(q => new[] { Truncate(q.CommandText ?? string.Empty, 160), Num(q.Durations.Count), Ms(q.Durations.P95Ms), Ms(q.Durations.TotalMs), Num(q.RowsRead) }));
        Table(md, "JavaScript interop", ["Identifier", "Calls", "Failures", "p95", "Total"], summary.Interop.OrderByDescending(i => i.Durations.TotalMs).Take(TopRows)
            .Select(i => new[] { i.Identifier ?? string.Empty, Num(i.Durations.Count), Num(i.Failures), Ms(i.Durations.P95Ms), Ms(i.Durations.TotalMs) }));

        md.AppendLine("---").AppendLine("_Generated by BlazorProfiler. Values are measured unless labelled estimated or inferred._");
        return md.ToString();
    }

    private static void Row(StringBuilder md, string label, string value) => md.Append("| ").Append(Cell(label)).Append(" | ").Append(Cell(value)).AppendLine(" |");

    private static void Table(StringBuilder md, string title, string[] headers, IEnumerable<string[]> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
        {
            return;
        }

        md.Append("## ").AppendLine(title).AppendLine();
        md.Append("| ").Append(string.Join(" | ", headers)).AppendLine(" |");
        md.Append("|---").Append(string.Concat(Enumerable.Repeat("|---:", headers.Length - 1))).AppendLine("|");
        foreach (var row in list)
        {
            md.Append("| ").Append(string.Join(" | ", row.Select(Cell))).AppendLine(" |");
        }

        md.AppendLine();
    }

    private static string Cell(string? value) => Escape(value).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Escape(string? value) =>
        (value ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static string Ms(double ms) => DiagnosticContext.FormatMs(ms);

    private static string Num(double value) => DiagnosticContext.FormatNumber(value);

    private static string Bytes(double value) => DiagnosticContext.FormatBytes(value);
}
