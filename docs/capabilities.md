# Capability matrix

Legend: **M** measured · **E** estimated (labelled in the UI) · **I** inferred (labelled) · **—** unavailable.
"Instrumented" means the component inherits `ProfiledComponentBase`. All columns assume .NET 10.

| Signal | Blazor Server / Interactive Server | WebAssembly / Interactive WebAssembly | Static SSR | Collector |
|---|---|---|---|---|
| Component creation (type, instance id) | M | M | M | component-activation |
| Component disposal, lifetime | M (instrumented) | M (instrumented) | — | component-instrumentation |
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
| Interop payload size | E (opt-in) | E (opt-in) | — | js-interop |
| JS → .NET calls | M as circuit activities | — | — | circuits |
| Allocation rate, heap size, GC counts, GC pause time | M (process) | M (.NET runtime in the tab) | M (process) | runtime |
| CPU time, working set, thread pool, lock contention | M (process) | — | M | runtime |
| Per-component allocations | — | — | — | — |
| Circuit open/close, connection up/down | M | — | — | circuits |
| Circuit inbound activity duration and hub method | M | — | — | circuits |
| User-perceived interaction latency (input delay / processing / presentation) | M (browser) | M (browser) | — | browser |
| Long animation frames / long tasks | M (browser) | M (browser) | — | browser |

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
| Event Timing (`event`, `interactionId`) | yes | yes (recent versions) | not at the time of writing |
| Long Animation Frames | yes | no (falls back to `longtask` where available) | no |
| Resource Timing | yes | yes | yes |

Unsupported entry types are skipped; the profiler never substitutes estimates for them.
