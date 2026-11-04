using BlazorProfiler.Collectors;
using BlazorProfiler.Events;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorProfiler.Http;

/// <summary>Reports the state of HTTP collection.</summary>
public sealed class HttpCollector : ProfilerCollectorBase
{
    /// <inheritdoc />
    public override CollectorDescriptor Descriptor { get; } = new()
    {
        Name = "http",
        Description = "HTTP requests sent through HttpClient: method, sanitized URL, endpoint, status, sizes from Content-Length headers, cancellation, failures, retry markers, and the originating operation/component.",
        Mechanism = "A DelegatingHandler (ProfilerHttpMessageHandler) added to every IHttpClientFactory client via ConfigureHttpClientDefaults, or added explicitly to manually created HttpClient instances.",
        Capability = CapabilityLevel.Partial,
        Overhead = OverheadLevel.Low,
        Compatibility = "All hosting models. HttpClient instances created with 'new HttpClient()' are only observed when constructed with ProfilerHttpMessageHandler.",
        Degradation = "Requests that bypass HttpClient (images, scripts, JavaScript fetch) are observed through browser Resource Timing when the browser collector is active.",
    };

    /// <summary>Whether requests should be recorded.</summary>
    public bool IsCollecting => IsActive;

    /// <summary>Emits an event.</summary>
    public bool Publish(ProfilerEvent profilerEvent) => Emit(profilerEvent);

    /// <inheritdoc />
    protected override bool IsEnabled(ProfilerOptions options) => options.Collectors.Http;

    /// <inheritdoc />
    protected override void OnStart(ProfilerCollectorContext context)
    {
    }

    /// <inheritdoc />
    protected override void OnStop()
    {
    }
}

/// <summary>
/// A <see cref="DelegatingHandler"/> that records HTTP requests. Never reads request or response bodies, headers other than
/// Content-Length, cookies, or credentials, and never changes request semantics: exceptions and cancellation propagate
/// unchanged.
/// </summary>
public sealed class ProfilerHttpMessageHandler : DelegatingHandler
{
    /// <summary>
    /// An <see cref="HttpRequestOptionsKey{TValue}"/> that callers (for example retry policies) can set to report the retry
    /// attempt number of a request (0 for the first attempt).
    /// </summary>
    public static readonly HttpRequestOptionsKey<int> RetryAttemptKey = new("BlazorProfiler.RetryAttempt");

    private static readonly AsyncLocal<bool> s_inside = new();

    private readonly HttpCollector _collector;
    private readonly IProfiler _profiler;

    /// <summary>Creates the handler.</summary>
    public ProfilerHttpMessageHandler(HttpCollector collector, IProfiler profiler)
    {
        _collector = collector;
        _profiler = profiler;
    }

    /// <summary>
    /// Creates a handler chain for a manually constructed <see cref="HttpClient"/>, for example the default Blazor
    /// WebAssembly client: <c>new HttpClient(ProfilerHttpMessageHandler.Create(sp)) { BaseAddress = ... }</c>.
    /// </summary>
    public static HttpMessageHandler Create(IServiceProvider services, HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var collector = services.GetService<HttpCollector>();
        var profiler = services.GetService<IProfiler>();
        innerHandler ??= new HttpClientHandler();
        return collector is null || profiler is null
            ? innerHandler
            : new ProfilerHttpMessageHandler(collector, profiler) { InnerHandler = innerHandler };
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_collector.IsCollecting || s_inside.Value)
        {
            return base.SendAsync(request, cancellationToken);
        }

        return SendTrackedAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendTrackedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var start = _profiler.Clock.GetTimestamp();
        var e = new HttpRequestEvent
        {
            Method = request.Method.Method,
            Url = request.RequestUri?.ToString(),
            RequestBytes = request.Content?.Headers.ContentLength,
            Source = InstrumentationSource.HttpMessageHandler,
            Timestamp = start,
        };

        if (request.Options.TryGetValue(RetryAttemptKey, out var attempt))
        {
            e.RetryAttempt = attempt;
        }

        // Prevent nested profiler handlers (for example registered twice) from recording the same request again.
        s_inside.Value = true;
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            e.StatusCode = (int)response.StatusCode;
            e.Outcome = response.IsSuccessStatusCode ? ProfilerOutcome.Success : ProfilerOutcome.Error;
            if (response.Content?.Headers.ContentLength is { } length)
            {
                e.ResponseBytes = length;
                e.ResponseBytesQuality = ValueQuality.Measured;
            }

            return response;
        }
        catch (OperationCanceledException ex)
        {
            e.Outcome = cancellationToken.IsCancellationRequested ? ProfilerOutcome.Canceled : ProfilerOutcome.Error;
            e.ErrorType = cancellationToken.IsCancellationRequested ? ex.GetType().FullName : "Timeout";
            throw;
        }
        catch (Exception ex)
        {
            e.Outcome = ProfilerOutcome.Error;
            e.ErrorType = ex.GetType().FullName;
            throw;
        }
        finally
        {
            s_inside.Value = false;
            e.Duration = _profiler.Clock.GetTimestamp() - start;
            try
            {
                _collector.Publish(e);
            }
            catch (Exception)
            {
                // Profiling must never affect the request.
            }
        }
    }
}
