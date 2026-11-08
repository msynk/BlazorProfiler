# Changelog

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
