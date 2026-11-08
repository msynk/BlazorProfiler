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
<BlazorProfilerClient />                       @* browser timings; WebAssembly: dashboard bridge *@
<BlazorProfilerClient ShowLauncher="true" />   @* also shows a floating "Profiler" button *@
```

It renders nothing, does nothing when the profiler is disabled, and does nothing during static SSR. Shortcut to open the
dashboard: **Alt+Shift+P** (configurable through `Shortcut`).

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
2. Open **Diagnostics**: findings are ordered by severity, confidence, and impact. Read the evidence and caveats.
3. Use **Show in timeline** to see the interaction, the render batches, requests, and interop calls of the same operation
   (outlined together).
4. Apply the suggested fix. In **Sessions**, start a new session, reproduce again, and use **Compare** to verify.
5. Export the session (native `.bptrace`) to share it, or Chrome format to open it in Perfetto.

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
