using System.Text.Json.Serialization;

namespace BlazorProfiler.Events;

/// <summary>
/// A periodic sample of managed runtime state. Deltas cover the interval
/// [<see cref="ProfilerEvent.Timestamp"/>, <see cref="ProfilerEvent.EndTimestamp"/>].
/// Values that are not available on the current platform are <see langword="null"/>.
/// </summary>
/// <remarks>
/// In Blazor WebAssembly these values describe the .NET runtime inside the browser tab. They are not the browser's
/// JavaScript heap and not server-process metrics. In Blazor Server they describe the whole server process, shared by
/// every circuit.
/// </remarks>
public sealed class RuntimeSampleEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.RuntimeSample;

    /// <summary>Bytes allocated by the process during the interval (<c>GC.GetTotalAllocatedBytes(precise: true)</c>).</summary>
    public long? AllocatedBytes { get; set; }

    /// <summary>The managed heap size reported by the most recent garbage collection.</summary>
    public long? HeapSizeBytes { get; set; }

    /// <summary>The managed memory currently known to the GC (<c>GC.GetTotalMemory(false)</c>).</summary>
    public long? TotalMemoryBytes { get; set; }

    /// <summary>Generation 0 collections during the interval.</summary>
    public int? Gen0Collections { get; set; }

    /// <summary>Generation 1 collections during the interval.</summary>
    public int? Gen1Collections { get; set; }

    /// <summary>Generation 2 collections during the interval.</summary>
    public int? Gen2Collections { get; set; }

    /// <summary>
    /// Time the runtime was paused for garbage collection during the interval, in clock ticks; <see langword="null"/> when the
    /// runtime does not report it (WebAssembly).
    /// </summary>
    public long? GcPauseTicks { get; set; }

    /// <summary>Process CPU time consumed during the interval, in clock ticks (server only).</summary>
    public long? CpuTicks { get; set; }

    /// <summary>The number of logical processors available to the process.</summary>
    public int? ProcessorCount { get; set; }

    /// <summary>The process working set (server only).</summary>
    public long? WorkingSetBytes { get; set; }

    /// <summary>The number of thread-pool threads (server only).</summary>
    public int? ThreadPoolThreads { get; set; }

    /// <summary>The number of queued thread-pool work items (server only).</summary>
    public long? ThreadPoolQueueLength { get; set; }

    /// <summary>Monitor lock contentions during the interval (server only).</summary>
    public long? LockContentions { get; set; }

    /// <summary>Exceptions thrown during the interval (first-chance: including those that were caught).</summary>
    public long? Exceptions { get; set; }

    /// <summary>
    /// The most frequent exception types thrown during the interval and how often (at most
    /// <see cref="MaxExceptionTypes"/>). Type names only; messages are never captured.
    /// </summary>
    public Dictionary<string, int>? ExceptionTypes { get; set; }

    /// <summary>The number of active timers in the process at the end of the interval (<c>Timer.ActiveCount</c>).</summary>
    public long? ActiveTimers { get; set; }

    /// <summary>The maximum number of exception types kept per sample.</summary>
    public const int MaxExceptionTypes = 5;

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + 176 + (ExceptionTypes?.Sum(p => StringSize(p.Key) + 32) ?? 0);
}

/// <summary>A circuit lifecycle change.</summary>
public enum CircuitChange
{
    /// <summary>The circuit was opened.</summary>
    Opened = 0,
    /// <summary>The circuit's connection was established or re-established.</summary>
    ConnectionUp = 1,
    /// <summary>The circuit's connection was lost.</summary>
    ConnectionDown = 2,
    /// <summary>The circuit was closed.</summary>
    Closed = 3,
}

/// <summary>A Blazor Server circuit lifecycle change.</summary>
public sealed class CircuitEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.Circuit;

    /// <summary>The change.</summary>
    public CircuitChange Change { get; set; }
}

/// <summary>
/// A message received by a Blazor Server circuit and processed as an inbound activity, such as a browser event,
/// a render-batch acknowledgement, or a JavaScript interop callback.
/// </summary>
public sealed class CircuitActivityEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.CircuitActivity;

    /// <summary>
    /// The SignalR hub method that delivered the activity (for example <c>DispatchBrowserEvent</c> or
    /// <c>OnRenderCompleted</c>), or <see langword="null"/> when it could not be determined.
    /// </summary>
    public string? ActivityName { get; set; }

    /// <summary>
    /// The size in bytes of the SignalR message that delivered the activity, as read by the circuit's hub protocol, or
    /// <see langword="null"/> when not measured.
    /// </summary>
    public long? ReceivedBytes { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(ActivityName);
}

