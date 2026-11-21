# Competitive analysis

A survey of profilers, devtools, and diagnostics tools across ecosystems (November 2026), the gaps it revealed in
BlazorProfiler, and what was changed as a result. A [second study](#second-study-late-november-2026) at the end of this
document covers the roadmap items left by the first and what changed in the field since. The goal was not feature parity with every tool. The goal was to adopt
the ideas that proved themselves elsewhere and that fit a profiler running *inside* a Blazor app, with its constraints:
components have no DOM element of their own, Blazor Server renders on the server and ships diffs over SignalR, and
WebAssembly runs .NET on the browser's main thread.

## Method

Five areas were researched in parallel from official documentation, release notes, source code, and package registries:

1. **.NET and Blazor**: Blazor-specific devtools, Microsoft's own .NET 10/11 diagnostics, general .NET profilers and APM,
   and other .NET UI frameworks.
2. **JavaScript frameworks**: React, Vue, Angular, Svelte, Solid, Preact, and newer AI-oriented tools.
3. **Browser platform and RUM**: Chrome DevTools, Firefox Profiler, Perfetto, Lighthouse, web-vitals, and RUM/APM
   products.
4. **Server-side frameworks and server-driven UI**: Phoenix LiveView, Laravel, Symfony, Django, Rails, Spring, Go, Node.
5. **Native, mobile, and game engines**: Flutter, Jetpack Compose, SwiftUI/Instruments, Unity, Unreal, Tracy.

## Landscape

### Blazor and .NET

| Tool | What it does | Relevance |
|---|---|---|
| **Bit.BevTools** (bitfoundation, NuGet since October 2026) | Zero-code tracking through `IComponentActivator` plus reflection over renderer internals; component inspector, "why did it render", per-click cost, unified timeline, EF Core SQL capture, disposed-but-alive components, INP, rules engine, Markdown/JSON/Chrome Trace export, CLI with SARIF output, and an MCP server | The closest competitor; overlaps heavily |
| BlazorDeveloperTools | Browser extension: tree, element picker, flamegraph and ranked view, "why did this render" | Server/Auto only |
| Blazor.WhyDidYouRender | Base class with parameter and state diffing; console and OpenTelemetry output | Narrow; popular |
| Blazor.Browser.DevTools, WebVella.BlazorTrace | Inspector without performance data; weaving-based tracing | Narrow or inactive |
| Microsoft (.NET 10/11) | Components meters and activities, WebAssembly EventPipe (`collectCpuSamples`, `collectGcDump`), the `browser` WebAssembly profiler, Aspire's browser telemetry gateway | Data sources, no Blazor-specific analysis |
| MiniProfiler, dotTrace/dotMemory, Visual Studio, PerfView, Application Insights, Sentry | Request badges and SQL timings; CPU and memory profiling (server only); generic APM | None are Blazor-aware; no WebAssembly profiling |
| Avalonia Developer Tools, .NET MAUI, Uno | Metrics overlays, layout counters, MCP server (Avalonia) | Shows where .NET UI tooling is heading |

No tool analyzes the Blazor WebAssembly download and boot sequence.

### JavaScript frameworks

- **React DevTools Profiler** set the vocabulary: the *commit* as the unit of work, flamegraph and ranked chart per commit,
  "why did this render", and "highlight updates". React 19.2 writes Scheduler and Components tracks straight into the
  Chrome Performance panel (`console.timeStamp`/`performance.measure` with `detail.devtools`, color by self time), and
  React DevTools 8 removed its own timeline in favor of those tracks.
- **React Scan**: canvas overlay with fading outlines and "×N" counters, gray outlines for renders that changed nothing,
  a toolbar with slowdown notifications, and an LLM-ready "Prompts" tab.
- **Angular DevTools** profiles change-detection cycles and, like React, writes an Angular track into the Performance panel
  with links back to the component.
- **Reassure** gates CI on render counts and durations against a baseline, posting a Markdown report.
- Vue DevTools 7 *removed* its performance timeline over overhead concerns, a reminder that a profiler must stay cheap.
- AI access is the new frontier: Chrome DevTools MCP (with third-party tools a page can register), Angular CLI MCP, the
  Next.js MCP endpoint.

### Browser platform and RUM

- **Chrome DevTools**: the Insights sidebar (INP and LCP breakdowns, layout shift culprits, forced reflow), Live metrics,
  annotations, "Debug with AI", the DevTools MCP server, and the extensibility API that frameworks now use.
- **web-vitals** attribution (input delay, processing, presentation delay; LCP subparts; largest layout shift source) and
  Long Animation Frame script attribution are the standard ways to explain slow interactions and loads.
- **Lighthouse CI** shows how budgets work: warn/error levels, numeric limits, and aggregation over runs.
- RUM products (Sentry, Datadog, SpeedCurve, DebugBear) add frustration signals (rage and dead clicks) and AI triage, but
  none understand Blazor components or circuits.

### Server-side frameworks and server-driven UI

- **Debug toolbars** (Symfony, Django Debug Toolbar, Laravel Debugbar, rack-mini-profiler, MiniProfiler) all center on
  the database: query lists, duplicate and "similar" query detection, slow-query thresholds, call sites.
- **N+1 detectors that fail tests**: Bullet (`raise = true` in tests), Prosopite (same call stack and query fingerprint),
  Rails strict loading. Symfony and Blackfire assert on profile data in tests and CI.
- **Phoenix LiveView**, the closest analogue to Blazor Server, offers `enableLatencySim(ms)` because local development
  hides latency, and a debugger that keeps state after a crash.

### Native, mobile, and game engines

- **Flutter**: rebuild counts per widget, repaint highlighting, and `leak_tracker` (objects disposed but still reachable
  after several GC cycles, with culprits separated from victims).
- **Jetpack Compose**: recomposition *and skip* counts in the Layout Inspector; compiler stability reports.
- **SwiftUI (Xcode 26)**: a Cause & Effect graph explaining which state change caused each view update.
- **LeakCanary**: watch objects after destruction, check after a GC, report retained instances.
- **Unity Profile Analyzer and Tracy**: per-marker statistics and side-by-side comparison of two captures.

## Where BlazorProfiler stood

Strong already: the deterministic rules engine with severity, confidence, evidence quality, and impact; per-interaction
*operations* with explicit correlation strength; the capability matrix that labels what is measured, estimated, or
unavailable; session comparison; Chrome Trace export; production safeguards.

Missing compared with the field:

| Gap | Seen in |
|---|---|
| Per-interaction breakdown (the "commit list") with ranked components | React, Angular, Symfony toolbar, Telescope |
| Render causes shown to the user ("why did this render") | React DevTools, React Scan, BevTools, SwiftUI |
| In-page overlay: update highlighting and live metrics | React Scan, React DevTools, Flutter, Compose, MiniProfiler |
| Data in the browser's own Performance panel | React 19.2, Angular |
| Database queries, N+1 and duplicate detection | Every server-side toolbar, MiniProfiler, BevTools |
| Memory leaks: components alive after disposal | Flutter leak_tracker, LeakCanary, BevTools |
| Startup and download analysis, Core Web Vitals | Lighthouse, Chrome Insights, RUM (none Blazor-aware) |
| Test and CI gating | Bullet, Prosopite, Reassure, LHCI, Blackfire, BevTools |
| Shareable, AI-ready reports | React Scan Prompts, Chrome "copy insight", BevTools |
| Latency simulation for server-driven UI | Phoenix LiveView |

## What changed

| Improvement | Inspired by | Where |
|---|---|---|
| **Interactions view**: every operation with duration, user-perceived latency, renders, batches, HTTP, database, interop, findings; per-operation waterfall and components ranked by render time | React commit list and ranked chart, Symfony per-request profile | Dashboard → Interactions |
| **Why it rendered**: render causes (first render, parameters, event handler, StateHasChanged, async completion) and the parameters whose values changed, per component | React "why did this render", SwiftUI Cause & Effect | `ComponentSummary.RenderReasons`, `ChangedParameters`; Components view |
| **In-page toolbar**: estimated INP, last interaction, long frames, DOM updates per second | MiniProfiler badge, React Scan toolbar | `<BlazorProfilerClient ShowToolbar="true" />` |
| **Update highlighting**: outlines DOM elements as render batches patch them, colors heat up on repeated updates, renders without DOM changes do not flash | React DevTools highlight updates, React Scan, Flutter repaint rainbow | Toolbar button, `Alt+Shift+H`, `HighlightUpdates` |
| **Blazor tracks in Chrome/Edge DevTools**: renders, batches, handlers, HTTP, interop, and database commands as a "Blazor" track group, colored by duration | React 19.2 performance tracks, Angular | `DevToolsTracks="true"` |
| **Database capture** through EF Core's `DiagnosticListener` (no EF dependency), normalized SQL, hashed arguments | MiniProfiler, Django Debug Toolbar, Bullet, Prosopite | `database` collector; Database view |
| **Database rules**: N+1 per operation or per component instance (BP8001), duplicates including the **prerender double fetch** (BP8002), slow queries (BP8003), synchronous queries blocking the renderer (BP8004), circuit-lifetime `DbContext` (BP8005) | Server toolbars, adapted to Blazor | `DatabaseRules.cs` |
| **Leak detection** (BP5004): weak references to disposed components and to every component of a closed circuit or finished request, checked after full GCs; a leak check command forces collections; bounded retention by the renderer's previous render tree is separated from accumulating leaks | Flutter leak_tracker, LeakCanary | `component-retention` collector, `check-leaks` command |
| **Startup analysis**: TTFB, FCP, interactive time, `_framework` files (transferred, decoded, cached, uncompressed, largest) | Lighthouse, Chrome Insights; no Blazor tool does this | `BrowserStartupEvent`; Network → Startup; BP7003, BP7004 |
| **Core Web Vitals**: LCP, CLS (session windows), estimated INP, with Blazor-specific advice | web-vitals, Chrome Live metrics | `BrowserVitalEvent`; overview tile; BP7005 |
| **Test assertions**: `ProfilerAssert.NoFindingsAsync`, `ProfilerBudget` (render, request, query, interop, retention counts) | Bullet, Prosopite, Symfony profiler tests, Reassure | `BlazorProfiler.Testing` |
| **CLI**: `check` (fail on findings or budgets; text, Markdown, SARIF, GitHub annotations), `report`, `compare` (regressions in deterministic counts) | BevTools CLI, LHCI, Reassure | `BlazorProfiler.Cli` (`blazor-profiler`) |
| **Markdown report** and per-finding **Copy for AI** | React Scan Prompts, Chrome "copy insight", BevTools | Export format `markdown`; finding cards |
| **Latency simulation** for Blazor Server circuits | Phoenix LiveView `enableLatencySim` | `simulate-latency` command; Circuits view |
| **Commands API**: actions the dashboard can run in the app (leak check, latency) | DevTools "collect garbage" buttons | `IProfilerCommand`, `POST /api/commands/{name}` |
| **Hot reload markers** on the timeline | BevTools hot reload awareness | `component-activation` collector |

Two findings came out of verifying the new features against the real renderer:

- Blazor's renderer keeps the previous render tree of every live component, and that buffer can reference the child most
  recently removed from a position. A naive leak detector reports such children (BevTools-style "disposed but alive")
  even though the retention is bounded. BP5004 therefore reports a likely leak only when retention *accumulates* (two or
  more instances under one parent), when the parent is gone too, or when a whole circuit's components survive.
- SignalR's hub filter factory rejects filter types with more than one applicable constructor; the end-to-end tests
  caught this before it shipped.

## Deliberately not adopted (yet)

| Idea | Why not now |
|---|---|
| Zero-code per-instance render timing via reflection over renderer internals (BevTools, BlazorDeveloperTools) | Fragile across framework versions and against the project's rule of never depending on undocumented behavior without a fallback. `ProfiledComponentBase` stays opt-in; framework metrics cover the rest. |
| Mapping DOM elements to components (element picker, per-component outlines) | Needs Blazor's internal JavaScript renderer state. Update highlighting shows DOM patches instead, which is what users see. |
| MCP server for coding agents | Promising (Chrome DevTools MCP, Avalonia, BevTools). The JSON API, the Markdown report, and "Copy for AI" cover the near-term need; an MCP endpoint should follow the same access rules as the API. |
| OpenTelemetry export of profiler operations | Still on the roadmap; W3C trace ids already link to OTel backends. |
| In-page CPU sampling (WebAssembly `collectCpuSamples`, `dotnet-trace` for Server) | Requires `EnableDiagnostics` builds and heavy trace parsing; better as a separate capture button later. |
| Frustration signals (rage and dead clicks) | Useful for RUM; less so for a development-time profiler. A Blazor-precise dead click (click without handler or batch) is a candidate rule. |

## Roadmap suggestions

1. MCP endpoint (`/_blazor-profiler/mcp`) exposing sessions, operations, findings, and commands to coding agents.
2. OpenTelemetry metrics and spans for operations and findings, visible in the Aspire dashboard.
3. Retaining-path hints for BP5004: scan event invocation lists of singleton services for delegates targeting a retained
   component (the classic `OnChange += StateHasChanged` leak).
4. Long Animation Frame script table and a forced-reflow diagnostic for interop callbacks.
5. Statistics per component across sessions (histograms, p50/p95 deltas) in the comparison view.
6. A Blazor-precise dead-click rule and an `@key` churn rule (same component type disposed and recreated at the same
   position within one operation).

## Second study (late November 2026)

A second pass looked at what the first one left on the roadmap and at what changed in the field since: Chrome's agent
tooling and new performance APIs, Microsoft's .NET 10/11 diagnostics, continuous profilers, and the comparison and
statistics views of native and game profilers. Three areas were researched in parallel from official documentation,
release notes, and source code:

1. **.NET and Blazor**: Bit.BevTools, BlazorDeveloperTools, Blazor.WhyDidYouRender, WebVella.BlazorTrace, the .NET 10/11
   Components and SignalR instrumentation, WebAssembly diagnostics, MiniProfiler, dotMemory, Visual Studio, Datadog,
   Pyroscope, Aspire, and Avalonia's MCP server.
2. **JavaScript frameworks and the browser**: Long Animation Frames script attribution, web-vitals v5 attribution, soft
   navigations and interaction contentful paint (Chrome 151), the DevTools extensibility API and agent tooling, React,
   Angular, Vue, and Nuxt devtools, and RUM frustration signals (Datadog, Sentry).
3. **Everything else**: server toolbars (Symfony, Django, Laravel, Phoenix LiveDebugger and LiveDashboard), continuous
   profiling and OpenTelemetry (Pyroscope, Sentry, Datadog, Honeycomb, the OTel profiles signal), native and game
   profilers (Tracy, Unity Profile Analyzer, Unreal Insights, Instruments, Flutter, Compose, LeakCanary), and MCP servers
   of profiling and observability products (Chrome DevTools, Sentry, Datadog, Grafana, Tidewave, Laravel Boost, Next.js).

### What the field added

| Pattern | Seen in | Relevance to BlazorProfiler |
|---|---|---|
| In-app MCP endpoints for coding agents, read-only by default, "list, then explain" tools, aggregates before raw data | Tidewave (`/tidewave/mcp`), Laravel Boost, Next.js `/_next/mcp`, Chrome DevTools MCP (`performance_analyze_insight`), Datadog, Aspire | The roadmap's first item; the shape of the tools matters as much as the transport |
| Long Animation Frame script attribution (`invoker`, `invokerType`, `sourceURL`, `sourceFunctionName`, `forcedStyleAndLayoutDuration`, `pauseDuration`) | Chrome DevTools "Forced reflow" insight, web-vitals v5 `longestScript`, SpeedCurve, DebugBear | Explains *which script* blocked the main thread; in Blazor the entry point is often the framework itself |
| Frustration signals: rage clicks, dead clicks, error clicks | Datadog RUM, Sentry session replay (`slowClickIgnoreSelector`) | Blazor can do better than RUM: it knows whether a handler ran |
| Leak "held by" classification and grouped leak signatures | BevTools (event handler, lambda, timer, `DotNetObjectReference`), LeakCanary, Flutter leak_tracker, Chrome DevTools MCP retaining paths | The roadmap's BP5004 retaining-path item |
| Spans and metrics in the user's own observability stack | Blazor.WhyDidYouRender (OpenTelemetry output), Sentry `ui.long-animation-frame` spans, Pyroscope span profiles, Aspire browser telemetry | The roadmap's OpenTelemetry item |
| Render tree flame graphs with skipped work greyed out | React DevTools, Angular DevTools profiler (components that did not run are greyed), Perfetto area flame graphs | Shows what memoization (`ShouldRender`, unchanged parameters) saved |
| Distribution-aware comparison (p50/p95, histograms, box plots), per-marker regression detection | Unity Profile Analyzer, Tracy compare, Sentry function regressions, Grafana diff flame graphs | The roadmap's per-component cross-session statistics item |
| Unstable-identity diagnostics (components rebuilt instead of reused) | Compose stability reports, Flutter rebuild stats, React key warnings | The roadmap's `@key` churn item |
| Interaction contentful paint and soft navigations (Chrome 151), `navigationId` on entries | Chrome, web-vitals | Fixes INP's blind spot for async and Blazor Server work; not adopted yet (see below) |
| In-page agent tools (`devtoolstooldiscovery`) and WebMCP | Chrome DevTools for agents (June 2026), Angular | Promising but experimental; not adopted yet |

### Gaps found in BlazorProfiler

| Gap | Severity for users |
|---|---|
| No way for a coding agent to read sessions and findings (only "Copy for AI" by hand) | High: agents are now a primary consumer of profiler output |
| Long frames named only "the longest script's URL"; forced layout invisible | High: layout thrashing in interop code is a common, invisible cost |
| No frustration signals; the dead-click rule was only an idea | Medium |
| BP5004 said *that* a component leaked, never *why* | High: the fix depends entirely on the retainer |
| No OpenTelemetry output; profiler data could not be seen next to traces in Aspire | Medium |
| Components recreated instead of reused were invisible (only counted as "many instances") | Medium |
| Interactions showed a ranked list but no hierarchy; no view of skipped components | Medium |
| Comparison and the CLI compared totals only; a regression in one component could hide in the sum | Medium |
| Long animation frames, interactions, and frustration had no view of their own | Low |

### What changed

| Improvement | Inspired by | Where |
|---|---|---|
| **MCP endpoint** `/_blazor-profiler/mcp` (Streamable HTTP, JSON responses): `list_sessions`, `get_overview`, `list_findings` then `explain_finding`, `list_interactions` then `get_interaction`, `list_components`, `get_component`, `get_report`; `control_session` and `run_command` only with `McpAllowControl`. Same access rules as the API; JSON content type and local `Origin` required | Tidewave, Laravel Boost, Next.js MCP, Chrome DevTools MCP (list then analyze), Datadog (aggregates first) | `ProfilerMcpServer` (Core), `MapBlazorProfiler` (Server) |
| **`blazor-profiler mcp <trace>`**: the same tools over stdio for exported traces (WebAssembly apps, CI artifacts) | BevTools (MCP over exports) | CLI |
| **Long animation frame script attribution**: the 5 longest scripts per frame (invoker, invoker type, sanitized source, function, forced layout, pause), frame render and style/layout phases | Chrome DevTools insights, web-vitals, SpeedCurve | `BrowserLongTaskEvent.Scripts`; BP7002 evidence |
| **BP7006 Forced synchronous layout**: names the script that forced layout, or the JS interop call running in the frame when the browser attributes the time to Blazor's own entry point (verified end to end: in WebAssembly, interop called from a handler is attributed to `blazor.web.js`) | Chrome "Forced reflow" insight | `BrowserRules.cs` |
| **Frustration signals** and **BP7007**: rage clicks (3+ clicks within 1 s on one spot) and dead clicks (no DOM or URL change within 1 s), grouped by element (tag, id, classes), with the .NET work running at that moment: a slow handler without feedback, a blocked main thread, requests in flight, or *no handler at all* | Datadog, Sentry | `BrowserFrustrationEvent`; browser module; `FrustrationIgnoreSelector` |
| **Retainers for BP5004**: event subscriptions on objects the component references (injected services), static events, undisposed `DotNetObjectReference` instances, and timers, reported as "held by" paths; a component with an identified retainer is a High-confidence leak | BevTools "held by", LeakCanary, dotMemory event-handler inspection | `RetainerInspector`, `ComponentRetainedEvent.RetainedBy` |
| **BP1008 Components recreated instead of reused**: instances of one type disposed and created within one operation (unstable `@key`, objects recreated on reload, a list removed while loading) | Compose stability, React keys, Flutter rebuild stats | `RenderingRules.cs` |
| **OpenTelemetry**: `BlazorProfiler` meter (render, lifecycle, interop, interaction, long frame, LCP/INP/CLS, retained, frustration) and activity source (render, lifecycle, and interop spans parented to the captured trace, so they nest under Blazor's `HandleEvent` span) | WhyDidYouRender, Sentry LoAF spans, Pyroscope span links | `ProfilerTelemetry`, `ProfilerOptions.Telemetry` |
| **Render-tree flame graph** per interaction: width is render time including descendants, components that did not render are hatched | React and Angular profilers | Interactions → operation details |
| **Responsiveness view**: interactions with subparts, long frames with a script table (Blazor/.NET scripts marked), rage and dead clicks per element | DebugBear and SpeedCurve script tables, Chrome insights | Dashboard |
| **Per-component comparison**: render p50/p95 changes in the dashboard comparison (sub-millisecond changes marked as noise); per-component render counts (and p95 with `--include-timings`) in `blazor-profiler compare` | Unity Profile Analyzer, Sentry function regressions | `compareTable`, `TraceCli.CompareComponents` |
| Demos for each new rule: *Rows recreated*, *Layout thrashing*, *Rage and dead clicks* | | `demo/Client/Demos` |
| **Interaction contentful paint** and **BP7008** (results that appear long after the first paint, invisible to INP). Verified with Chromium 156 before building on it: entries carry `interactionId`, `startTime`, `duration`, `paintTime`, `presentationTime`; they cover content painted after awaited JavaScript interop in WebAssembly, but not after `HttpClient` responses and only sometimes after `Task.Delay` (the runtime resumes those from its own shared timer and fetch callbacks), and are not emitted for Blazor Server | Chrome 151 soft navigations work, web-vitals | `BrowserInteractionEvent.ContentPaintMs`; Responsiveness view |
| **Latency heatmap** of operations with "what differs in the selection": attributes over-represented among the selected (slow) operations, ranked by the difference in share | Honeycomb BubbleUp, Datadog latency distributions | Interactions view |
| **Per-render allocations** for instrumented components and BP5005, measured on the rendering thread (verified in WebAssembly too) | BevTools per-render allocations, Unity GC Alloc column | `ComponentRenderEvent.AllocatedBytes`; Components view |
| **BP3007**: HTTP requests repeated after prerendering | BevTools render-mode diagnostics | `NetworkRules.cs` |
| **Exception storms and timer leaks** (BP5006, BP5007) from first-chance exception counts by type and `Timer.ActiveCount` | .NET `System.Runtime` metrics (`dotnet.exceptions` by `error.type`, `dotnet.timer.count`), Datadog exception profiles | Runtime collector |
| **Environment metadata** (application version, Debug/Release build, OS, processors; browser cores, memory, CPU tier, connection) with comparison warnings | Datadog deployment tracking, Chrome DevTools "consider your local conditions" | Trace metadata, startup event, `compare` |
| **Circuit message sizes** measured from Blazor's hub protocol and **BP6006** (large render batches, browser messages near the receive limit) | Chrome DevTools WebSocket frame sizes, SignalR transport logging | Hub protocol wrapper; Circuits view |
| **JavaScript-to-.NET calls** recorded in the browser and **BP4007** (frequent calls from scroll, pointer, and input listeners; lower threshold on Blazor Server), BP4003 for messages near the hub's receive limit | Chrome DevTools event listener breakdown, SignalR message size diagnostics | Browser module; Interop view |
| **DOM size** and BP7009 (Lighthouse thresholds, with the component types created in large numbers) | Chrome DevTools "Optimize DOM size" insight, Lighthouse | Browser module; overview tile |
| **Window comparison** within one session: select a range on the timeline, use it as baseline, compare another range (same table as session comparison) | Grafana Pyroscope baseline/comparison picker, Unity Profile Analyzer | Timeline selection panel |
| **Deep capture** in WebAssembly: CPU samples, GC dump, and runtime counters through the runtime's diagnostics API (`collectCpuSamples`, `collectGcDump`, `collectMetrics`), offered only when the build enables diagnostics, with a marker on the timeline | .NET WebAssembly EventPipe diagnostics, Tracy/Superluminal capture buttons | Toolbar |
| **Leaks across sessions**: retained components grouped by a signature of component type and retainers, counted across live sessions and imported traces | LeakCanary leak signatures, Sentry issue grouping | `LeakGrouping`; Sessions view, `/api/leaks`, `list_leaks`, `blazor-profiler leaks` |
| **AI explanations** (opt-in): an **Explain with AI** button on findings that sends the finding, its evidence, and the component's statistics to an `IChatClient`, with the answer shown as text | Datadog Bits AI, Sentry Seer, Chrome DevTools "Ask AI", Laravel Debugbar prompts | `IFindingExplainer`, `ProfilerServerOptions.AiExplanations` |
| **In-page agent tools** (WebMCP): the read-only MCP tools registered with `document.modelContext` as debugging tools, so agents in the browser can ask the profiler what it measured | Chrome WebMCP and DevTools for agents, Angular | `BlazorProfilerClient.AgentTools` |
| **Streamed interop sizes**: bytes passed through `DotNetStreamReference` and `IJSStreamReference` in both directions | Chrome DevTools network sizes | `JSInteropEvent.StreamBytes`; Interop view |
| End-to-end coverage in **Firefox and WebKit** (WebAssembly collection and bridge, server collection, dashboard, rage clicks) and of **circuit reconnection** (offline, reconnect, collection resumes on the same circuit) | | E2E tests |

Two findings came out of testing against real browsers:

- In Blazor WebAssembly, JavaScript called through interop from an event handler runs inside Blazor's own event listener,
  so the Long Animation Frames API attributes its forced layout to `blazor.web.js` (`onGlobalEvent`). A rule that trusted
  script attribution alone would blame the framework; BP7006 therefore falls back to the interop call that ran during the
  frame and says that the attribution is temporal.
- A dead-click check that looked for "any DOM change since the click" missed real dead clicks whenever an unrelated update
  (an earlier handler completing) happened later; the check is now bounded to one second after each click.

### Deliberately not adopted (yet)

| Idea | Why not now |
|---|---|
| Soft navigations (Chrome 151) | Verified with Chromium 156: Blazor's router and enhanced navigation produce **no** `soft-navigation` entries (Server or WebAssembly), so per-route vitals cannot rely on them; the profiler's own navigation events already split sessions by route. |
| Zero-code per-instance render timing through renderer internals (BlazorDeveloperTools) | Unchanged: depends on undocumented internals. |
| Field-level state tracking (`[TrackState]`) and state-container history (BevTools `IBevStateProvider`) | Needs app cooperation through a new API; render causes and changed parameters cover the common case. |
| OTLP profiles export | The OpenTelemetry profiles signal is still alpha. |
| Parsing `.nettrace` captures inside the dashboard | The captures open in PerfView, Visual Studio, and `dotnet-trace report`; a trace parser in the dashboard would be a large dependency. |

### Roadmap suggestions (updated)

1. ~~In-page agent tools through Chrome's DevTools extensibility once it leaves experimental status.~~ Done through WebMCP
   (origin trial since Chrome 149): `BlazorProfilerClient.AgentTools` registers the read-only MCP tools with
   `document.modelContext`, verified against Chromium's implementation.
2. ~~Grouping leak reports across sessions by retainer signature (LeakCanary).~~ Done: leak signatures, the leaks report
   (Sessions view, `/api/leaks`, `list_leaks`), and `blazor-profiler leaks`.
