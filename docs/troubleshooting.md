# Troubleshooting

Start with the dashboard's **Profiler health** page: it lists every collector's state (Running, Disabled, Unsupported,
Failed) with the reason and last error, plus dropped and sampled event counts.

| Symptom | Cause and fix |
|---|---|
| `/_blazor-profiler/` returns 404 | The profiler is disabled (not Development and `Enabled` not set), `MapBlazorProfiler()` is not called, or you lack access: outside Development configure `ProfilerServerOptions.AuthorizationPolicy` and sign in with a user that satisfies it. |
| Dashboard says "No running app found" (WebAssembly) | The app tab must render `<BlazorProfilerClient />` in an interactive component, run the profiler (`Enabled`), and be on the same origin as the dashboard. Click **Rescan** after the app has loaded. |
| `component-metrics` is *Unsupported* in WebAssembly | `MetricsSupport` is false. The `BlazorProfiler.WebAssembly` package enables it unless the project sets it; when referencing the project instead of the package, import `buildTransitive/BlazorProfiler.WebAssembly.props` or set `<MetricsSupport>true</MetricsSupport>`. |
| No render batches or handlers in WebAssembly | Make sure `builder.AddBlazorProfiler()` runs on the `WebAssemblyHostBuilder` (it registers component metrics and tracing, which the WebAssembly host does not do by default). |
| Handlers are not grouped into interactions | The `component-activities` collector must be running. Interaction context comes from the framework's `HandleEvent` activity (.NET 10+). |
| No per-component renders, lifecycle times, or tree | Components must inherit `ProfiledComponentBase` (`@inherits` in `_Imports.razor`). Components with their own `@inherits` keep their base class. |
| A component's disposal is not recorded | It implements `IDisposable`/`IAsyncDisposable` itself; its own disposal runs unchanged, and the profiler does not intercept it. |
| HTTP requests missing | Only `HttpClient`s from `IHttpClientFactory` are instrumented automatically. For `new HttpClient(...)` (for example the WebAssembly template), construct it with `ProfilerHttpMessageHandler.Create(serviceProvider)`. |
| JS interop missing on the server | Inject `IProfiledJSRuntime` in the components you want to profile. Replacing `IJSRuntime` is not possible on the server. |
| Calls on imported JS modules missing | Enable `Collectors.JSObjectReferences`. |
| "Events dropped" markers | The queue overflowed. Increase `Pipeline.QueueCapacity`, enable sampling, or use the `LowOverhead` profile. |
| Old events disappear | Retention limits (`Retention.MaxEvents`, `MaxMemoryBytes`, `MaxAge`). Aggregates and findings still include evicted events; the timeline cannot. |
| Browser interactions missing | Event Timing needs a Chromium-based browser or recent Firefox; only interactions longer than `BrowserInteractionThresholdMs` (40 ms) are reported. |
| Component creation not recorded with a custom activator | Register your `IComponentActivator` before `AddBlazorProfiler` so the profiler can wrap it (DI resolves the last registration). |
| Imported trace shows different times | Imported sessions use the trace's own clock; times are relative to that session's start. |
| Findings say "Low confidence" | The evidence is temporal or indirect (for example frequent renders without instrumentation). Instrument the component or check the caveats. |
