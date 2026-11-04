using BlazorProfiler.Api;
using BlazorProfiler.Collectors;
using BlazorProfiler.Interop;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorProfiler.Browser;

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

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    /// <summary>
    /// Whether to expose the profiler to the dashboard through a same-origin <c>BroadcastChannel</c>. Defaults to
    /// <see langword="true"/> in WebAssembly and <see langword="false"/> on the server, where the dashboard uses HTTP endpoints.
    /// </summary>
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
        try
        {
            _callbacks = DotNetObjectReference.Create(new BrowserCallbacks(collector, bridge ? Services.GetService<ProfilerApi>() : null));
            _module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/BlazorProfiler/blazor-profiler.js");
            var options = profiler.Options.Collectors;
            var clientNow = await _module.InvokeAsync<double>("start", _callbacks, SerializeOptions(
                options.Browser,
                Math.Max(16, options.BrowserInteractionThresholdMs),
                bridge,
                hosting.Source,
                Shortcut ?? string.Empty,
                ShowLauncher,
                DashboardUrl ?? hosting.DashboardUrl));

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
                await _module.InvokeVoidAsync("stop");
                await _module.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or ObjectDisposedException)
            {
            }
        }

        _callbacks?.Dispose();
    }

    private static string SerializeOptions(bool collect, int thresholdMs, bool bridge, string source, string shortcut, bool showLauncher, string dashboardUrl)
    {
        // Serialized explicitly (no reflection) so the payload is trimming-safe in WebAssembly.
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("collect", collect);
            writer.WriteNumber("interactionThresholdMs", thresholdMs);
            writer.WriteNumber("flushIntervalMs", 2000);
            writer.WriteBoolean("bridge", bridge);
            writer.WriteString("source", source);
            writer.WriteString("shortcut", shortcut);
            writer.WriteBoolean("showLauncher", showLauncher);
            writer.WriteString("dashboardUrl", dashboardUrl);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Callbacks invoked by the browser module.</summary>
    public sealed class BrowserCallbacks
    {
        private readonly BrowserCollector _collector;
        private readonly ProfilerApi? _api;

        internal BrowserCallbacks(BrowserCollector collector, ProfilerApi? api)
        {
            _collector = collector;
            _api = api;
        }

        internal double ClockOffsetMs { get; set; }

        /// <summary>Receives a batch of browser entries.</summary>
        [JSInvokable("BlazorProfiler.Ingest")]
        public int Ingest(string json) => _collector.Ingest(json, ClockOffsetMs);

        /// <summary>Handles a dashboard request received through the same-origin bridge.</summary>
        [JSInvokable("BlazorProfiler.Api")]
        public Task<string> HandleApiRequest(string json) =>
            _api is null
                ? Task.FromResult("{\"ok\":false,\"error\":\"The dashboard bridge is disabled.\"}")
                : _api.HandleAsync(json, ProfilerApiAccess.Full);
    }
}
