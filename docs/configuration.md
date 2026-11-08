# Configuration

All options live on `ProfilerOptions` and are set through the `configure` delegate of `AddBlazorProfiler`.

```csharp
builder.AddBlazorProfiler(options =>
{
    options.UseProfile(ProfilerProfile.Detailed);   // apply a preset first, then override
    options.Enabled = true;                          // default: Development only
    options.Retention.MaxEvents = 50_000;
    options.Sampling.Rates[ProfilerEventKind.ParametersSet] = 0.5;
    options.Filters.ExcludedUrlFragments.Add("/health");
    options.Redaction.AllowedQueryParameters.Add("page");
    options.Diagnostics.SlowEventHandlerMs = 150;
    options.Diagnostics.Rules["BP3003"] = new RuleSettings { Enabled = false };
});
```

## Profiles

| Setting | LowOverhead | Development (default) | Detailed |
|---|---|---|---|
| Max events / memory / age per session | 20,000 / 16 MB / 10 min | 100,000 / 64 MB / 15 min | 250,000 / 128 MB / 30 min |
| Sampling | ParametersSet, ComponentRender, LifecycleMethod 25%; RenderRequest 10%; JSInterop 50% | none | none |
| Runtime sample interval | 5 s | 1 s | 500 ms |
| Parameter-change detection | off | on | on |
| Render-output comparison | off | off | on |
| Component tree | off | on | on |
| Render-request tracking | off | on | on |
| Minimum recorded lifecycle duration | 1 ms | 0.1 ms | 0 (all) |
| Interop payload estimation | off | off | on |

Event handlers, navigations, custom operations, render batches, HTTP requests, component creation/disposal, circuit
events, markers, and failed events are never sampled out.

## Reference

### Top level

| Option | Default | Description |
|---|---|---|
| `Enabled` | `null` | `null` = hosting integrations enable the profiler in Development only. |
| `AutoStartSession` | `true` | Start a process-wide "Default session" at startup. |
| `CaptureTraceContext` | `true` | Record W3C trace/span ids of the ambient `Activity` on HTTP, handler, navigation, and operation events. |

### `Pipeline`

| Option | Default | Description |
|---|---|---|
| `QueueCapacity` | 16,384 | Bounded queue between collectors and processing. When full, events are dropped and counted. |
| `MaxBatchSize` | 512 | Events processed before the consumer yields. |
| `ConsumerInterval` | 25 ms | Wait after draining; reduces the cost of `Emit` (writers rarely wake the consumer) at the price of this much latency. |
| `SynchronousProcessing` | `false` | Process on the emitting thread (tests only). |

### `Retention` (per session)

| Option | Default (Development) | Description |
|---|---|---|
| `MaxEvents` | 100,000 | Raw events retained; oldest evicted first. |
| `MaxMemoryBytes` | 64 MB | Approximate memory of raw events. |
| `MaxAge` | 15 min | Maximum event age. |
| `MaxSessions` | 16 | Concurrent sessions per profiler. |
| `MaxAggregateKeys` | 2,000 | Distinct component types / handlers / endpoints / identifiers; overflow goes to `(other)`. |
| `MaxTrackedInstances` | 20,000 | Component instances tracked in aggregates (disposed instances are evicted first). |
| `TimeSeriesBuckets` | 900 | One-second buckets for charts. |

Aggregates (counts, totals, percentile estimates) are maintained for every accepted event and are not affected by raw
event eviction.

### `Sampling`

`Rates[kind] = 0..1`. Kept events carry `SampleWeight = 1/rate` so totals stay unbiased; the dashboard flags sampled data.
Sampling is deterministic and counted per subject (component type, plus the method for lifecycle events, or interop
identifier), so each subject keeps one event in `1/rate` however its events interleave with others; per-subject counts,
rates, and durations stay within one sampling interval of the real values.

### `Filters`

