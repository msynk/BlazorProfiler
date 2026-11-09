# Changelog

## Unreleased

Improvements from a [study of profilers across ecosystems](docs/competitive-analysis.md).

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
