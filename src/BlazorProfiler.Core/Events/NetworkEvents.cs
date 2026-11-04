using System.Text.Json.Serialization;

namespace BlazorProfiler.Events;

/// <summary>An HTTP request sent by the application.</summary>
/// <remarks>
/// Request and response bodies, headers, cookies, and authorization data are never captured. The URL is sanitized by the
/// active <see cref="Pipeline.IProfilerRedactionPolicy"/> before the event is stored.
/// </remarks>
public sealed class HttpRequestEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.HttpRequest;

    /// <summary>The HTTP method.</summary>
    public string? Method { get; set; }

    /// <summary>The sanitized URL.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// A normalized endpoint key (method, host, and templated path) used to group requests to the same endpoint.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// A salted, process-local hash of the original (unsanitized) URL. It allows exact-duplicate detection without
    /// storing sensitive values and is meaningless outside the producing process.
    /// </summary>
    public long UrlHash { get; set; }

    /// <summary>The response status code, when a response was received.</summary>
    public int? StatusCode { get; set; }

    /// <summary>The request content length, when known without reading the body.</summary>
    public long? RequestBytes { get; set; }

    /// <summary>The response size, when known.</summary>
    public long? ResponseBytes { get; set; }

    /// <summary>The quality of <see cref="ResponseBytes"/>.</summary>
    public ValueQuality ResponseBytesQuality { get; set; }

    /// <summary>Whether the browser served the response from its cache, when known (Resource Timing enrichment).</summary>
    public bool? FromCache { get; set; }

    /// <summary>The retry attempt number (0 for the first attempt), when the request carried a retry marker.</summary>
    public int RetryAttempt { get; set; }

    /// <summary>The mechanism that observed the request.</summary>
    public InstrumentationSource Source { get; set; }

    /// <summary>The component type whose lifecycle or event started the request, when known.</summary>
    public string? OriginComponentType { get; set; }

    /// <summary>The component instance whose lifecycle or event started the request, when known.</summary>
    public long OriginComponentInstanceId { get; set; }

    /// <summary>The component lifecycle phase that started the request, when known.</summary>
    public LifecycleMethod? OriginLifecycleMethod { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() =>
        base.EstimateSize() + StringSize(Method) + StringSize(Url) + StringSize(Endpoint) + StringSize(OriginComponentType) + 64;

    /// <summary>
    /// Returns a copy enriched with browser Resource Timing data. Accepted events are immutable, so enrichment that arrives
    /// after the request was stored replaces the stored event with this copy.
    /// </summary>
    /// <param name="responseBytes">The measured transfer size, or <see langword="null"/> to keep the current value.</param>
    /// <param name="fromCache">Whether the browser served the response from its cache, when known.</param>
    internal HttpRequestEvent WithBrowserTiming(long? responseBytes, bool? fromCache)
    {
        var copy = (HttpRequestEvent)MemberwiseClone();
        if (responseBytes is { } bytes)
        {
            copy.ResponseBytes = bytes;
            copy.ResponseBytesQuality = ValueQuality.Measured;
        }

        copy.FromCache = fromCache;
        return copy;
    }
}

/// <summary>Direction of a JavaScript interop call.</summary>
public enum InteropDirection
{
    /// <summary>.NET invoked JavaScript.</summary>
    DotNetToJS = 0,
    /// <summary>JavaScript invoked .NET.</summary>
    JSToDotNet = 1,
}

/// <summary>A JavaScript interop invocation.</summary>
/// <remarks>Arguments and return values are never captured. Payload sizes are estimates and labeled as such.</remarks>
public sealed class JSInteropEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.JSInterop;

    /// <summary>The call direction.</summary>
    public InteropDirection Direction { get; set; }

    /// <summary>The (possibly redacted) function identifier or method name.</summary>
    public string? Identifier { get; set; }

    /// <summary>Whether the call was synchronous (in-process) rather than asynchronous.</summary>
    public bool Synchronous { get; set; }

    /// <summary>Whether the call targeted a JavaScript object reference (for example an imported module).</summary>
    public bool ViaObjectReference { get; set; }

    /// <summary>The number of arguments passed.</summary>
    public int ArgumentCount { get; set; }

    /// <summary>The estimated serialized size of the arguments in bytes, when estimation is enabled.</summary>
    public long? PayloadBytes { get; set; }

    /// <summary>The quality of <see cref="PayloadBytes"/>; always <see cref="ValueQuality.Estimated"/> when present.</summary>
    public ValueQuality PayloadBytesQuality { get; set; }

    /// <summary>The mechanism that observed the call.</summary>
    public InstrumentationSource Source { get; set; }

    /// <summary>The component type whose lifecycle or event started the call, when known.</summary>
    public string? OriginComponentType { get; set; }

    /// <summary>The component instance whose lifecycle or event started the call, when known.</summary>
    public long OriginComponentInstanceId { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(Identifier) + StringSize(OriginComponentType) + 40;
}
