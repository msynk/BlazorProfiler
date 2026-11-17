using BlazorProfiler.Demo.Standalone;
using BlazorProfiler.Http;
using BlazorProfiler.WebAssembly;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.AddBlazorProfiler(options =>
{
    // The sample enables the profiler in every environment so it also works when published. Real applications should keep
    // the default (Development only) or enable it deliberately.
    options.Enabled = true;
    options.Instrumentation.TrackRenderOutput = true;
});

builder.Services.AddSingleton<AppState>();

// The default WebAssembly HttpClient is created manually, so it needs the profiler handler explicitly.
builder.Services.AddScoped(sp => new HttpClient(ProfilerHttpMessageHandler.Create(sp)) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
