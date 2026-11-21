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
per-instance data. Components with an explicit `@inherits` keep their base class. A component that implements `IDisposable`
itself keeps its own `Dispose` (called once, as with `ComponentBase`) and its disposal is recorded too, so leak detection
covers the components most likely to leak. Components that implement `IAsyncDisposable` themselves, and components that
are not instrumented at all (third-party components, `@inherits` other bases), are recorded by their instrumented parent:
when they leave its render tree, or the parent is disposed, the parent records the disposal and hands them to the leak
check. Only such components whose parent is not instrumented either go unrecorded (their scope's end is still watched on
Blazor Server).

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
keeps in a parent's previous render tree. For each reported instance the profiler names what most likely holds it: an
event of an injected service that still has its handler (`AppState.OnChange → Counter.StateHasChanged`), a static event,
an undisposed `DotNetObjectReference`, or a timer.

The same leak usually shows up in many circuits or test runs. **Sessions → Leaks across sessions** groups the retained
instances of every session by leak signature (the component type and its retainers), the most widespread first; the
signature is also listed in the BP5004 finding. For traces from several runs, use `blazor-profiler leaks a.bptrace
b.bptrace` (add `--fail-on-leaks` to fail a build).

## Responsiveness

The **Responsiveness** page shows what users felt on the browser's main thread: slow interactions split into input delay,
processing, and presentation; long animation frames with the scripts that ran in them (Chromium's Long Animation Frames
API) and how much forced layout each caused (BP7006); and rage or dead clicks (BP7007) with the .NET work that was running.
In the **Interactions** view, each operation also has a render-tree flame graph: width is render time including
descendants, and components that did not render are hatched.

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
blazor-profiler compare baseline.bptrace current.bptrace --threshold 10                      # fails on count regressions,
                                                                                             # including per-component renders
blazor-profiler report trace.bptrace --output report.md
blazor-profiler leaks run1.bptrace run2.bptrace --fail-on-leaks                                # leaks grouped by signature
```

Exit codes: 0 success, 1 findings, budget violations, or regressions, 2 invalid usage or input.

## Comparing before and after

To compare two sessions (before and after a change), use **Sessions → Compare**: totals, per-component render counts, and
render p50/p95 per component. To compare two moments of one session (the first and second time a page loads, or before
and after a cache warms up), select a range in the **Timeline** with Shift+drag, press **Use as baseline**, select another
range, and press **Compare with baseline**.

To find out what slow interactions have in common, use the **latency heatmap** on the Interactions page (operations by
start time and duration): drag over the slow cells, or press **Explain the slowest 10%**, and the profiler ranks the
attributes that occur more often in the selection than in the other operations (trigger, components rendered, endpoints,
queries, interop calls, circuit, errors, findings).

## Deep capture (WebAssembly)

For questions the profiler's events cannot answer (which .NET methods use the CPU, what fills the heap), build the
WebAssembly app with the runtime's diagnostics support, for example only in a dedicated configuration:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Profiling'">
  <EnableDiagnostics>true</EnableDiagnostics>
  <!-- CPU samples also need method instrumentation; limit it to your own code. -->
  <WasmPerformanceInstrumentation>N:MyApp</WasmPerformanceInstrumentation>
</PropertyGroup>
```

The toolbar then shows **CPU**, **GC dump**, and **Counters** buttons. Each one downloads a `.nettrace` file (open it in
PerfView or Visual Studio; convert heap dumps with `dotnet-gcdump`) and records a marker on the profiler timeline, so the
capture lines up with the interactions around it. Without these properties the buttons do not appear. Do not ship such
builds: diagnostics support makes the download larger and the app slower.

## Coding agents (MCP)

