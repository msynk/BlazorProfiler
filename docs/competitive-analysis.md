# Competitive analysis

A survey of profilers, devtools, and diagnostics tools across ecosystems (November 2026), the gaps it revealed in
BlazorProfiler, and what was changed as a result. The goal was not feature parity with every tool. The goal was to adopt
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
