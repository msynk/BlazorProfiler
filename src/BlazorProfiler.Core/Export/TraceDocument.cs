using System.Reflection;
using System.Runtime.InteropServices;
using BlazorProfiler.Analysis;
using BlazorProfiler.Collectors;
using BlazorProfiler.Events;
using BlazorProfiler.Sessions;
using BlazorProfiler.Storage;

namespace BlazorProfiler.Export;

/// <summary>
/// The native, versioned trace format. A trace contains session metadata, the clock needed to interpret timestamps,
/// collector capabilities, sampling information, raw events, aggregates, and (optionally) diagnostic findings.
/// </summary>
/// <remarks>See <c>docs/trace-format.md</c> for the specification.</remarks>
public sealed class TraceDocument
{
    /// <summary>The format identifier.</summary>
    public const string FormatName = "blazor-profiler-trace";

    /// <summary>The current format version.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The format identifier. Must equal <see cref="FormatName"/>.</summary>
    public string Format { get; set; } = FormatName;

    /// <summary>The format version.</summary>
    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>The event schema version (<see cref="ProfilerEvent.SchemaVersion"/>).</summary>
    public int EventSchemaVersion { get; set; } = ProfilerEvent.SchemaVersion;

    /// <summary>Session and environment metadata.</summary>
    public TraceMetadata Metadata { get; set; } = new();

    /// <summary>The clock used for all timestamps in the trace.</summary>
    public TraceClock Clock { get; set; } = new();

    /// <summary>Collector capabilities and health at export time.</summary>
    public List<CollectorStatus> Collectors { get; set; } = [];

    /// <summary>Raw events in storage order.</summary>
    public List<ProfilerEvent> Events { get; set; } = [];

    /// <summary>Aggregates at export time (covering events evicted before export).</summary>
    public SessionSummary? Summary { get; set; }

    /// <summary>Diagnostic findings at export time.</summary>
    public List<DiagnosticFinding>? Findings { get; set; }

    /// <summary>Suppressions at export time.</summary>
    public List<FindingSuppression>? Suppressions { get; set; }
}

/// <summary>Trace metadata.</summary>
public sealed class TraceMetadata
{
    /// <summary>The source session identifier.</summary>
    public string? SessionId { get; set; }

    /// <summary>The source session name.</summary>
    public string? SessionName { get; set; }

    /// <summary>The circuit the session was restricted to, if any.</summary>
    public string? CircuitId { get; set; }

    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the trace was exported.</summary>
    public DateTimeOffset ExportedAt { get; set; }

    /// <summary>The recorded duration in milliseconds.</summary>
    public double DurationMs { get; set; }

    /// <summary>A label of the runtime that produced the trace (for example <c>server</c> or <c>webassembly</c>).</summary>
    public string? Source { get; set; }

    /// <summary>The .NET runtime description.</summary>
    public string? Runtime { get; set; }

    /// <summary>The profiler version.</summary>
    public string? ProfilerVersion { get; set; }

    /// <summary>The configuration profile.</summary>
    public ProfilerProfile Profile { get; set; }

    /// <summary>Sampling rates by event kind; kinds not listed were not sampled.</summary>
    public Dictionary<string, double> SamplingRates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Events dropped by the pipeline during the session (approximate).</summary>
    public long DroppedEvents { get; set; }

    /// <summary>Events evicted by retention limits before export.</summary>
    public long EvictedEvents { get; set; }

    /// <summary>Whether the session itself was imported.</summary>
    public bool Imported { get; set; }
}

/// <summary>The clock of a trace.</summary>
public sealed class TraceClock
{
    /// <summary>Ticks per second.</summary>
    public long Frequency { get; set; }

    /// <summary>The monotonic timestamp corresponding to <see cref="AnchorTime"/>.</summary>
    public long AnchorTimestamp { get; set; }

    /// <summary>The wall-clock anchor.</summary>
    public DateTimeOffset AnchorTime { get; set; }
}

/// <summary>Options for creating a trace.</summary>
public sealed class TraceExportOptions
{
    /// <summary>Include diagnostic findings.</summary>
    public bool IncludeFindings { get; set; } = true;

    /// <summary>Include aggregates.</summary>
    public bool IncludeSummary { get; set; } = true;

