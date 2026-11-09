# Implementation status

Status of each part of the implementation plan for 0.1.0-preview.1. "Done" means implemented and covered by tests;
limitations are listed explicitly rather than hidden.

## Test coverage summary

| Suite | Tests | What it covers |
|---|---:|---|
| `BlazorProfiler.Core.Tests` | 160 | Clock, redaction, per-subject sampling, filtering, storage limits and eviction, cursors (with atomic reset and gap flags), sessions and isolation, queue overflow, concurrency, collector failure isolation, correlation, browser merge, aggregates, every rule (positive and negative cases, weighted counts and durations under sampling), documentation links, suppression, export/import round trips and invalid input, render-cause aggregation, Markdown report, `ProfilerAssert`/`ProfilerBudget`, CLI (`check` with SARIF/GitHub output and budgets, `compare`, `report`, usage errors) |
| `BlazorProfiler.Tests` | 59 | Collectors driven through a real `Renderer`: framework metrics (including pending batches across collection restarts), interaction correlation and non-leakage, activator wrapping, `ProfiledComponentBase` (renders, reasons, parameter changes, tree, disposal, output comparison, lifecycle timing, equivalence with `ComponentBase`), HTTP handler, JS interop decorator, browser ingestion and batch size budget, dashboard bridge (streamed requests, authorization before reading, work off the dispatcher), runtime sampling, registration, retention tracking through a real renderer (accumulating leaks vs. the renderer's previous render tree, ended scopes, forced checks, access), Entity Framework Core command capture through a `DiagnosticListener` (reader timing, correlation, hashes, errors) and SQL normalization, startup and web vitals ingestion, DevTools track subscriptions and serialization |
| `BlazorProfiler.Server.Tests` | 44 | Access levels, per-user circuit isolation, CSRF header, import limits, session control, redirect, circuit correlation, local-request detection (relayed remote clients, local hops, HTTP/1.0, browser origin), bridge authorization, closed-circuit session retention (including sessions in use), commands over HTTP (CSRF header, validation), latency simulation (never in Production), hub filter construction |
| `BlazorProfiler.E2E.Tests` | 13 | Real Kestrel + Chromium: Interactive Server and Interactive WebAssembly demos, hub method naming, circuit correlation, request-pattern rules, synchronous handler detection, dashboard UI over HTTP and over the WebAssembly bridge, export and re-import, browser interaction latency, production lockout |

## Phases

| Phase | Status | Notes and limitations |
|---|---|---|
| Repository assessment | Done | [assessment.md](assessment.md), [capabilities.md](capabilities.md). |
| A — Instrumentation foundation | Done | Sessions (start/pause/resume/stop/clear/dispose), collector lifecycle and health, profiles, sampling, filtering, redaction, retention, backpressure, versioned schema. |
| B — Component and render profiler | Done, within framework limits | Per-component render timing requires `ProfiledComponentBase`; framework metrics give per-type parameter updates only. Render-batch timing boundaries are documented per hosting model. Dashboard responsiveness was verified with the sample sizes (hundreds of instances, thousands of events); the component tree renders lazily and caps 500 children per level, but large-tree UI performance has no automated test. |
| C — Unnecessary rendering diagnostics | Done | BP1001–BP1007. Identical-output detection needs `TrackRenderOutput`. |
| D — Event handler profiler | Done | Handler duration, synchronous portion (instrumented receiver), correlated batches/HTTP/interop, rapid streams, duplicate requests. Time "awaiting external operations" is reported as overlap with HTTP requests started by the handler, not as measured wait time. |
| E — HTTP and network | Done for `IHttpClientFactory` and handler-wrapped clients | Passive observation of every `HttpClient` through `System.Net.Http` diagnostics was not implemented: the runtime redacts query values in its activity tags, which would make duplicate detection unreliable. Cache indicators are available in WebAssembly only (Resource Timing). |
| F — JavaScript interop | Done (WebAssembly); opt-in (Server) | The server cannot decorate `IJSRuntime` (the framework casts it to an internal type), so server profiling uses `IProfiledJSRuntime`. JS-to-.NET calls are not observed in WebAssembly. Streaming interop calls are recorded as ordinary calls; stream sizes are not estimated. |
| G — Runtime, memory, allocations | Done | Public runtime APIs only. Not implemented: EventPipe-based deep collection (use `dotnet-trace`/PerfView alongside). Retained components (BP5004) are detected through weak references; the retaining path is not determined. Allocation attribution is process-wide and temporal by design. |
| H — Blazor Server and circuits | Done | Lifecycle, connection state, inbound activities with hub method names, per-circuit correlation and isolation, round-trip batch timing. Not available: transport byte counts per batch, per-circuit memory (the runtime exposes process-wide memory only). Reconnection scenarios are not covered by end-to-end tests. |
| I — Unified timeline and causal analysis | Done | Lanes with overlap rows, zoom, pan, search, range selection, event details with operation-related events, causal (same-operation) highlighting, bookmarks, dropped-event markers. Comparison is session-to-session, not between two windows of one session. |
| J — Diagnostic engine | Done | 42 rules with ids, versions, severity, confidence, evidence quality, impact method, caveats, suppression, threshold and per-rule overrides, extensibility. Optional AI explanations were not added. |
| K — Dashboard | Done | All sections. Keyboard support for lists, tables, tree, timeline, and drawer; respects light/dark and reduced motion. No formal screen-reader audit has been performed. |
| L — Developer experience | Done | Four packages, one-line registration per hosting model, environment-based activation, production safeguards, XML documentation on all public APIs (enforced by the build), troubleshooting guide. One Blazor Web App sample covers static SSR, Interactive Server, and Interactive WebAssembly; there is no separate standalone WebAssembly sample. |
| Export, import, integrations | Done | Native gzip/JSON, Chrome Trace Event (Perfetto), validated import with limits, export redaction, session comparison. W3C trace ids are captured for correlation with OpenTelemetry backends; the profiler does not publish its own OpenTelemetry metrics or activities. |
| Performance and reliability | Done | Benchmarks and measured optimizations ([overhead.md](overhead.md)). Benchmarks run on demand in CI; no automated regression gate yet. |
| Security and privacy | Done | [security.md](security.md). |
| Competitive study and follow-ups | Done | [competitive-analysis.md](competitive-analysis.md). Interactions view, render causes, in-page toolbar and update highlighting, DevTools tracks, Entity Framework Core capture with BP8001–BP8005, leak detection (BP5004), startup and Core Web Vitals (BP7003–BP7005), test assertions and budgets, CLI, Markdown report, commands API, latency simulation, hot reload markers. End-to-end tests cover the toolbar, update highlighting, startup reporting, the new dashboard views, and DevTools track entries on Server and WebAssembly; how Chrome renders the tracks in its Performance panel is not tested automatically. |
| Documentation and samples | Done | Demos reproduce rules in every category; server-infrastructure rules (BP6001–BP6003, BP6005) and BP5001/BP7002 depend on network, load, and browser conditions and are not deterministically reproduced by a demo. |

## Deviations from the plan

- **Dashboard technology.** The dashboard is plain JavaScript served as static assets instead of a Blazor component, so that
  viewing profiling data never perturbs the measured app (no extra render batches, circuit traffic, or interop).
- **Server JS interop** is opt-in, as explained above.
- **Passive HTTP observation** was replaced by the handler-based collector, as explained above.

## Known gaps to address next

1. End-to-end tests for circuit reconnection, long-running sessions, and Firefox/WebKit.
2. Automated benchmark regression thresholds in CI.
3. Optional OpenTelemetry export of profiler operations.
4. An MCP endpoint for coding agents, and retaining-path hints for BP5004 (see [competitive analysis](competitive-analysis.md)).
