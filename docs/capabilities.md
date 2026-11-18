# Capability matrix

Legend: **M** measured · **E** estimated (labelled in the UI) · **I** inferred (labelled) · **—** unavailable.
"Instrumented" means the component inherits `ProfiledComponentBase`. All columns assume .NET 10.

| Signal | Blazor Server / Interactive Server | WebAssembly / Interactive WebAssembly | Static SSR | Collector |
|---|---|---|---|---|
| Component creation (type, instance id) | M | M | M | component-activation |
| Component disposal, lifetime | M (instrumented, including components with their own `IDisposable`; components with their own `IAsyncDisposable` and non-instrumented components are recorded by an instrumented parent) | same | — | component-instrumentation |
| Parameter updates per component type, duration incl. awaited work | M | M | M | component-metrics |
| Parameter change detection (unchanged / reference-only / changed) | M (instrumented) | M (instrumented) | M (instrumented) | component-instrumentation |
| Render (`BuildRenderTree`) duration per instance | M (instrumented) | M (instrumented) | M (instrumented) | component-instrumentation |
| Renders with identical output | M (instrumented + `TrackRenderOutput`) | same | same | component-instrumentation |
| Render requests (queued, coalesced, suppressed by `ShouldRender`) | M (instrumented) | M (instrumented) | — | component-instrumentation |
| Lifecycle method time, synchronous vs. awaited | M (instrumented) | M (instrumented) | M (instrumented) | component-instrumentation |
| Component tree (who rendered whom) | M (instrumented parents) | M (instrumented parents) | — | component-instrumentation |
| Render batch duration and component-diff count | M — includes round trip to the browser | M — includes DOM patching, not paint | M | component-metrics |
| Event handler duration | M | M | — | component-metrics |
| Event handler synchronous portion | M (instrumented receiver) | M (instrumented receiver) | — | component-instrumentation |
| Interaction → handler → batches/HTTP/interop correlation | M (execution context) | M (execution context) | — | component-activities |
| Navigation (route template, page type) | M | M | — | component-metrics |
| HTTP requests via `IHttpClientFactory` | M | M | M | http |
| HTTP requests via manually created `HttpClient` | M if built with `ProfilerHttpMessageHandler` | same | same | http |
| HTTP response size | M (Content-Length) | M (Content-Length, or Resource Timing transfer size) | M | http, browser |
| HTTP cache hit | — | M (Resource Timing) | — | browser |
| Requests not using HttpClient (images, scripts, JS fetch) | M (Resource Timing) | M (Resource Timing) | — | browser |
| .NET → JS interop calls | M via `IProfiledJSRuntime` only | M (all `IJSRuntime` calls) | — | js-interop |
| Calls on `IJSObjectReference` (modules) | M via `IProfiledJSRuntime` + `JSObjectReferences` | M with `JSObjectReferences` | — | js-interop |
| Interop payload size | M (.NET to JS through `IProfiledJSRuntime`, from the circuit message); E (opt-in) otherwise | E (opt-in) | — | js-interop + circuits |
| Circuit message sizes: each render batch sent, each message received (by hub method) | M (Blazor's hub protocol) | — | — | circuits |
| Streaming interop sizes (`DotNetStreamReference` over seekable streams, `IJSStreamReference` results, `DotNet.createJSStreamReference` to .NET) | M | M | — | js-interop + browser |
| JS → .NET calls (`DotNetObjectReference`, `DotNet.invokeMethodAsync`), timed until .NET returns | M (browser; includes the round trip) | M (browser) | — | browser + js-interop |
| JS → .NET calls | M as circuit activities | — | — | circuits |
| Allocation rate, heap size, GC counts, GC pause time | M (process) | M (.NET runtime in the tab) | M (process) | runtime |
| CPU time, working set, thread pool, lock contention | M (process) | — | M | runtime |
| Exceptions thrown (first-chance) by type, active timers | M (process) | M (.NET runtime in the tab) | M (process) | runtime |
| Per-component allocations (bytes allocated by each render) | M (instrumented) | M (instrumented) | M (instrumented) | component-instrumentation |
| Circuit open/close, connection up/down | M | — | — | circuits |
| Circuit inbound activity duration and hub method | M | — | — | circuits |
| User-perceived interaction latency (input delay / processing / presentation) | M (browser) | M (browser) | — | browser |
| Long animation frames / long tasks | M (browser) | M (browser) | — | browser |
| Render causes (first render, parameters, handler, `StateHasChanged`, async completion) and changed parameter names | M (instrumented) | M (instrumented) | M (instrumented) | component-instrumentation |
| Components retained after disposal (instrumented) or after their circuit/request scope ended (all) | M | M (disposal of instrumented components only; the app scope never ends) | M (request scope) | component-retention |
| Database commands (Entity Framework Core): normalized SQL, duration incl. reading rows, rows read, sync/async, originating component | M | M (if EF Core runs in the browser) | M | database |
| Page startup: TTFB, FCP, interactive time (E), `_framework` download (bytes, cache, compression, largest files) | M (browser) | M (browser) | — | browser |
| Browser conditions: cores, device memory, CPU tier, connection type | M (browser; memory and tier Chromium only) | M (browser) | — | browser |
| Application name, version, build configuration, OS, processor count (trace metadata) | M | M | M | export |
| Core Web Vitals: LCP, CLS, INP (E) | M (browser) | M (browser) | — | browser |
| DOM size (elements in the document, sampled) | M (browser) | M (browser) | — | browser |
| DOM updates per second, update highlighting (in-page) | M (browser only) | M (browser only) | — | toolbar (no collector) |
| Long animation frame script attribution (invoker, source, function, forced layout, pause) | M (Chromium) | M (Chromium) | — | browser |
| Forced layout attributed to a JS interop call | I (temporal, when the browser attributes it to Blazor) | I (same) | — | browser + js-interop |
| Rage clicks and dead clicks | M (browser) | M (browser) | — | browser |
| Input to the paint of the content an interaction changed (interaction contentful paint) | — (updates arrive over the circuit) | M (Chromium; after JavaScript interop promises; not after HttpClient responses; after `Task.Delay` only sometimes) | — | browser |
| Retainers of a leaked component (event subscriptions on referenced services, static events, `DotNetObjectReference`, timers) | I (heuristic inspection) | I (same) | I (same) | component-retention |
| Component recreation (dispose + create of one type in one operation) | M (instrumented) | M (instrumented) | — | component-activation + instrumentation |
| CPU samples, GC dump, runtime counters (`.nettrace` download, marker on the timeline) | — (use `dotnet-trace`, `dotnet-gcdump`) | M (builds with `EnableDiagnostics`; CPU samples also need `WasmPerformanceInstrumentation`) | — | toolbar |
| OpenTelemetry metrics and spans of profiler measurements | M (opt-in) | M (opt-in; needs an OTLP exporter in the browser) | M (opt-in) | telemetry processor |

## Hosting-model notes

- **Blazor Web App with Interactive Auto**: the server host collects server-side data; the client project collects
  WebAssembly data. They are separate runtimes with separate sessions. The server dashboard shows server data; the bridge
  dashboard shows the WebAssembly runtime of the selected tab.
- **Static SSR**: framework metrics and HTTP calls made during prerendering are collected on the server. There is no
  interactivity, so no handlers, interactions, or browser data.
- **WebAssembly metrics** require `System.Diagnostics.Metrics` (`MetricsSupport`). The `BlazorProfiler.WebAssembly` package
  turns it on unless the project sets `MetricsSupport` explicitly or `BlazorProfilerEnableMetrics=false`.

## Browser support for browser entries

| Entry | Chromium | Firefox | Safari |
|---|---|---|---|
| Event Timing (`event`, `interactionId`) | yes | yes (recent versions) | yes (26.2+) |
| Long Animation Frames | yes | no (falls back to `longtask` where available) | no |
| Resource Timing | yes | yes | yes |
| Largest Contentful Paint | yes | yes | yes (26.2+) |
| Layout Shift (CLS) | yes | no | no |
| DevTools custom tracks (`detail.devtools`) | yes (128+) | recorded as plain user timings | recorded as plain user timings |
| Script attribution of long frames (BP7006) | yes | no | no |
| Interaction contentful paint (BP7008) | yes (verified with 156) | no | no |
| Rage and dead clicks | yes | yes | yes |

The end-to-end suite runs WebAssembly collection, the dashboard bridge, server collection, the dashboard, and rage-click
detection in Chromium, Firefox, and WebKit (Playwright builds).

Unsupported entry types are skipped; the profiler never substitutes estimates for them.
