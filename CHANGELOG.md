# Changelog

## Unreleased

Improvements from a [second study of profilers across ecosystems](docs/competitive-analysis.md#second-study-late-november-2026).

- **MCP** for coding agents: `/_blazor-profiler/mcp` (Streamable HTTP; read-only by default, `McpAllowControl` adds
  session control and commands; same access rules as the API) and `blazor-profiler mcp <trace>` (stdio). Tools:
  `list_sessions`, `get_overview`, `list_findings`, `explain_finding`, `list_interactions`, `get_interaction`,
  `list_components`, `get_component`, `get_report`, `control_session`, `run_command`.
- **Long Animation Frame script attribution** (invoker, invoker type, sanitized source, function, forced layout, pause;
  frame render and style/layout phases) and rule **BP7006** (forced synchronous layout, attributed to the script or to the
  JS interop call running in the frame). BP7002 evidence lists attributed scripts and the share spent in Blazor/.NET.
- **Frustration signals** (rage clicks, dead clicks; `Collectors.FrustrationSignals`, `FrustrationIgnoreSelector`) and rule
  **BP7007** with the correlated .NET work.
- **BP5004 retainers**: event subscriptions on referenced services, static events, undisposed `DotNetObjectReference`
  instances, and timers (`ComponentRetainedEvent.RetainedBy`).
- Rule **BP1008**: components recreated instead of reused (unstable `@key`, objects recreated on reload, markup replaced
  while loading).
- **OpenTelemetry** (opt-in, `ProfilerOptions.Telemetry`): `BlazorProfiler` meter and activity source.
- Dashboard: **Responsiveness** view; render-tree **flame graph** per interaction; per-component render p50/p95 in the
  session comparison; script attribution and retainers in event details.
- CLI: `compare` reports per-component regressions; new `mcp` command.
- Benchmarks: allocation regression gate in CI (`benchmarks/check-allocations.ps1`, `benchmarks/allocation-baseline.json`).
- Demos: *Rows recreated*, *Layout thrashing*, *Rage and dead clicks*.
- **Interaction contentful paint** (`BrowserInteractionEvent.ContentPaintMs`, Chromium) and rule **BP7008**: interaction
  results that paint long after the first paint.
- **Per-render allocations** (`ComponentRenderEvent.AllocatedBytes`, `Instrumentation.TrackRenderAllocations`) and rule
  **BP5005** (allocation-heavy rendering); BP5002 lists the components whose renders allocated during a spike; the
  Components view, MCP, and `blazor-profiler compare` show allocations per render.
- Runtime samples count **first-chance exceptions by type** and **active timers**; rules **BP5006** (frequent exceptions)
  and **BP5007** (active timers keep growing, per navigation or per instance of the owning component type, which also
  covers apps with enhanced navigation).
- Rule **BP3007**: HTTP requests repeated after prerendering (BP8002 already covered database queries).
- **Environment metadata**: application name, version, build configuration, OS, and processor count in traces;
  browser cores, memory, CPU tier, and connection type with startup. `compare` and the dashboard comparison warn when
  sessions ran under different conditions.
- Interactions: **latency heatmap** with "explain the selection" (attributes over-represented among slow operations).
- Timeline: **compare two windows** of one session (select a range, use it as baseline, compare another).
- Toolbar: **deep capture** (CPU samples, GC dump, runtime counters as `.nettrace`) in WebAssembly builds with
  `EnableDiagnostics`, recorded as timeline markers.
- **Disposal of components that implement `IDisposable` themselves is now recorded** (`ProfiledComponentBase` implements
  `IAsyncDisposable` and calls the component's own `Dispose` from it), so BP5004 and BP1008 cover the components most
  likely to leak; previously their disposal was invisible. Components with their own `IAsyncDisposable`, and components
  that are not instrumented at all, are recorded (and watched by the leak check) by their instrumented parent when they
  leave its render tree or the parent is disposed.
- **AI explanations** (opt-in): with `ProfilerServerOptions.AiExplanations` and an `IChatClient` (Microsoft.Extensions.AI;
  the keyed service `"BlazorProfiler"` or the app's), findings get **Explain with AI** in the dashboard. The profiler
  builds the prompt from the finding, its evidence, and the component's statistics; the answer is shown as text with the
  model's name and kept while the dashboard is open. An explainer's own timeout is reported as a failure (502). `IFindingExplainer` lets WebAssembly apps (or anything else) provide explanations, for example through
  their own backend. API: `POST /api/sessions/{id}/findings/explain?fingerprint=...` and the bridge operation `explain`;
  control access required. The demo has a stand-in assistant (`Demo:EchoAssistant`).
- **Leaks across sessions**: retained components (BP5004) of every visible session are grouped by **leak signature**
  (a stable hash of the component type and its likely retainers), so a leak seen in many circuits, sessions, or runs is
  listed once with the number of sessions, circuits, and instances it affected. Available in the Sessions view, at
  `GET /api/leaks` (and the bridge operation `leaks`), as the MCP tool `list_leaks`, and as `blazor-profiler leaks
  <traces...> [--fail-on-leaks] [--format markdown|json]`. BP5004 lists the signature as evidence.
- **Streaming interop sizes**: calls record the bytes passed through stream references (`streamBytes`):
  `DotNetStreamReference` arguments over seekable streams (taken before JavaScript reads them), `IJSStreamReference`
  results, and `DotNet.createJSStreamReference` arguments of calls from JavaScript to .NET. The Interop view shows them
  next to the payload. BP6006 no longer reports the framework's stream chunks (`ReceiveJSDataChunk`, sized just below the
  hub's limit). The *Interop chatter* demo streams 200 KB in each direction.
- **Dashboard at scale**: an end-to-end test measures the dashboard with about 19,000 component instances and 58,000
  events. It found that the first load of a large session reset the view when it completed (collapsing an expanded tree
  and clearing filters): a view shown early from the summary is now kept, and the refresh hint appears only when the
  summary changed meanwhile.
- **Circuit message sizes** (Blazor Server): the profiler wraps Blazor's SignalR hub protocol and measures each render
  batch sent to the browser (`renderBatch.transportBytes`), each message received from it
  (`circuitActivity.receivedBytes`), and the arguments of .NET-to-JavaScript calls through `IProfiledJSRuntime` (measured
  instead of estimated). New rule **BP6006** (render batches of 64 KB or more, and messages from the browser near the hub's
  32 KB receive limit, such as long text bound with `@bind`); BP6004 reports the peak bytes per second. The Circuits view
  shows bytes sent and received per circuit and per hub method. `ProfilerServerOptions.MeasureCircuitMessages` turns it
  off.
- **Calls from JavaScript to .NET** (`DotNetObjectReference.invokeMethodAsync`, `DotNet.invokeMethodAsync`, and their
  synchronous forms) are recorded by the browser module in both hosting models, timed until .NET returns; previously they
  were invisible in WebAssembly. Blazor's own plumbing (event dispatch, root components) is excluded. New rule **BP4007**
  (frequent JavaScript-to-.NET calls; 20 per second on Blazor Server, 60 in WebAssembly); BP4003 reports JavaScript-to-.NET
  payloads on the server from 24 KB (the hub's default 32 KB message limit closes the circuit); BP4004 covers slow calls
  in this direction. BP4001 and BP4002 now count .NET-to-JavaScript calls only.
- **DOM size** (sampled number of elements, reported as the `DOM` browser value) and rule **BP7009** (excessive DOM size,
  Lighthouse thresholds).
- **Standalone WebAssembly sample** (`demo/Standalone`) with end-to-end tests: interactive-router navigations, BP5007,
  the leak check with retainers, N+1 requests, and the dashboard bridge.
- End-to-end tests in Firefox and WebKit, and for Blazor Server circuit reconnection; CI installs all three browsers.
- A two-hour simulated session test: storage, aggregates, analysis, the API, MCP, and export stay bounded and consistent
  after eviction.
- Event schema version 3 (see [migration notes](docs/migration.md)).

- **Demos for server infrastructure rules**: *Thread-pool saturation* (BP6005), *Allocate large buffers* (BP5001),
  *Block the main thread* (BP7002), and *Slow handlers* with the *Simulate latency* command (BP6001, BP6003), each verified
  by an end-to-end test; BP6002 is verified by dropping a circuit's connection three times.
- **Accessibility**: the dashboard and the in-page toolbar are audited with axe-core against WCAG 2.1 A and AA in light and
  dark color schemes. Fixed: text contrast on filled buttons, counts, and severity badges (new `--on-accent` and `--on-sev`
  colors), grid cells in virtualized lists (`role="gridcell"`), and the trace drop zone's accessible name (now its visible
  text).
- **In-page agent tools (WebMCP)**: with `<BlazorProfilerClient AgentTools="true" />`, the profiler's read-only MCP tools
  are registered with `document.modelContext` (as `blazor_profiler_*` debugging tools) where the browser supports WebMCP, so
  agents in the browser can ask the profiler what it measured. They go through the dashboard bridge (new operation `mcp`)
  or, on Blazor Server, the MCP endpoint. Verified end to end against Chromium's WebMCP implementation.
- An end-to-end test records a Chrome performance trace over the DevTools protocol and checks that it contains the Blazor
  track entries.

Fixed:

- Leaks across sessions: a session whose leaked instances were all counted already from an overlapping session (a
  circuit's session inside the process-wide one) no longer counts, so overlapping sessions do not make a leak look
  widespread.
- On Blazor Server, the measured size of a .NET-to-JavaScript call now includes its `byte[]` arguments, which the
  framework sends in messages of their own before the call (`JS.ReceiveByteArray`); before, a large byte array was
  reported as a few bytes and BP4003 missed it.
- A stream passed in a `DotNetStreamReference` whose size cannot be read (any exception, for example from a custom
  stream) no longer fails the app's interop call; its size is reported as unknown.
- `blazor-profiler leaks` counts traces with the same file name in different folders (CI artifacts) as different
  sessions, named by path.

### Earlier unreleased changes

Improvements from the [first study of profilers across ecosystems](docs/competitive-analysis.md).

- Dashboard: **Interactions** view (operations with duration, browser latency, renders, batches, HTTP, database, interop,
  findings; per-operation waterfall and components ranked by render time); **Database** view; **Startup** panel; render
  causes ("why it rendered") and changed parameters per component; retained components; web vitals tile; leak check and
  latency simulation controls; Markdown export; **Copy for AI** on findings; comparison of database and retention metrics.
- In-page **toolbar** (`ShowToolbar`): estimated INP, last interaction, long frames, DOM updates per second. **Update
  highlighting** (`Alt+Shift+H`, `HighlightUpdates`).
- **DevTools tracks** (`DevToolsTracks`): Blazor activity in the Chrome/Edge Performance panel through the extensibility API.
- **Database collector** for Entity Framework Core (normalized SQL, hashed arguments) and rules BP8001 (N+1 queries),
  BP8002 (duplicate queries, prerender double fetch), BP8003 (slow query), BP8004 (synchronous query on the renderer),
  BP8005 (circuit-lifetime DbContext).
- **Leak detection**: `component-retention` collector, `check-leaks` command, rule BP5004 (closes a known gap).
- **Startup and Core Web Vitals**: `BrowserStartupEvent`, `BrowserVitalEvent`, rules BP7003 (large framework download),
  BP7004 (slow interactivity after first paint), BP7005 (Core Web Vitals).
- **Tests and CI**: `ProfilerAssert`, `ProfilerBudget`; new `BlazorProfiler.Cli` tool (`check` with text, Markdown, SARIF,
  and GitHub output; `report`; `compare`).
- **Commands API** (`IProfilerCommand`, `POST /api/commands/{name}`); **latency simulation** for Blazor Server
  (`simulate-latency`, `ProfilerServerOptions.SimulatedLatency`).
- Markdown report exporter; hot reload markers on the timeline.
- Event schema version 2 (see [migration notes](docs/migration.md)).

## 0.1.0-preview.1

First preview.

- Core: event model (20 event types, schema version 1), sessions with isolation, bounded storage, incremental aggregates,
  processing pipeline with sampling, filtering, redaction, and backpressure, correlation through ambient operation context,
  profiler health and self-overhead reporting.
- Collectors: framework component metrics and activities (.NET 10), component activation, opt-in `ProfiledComponentBase`
  instrumentation, HTTP (`ProfilerHttpMessageHandler`), JavaScript interop, runtime/GC, Blazor Server circuits, browser
  performance entries.
- Diagnostics: 33 rules across rendering, event handling, network, interop, memory, server, and browser categories, with
  suppression and configurable thresholds.
- Dashboard: overview, components and tree, unified timeline, network, interop, runtime, circuits, diagnostics, sessions and
  comparison, profiler health; HTTP, WebAssembly bridge, and offline file sources.
- Export/import: native trace format v1 (gzip JSON), JSON, Chrome Trace Event format; validated import.
- Hosting: `BlazorProfiler.Server` (authorization, per-circuit isolation, HTTP API) and `BlazorProfiler.WebAssembly`.
