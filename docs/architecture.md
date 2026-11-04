# Architecture

```
 Collectors ──► Emit (correlate, sample) ──► bounded queue ──► consumer: filter ► redact ► merge ► custom processors
                                                                                   │
                                     ┌─────────────────────────────────────────────┘
                                     ▼
                         sessions (scope filter) ──► bounded store + incremental aggregates
                                     │
                     ┌───────────────┼──────────────────────┐
                     ▼               ▼                      ▼
              diagnostic engine   ProfilerApi         trace export/import
                     │               │ (HTTP or BroadcastChannel)
                     └──────────────►dashboard (plain JavaScript)
```

## Layers and projects

| Layer | Project | Key types |
|---|---|---|
| Event model | Core | `ProfilerEvent` and 20 concrete event types, `CorrelationKind`, `ValueQuality` |
| Collection | Core (abstractions), Blazor, Server | `IProfilerCollector`, `ProfilerCollectorBase`, collectors listed below |
| Correlation | Core, Server | `ProfilerContext` / `OperationFrame` (ambient `AsyncLocal`), `IProfilerCorrelationProvider`, `CircuitCorrelationProvider` |
| Processing | Core | `ProfilerHost` (queue, consumer, sampling, health), `IProfilerEventProcessor`, `IProfilerRedactionPolicy` |
| Storage | Core | `IProfilerSession`, `IProfilerEventStore` (`BoundedEventStore`), `SessionAggregates` |
| Diagnostics | Core | `IDiagnosticRule`, `DiagnosticEngine`, `IDiagnosticRecommendationProvider`, `SuppressionList` |
| Export | Core | `TraceDocument`, `IProfilerExporter` (native gzip, JSON, Chrome trace), `TraceImporter` |
| API | Core | `ProfilerApi`, `ProfilerApiAccess` (transport independent) |
| Hosting | Server, WebAssembly | `AddBlazorProfiler` extensions, `MapBlazorProfiler`, access evaluation |
| Presentation | Blazor (static assets) | `wwwroot/dashboard/*.js`, `wwwroot/blazor-profiler.js` |

The names requested in the plan map to: `IProfilerSession`, `IProfilerCollector`, `IProfilerEventProcessor`,
`IProfilerEventStore`, `IProfilerClock`, `IProfilerCorrelationProvider`, `IDiagnosticRule`,
`IDiagnosticRecommendationProvider`, `IProfilerExporter`, `IProfilerRedactionPolicy` (all implemented under those names).

## Event model

Every event has a process-unique `Id`, a monotonic `Timestamp` and `Duration` in clock ticks, an optional `CircuitId`, an
`OperationId` plus the `CorrelationKind` that established it, an `Outcome` and `ErrorType` (never exception messages), a
`SampleWeight`, and optional W3C trace/span ids. Wall-clock time is derived from the clock anchor
(`IProfilerClock.AnchorTime/AnchorTimestamp`) and stored once per session and trace, not per event. The schema version
(`ProfilerEvent.SchemaVersion`) is written into every trace header.

Values that are estimates carry a `ValueQuality` (for example `HttpRequestEvent.ResponseBytesQuality`,
`JSInteropEvent.PayloadBytesQuality`). Aggregates computed from sampled events are flagged
(`SessionSummary.ContainsSampledData`).

## Collectors

| Collector | Mechanism | Default |
|---|---|---|
| component-metrics | `MeterListener` on the two Blazor meters, scoped to the app's `IMeterFactory` | on |
| component-activities | `ActivityListener` on `Microsoft.AspNetCore.Components` (HandleEvent, Navigate) | on |
| component-activation | Decorating `IComponentActivator` | on |
| component-instrumentation | `ProfiledComponentBase` (opt-in per component) | on when components opt in |
| http | `DelegatingHandler` added to every `IHttpClientFactory` client | on |
| js-interop | Decorating `IJSRuntime` (WebAssembly) / `IProfiledJSRuntime` (server opt-in) | on |
| runtime | Periodic sampling of public GC/runtime APIs | on |
| browser | `PerformanceObserver` in a JS module started by `BlazorProfilerClient` | on when the component is rendered |
| circuits | `CircuitHandler` + `IHubFilter` | on (server) |

