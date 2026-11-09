# Getting started

## Requirements

- .NET 10 (the framework metrics and activities the profiler reads were introduced in .NET 10).
- Blazor Server, Blazor WebAssembly, or a Blazor Web App (static SSR, Interactive Server, WebAssembly, Auto).

## Blazor Server / Blazor Web App host

```csharp
using BlazorProfiler;
using BlazorProfiler.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.AddBlazorProfiler(
    options => options.UseProfile(ProfilerProfile.Development),
    server => server.AuthorizationPolicy = builder.Environment.IsDevelopment() ? null : "ProfilerAccess");

var app = builder.Build();
app.MapStaticAssets();
app.MapBlazorProfiler();          // /_blazor-profiler/  and  /_blazor-profiler/api/*
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
```

- The profiler is **enabled only in Development** unless you set `options.Enabled` explicitly.
- In Development, requests from the local machine get full access. Everywhere else access requires
  `ProfilerServerOptions.AuthorizationPolicy` (full access) or `OwnCircuitPolicy` (a user sees only circuits they opened).
  See [Security](security.md).
- Requests made through `IHttpClientFactory` are recorded automatically.
- JS interop on the server: inject `IProfiledJSRuntime` instead of `IJSRuntime` in components you want to profile. (The
  framework requires the original runtime for circuits, so the profiler does not replace it.)

## Blazor WebAssembly (standalone or Web App client project)

```csharp
using BlazorProfiler.Http;
using BlazorProfiler.WebAssembly;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddBlazorProfiler();

builder.Services.AddScoped(sp => new HttpClient(ProfilerHttpMessageHandler.Create(sp))
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});

await builder.Build().RunAsync();
```

- `IJSRuntime` is profiled automatically.
- `MetricsSupport` is enabled by the package (needed for Blazor's metrics in WebAssembly).
- In a Blazor Web App the client's environment may differ from the server's; set `options.Enabled` explicitly in the client
  if you want it on regardless of environment.

## The browser component

Render once in an interactive component (for example the main layout of an interactive app, or a shared page component):

```razor
<BlazorProfilerClient />                        @* browser timings, startup, web vitals; WebAssembly: dashboard bridge *@
<BlazorProfilerClient ShowLauncher="true" />    @* also shows a floating "Profiler" button *@
<BlazorProfilerClient ShowToolbar="true" />     @* in-page toolbar: live INP, long frames, DOM updates, highlighting *@
<BlazorProfilerClient DevToolsTracks="true" />  @* Blazor tracks in the Chrome/Edge Performance panel *@
```

It renders nothing, does nothing when the profiler is disabled, and does nothing during static SSR. Shortcut to open the
dashboard: **Alt+Shift+P** (configurable through `Shortcut`). **Alt+Shift+H** toggles update highlighting: DOM elements
are outlined as render batches patch them, and the outline heats up from blue to red when the same element changes
repeatedly. A component that re-renders without changing its output patches nothing and does not flash.

With `DevToolsTracks`, record a profile in the browser's Performance panel (enable *Show custom tracks* in its capture
settings): renders, batches, handlers, HTTP, interop, and database commands appear in a **Blazor** track group next to the
browser's own main-thread, network, and interaction tracks. Keep recording a second after the interaction; events are sent
once per second.

## Per-component detail

```razor
@* _Imports.razor *@
@inherits BlazorProfiler.Instrumentation.ProfiledComponentBase
```

`ProfiledComponentBase` behaves like `ComponentBase` (same lifecycle order, rendering, and error behavior) and adds
per-instance data. Components with an explicit `@inherits` keep their base class. If a component implements `IDisposable` or
`IAsyncDisposable` itself, its own disposal runs as before and the profiler does not record its disposal.

## Opening the dashboard

| Hosting | URL |
|---|---|
| Server / Web App host | `/_blazor-profiler/` |
| WebAssembly | `_content/BlazorProfiler/dashboard/index.html?source=bridge` (or Alt+Shift+P in the app) |
| Offline trace | `_content/BlazorProfiler/dashboard/index.html` and *Open trace…* |

## A typical investigation

1. Reproduce the problem with the profiler recording (a *Default session* starts automatically).
2. Open **Interactions** to see each click, navigation, or custom operation with everything it caused (renders, batches,
   requests, queries, interop, browser latency), ranked by duration. Select one for its waterfall and the components it
   rendered, ranked by render time.
3. Open **Diagnostics**: findings are ordered by severity, confidence, and impact. Read the evidence and caveats. **Copy
   for AI** copies a finding as Markdown with a request for a fix.
4. Use **Show in timeline** to see the interaction, the render batches, requests, and interop calls of the same operation
   (outlined together). The **Components** view explains *why* each component rendered (first render, parameters, event
   handler, `StateHasChanged`, async completion) and which parameters changed.
5. Apply the suggested fix. In **Sessions**, start a new session, reproduce again, and use **Compare** to verify.
6. Export the session (native `.bptrace`) to share it, Chrome format to open it in Perfetto, or Markdown for a pull
   request or issue.

## Memory leaks

Disposed components and the components of closed circuits are watched through weak references. Navigate back and forth a
few times, then press **Check for leaks** on the Components page: it forces full garbage collections and reports
instances that are still reachable (rule BP5004), separating leaks that accumulate from the single child Blazor's renderer
keeps in a parent's previous render tree.

## Database queries

Entity Framework Core commands are recorded automatically (no extra package): the **Database** view lists queries by
shape and every command with its originating component, and rules detect N+1 queries, duplicates (including the
prerender-then-interactive double fetch), slow queries, synchronous queries on the renderer, and circuit-lifetime
`DbContext` instances.

## Slow connections (Blazor Server)

Local development has almost no latency. On the **Circuits** page, set a simulated latency (for example 150 ms): every
message from the browser waits that long, so missing loading states and chatty interop become visible. Never active in
Production.

## Tests and CI

Rules are deterministic, so they make good test assertions:

```csharp
// bUnit or integration test with the profiler registered (options.Pipeline.SynchronousProcessing = true)
var profiler = Services.GetRequiredService<IProfiler>();
var session = profiler.StartSession();
cut.Find("button").Click();

await ProfilerAssert.NoFindingsAsync(profiler, session, DiagnosticSeverity.Medium);
await new ProfilerBudget { MaxComponentRenders = 20, MaxDatabaseCommands = 2, MaxRetainedComponents = 0 }
    .WithMaxRenders("MyApp.Components.ProductRow", 10)
    .AssertAsync(profiler, session);
```

For end-to-end tests, export a trace (for example from a Playwright run through `/_blazor-profiler/api/sessions/{id}/export`)
and check it with the CLI:

```bash
dotnet tool install -g BlazorProfiler.Cli
blazor-profiler check trace.bptrace --fail-on medium --budget budget.json --format github   # or sarif, markdown, text
blazor-profiler compare baseline.bptrace current.bptrace --threshold 10                      # fails on count regressions
blazor-profiler report trace.bptrace --output report.md
```

Exit codes: 0 success, 1 findings, budget violations, or regressions, 2 invalid usage or input.

## Manual instrumentation

```csharp
@inject IProfiler Profiler

using (Profiler.StartOperation("Load dashboard", "data"))
{
    await LoadAsync();   // HTTP/interop inside are attributed to this operation
}

Profiler.Mark("Cache warmed");
```

## Samples

`demo/Server` contains one demo per problem category in both Interactive Server and Interactive
WebAssembly modes. Run it with `dotnet run --project demo/Server` and open `http://localhost:5199`.
