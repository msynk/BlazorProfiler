using BlazorProfiler;
using BlazorProfiler.Demo.Server;
using BlazorProfiler.Demo.Server.Components;
using BlazorProfiler.Server;
using Microsoft.AspNetCore.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Enabled in Development only (the default). The dashboard is served at /_blazor-profiler and, outside Development,
// requires the "ProfilerAccess" policy.
builder.AddBlazorProfiler(
    options =>
    {
        options.Instrumentation.TrackRenderOutput = true;
        options.Collectors.JSInteropPayloadEstimation = true;
        options.Collectors.JSObjectReferences = true;
    },
    server => server.AuthorizationPolicy = builder.Environment.IsDevelopment() ? null : "ProfilerAccess");
builder.Services.AddAuthorization(o => o.AddPolicy("ProfilerAccess", p => p.RequireRole("Developer")));

// Server-side components call the demo API through IHttpClientFactory, which the profiler instruments automatically.
builder.Services.AddHttpClient();
builder.Services.AddScoped(sp =>
{
    var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("demo-api");
    client.BaseAddress = new Uri(sp.GetRequiredService<NavigationManager>().BaseUri);
    return client;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapDemoApi();
app.MapBlazorProfiler();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(BlazorProfiler.Demo.Client.Product).Assembly);

app.Run();

/// <summary>Entry point, exposed for integration tests.</summary>
public partial class Program;