/// <summary>A user interaction observed by the browser's Event Timing API.</summary>
/// <remarks>
/// <see cref="ProfilerEvent.Duration"/> is the browser-reported interaction duration: from the input event's timestamp until
/// the next paint after its handlers ran. It reflects user-perceived latency, including work outside .NET.
/// </remarks>
public sealed class BrowserInteractionEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserInteraction;

    /// <summary>The DOM event type (for example <c>click</c> or <c>keydown</c>).</summary>
    public string? EventType { get; set; }

    /// <summary>The lower-case tag name of the event target, when available. Element content is never captured.</summary>
    public string? TargetTag { get; set; }

    /// <summary>Time between the input and the start of event processing, in milliseconds.</summary>
    public double InputDelayMs { get; set; }

    /// <summary>Time spent running event handlers, in milliseconds.</summary>
    public double ProcessingMs { get; set; }

    /// <summary>Time from the end of processing until the next frame was presented, in milliseconds.</summary>
    public double PresentationDelayMs { get; set; }

    /// <summary>
    /// Time from the input until the content the interaction changed was painted, in milliseconds (the browser's
    /// <c>interaction-contentful-paint</c> entries, Chromium). Unlike <see cref="ProfilerEvent.Duration"/>, which ends at the
    /// first paint after the event handlers, it includes content rendered after asynchronous work, such as an awaited
    /// WebAssembly handler that loads data. Null when the browser did not report it: other browsers, interactions that
    /// painted no new content, and Blazor Server, whose updates arrive over the circuit outside the interaction's task chain.
    /// </summary>
    public double? ContentPaintMs { get; set; }

    /// <summary>
    /// Whether the interaction was faster than the reporting threshold and was recorded only because its content painted
    /// late. Such interactions are left out of the interaction latency statistics, which would otherwise depend on the
    /// browser's support for content paint.
    /// </summary>
    public bool ContentPaintOnly { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(EventType) + StringSize(TargetTag) + 32;
}

/// <summary>A long task or long animation frame observed by the browser.</summary>
public sealed class BrowserLongTaskEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserLongTask;

    /// <summary>The entry type (<c>long-animation-frame</c> or <c>longtask</c>).</summary>
    public string? EntryType { get; set; }

    /// <summary>The blocking duration in milliseconds, when reported.</summary>
    public double BlockingMs { get; set; }

    /// <summary>The sanitized source of the longest script, when reported (long animation frames only).</summary>
    public string? Script { get; set; }

    /// <summary>
    /// Time the frame spent in forced (synchronous) style and layout, summed over its scripts, in milliseconds: layout that a
    /// script triggered by reading geometry after changing the DOM (long animation frames only; zero when not reported).
    /// </summary>
    public double ForcedStyleAndLayoutMs { get; set; }

    /// <summary>Time from the start of the frame's rendering phase (<c>renderStart</c>) until its end, in milliseconds.</summary>
    public double RenderMs { get; set; }

    /// <summary>Time from the start of style and layout (<c>styleAndLayoutStart</c>) until the end of the frame, in milliseconds.</summary>
    public double StyleAndLayoutMs { get; set; }

    /// <summary>
    /// The longest scripts that ran in the frame (long animation frames only; at most <see cref="MaxScripts"/>), with Long
    /// Animation Frame script attribution.
    /// </summary>
    public List<LongFrameScript>? Scripts { get; set; }

    /// <summary>The maximum number of scripts kept per frame.</summary>
    public const int MaxScripts = 5;

    /// <inheritdoc />
    public override int EstimateSize() =>
        base.EstimateSize() + StringSize(EntryType) + StringSize(Script) + 32 + (Scripts?.Sum(s => s.EstimateSize()) ?? 0);
}

/// <summary>
/// A script that ran during a long animation frame (a <c>PerformanceScriptTiming</c> entry of the Long Animation Frames API).
/// Script contents are never captured; only the invoker, the sanitized source location, and timings.
/// </summary>
public sealed class LongFrameScript
{
    /// <summary>
    /// What invoked the script, as reported by the browser: for example <c>BUTTON#save.onclick</c>,
    /// <c>Window.requestAnimationFrame</c>, <c>Response.json.then</c>, or a script URL.
    /// </summary>
    public string? Invoker { get; set; }

    /// <summary>
    /// The invoker type: <c>user-callback</c>, <c>event-listener</c>, <c>resolve-promise</c>, <c>reject-promise</c>,
    /// <c>classic-script</c>, or <c>module-script</c>.
    /// </summary>
    public string? InvokerType { get; set; }

    /// <summary>The sanitized URL of the script source, when known.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>The name of the function that was invoked, when known.</summary>
    public string? SourceFunctionName { get; set; }

    /// <summary>The character position of the function in the source, or -1 when unknown.</summary>
    public int SourceCharPosition { get; set; } = -1;

