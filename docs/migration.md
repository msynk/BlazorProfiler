# Migration notes

## Unreleased

- **Event schema version 3.** New event type `browserFrustration`; new optional properties on `browserLongTask`
  (script attribution and forced layout) and `componentRetained` (`retainedBy`). Traces written with schema 1 and 2 still
  import; traces written with schema 3 are rejected by readers that only know schema 2.
- `ProfilerEventKind` gained `BrowserFrustration` (85). Code that switches over it exhaustively should handle it.
- New rules BP1008, BP7006, and BP7007 are on by default. Disable one with `options.Diagnostics.Rules["BP7007"] = new() { Enabled = false }`.
- The server maps an MCP endpoint at `{RoutePrefix}/mcp` by default (read-only, same access rules as the dashboard API).
  Set `ProfilerServerOptions.EnableMcp = false` to remove it.
- Frustration signals are collected by default (`Collectors.FrustrationSignals`); the browser module adds one passive
  click listener and a mutation observer.
- `ProfiledComponentBase` now also implements `IAsyncDisposable` (the renderer prefers it). Components that implement
  `IDisposable` themselves still have their `Dispose` called exactly once, synchronously, and their disposal is now
  recorded; code that checks `component is IAsyncDisposable` sees `true` for every instrumented component.
- `TraceCli.RunAsync` has an overload that takes the standard input reader (used by `blazor-profiler mcp`).

- **Event schema version 2.** New event types: `componentRetained`, `dbCommand`, `browserStartup`, `browserVital`. Traces
  written with schema 1 still import; traces written with schema 2 are rejected by 0.1.0-preview.1 readers.
- `ProfilerEventKind`, `DiagnosticCategory` (`Database`), and `InstrumentationSource` (`DatabaseDiagnostics`) gained
  values; code that switches over them exhaustively should handle the new members.
- `ProfilerHubFilter` has a single constructor with an optional `LatencySimulator` (SignalR requires one applicable
  constructor).
- `ProfilerComponentActivator` implements `IDisposable` (hands the scope's components to retention tracking).
- New options default to on: `Collectors.Database` and `Collectors.ComponentRetention` (off in the LowOverhead profile).

## 0.1.0-preview.1

Initial release. Trace format version 1, event schema version 1.

Future breaking changes to public APIs, the event schema, or the trace format will be listed here with upgrade
instructions. Additive changes (new optional event properties, new rules, new options) do not require migration.
