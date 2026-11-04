# Profiling modes and overhead

The profiler measures itself: the **Profiler health** page shows the sampled cost of `Emit` on instrumented threads, the
background processing cost per event, queue length, dropped events, and sampled-out events. The numbers below come from the
benchmark suite in `benchmarks/BlazorProfiler.Benchmarks`.

## Method

- BenchmarkDotNet 0.15.8, `ShortRun` job (3 iterations; error bars are wide, treat results as indicative), Release build.
- Intel Core i9-14900K, Windows 11 24H2, .NET 10.0.14, x64 RyuJIT.
- Rendering benchmarks use a renderer built on the public `Renderer` base class whose display update completes synchronously
  (like WebAssembly, without the browser). They re-render a list of 200 child components whose parameters change on every
  render, so every child re-renders. The "recording" variants run the Development profile (framework metrics, activities,
  component activation, instrumentation with parameter-change detection and the component tree; render-output hashing off).
- Reproduce: `dotnet run -c Release --project benchmarks/BlazorProfiler.Benchmarks -- --job short --filter "*"`.

## Results

### Emitting events

| Scenario | Time per event | Allocated |
|---|---:|---:|
| Profiler idle (no session recording) | 9.8 ns | 0 B by the profiler* |
| Recording, background pipeline (default) | 152 ns | 36 B by the profiler* |
| Recording, full pipeline inline (filter, redaction, store, aggregates) | 112 ns | 40 B by the profiler* |

\* The benchmark allocates the 144-byte event itself; collectors do not allocate events when no session is recording.

### Rendering 200 components

| Scenario | Time per render | vs. baseline | Allocated |
|---|---:|---:|---:|
| `ComponentBase`, no profiler (baseline) | 54 µs | 1.00× | 17.9 KB |
| `ComponentBase`, profiler recording | 147 µs | 2.72× | 48.5 KB |
| `ProfiledComponentBase`, profiler registered but idle | 55 µs | 1.02× | 17.9 KB |
| `ProfiledComponentBase`, profiler recording | 154 µs | 2.83× | 122 KB |

The recording overhead is about **0.5 µs per re-rendered component**, including the framework's own cost of recording its
metrics once a listener is attached. `ProfiledComponentBase` costs nothing measurable when the profiler is idle.

This is a worst case: the components do no work, so the overhead is compared with the cheapest possible render. Real
components spend more time in their own logic, so the relative overhead is lower.

### Storage, analysis, export

| Operation | Time | Allocated |
|---|---:|---:|
| Append to a full store (with eviction) | 19 ns | 0 B |
| URL sanitization | 334 ns | 1.5 KB |
| Session summary (50,000 events) | 157 µs | 197 KB |
| All 32 rules on 50,000 events | 12 ms | 13.5 MB |
| Export compressed trace (50,000 events) | 59 ms | 3 MB |
| Import compressed trace (50,000 events) | 64 ms | 13.7 MB |

Analysis and export run on demand (dashboard refresh, export button), not on instrumented threads.

## Optimization history

The first benchmark run showed `ProfiledComponentBase` recording at **765 µs (14.4×)** with 573 KB allocated, and
background-pipeline emits at 215 ns. Changes made in response:

| Change | Effect |
|---|---|
| Lifecycle methods a component does not override are called directly (no timing, no ambient frame, no event). | Removes most per-render events and allocations. |
| Lifecycle calls faster than `MinimumLifecycleDuration` (0.1 ms) are not recorded individually. | Fewer events for trivial overrides. |
| Queued render requests are no longer recorded (each one produces a render event anyway). | One event less per render. |
| Parameter comparison double-buffers its arrays. | No allocation per parameter update. |
| No ambient frame around `SetParametersAsync` (lifecycle frames cover user code). | Two fewer execution-context changes per update. |
| The pipeline consumer drains in 25 ms intervals instead of waking on every event. | Emit 215 → 152 ns. |

Result: **765 → 154 µs** for 200 components, 573 → 122 KB allocated.

## Choosing a mode

| Mode | Use for | Notes |
|---|---|---|
| Disabled (default outside Development) | Production by default | No interception points registered. |
| `LowOverhead` | Staging, controlled production diagnostics | Samples high-volume per-component events (weights keep totals unbiased), no tree/output/parameter tracking, 5 s runtime sampling, smaller retention. |
| `Development` (default) | Day-to-day work | Everything except render-output hashing and payload estimation. |
| `Detailed` | Short investigations | Adds render-output hashing (cost proportional to render-tree size) and interop payload estimation (serializes arguments a second time). |

`ProfiledComponentBase` is opt-in per component; inherit it only where you need per-instance data if overhead matters.

## Uncertainty

- Framework metrics measure until a task completes; for asynchronous work this is wall time, not CPU time.
- Server render-batch durations include network round trips.
- Allocation data is process-wide (per browser tab in WebAssembly) and sampled at the runtime interval.
- Sampled aggregates are estimates (flagged in the dashboard).
- Browser timestamps on Blazor Server are aligned with a clock offset measured once per connection; error is bounded by the
  one-way network latency at that moment.
