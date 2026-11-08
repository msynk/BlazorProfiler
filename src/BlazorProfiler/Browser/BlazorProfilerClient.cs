using BlazorProfiler.Api;
using BlazorProfiler.Collectors;
using BlazorProfiler.Interop;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorProfiler.Browser;

/// <summary>
/// Decides what a dashboard connected through the same-origin bridge of <see cref="BlazorProfilerClient"/> may access.
/// </summary>
/// <remarks>
/// In WebAssembly the profiler runs in the user's own browser, so the bridge has full access to it. On the server the
/// profiler holds every user's data, so the bridge is only enabled when the hosting integration registers a provider that
/// authorizes the circuit's user (the Blazor Server integration does).
/// </remarks>
public interface IProfilerBridgeAccessProvider
{
    /// <summary>Returns the access granted to bridge requests of the current user, or <see langword="null"/> to refuse them.</summary>
    ValueTask<ProfilerApiAccess?> GetAccessAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Connects the browser to the profiler: collects browser performance entries and, in WebAssembly, exposes the profiler to
/// the dashboard through a same-origin <c>BroadcastChannel</c>. Renders nothing.
/// </summary>
/// <remarks>
/// Place the component once in an interactive part of the app, for example in <c>MainLayout.razor</c> or <c>Routes.razor</c>.
/// It does nothing when the profiler is disabled or not registered, and during static server-side rendering.
/// </remarks>
public sealed class BlazorProfilerClient : IComponent, IHandleAfterRender, IAsyncDisposable
{
    private RenderHandle _renderHandle;
    private IJSObjectReference? _module;
    private DotNetObjectReference<BrowserCallbacks>? _callbacks;
    private bool _started;
    private bool _disposed;

    // Identifies this client to the browser module, which is shared by the whole document: when a client is disposed after
    // its replacement started (for example on an enhanced navigation), its stop call must not stop the replacement.
    private readonly string _owner = Guid.NewGuid().ToString("N");

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    /// <summary>
    /// Whether to expose the profiler to the dashboard through a same-origin <c>BroadcastChannel</c>. Defaults to
    /// <see langword="true"/> in WebAssembly and <see langword="false"/> on the server, where the dashboard uses HTTP endpoints.
    /// </summary>
    /// <remarks>
    /// On the server, bridge requests are authorized for the circuit's user by the registered
    /// <see cref="IProfilerBridgeAccessProvider"/> (with the same policies as the HTTP endpoints); without a provider the
    /// bridge stays disabled.
    /// </remarks>
    [Parameter]
    public bool? EnableDashboardBridge { get; set; }

    /// <summary>A keyboard shortcut that opens the dashboard, for example <c>Alt+Shift+P</c>. Set to an empty string to disable.</summary>
    [Parameter]
    public string? Shortcut { get; set; } = "Alt+Shift+P";

    /// <summary>Whether to show a small floating button that opens the dashboard.</summary>
    [Parameter]
    public bool ShowLauncher { get; set; }

    /// <summary>Overrides the dashboard URL.</summary>
    [Parameter]
    public string? DashboardUrl { get; set; }

    /// <inheritdoc />
    public void Attach(RenderHandle renderHandle) => _renderHandle = renderHandle;

    /// <inheritdoc />
    public Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        if (!_started)
        {
            // Render once (empty) so OnAfterRenderAsync runs when the component is interactive.
            _renderHandle.Render(static _ => { });
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task OnAfterRenderAsync()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        var profiler = Services.GetService<IProfiler>();
        var collector = Services.GetService<BrowserCollector>();
        var hosting = Services.GetService<ProfilerHostingInfo>();
        if (profiler is null || collector is null || hosting is null || !profiler.IsEnabled)
        {
            return;
        }

        // Use the unprofiled runtime so the profiler's own interop calls are never recorded.
        var js = JSRuntime is IProfiledJSRuntime profiled ? profiled.InnerRuntime : JSRuntime;
        var bridge = EnableDashboardBridge ?? hosting.IsWebAssembly;
        var api = bridge ? Services.GetService<ProfilerApi>() : null;

        // In WebAssembly the profiler only holds this browser's data. On the server it holds every user's data, so the
        // bridge needs a provider that authorizes the circuit's user; without one it stays disabled.
        var access = !bridge ? null : Services.GetService<IProfilerBridgeAccessProvider>() ?? (hosting.IsWebAssembly ? FullBridgeAccess.Instance : null);
        if (api is null || access is null)
        {
            bridge = false;
            api = null;
            access = null;
        }

        try
        {
            // On the server, bridge requests arrive on the circuit's dispatcher; their work (analysis, serializing large pages)
            // is moved off it so that it never blocks the user's UI. WebAssembly has a single thread, so there is nothing to
            // gain there.
            _callbacks = DotNetObjectReference.Create(new BrowserCallbacks(collector, api, access, offload: !hosting.IsWebAssembly));
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/BlazorProfiler/blazor-profiler.js");
            var options = profiler.Options.Collectors;
            var clientNow = await _module.InvokeAsync<double>("start", _callbacks, SerializeOptions(
                options.Browser,
                Math.Max(16, options.BrowserInteractionThresholdMs),
                hosting.IsWebAssembly ? BrowserCollector.WebAssemblyMaxBatchBytes : BrowserCollector.ServerMaxBatchBytes,

                // Bridge requests above this size are streamed: on the server a single message above SignalR's limit
                // would close the circuit. WebAssembly calls are in-process and have no such limit (0).
                hosting.IsWebAssembly ? 0 : BrowserCollector.ServerMaxBatchBytes,
                bridge,
                hosting.Source,
                Shortcut ?? string.Empty,
                ShowLauncher,
                DashboardUrl ?? hosting.DashboardUrl,
                _owner));

            _callbacks.Value.ClockOffsetMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clientNow;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // The browser integration is optional; the rest of the profiler keeps working.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("stop", _owner);
                await _module.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
            {
            }
        }

        _callbacks?.Dispose();
    }

