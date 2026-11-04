# Repository assessment and technical validation

This document records the state of the repository before implementation, the platform facts the design depends on, and
how each fact was verified. It is the evidence behind the [capability matrix](capabilities.md).

## Initial repository state

| Item | Finding |
|---|---|
| Contents | `LICENSE` (MIT) and a two-line `README.md`; one commit (`Initial commit`). |
| Solution, projects, conventions | None. Conventions were established for this project (see below). |
| Existing diagnostics, telemetry, JS interop abstractions, UI components | None. |
| Tests, demos, CI, release conventions | None. |

Conventions adopted: SDK-style projects targeting `net10.0`; nullable reference types; `latest-recommended` analyzers with
warnings as errors in `src/`; XML documentation required on public APIs; central package management
(`Directory.Packages.props`); `.slnx` solution; xUnit v3 tests; file-scoped namespaces.

## Environment

| Item | Value |
|---|---|
| SDKs installed | 8.0.414, 10.0.403 (pinned by `global.json`, roll-forward `latestFeature`), 11.0 RC (not used) |
| ASP.NET Core shared framework | 10.0.14 |
| Browsers for end-to-end tests | Playwright Chromium revision 1248 (matches `Microsoft.Playwright` 1.64.1) |

## Platform facts and how they were verified

Each fact below was confirmed by a prototype, by decompiling the .NET 10.0.14 framework assemblies, or by a test in this
repository. Nothing in the design relies on undocumented behavior without a fallback.

### Built-in Blazor diagnostics (.NET 10)

| Fact | Evidence |
|---|---|
| Meter `Microsoft.AspNetCore.Components` publishes `aspnetcore.components.navigate` (counter; tags `aspnetcore.components.type`, `aspnetcore.components.route`) and `aspnetcore.components.handle_event.duration` (histogram, seconds; tags type, `code.function.name`, `aspnetcore.components.attribute.name`, `error.type`). | Prototype `MeterListener`; decompiled `ComponentsMetrics`. |
| Meter `Microsoft.AspNetCore.Components.Lifecycle` publishes `update_parameters.duration` (tag: component type), `render_diff.duration`, and `render_diff.size` (no component tag). | Same. |
| **Per-component render timing is not exposed.** Batch metrics are per batch; parameter metrics are per component *type* with no instance identity. | `CaptureBatchDuration(Task, long, int diffLength)` has no component argument. |
| `render_diff.duration` spans from the start of the batch until the task returned by `UpdateDisplayAsync` completes. In WebAssembly that task completes synchronously after JS applies the DOM changes; on Blazor Server it completes when the browser acknowledges the batch, so the duration includes serialization, network, and browser-side application. | Decompiled `Renderer.ProcessRenderQueue`, `RemoteRenderer.UpdateDisplayAsync`, `WebAssemblyRenderer.UpdateDisplayAsync`. |
| `handle_event.duration` covers the handler's task. For asynchronous `ComponentBase` handlers that includes the final `StateHasChanged` render; for synchronous handlers the following batch is separate. | Decompiled `Renderer.DispatchEventAsync`, `ComponentBase.HandleEventAsync`. |
| Activity source `Microsoft.AspNetCore.Components` creates `HandleEvent` and `Navigate` activities when a listener samples them. They start as root activities. | Decompiled `ComponentsActivitySource`; prototype `ActivityListener`. |
| `AddRazorComponents` registers component metrics and tracing; **the WebAssembly host does not**. Both are public static methods: `ComponentsMetricsServiceCollectionExtensions.AddComponentsMetrics/AddComponentsTracing` (not extension methods). | Decompiled `RazorComponentsServiceCollectionExtensions` and `WebAssemblyHostBuilder`; reference assembly inspection. |
| Measurement callbacks run on the renderer's thread with `SynchronizationContext.Current` equal to the renderer's `RendererSynchronizationContext`. | Prototype output. |
| An `AsyncLocal` value set inside `ActivityListener.ActivityStarted` for `HandleEvent` is visible to the handler, its continuations, and the render batch processed right after a synchronous handler, and does not leak after the dispatcher work item completes. | Prototype; integration tests `RenderBatchAfterSynchronousHandlerIsCorrelatedToTheInteraction`, `AmbientInteractionDoesNotLeakIntoLaterWork`. |

### Interception points

| Fact | Consequence |
|---|---|
| `IComponentActivator` is public; renderers resolve it from their (scoped) service provider. The default activator uses `ActivatorUtilities.CreateFactory` (constructor injection). | Component creation is tracked by a decorating activator that reproduces the default behavior or wraps an app-registered activator. |
| `ComponentBase` semantics are fully implementable with public APIs (`RenderHandle`, `ParameterView`, `IHandleEvent`, `IHandleAfterRender`). | `ProfiledComponentBase` reproduces `ComponentBase` member for member; an equivalence test compares lifecycle order, output, and batches. |
| The renderer disposes a component through `IAsyncDisposable` if implemented, otherwise `IDisposable`. | `ProfiledComponentBase` records disposal through an explicit `IDisposable` implementation without changing the disposal behavior of derived components. |
| Blazor's change detection compares by value only types whose `TypeCode` is not `Object`, plus `Guid`, `DateOnly`, `TimeOnly`, and `EventCallback`/`EventCallback<T>`. | Parameter-change detection uses the same rules. |
| **On Blazor Server `CircuitFactory` casts the scoped `IJSRuntime` to the internal `RemoteJSRuntime`.** Decorating `IJSRuntime` in DI would break every circuit. | Server-side interop profiling is opt-in through `IProfiledJSRuntime`. WebAssembly decorates `IJSRuntime` safely. |
| `CircuitHandler.CreateInboundActivityHandler` wraps every inbound circuit message; `IHubFilter` sees the hub method name. Global hub filters apply to Blazor's component hub. | Circuit activities are timed and named. Verified end to end. |
| In .NET 10, browser events reach the server as `BeginInvokeDotNetFromJS` hub invocations (not `DispatchBrowserEvent`). | Documented in the circuit view and rules. Verified end to end. |
| `GC.GetTotalAllocatedBytes(precise: false)` under-reports by the unflushed allocation contexts (measured 981 KB for 1 MB allocated). | The runtime collector uses `precise: true`. |

### Browser

| Fact | Consequence |
|---|---|
| Event Timing (`event` entries with `interactionId`), Long Animation Frames, and Resource Timing are available through `PerformanceObserver` in Chromium. | Browser collector feature-detects each entry type and reports unsupported ones as missing. |
| `BroadcastChannel` delivers messages between same-origin browsing contexts, including other channel objects in the same page. | The WebAssembly dashboard bridge. |

## Signals that cannot be collected reliably

| Signal | Why | What the profiler does instead |
|---|---|---|
| Per-component render time for components that do not inherit `ProfiledComponentBase` | Not exposed by the framework. | Reports per-type parameter updates from metrics, labelled as a proxy; recommends instrumentation. |
| Per-component allocations | No supported attribution mechanism. | Process-wide allocation samples; rules report only temporal overlap with operations. |
| Server: diff-generation time separate from transport | Not exposed. | Round-trip time is labelled as such; small-batch medians are used as evidence of network-bound latency. |
| Browser paint time for a specific render batch | Not exposed to .NET. | Event Timing presentation delay per interaction. |
| Server-side JS interop through `IJSRuntime` | Decoration breaks circuits (see above). | Opt-in `IProfiledJSRuntime`. |
| JavaScript-to-.NET callbacks (WebAssembly) | No public hook. | Not reported (server: visible as `BeginInvokeDotNetFromJS` circuit activities). |