| Option | Default |
|---|---|
| `ExcludedComponentTypePrefixes` | `BlazorProfiler.Browser.` (the profiler's own component) |
| `ExcludedUrlFragments` | `/_blazor-profiler` |
| `ExcludedResourceFragments` | `/_framework/`, `/_content/`, `/_blazor`, `/_blazor-profiler` |
| `ExcludedJSIdentifierPrefixes` | `BlazorProfiler.`, `./_content/BlazorProfiler/` |
| `Predicate` | none; return `false` to drop an event |

### `Redaction`

| Option | Default | Description |
|---|---|---|
| `RedactQueryValues` | `true` | Replace query values with `[redacted]`; keys are kept. |
| `AllowedQueryParameters` | empty | Query keys whose values may be kept (never applied to keys that look sensitive, such as `token`, `key`, `code`, `session`). |
| `TemplatePathIdentifiers` | `true` | Replace numeric, GUID, long hex/token, and e-mail path segments with `{id}`, `{guid}`, `{hex}`, `{token}`, `{email}`. |
| `RedactedJSIdentifierPrefixes` | empty | Interop identifiers to hide. |

Replace the whole policy by registering your own `IProfilerRedactionPolicy` before `AddBlazorProfiler`.

### `Collectors`

| Option | Default | Description |
|---|---|---|
| `ComponentMetrics` | on | Framework component metrics. |
| `ComponentActivities` | on | Interaction/navigation context. |
| `ComponentActivator` | on | Component creation tracking. Register a custom `IComponentActivator` *before* `AddBlazorProfiler` so the profiler wraps it. |
| `Http` | on | `ProfilerHttpMessageHandler` on all `IHttpClientFactory` clients. |
| `JSInterop` | on | `IJSRuntime` (WebAssembly) / `IProfiledJSRuntime` (server). |
| `JSObjectReferences` | off | Wrap returned `IJSObjectReference`s (modules). A wrapped reference cannot be passed inside a nested argument object. |
| `JSInteropPayloadEstimation` | off | Serialize arguments a second time to estimate size. |
| `Runtime`, `RuntimeSampleInterval` | on, 1 s | Runtime sampling. |
| `Circuits` | on | Circuit handler and hub filter (server). |
| `Browser`, `BrowserInteractionThresholdMs` | on, 40 ms | Browser entries (needs `BlazorProfilerClient`). |

### `Instrumentation` (for `ProfiledComponentBase`)

| Option | Default | Description |
|---|---|---|
| `TrackLifecycle` | on | Lifecycle method events (overridden methods only). |
| `MinimumLifecycleDuration` | 0.1 ms | Faster synchronous calls are not recorded individually. |
| `TrackRenderRequests` | on | Coalesced and `ShouldRender`-suppressed requests. |
| `TrackParameterChanges` | on | Compare incoming parameters with the previous set (values are never stored in events). |
| `TrackRenderOutput` | off | Hash render output to detect identical renders (cost proportional to tree size). |
| `TrackComponentTree` | on | Discover children after each render. |

### `Diagnostics`

Thresholds for every rule (see [rules](rules.md)) and per-rule settings: `Rules["BP1001"] = new RuleSettings { Enabled = false, Severity = DiagnosticSeverity.Low }`.
`MaxFindingsPerRule` limits output (default 10).

## Server options (`ProfilerServerOptions`)

| Option | Default | Description |
|---|---|---|
| `RoutePrefix` | `/_blazor-profiler` | Dashboard redirect and API prefix. |
| `AuthorizationPolicy` | `null` | Policy granting full access. Required outside Development. |
| `OwnCircuitPolicy` | `null` | Policy granting access to the caller's own circuits only. |
| `AllowLoopbackInDevelopment` | `true` | Full access for local requests in Development when no policy is configured. Requests that relay remote clients (forwarding headers naming another client, non-local `Host`, HTTP/1.0, a remote browser `Origin`/`Referer`) never qualify; local hops such as IIS Express do. See [security](security.md) for the limits of this check. |
| `AutoCircuitSessions` | `false` | Start a session per circuit (subject to `MaxSessions`). |
| `RetainedCircuitSessions` | 4 | Automatic circuit sessions kept (stopped) after their circuit closes, visible to the circuit's owner. Older ones are removed, and a retained one is removed when a new circuit needs room under `MaxSessions`, except sessions a dashboard showed in the last 5 minutes (`RetainedSessionViewWindow`), which are never removed automatically. `0` removes them on close. |
| `MaxImportBytes` | 64 MB | Upload limit for trace import. |

## `BlazorProfilerClient` parameters

| Parameter | Default | Description |
|---|---|---|
| `EnableDashboardBridge` | WebAssembly: on; server: off | Answer dashboard requests through `BroadcastChannel`. On the server, requests are authorized for the circuit's user with the configured policies, run off the circuit's dispatcher, and are streamed when larger than one circuit message (trace imports). |
| `Shortcut` | `Alt+Shift+P` | Opens the dashboard; empty disables. |
| `ShowLauncher` | `false` | Floating button that opens the dashboard. |
| `DashboardUrl` | hosting default | Override the dashboard URL. |