    /// <summary>Total script duration in milliseconds (including microtasks it scheduled).</summary>
    public double DurationMs { get; set; }

    /// <summary>Time spent in forced (synchronous) style and layout caused by this script, in milliseconds.</summary>
    public double ForcedStyleAndLayoutMs { get; set; }

    /// <summary>Time the script spent paused in synchronous operations (alert, synchronous XHR), in milliseconds.</summary>
    public double PauseMs { get; set; }

    /// <summary>A display label: the function name and source file when known, otherwise the invoker.</summary>
    public string Label
    {
        get
        {
            var file = SourceUrl is { Length: > 0 } url ? url[(url.LastIndexOf('/') + 1)..] : null;
            if (file is { Length: > 0 } && file.IndexOfAny(['?', '#']) is var cut and > 0)
            {
                file = file[..cut];
            }

            var function = SourceFunctionName is { Length: > 0 } name ? name : null;
            return (function, file) switch
            {
                (not null, not null) => $"{function} ({file})",
                (not null, null) => function,
                (null, not null) => $"{Invoker ?? "script"} ({file})",
                _ => Invoker ?? "(unknown script)",
            };
        }
    }

    internal int EstimateSize() => 64 + Size(Invoker) + Size(InvokerType) + Size(SourceUrl) + Size(SourceFunctionName);

    private static int Size(string? value) => value is null ? 0 : 22 + (value.Length * 2);
}

/// <summary>The kind of a <see cref="BrowserFrustrationEvent"/>.</summary>
public enum FrustrationSignal
{
    /// <summary>Several clicks in quick succession on the same spot, typically because the page did not respond.</summary>
    RageClick = 0,

    /// <summary>A click on an interactive element (button, link, input) after which the page did not change.</summary>
    DeadClick = 1,
}

/// <summary>
/// A user frustration signal observed in the browser: a rage click or a dead click. Detected by the browser module from
/// click positions and DOM mutations; element content, text, and input values are never captured.
/// </summary>
public sealed class BrowserFrustrationEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserFrustration;

    /// <summary>The signal.</summary>
    public FrustrationSignal Signal { get; set; }

    /// <summary>The number of clicks the signal covers (one for a dead click).</summary>
    public int ClickCount { get; set; } = 1;

    /// <summary>The lower-case tag name of the clicked element.</summary>
    public string? TargetTag { get; set; }

    /// <summary>
    /// A short structural description of the clicked element: its tag, <c>id</c>, and up to two class names (for example
    /// <c>button#save.btn.btn-primary</c>). Never contains text or attribute values other than the id and classes.
    /// </summary>
    public string? Target { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + 8 + StringSize(TargetTag) + StringSize(Target);
}

/// <summary>
/// A browser Resource Timing entry for a request that was not matched to an <see cref="HttpRequestEvent"/>
/// (for example images, scripts, or fetches that do not use <c>HttpClient</c>).
/// </summary>
public sealed class BrowserResourceEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserResource;

    /// <summary>The sanitized URL.</summary>
    public string? Url { get; set; }

    /// <summary>The resource initiator type (for example <c>fetch</c>, <c>img</c>, or <c>script</c>).</summary>
    public string? InitiatorType { get; set; }

    /// <summary>The transferred size in bytes (zero for cache hits or cross-origin resources without Timing-Allow-Origin).</summary>
    public long TransferSize { get; set; }

    /// <summary>The decoded body size in bytes.</summary>
    public long DecodedBodySize { get; set; }

    /// <summary>Whether the response appears to have been served from the browser cache.</summary>
    public bool? FromCache { get; set; }

    /// <summary>The HTTP response status when reported by the browser; otherwise <see langword="null"/>.</summary>
    public int? StatusCode { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(Url) + StringSize(InitiatorType) + 32;
}

/// <summary>A framework file downloaded during startup.</summary>
public sealed class StartupResource
{
    /// <summary>The file name (without path or query).</summary>
    public string? Name { get; set; }

    /// <summary>Bytes transferred over the network (zero when served from the browser cache).</summary>
    public long TransferSize { get; set; }

    /// <summary>The decoded (uncompressed) size in bytes.</summary>
    public long DecodedSize { get; set; }

    /// <summary>The download duration in milliseconds.</summary>
    public double DurationMs { get; set; }

    /// <summary>Whether the file came from the browser cache.</summary>
    public bool Cached { get; set; }
}