Collectors start when the first session starts recording and stop when none is recording, so an idle profiler costs a
field check per instrumented call. A collector that throws is marked `Failed` with the error in its status; the application
and the other collectors are unaffected.

## Pipeline

1. **Emit** (instrumented thread, never blocks, never throws): assign `Id`; run correlation providers (they must run here
   because ambient context is only visible on the emitting thread); apply head sampling; `TryWrite` to a bounded channel.
   A full queue drops the event and increments a counter.
2. **Consume** (background task; batches of up to 512 events, then yields — important on single-threaded WebAssembly):
   report dropped events as a marker; filter; redact; merge browser resource entries into matching HTTP events
   (WebAssembly only); run custom processors; append to every recording session whose scope accepts the event.
3. **Store**: each session has its own ring buffer bounded by count, approximate memory, and age, plus an operation index,
   and incremental aggregates that survive eviction.

Duplicate ingestion is prevented at the source: the HTTP handler marks its async flow so nested profiler handlers skip the
request; framework parameter metrics are ignored for instrumented component types; browser resource entries that match an
`HttpClient` request enrich it instead of creating a second event.

Ordering: events are stored in processing order; timestamps are monotonic within one runtime. The dashboard sorts by
timestamp where order matters. No global order is assumed across runtimes (server vs. browser); browser timestamps on the
server are aligned with an offset measured at connection time.

## Correlation

When a `HandleEvent` (or `Navigate`) activity starts, the activity collector creates an operation id and pushes an
`OperationFrame` onto the ambient execution context. Because the push happens synchronously inside `Activity.Start()`, the
frame is visible to the handler, its continuations, HTTP and interop calls it starts, child components initialized by the
following render, and the render batch processed right after a synchronous handler. Asynchronous methods restore the
previous context on completion, so the frame does not leak into unrelated work.

`ProfiledComponentBase` pushes component frames around lifecycle methods and handlers, which attribute HTTP and interop
calls to the originating component and lifecycle method.

On the server, `CircuitCorrelationProvider` maps the renderer's `SynchronizationContext` (captured when the circuit opens) to
the circuit id, and the circuit handler pushes a circuit frame around every inbound activity.

Correlation strength is explicit on every event: `Self`, `Explicit`, `ExecutionContext`, `Dispatcher`, `Temporal`, `None`.
Rules that rely on time overlap (allocation spikes, saturation, browser interactions) say so in the finding and use lower
confidence.

## Sessions and isolation

A session has its own store and aggregates. A session may be restricted to one circuit (`ProfilerSessionOptions.CircuitId`);
it then accepts only that circuit's events plus process-wide runtime samples. Sessions never share stores, so clearing,
pausing, or disposing one does not affect another. The number of sessions is bounded (`Retention.MaxSessions`).

## Dashboard

The dashboard is a static, dependency-free JavaScript application under `_content/BlazorProfiler/dashboard/`. It reads data
through one of three sources with the same interface:

- **HTTP** (`?source=api&api=...`): the endpoints mapped by `MapBlazorProfiler`.
- **Bridge** (`?source=bridge`): a same-origin `BroadcastChannel` answered by the `BlazorProfilerClient` component running in
  a WebAssembly app tab, which forwards requests to `ProfilerApi`.
- **File**: traces opened locally; read-only, never uploaded unless the user chooses to import into a live profiler.

Because the dashboard is not a Blazor app, viewing data never adds render batches, circuit traffic, or interop calls to the
application being profiled. Live polling fetches only new events (cursor-based) and the summary; views that the user is
reading are not re-rendered automatically (a refresh prompt appears instead), except overview, runtime, and health.

## Extensibility

- Custom collectors: implement `IProfilerCollector` (or derive from `ProfilerCollectorBase`) and register with
  `AddCollector<T>()`.
- Custom processors: `IProfilerEventProcessor` via `AddProcessor<T>()`.
- Custom rules: `IDiagnosticRule` (or derive from `DiagnosticRule`) via `AddRule<T>()`.
- Custom recommendations/documentation: register `IDiagnosticRecommendationProvider`.
- Custom redaction: register `IProfilerRedactionPolicy`.
- Custom exporters: implement `IProfilerExporter`.
- Manual instrumentation: `profiler.StartOperation("name")` and `profiler.Mark("name")`.
