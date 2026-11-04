using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorProfiler.Collectors;
using BlazorProfiler.Events;

namespace BlazorProfiler.Browser;

/// <summary>An entry reported by the browser module. Short property names keep the interop payload small.</summary>
public sealed class BrowserEntry
{
    /// <summary>Entry kind: <c>i</c> interaction, <c>l</c> long task or long animation frame, <c>r</c> resource.</summary>
    public string? K { get; set; }

    /// <summary>Event type, entry type, or URL.</summary>
    public string? N { get; set; }

    /// <summary>Start time in Unix epoch milliseconds (browser clock).</summary>
    public double T { get; set; }

    /// <summary>Duration in milliseconds.</summary>
    public double D { get; set; }

    /// <summary>Input delay in milliseconds (interactions).</summary>
    public double Id { get; set; }

    /// <summary>Processing time in milliseconds (interactions).</summary>
    public double Pr { get; set; }

    /// <summary>Presentation delay in milliseconds (interactions).</summary>
    public double Pd { get; set; }

    /// <summary>Lower-case target tag name (interactions).</summary>
    public string? Tag { get; set; }

    /// <summary>Blocking duration in milliseconds (long tasks).</summary>
    public double B { get; set; }

    /// <summary>Longest script source (long animation frames).</summary>
    public string? S { get; set; }

    /// <summary>Initiator type (resources).</summary>
    public string? It { get; set; }

    /// <summary>Transfer size in bytes (resources).</summary>
    public long Ts { get; set; }

    /// <summary>Decoded body size in bytes (resources).</summary>
    public long Ds { get; set; }

    /// <summary>Response status (resources), when reported.</summary>
    public int St { get; set; }
}

/// <summary>Source-generated JSON metadata for browser payloads.</summary>
[JsonSerializable(typeof(BrowserEntry[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 4)]
internal sealed partial class BrowserJsonContext : JsonSerializerContext;

/// <summary>Collects browser performance entries reported by <see cref="BlazorProfilerClient"/>.</summary>
public sealed class BrowserCollector : ProfilerCollectorBase
{
    /// <summary>The maximum number of entries accepted per batch.</summary>
    public const int MaxEntriesPerBatch = 1_000;

    /// <summary>The maximum accepted payload length in characters.</summary>
    public const int MaxPayloadLength = 512 * 1024;

    /// <summary>
    /// The batch size budget, in characters, used by the browser module on Blazor Server. Batches are sent over the circuit,
    /// whose SignalR connection rejects (and closes on) incoming messages above <c>HubOptions.MaximumReceiveMessageSize</c>
    /// (32 KB by default); the budget leaves room for escaping and the invocation envelope.
    /// </summary>
    public const int ServerMaxBatchLength = 16_000;

    /// <summary>The batch size budget, in characters, used by the browser module in WebAssembly (in-process calls).</summary>
    public const int WebAssemblyMaxBatchLength = MaxPayloadLength / 2;

    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "browser",
        Description = "User-perceived interaction latency (Event Timing API), long animation frames / long tasks, and resource timing for requests that do not use HttpClient.",
        Mechanism = "PerformanceObserver in a JavaScript module loaded by the BlazorProfilerClient component; batches are sent to .NET every few seconds.",
        Capability = CapabilityLevel.Partial,
        Overhead = OverheadLevel.Low,
        Compatibility = "Requires an interactive BlazorProfilerClient component. Event Timing and Long Animation Frames are available in Chromium-based browsers; other browsers report a subset.",
        Degradation = "Entry types the browser does not support are skipped. On Blazor Server, browser timestamps are aligned to the server clock using an offset measured at connection time (accurate to the network latency).",
    };

    /// <summary>Whether browser data should be collected.</summary>
    public bool IsCollecting => IsActive;

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.Browser;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
    }

    /// <summary>Converts a batch of browser entries into events.</summary>
    /// <param name="json">The serialized entries.</param>
    /// <param name="clockOffsetMs">Milliseconds to add to browser timestamps to obtain .NET wall-clock time.</param>
    /// <returns>The number of events emitted.</returns>
    public int Ingest(string? json, double clockOffsetMs)
    {
        var profiler = Profiler;
        if (!IsCollecting || profiler is null || string.IsNullOrEmpty(json))
        {
            return 0;
        }

        if (json.Length > MaxPayloadLength)
        {
            // Never parse oversized input, but make the loss visible in the collector's health.
            RecordError(new InvalidDataException($"A browser batch of {json.Length} characters exceeded the limit of {MaxPayloadLength} characters and was discarded."));
            return 0;
        }

        BrowserEntry[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize(json, BrowserJsonContext.Default.BrowserEntryArray);
        }
        catch (JsonException ex)
        {
            RecordError(ex);
            return 0;
        }

        if (entries is null)
        {
            return 0;
        }

        if (entries.Length > MaxEntriesPerBatch)
        {
            RecordError(new InvalidDataException($"A browser batch contained {entries.Length} entries; only the first {MaxEntriesPerBatch} were recorded."));
        }

        var clock = profiler.Clock;
        var emitted = 0;
        foreach (var entry in entries.AsSpan(0, Math.Min(entries.Length, MaxEntriesPerBatch)))
        {
            if (entry is null || !double.IsFinite(entry.T) || !double.IsFinite(entry.D) || entry.D < 0 || entry.D > 3_600_000)
            {
                continue;
            }

            long start;
            try
            {
                start = clock.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddMilliseconds(entry.T + clockOffsetMs));
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            var duration = clock.FromMilliseconds(entry.D);
            ProfilerEvent? e = entry.K switch
            {
                "i" => new BrowserInteractionEvent
                {
                    EventType = Truncate(entry.N, 64),
                    TargetTag = Truncate(entry.Tag, 32),
                    InputDelayMs = Sanitize(entry.Id),
                    ProcessingMs = Sanitize(entry.Pr),
                    PresentationDelayMs = Sanitize(entry.Pd),
                },
                "l" => new BrowserLongTaskEvent
                {
                    EntryType = Truncate(entry.N, 64),
                    BlockingMs = Sanitize(entry.B),
                    Script = Truncate(entry.S, 512),
                },
                "r" => new BrowserResourceEvent
                {
                    Url = Truncate(entry.N, 2048),
                    InitiatorType = Truncate(entry.It, 32),
                    TransferSize = Math.Max(0, entry.Ts),
                    DecodedBodySize = Math.Max(0, entry.Ds),
                    FromCache = entry.Ds > 0 ? entry.Ts == 0 : null,
                    StatusCode = entry.St > 0 ? entry.St : null,
                },
                _ => null,
            };

            if (e is null)
            {
                continue;
            }

            e.Timestamp = start;
            e.Duration = duration;
            e.Correlation = CorrelationKind.None;
            if (Emit(e))
            {
                emitted++;
            }
        }

        return emitted;
    }

    private static double Sanitize(double value) => double.IsFinite(value) && value >= 0 ? Math.Round(value, 3) : 0;

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