/// <summary>
/// Page startup as measured by the browser: navigation and paint timing, the moment the page became interactive (the
/// first interactive render of <c>BlazorProfilerClient</c>), and the Blazor framework download (<c>_framework</c> files).
/// The event starts at navigation start and lasts until the page became interactive. Reported once per page load.
/// </summary>
public sealed class BrowserStartupEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserStartup;

    /// <summary>The navigation type (navigate, reload, back_forward, prerender).</summary>
    public string? NavigationType { get; set; }

    /// <summary>The detected Blazor runtime: webassembly, webapp-webassembly, webapp, server, or unknown.</summary>
    public string? RuntimeKind { get; set; }

    /// <summary>Time to first byte of the document, in milliseconds after navigation start.</summary>
    public double TimeToFirstByteMs { get; set; }

    /// <summary>First Contentful Paint, in milliseconds after navigation start (zero when unavailable).</summary>
    public double FirstContentfulPaintMs { get; set; }

    /// <summary>The end of DOMContentLoaded, in milliseconds after navigation start.</summary>
    public double DomContentLoadedMs { get; set; }

    /// <summary>The end of the load event, in milliseconds after navigation start (zero when it had not finished).</summary>
    public double LoadMs { get; set; }

    /// <summary>
    /// When the page became interactive, in milliseconds after navigation start: the first interactive render of
    /// <c>BlazorProfilerClient</c> (estimated; render it early in the layout for accuracy).
    /// </summary>
    public double InteractiveMs { get; set; }

    /// <summary>The number of <c>_framework</c> files loaded.</summary>
    public int FrameworkFiles { get; set; }

    /// <summary>Bytes of <c>_framework</c> files transferred over the network.</summary>
    public long FrameworkTransferBytes { get; set; }

    /// <summary>Decoded (uncompressed) bytes of <c>_framework</c> files.</summary>
    public long FrameworkDecodedBytes { get; set; }

    /// <summary>The number of <c>_framework</c> files served from the browser cache.</summary>
    public int FrameworkCachedFiles { get; set; }

    /// <summary>The number of large <c>_framework</c> files that arrived without content encoding (compression).</summary>
    public int FrameworkUncompressedFiles { get; set; }

    /// <summary>The time from the first <c>_framework</c> request to the end of the last one, in milliseconds.</summary>
    public double FrameworkDownloadMs { get; set; }

    /// <summary>The largest framework files.</summary>
    public List<StartupResource> LargestFiles { get; set; } = [];

    /// <summary>Logical processors reported by the browser (<c>navigator.hardwareConcurrency</c>), when available.</summary>
    public int? HardwareConcurrency { get; set; }

    /// <summary>Approximate device memory in GB (<c>navigator.deviceMemory</c>, Chromium; coarse buckets), when available.</summary>
    public double? DeviceMemoryGb { get; set; }

    /// <summary>
    /// The browser's CPU performance tier (<c>navigator.cpuPerformance</c>, Chromium: 1 low to 4 high, 0 unknown), when
    /// available.
    /// </summary>
    public int? CpuPerformance { get; set; }

    /// <summary>The effective connection type (<c>navigator.connection.effectiveType</c>: slow-2g, 2g, 3g, 4g), when available.</summary>
    public string? EffectiveConnectionType { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + 160 + StringSize(NavigationType) + StringSize(RuntimeKind) + (LargestFiles.Count * 96);
}

/// <summary>
/// A page quality value reported by the browser: the Core Web Vitals LCP (ms), CLS (unitless), and INP (ms, estimated), and
/// the DOM size (<c>DOM</c>: number of elements, reported when it changes by 10% or more).
/// </summary>
public sealed class BrowserVitalEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.BrowserVital;

    /// <summary>The metric: <c>LCP</c>, <c>CLS</c>, <c>INP</c>, or <c>DOM</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The value (milliseconds for LCP and INP; unitless for CLS).</summary>
    public double Value { get; set; }

    /// <summary>The element tag of the LCP element or the largest layout shift source, when known.</summary>
    public string? TargetTag { get; set; }

    /// <summary>The Web Vitals rating: good, needs-improvement, or poor.</summary>
    public string? Rating => Rate(Name, Value);

    /// <summary>
    /// Rates a value with the Web Vitals thresholds (LCP 2.5 s / 4 s, CLS 0.1 / 0.25, INP 200 / 500 ms) and Lighthouse's DOM
    /// size thresholds (800 / 1,400 elements).
    /// </summary>
    public static string? Rate(string? name, double value) => name switch
    {
        "DOM" => value <= 800 ? "good" : value <= 1400 ? "needs-improvement" : "poor",
        "LCP" => value <= 2500 ? "good" : value <= 4000 ? "needs-improvement" : "poor",
        "CLS" => value <= 0.1 ? "good" : value <= 0.25 ? "needs-improvement" : "poor",
        "INP" => value <= 200 ? "good" : value <= 500 ? "needs-improvement" : "poor",
        _ => null,
    };

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + 16 + StringSize(Name) + StringSize(TargetTag);
}