The server maps a [Model Context Protocol](https://modelcontextprotocol.io) endpoint at `/_blazor-profiler/mcp`
(Streamable HTTP) with the same access rules as the dashboard: in Development, local requests only. Agents can list
sessions, read an overview, list findings and explain one, list the slowest interactions and see what one did, and ask why
a component rendered. Register it in your agent, for example:

```json
{ "mcpServers": { "blazor-profiler": { "type": "http", "url": "https://localhost:5001/_blazor-profiler/mcp" } } }
```

Tools: `list_sessions`, `get_overview`, `list_findings`, `explain_finding`, `list_interactions`, `get_interaction`,
`list_components`, `get_component`, `list_leaks` (across sessions), `get_report`. The endpoint is read-only unless `ProfilerServerOptions.McpAllowControl`
is set, which adds `control_session` and `run_command` (for example `check-leaks`). For WebAssembly apps and CI, export a
trace and serve it from the CLI over standard input and output:

```json
{ "mcpServers": { "blazor-profiler": { "command": "blazor-profiler", "args": ["mcp", "trace.bptrace"] } } }
```

### Agents in the browser (WebMCP)

Browser agents (for example Chrome's built-in assistant, or an agent driving Chrome through Chrome DevTools MCP with
`--categoryExperimentalWebmcp`) can call tools that a page registers with WebMCP. With `AgentTools`, the profiler offers its
read-only MCP tools there too, so an agent looking at the running app can ask the profiler what it measured:

```razor
<BlazorProfilerClient AgentTools="true" />
```

The tools are the read-only ones listed above, prefixed `blazor_profiler_` (`blazor_profiler_list_findings`, ...), and
marked as read-only debugging tools whose results are untrusted content. Tools that change the profiling state are never
offered in the page. In WebAssembly they run in-process through the dashboard bridge; on Blazor Server they call the MCP
endpoint (same access rules: in Development, local requests only), or the bridge when it is enabled. WebMCP is available in
Chrome through an origin trial and `chrome://flags/#enable-webmcp-testing`; in other browsers nothing is registered. Chrome
DevTools lists the tools under **Application → WebMCP**, where they can also be run by hand.

## Explaining findings with AI

Findings are explained by deterministic rules. A language model can add an explanation in the context of your code: set
`ProfilerServerOptions.AiExplanations` and register an `IChatClient` (Microsoft.Extensions.AI) for the profiler, and every
finding in the dashboard gets an **Explain with AI** button:

```csharp
builder.AddBlazorProfiler(configureServer: server => server.AiExplanations = true);

// Any Microsoft.Extensions.AI chat client: OpenAI, Azure OpenAI, Ollama, ... Keyed, so the profiler never uses the app's
// own client by accident; without the key it falls back to the app's IChatClient.
// (AddKeyedChatClient comes with the Microsoft.Extensions.AI package; AddKeyedSingleton<IChatClient> works as well.)
builder.Services.AddKeyedChatClient(ProfilerServerOptions.ChatClientKey, sp => CreateYourChatClient(sp));
```

The profiler sends the finding (rule texts, evidence, caveats) and the statistics of the component it concerns, framed by
a system prompt that tells the model to stay with the evidence and to treat the data as data. It does not send source code,
parameter values, or request bodies, which it never records; type and method names, URLs (with redacted query values),
and normalized SQL are part of findings. The answer is shown as text, with the model's name and a reminder to check it.
Explanations need control access, like other actions.

In WebAssembly, implement `IFindingExplainer` with a call to your own backend (so no model credentials reach the browser)
and register it as a singleton; the dashboard then offers the button there too. Coding agents connected through
[MCP](#coding-agents-mcp) already have the findings and do not need this.

## OpenTelemetry

Set `options.Telemetry.Metrics` and `options.Telemetry.Traces` and add the `BlazorProfiler` meter and activity source to
your OpenTelemetry setup to see component render and lifecycle spans, JS interop spans, interaction latency, Core Web
Vitals, leaks, and frustration signals in the Aspire dashboard or any OTLP backend. Render and interop spans appear inside
Blazor's own `HandleEvent` span of the click that caused them. See [configuration](configuration.md#telemetry-opentelemetry).

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

`demo/Standalone` is a standalone Blazor WebAssembly app (no server project, interactive router): a page with N+1
requests and a leaky page that subscribes to a singleton's event and starts a timer it never stops. Run it with
`dotnet run --project demo/Standalone` and press Alt+Shift+P for the dashboard.