    /// <summary>Replace circuit identifiers with stable pseudonyms (<c>circuit-1</c>, <c>circuit-2</c>, ...).</summary>
    public bool PseudonymizeCircuitIds { get; set; }

    /// <summary>Remove W3C trace and span identifiers.</summary>
    public bool RemoveTraceContext { get; set; }

    /// <summary>The maximum number of events to export (most recent events win).</summary>
    public int MaxEvents { get; set; } = 1_000_000;
}

/// <summary>Creates <see cref="TraceDocument"/>s from sessions.</summary>
public static class TraceDocumentBuilder
{
    /// <summary>The profiler version string.</summary>
    public static string ProfilerVersion { get; } =
        typeof(TraceDocumentBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Creates a trace document from a session.</summary>
    public static TraceDocument Create(
        IProfilerSession session,
        ProfilerOptions options,
        DiagnosticReport? report = null,
        ProfilerHealthSnapshot? health = null,
        string? source = null,
        TraceExportOptions? exportOptions = null)
    {
        exportOptions ??= new TraceExportOptions();
        var snapshot = session.CreateSnapshot();
        var clock = session.Clock;
        var events = snapshot.Events.AsEnumerable();
        if (snapshot.Events.Length > exportOptions.MaxEvents)
        {
            events = events.Skip(snapshot.Events.Length - exportOptions.MaxEvents);
        }

        var document = new TraceDocument
        {
            Metadata = new TraceMetadata
            {
                SessionId = session.Id,
                SessionName = session.Name,
                CircuitId = session.Options.CircuitId,
                CreatedAt = snapshot.Statistics.CreatedAt,
                ExportedAt = DateTimeOffset.UtcNow,
                DurationMs = snapshot.Statistics.DurationMs,
                Source = source,
                Runtime = RuntimeInformation.FrameworkDescription,
                ProfilerVersion = ProfilerVersion,
                Profile = options.Profile,
                SamplingRates = options.Sampling.Rates.ToDictionary(p => p.Key.ToString(), p => p.Value, StringComparer.Ordinal),
                DroppedEvents = health?.DroppedQueueFull ?? snapshot.Summary.DroppedEventsReported,
                EvictedEvents = snapshot.Statistics.EvictedEvents,
                Imported = session.IsImported,
            },
            Clock = new TraceClock
            {
                Frequency = clock.Frequency,
                AnchorTimestamp = clock.AnchorTimestamp,
                AnchorTime = clock.AnchorTime,
            },
            Collectors = health is null ? [] : [.. health.Collectors],
            Events = [.. events],
            Summary = exportOptions.IncludeSummary ? snapshot.Summary : null,
            Findings = exportOptions.IncludeFindings && report is not null ? [.. report.Findings] : null,
        };

        if (exportOptions.PseudonymizeCircuitIds || exportOptions.RemoveTraceContext)
        {
            ApplyExportRedaction(document, exportOptions);
        }

        return document;
    }

    private static void ApplyExportRedaction(TraceDocument document, TraceExportOptions options)
    {
        var pseudonyms = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Map(string? circuitId)
        {
            if (circuitId is null || !options.PseudonymizeCircuitIds)
            {
                return circuitId;
            }

            if (!pseudonyms.TryGetValue(circuitId, out var pseudonym))
            {
                pseudonym = "circuit-" + (pseudonyms.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                pseudonyms[circuitId] = pseudonym;
            }

            return pseudonym;
        }

        // Events are shared with the live session, so redaction is applied to clones.
        var clones = new List<ProfilerEvent>(document.Events.Count);
        foreach (var e in document.Events)
        {
            var clone = EventCloner.Clone(e);
            clone.CircuitId = Map(clone.CircuitId);
            if (options.RemoveTraceContext)
            {
                clone.TraceId = null;
                clone.SpanId = null;
            }

            clones.Add(clone);
        }

        document.Events = clones;
        document.Metadata.CircuitId = Map(document.Metadata.CircuitId);
        if (document.Summary is not null)
        {
            foreach (var circuit in document.Summary.Circuits)
            {
                circuit.CircuitId = Map(circuit.CircuitId) ?? string.Empty;
            }
        }
    }
}
