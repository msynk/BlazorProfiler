# BlazorProfiler

A lightweight, Blazor-specific performance profiler that identifies, correlates, explains, and helps resolve performance
problems across the complete application execution path: user interactions, component lifecycle and rendering, render
batches, HTTP, JavaScript interop, runtime/GC, Blazor Server circuits, and browser-perceived latency.

> Status: **0.1.0-preview.1**. Targets **.NET 10**. See [implementation status](docs/implementation-status.md) for what is
> complete and what is not.

## What it does

- **Collects** Blazor's built-in .NET 10 component metrics and activities, component creation (through a decorating
  `IComponentActivator`), HTTP requests, JS interop, runtime/GC samples, circuit lifecycle and inbound activity, and browser
  performance entries (Event Timing, long animation frames, resource timing).
- **Correlates** everything that runs in the execution context of an interaction or navigation into one *operation*, and
  labels every relationship with how it was established (explicit, execution context, dispatcher, or temporal-only).
- **Instruments components on request**: inheriting `ProfiledComponentBase` (one line in `_Imports.razor`) adds per-instance
  render timing, synchronous vs. awaited lifecycle time, parameter-change detection, render-output comparison, the component
  tree, and disposal.
- **Explains** problems with 33 deterministic diagnostic rules. Every finding carries severity, confidence, evidence (each
  value labelled measured / estimated / inferred / unavailable), an impact estimate with its calculation method, a suggested
  fix, conditions under which the advice may not apply, and suppression support.
- **Shows** it all in a dependency-free dashboard (overview, components and tree, unified timeline, network waterfall,
  interop, runtime, circuits, diagnostics, sessions and comparison, profiler health). The dashboard is plain JavaScript, so it
  never adds renders or interop calls to the app being measured.
- **Exports** traces in a versioned native format (gzip JSON), plain JSON, and Chrome Trace Event format (Perfetto), and
  imports them back for re-analysis or offline viewing.

## Packages

| Package | Use it in | Contents |
|---|---|---|
| `BlazorProfiler.Core` | (dependency) | Event model, sessions, bounded storage, pipeline, correlation, rules, export/import. No ASP.NET dependency. |
| `BlazorProfiler` | (dependency) | Blazor collectors, `ProfiledComponentBase`, HTTP and JS interop decorators, `BlazorProfilerClient`, dashboard assets. |
| `BlazorProfiler.Server` | Server / Blazor Web App host project | Circuit collector, per-circuit isolation, authorization, HTTP API, `MapBlazorProfiler()`. |
| `BlazorProfiler.WebAssembly` | WebAssembly / Web App client project | Environment-aware activation, metrics/tracing registration, `IJSRuntime` profiling, dashboard bridge. |

## Quick start

### Blazor Server or Blazor Web App (host project)

```csharp
// Program.cs
builder.AddBlazorProfiler();          // enabled in Development only, unless configured otherwise
...
app.MapBlazorProfiler();              // dashboard at /_blazor-profiler (local requests in Development)
```

Add the client component once in an interactive part of the UI to collect browser timings:

```razor
<BlazorProfilerClient />
```

Open `/_blazor-profiler/` in another tab.

### Blazor WebAssembly (standalone app or Web App client project)

```csharp
// Program.cs
builder.AddBlazorProfiler();          // enabled in Development only, unless configured otherwise

// The default WebAssembly HttpClient is created manually; give it the profiler handler:
builder.Services.AddScoped(sp => new HttpClient(ProfilerHttpMessageHandler.Create(sp))
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
```

Render `<BlazorProfilerClient ShowLauncher="true" />` in an interactive component, then press **Alt+Shift+P** (or the
*Profiler* button) to open the dashboard in a new tab. It talks to the app tab through a same-origin `BroadcastChannel`.

### Per-component detail (optional)

```razor
@* _Imports.razor: applies to every component without its own @inherits *@
@inherits BlazorProfiler.Instrumentation.ProfiledComponentBase
```

See [Getting started](docs/getting-started.md) for authorization, configuration profiles, server-side JS interop, and samples.

## Documentation

- [Getting started](docs/getting-started.md)
- [Architecture](docs/architecture.md) and [repository assessment](docs/assessment.md)
- [Capability matrix](docs/capabilities.md) — what is measured, estimated, or unavailable per hosting model
- [Configuration](docs/configuration.md) and [profiling modes and overhead](docs/overhead.md)
- [Diagnostic rule reference](docs/rules.md)
- [Security and privacy](docs/security.md)
- [Trace format specification](docs/trace-format.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Implementation status](docs/implementation-status.md) and [changelog](CHANGELOG.md)

## Samples

`demo/Server` is a Blazor Web App with static SSR, Interactive Server, and Interactive WebAssembly pages.
Each demo reproduces a problem that specific rules detect (render storms, large lists, slow handlers, rapid input, N+1 and
duplicate requests, failing endpoints, interop chatter, allocation spikes, leaky components).

```bash
dotnet run --project demo/Server
```

## Building and testing

```bash
dotnet build
dotnet test tests/BlazorProfiler.Core.Tests
dotnet test tests/BlazorProfiler.Tests
dotnet test tests/BlazorProfiler.Server.Tests
dotnet test tests/BlazorProfiler.E2E.Tests   # starts the sample app; needs Playwright Chromium installed
dotnet run -c Release --project benchmarks/BlazorProfiler.Benchmarks -- --filter "*"
```

## License

MIT
