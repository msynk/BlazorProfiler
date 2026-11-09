# Migration notes

## Unreleased

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
