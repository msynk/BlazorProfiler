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

    /// <summary>The <see cref="ProfilerEvent.ErrorType"/> of a call that JavaScript ran and that threw.</summary>
    internal const string JavaScriptErrorType = "Microsoft.JSInterop.JSException";

    /// <summary>The <see cref="ProfilerEvent.ErrorType"/> of a call made after the connection to the browser was lost.</summary>
    internal const string DisconnectedErrorType = "Microsoft.JSInterop.JSDisconnectedException";

    /// <summary>
    /// The <see cref="ProfilerEvent.ErrorType"/> of a call that could not be issued because JavaScript interop was unavailable,
    /// for example while the component was statically rendered (prerendered).
    /// </summary>
    internal const string UnavailableErrorType = "System.InvalidOperationException";

    /// <summary>Whether JavaScript answered the call: it returned a result or threw a JavaScript exception.</summary>
    internal bool Answered => Outcome == ProfilerOutcome.Success || (Outcome == ProfilerOutcome.Error && ErrorType == JavaScriptErrorType);

    /// <summary>
    /// Whether the call ended without a response from JavaScript: it was canceled or timed out
    /// (<see cref="ProfilerOutcome.Canceled"/>), the connection to the browser was lost (<c>JSDisconnectedException</c>), or
    /// interop was unavailable (<c>InvalidOperationException</c>, for example during prerendering). The duration of such a
    /// call is how long the caller waited, not how long JavaScript took.
    /// </summary>
    internal bool EndedWithoutResponse =>
        Outcome == ProfilerOutcome.Canceled || (Outcome == ProfilerOutcome.Error && ErrorType is DisconnectedErrorType or UnavailableErrorType);

    /// <inheritdoc />
    public override int EstimateSize() => base.EstimateSize() + StringSize(Identifier) + StringSize(OriginComponentType) + 40;
}

/// <summary>
/// A database command executed through Entity Framework Core (observed through its <c>DiagnosticListener</c>). The command
/// text is normalized (literals replaced with <c>?</c>) and parameter values are never stored; only a hash of them is kept
/// so identical repeated queries can be recognized.
/// </summary>
public sealed class DatabaseCommandEvent : ProfilerEvent
{
    /// <inheritdoc />
    [JsonIgnore]
    public override ProfilerEventKind Kind => ProfilerEventKind.DatabaseCommand;

    /// <summary>The normalized command text (literals replaced, whitespace collapsed, truncated).</summary>
    public string? CommandText { get; set; }

    /// <summary>A hash of the normalized command text: equal for the same query shape.</summary>
    public long CommandHash { get; set; }

    /// <summary>
    /// A hash of the command text and parameter values: equal for identical queries (same shape and arguments). Values are
    /// hashed, never stored.
    /// </summary>
    public long ParameterHash { get; set; }

    /// <summary>The number of parameters.</summary>
    public int ParameterCount { get; set; }

    /// <summary>How the command was executed: <c>Reader</c>, <c>NonQuery</c>, or <c>Scalar</c>.</summary>
    public string? ExecuteMethod { get; set; }

    /// <summary>Whether the command was executed asynchronously.</summary>
    public bool IsAsync { get; set; }

    /// <summary>The rows read from a data reader, when known.</summary>
    public int? RowsRead { get; set; }

    /// <summary>The <c>DbContext</c> type that ran the command.</summary>
    public string? ContextType { get; set; }

    /// <summary>A process-unique identifier of the <c>DbContext</c> instance (not stable across runs).</summary>
    public long ContextInstanceId { get; set; }

    /// <summary>The database provider (connection type name, for example <c>SqlConnection</c>).</summary>
    public string? Provider { get; set; }

    /// <summary>The component that started the command, if it ran inside a component context.</summary>
    public string? OriginComponentType { get; set; }

    /// <summary>The instance id of the originating component.</summary>
    public long OriginComponentInstanceId { get; set; }

    /// <summary>The lifecycle method in which the command was started, if known.</summary>
    public LifecycleMethod? OriginLifecycleMethod { get; set; }

    /// <summary>Whether the command started on a renderer's dispatcher (blocking it when synchronous), when known.</summary>
    public bool? OnRendererThread { get; set; }

    /// <summary>How the command was observed.</summary>
    public InstrumentationSource Source { get; set; }

    /// <inheritdoc />
    public override int EstimateSize() =>
        base.EstimateSize() + 72 + StringSize(CommandText) + StringSize(ExecuteMethod) + StringSize(ContextType) + StringSize(Provider) + StringSize(OriginComponentType);
}
