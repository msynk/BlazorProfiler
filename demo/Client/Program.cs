using BlazorProfiler;
using BlazorProfiler.Http;
using BlazorProfiler.WebAssembly;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.AddBlazorProfiler(options =>
{
    // The sample enables the profiler in every environment so the demos work when published. Real applications should
    // keep the default (Development only) or enable it deliberately.
    options.Enabled = true;
    options.Instrumentation.TrackRenderOutput = true;
    options.Collectors.JSObjectReferences = true;
    options.Collectors.JSInteropPayloadEstimation = true;
});

// The default WebAssembly HttpClient is created manually, so it needs the profiler handler explicitly.
builder.Services.AddScoped(sp => new HttpClient(ProfilerHttpMessageHandler.Create(sp)) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

await builder.Build().RunAsync();