    private static string SerializeOptions(bool collect, int thresholdMs, int maxBatchBytes, int maxRequestBytes, bool bridge, string source, string shortcut, bool showLauncher, string dashboardUrl, string owner)
    {
        // Serialized explicitly (no reflection) so the payload is trimming-safe in WebAssembly.
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("collect", collect);
            writer.WriteNumber("interactionThresholdMs", thresholdMs);
            writer.WriteNumber("flushIntervalMs", 2000);
            writer.WriteNumber("maxBatchBytes", maxBatchBytes);
            writer.WriteNumber("maxRequestBytes", maxRequestBytes);
            writer.WriteBoolean("bridge", bridge);
            writer.WriteString("source", source);
            writer.WriteString("shortcut", shortcut);
            writer.WriteBoolean("showLauncher", showLauncher);
            writer.WriteString("dashboardUrl", dashboardUrl);
            writer.WriteString("owner", owner);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Callbacks invoked by the browser module.</summary>
    public sealed class BrowserCallbacks
    {
        private const string BridgeDisabled = "{\"ok\":false,\"error\":\"The dashboard bridge is disabled.\"}";
        private const string NotAuthorized = "{\"ok\":false,\"error\":\"Not authorized to access the profiler.\"}";
        private const string TooLarge = "{\"ok\":false,\"error\":\"The request is too large.\"}";

        private readonly BrowserCollector _collector;
        private readonly ProfilerApi? _api;
        private readonly IProfilerBridgeAccessProvider? _access;
        private readonly bool _offload;

        internal BrowserCallbacks(BrowserCollector collector, ProfilerApi? api, IProfilerBridgeAccessProvider? access, bool offload)
        {
            _collector = collector;
            _api = api;
            _access = access;
            _offload = offload;
        }

        internal double ClockOffsetMs { get; set; }

        /// <summary>Receives a batch of browser entries.</summary>
        [JSInvokable("BlazorProfiler.Ingest")]
        public int Ingest(string json) => _collector.Ingest(json, ClockOffsetMs);

        /// <summary>Handles a dashboard request received through the same-origin bridge.</summary>
        /// <remarks>Access is evaluated per request, so revoked permissions and closed circuits take effect immediately.</remarks>
        [JSInvokable("BlazorProfiler.Api")]
        public async Task<string> HandleApiRequest(string json)
        {
            if (_api is null || _access is null)
            {
                return BridgeDisabled;
            }

            var access = await _access.GetAccessAsync();
            return access is null ? NotAuthorized : await RunAsync(_api, json, access);
        }

        /// <summary>
        /// Handles a dashboard request too large for one interop message (for example a trace import), streamed from the
        /// browser. On Blazor Server, a message above SignalR's <c>MaximumReceiveMessageSize</c> would close the circuit,
        /// whereas a stream is transferred in chunks that respect it.
        /// </summary>
        /// <remarks>Access is evaluated before anything is read, so unauthorized callers cannot make the server buffer data.</remarks>
        [JSInvokable("BlazorProfiler.ApiStream")]
        public async Task<string> HandleApiStreamRequest(IJSStreamReference request)
        {
            ArgumentNullException.ThrowIfNull(request);
            await using (request)
            {
                if (_api is null || _access is null)
                {
                    return BridgeDisabled;
                }

                var access = await _access.GetAccessAsync();
                if (access is null)
                {
                    return NotAuthorized;
                }

                if (request.Length > ProfilerApi.MaxRequestLength)
                {
                    return TooLarge;
                }

                string json;
                await using (var stream = await request.OpenReadStreamAsync(ProfilerApi.MaxRequestLength))
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    json = await reader.ReadToEndAsync();
                }

                return await RunAsync(_api, json, access);
            }
        }

        private Task<string> RunAsync(ProfilerApi api, string json, ProfilerApiAccess access) =>
            _offload ? Task.Run(() => api.HandleAsync(json, access)) : api.HandleAsync(json, access);
    }

    /// <summary>Bridge access in WebAssembly, where the profiler only contains the current browser's own data.</summary>
    private sealed class FullBridgeAccess : IProfilerBridgeAccessProvider
    {
        public static FullBridgeAccess Instance { get; } = new();

        public ValueTask<ProfilerApiAccess?> GetAccessAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<ProfilerApiAccess?>(ProfilerApiAccess.Full);
    }
}
