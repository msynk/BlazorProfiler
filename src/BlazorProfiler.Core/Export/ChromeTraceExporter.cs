using System.Globalization;
using System.Text.Json;
using BlazorProfiler.Analysis;
using BlazorProfiler.Events;

namespace BlazorProfiler.Export;

/// <summary>
/// Exports a trace in the Chrome Trace Event format, which can be opened in Perfetto (https://ui.perfetto.dev) or
/// <c>chrome://tracing</c> alongside other trace sources.
/// </summary>
/// <remarks>
/// Each circuit (or the WebAssembly runtime) becomes a process, and event categories become threads. Runtime samples are
/// exported as counter tracks. The native format remains the lossless format; this export is for interoperability.
/// </remarks>
public sealed class ChromeTraceExporter : IProfilerExporter
{
    private static readonly string[] s_lanes =
    [
        "Interactions", "Render batches", "Components", "HTTP", "JS interop", "Navigation", "Circuit", "Browser", "Operations", "Markers",
    ];

    /// <inheritdoc />
    public string Name => "chrome";

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public string FileExtension => ".chrome-trace.json";

    /// <inheritdoc />
    public async Task ExportAsync(TraceDocument document, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var frequency = (double)document.Clock.Frequency;
        var origin = document.Events.Count == 0 ? 0 : document.Events.Min(e => e.Timestamp);
        var processes = new Dictionary<string, int>(StringComparer.Ordinal);

        await using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString("displayTimeUnit", "ms");
        writer.WritePropertyName("otherData");
        writer.WriteStartObject();
        writer.WriteString("format", TraceDocument.FormatName);
        writer.WriteString("session", document.Metadata.SessionName);
        writer.WriteString("anchorTime", document.Clock.AnchorTime.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
        writer.WritePropertyName("traceEvents");
        writer.WriteStartArray();

        foreach (var e in document.Events)
        {
            var processName = e.CircuitId is null ? (document.Metadata.Source ?? "app") : "circuit " + e.CircuitId;
            if (!processes.TryGetValue(processName, out var pid))
            {
                pid = processes.Count + 1;
                processes[processName] = pid;
                WriteMetadata(writer, pid, processName);
            }

            var ts = (e.Timestamp - origin) / frequency * 1_000_000;
            var dur = e.Duration / frequency * 1_000_000;

            if (e is RuntimeSampleEvent runtime)
            {
                writer.WriteStartObject();
                writer.WriteString("ph", "C");
                writer.WriteString("name", "Runtime");
                writer.WriteNumber("pid", pid);
                writer.WriteNumber("ts", Math.Round(ts + dur, 3));
                writer.WritePropertyName("args");
                writer.WriteStartObject();
                if (runtime.HeapSizeBytes is { } heap)
                {
                    writer.WriteNumber("heapMB", Math.Round(heap / 1048576.0, 3));
                }

                if (runtime.AllocatedBytes is { } allocated && e.Duration > 0)
                {
                    writer.WriteNumber("allocMBps", Math.Round(allocated / 1048576.0 / (e.Duration / frequency), 3));
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                continue;
            }

            var (lane, name, category) = Describe(e);
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("cat", category);
            writer.WriteString("ph", e.Duration > 0 ? "X" : "i");
            if (e.Duration == 0)
            {
                writer.WriteString("s", "t");
            }

            writer.WriteNumber("ts", Math.Round(ts, 3));
            if (e.Duration > 0)
            {
                writer.WriteNumber("dur", Math.Round(dur, 3));
            }

            writer.WriteNumber("pid", pid);
            writer.WriteNumber("tid", lane + 1);
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            writer.WriteNumber("id", e.Id);
            if (e.OperationId != 0)
            {
                writer.WriteNumber("operation", e.OperationId);
                writer.WriteString("correlation", e.Correlation.ToString());
            }

            if (e.Outcome != ProfilerOutcome.Success)
            {
                writer.WriteString("outcome", e.Outcome.ToString());
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void WriteMetadata(Utf8JsonWriter writer, int pid, string processName)
    {
        writer.WriteStartObject();
        writer.WriteString("ph", "M");
        writer.WriteString("name", "process_name");
        writer.WriteNumber("pid", pid);
        writer.WritePropertyName("args");
        writer.WriteStartObject();
        writer.WriteString("name", processName);
        writer.WriteEndObject();
        writer.WriteEndObject();

        for (var lane = 0; lane < s_lanes.Length; lane++)
        {
            writer.WriteStartObject();
            writer.WriteString("ph", "M");
            writer.WriteString("name", "thread_name");
            writer.WriteNumber("pid", pid);
            writer.WriteNumber("tid", lane + 1);
            writer.WritePropertyName("args");
            writer.WriteStartObject();
            writer.WriteString("name", s_lanes[lane]);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }

    private static (int Lane, string Name, string Category) Describe(ProfilerEvent e) => e switch
    {
        EventHandlerEvent h => (0, $"{h.AttributeName} → {DiagnosticContext.ShortType(h.ComponentType)}.{h.MethodName}", "interaction"),
        BrowserInteractionEvent b => (7, "interaction " + b.EventType, "browser"),
        RenderBatchEvent b => (1, $"Render batch ({b.UpdatedComponentCount})", "rendering"),
        NavigationEvent n => (5, "Navigate " + n.Route, "navigation"),
        ComponentEvent c => (2, $"{e.Kind} {DiagnosticContext.ShortType(c.ComponentType)}", "component"),
        HttpRequestEvent h => (3, $"{h.Method} {h.Url}", "http"),
        JSInteropEvent j => (4, j.Identifier ?? "interop", "interop"),
        CircuitEvent c => (6, "Circuit " + c.Change, "circuit"),
        CircuitActivityEvent a => (6, a.ActivityName ?? "inbound activity", "circuit"),
        BrowserLongTaskEvent l => (7, l.EntryType ?? "long task", "browser"),
        BrowserResourceEvent r => (7, r.Url ?? "resource", "browser"),
        OperationEvent o => (8, o.Name ?? "operation", "operation"),
        MarkerEvent m => (9, m.Name ?? m.MarkerType.ToString(), "marker"),
        _ => (9, e.Kind.ToString(), "other"),
    };
}
