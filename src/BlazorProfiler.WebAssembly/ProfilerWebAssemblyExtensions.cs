using BlazorProfiler.Collectors;
using BlazorProfiler.Interop;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BlazorProfiler.WebAssembly;

/// <summary>Registration of the Blazor WebAssembly integration.</summary>
public static class ProfilerWebAssemblyExtensions
{
    /// <summary>
    /// Adds the Blazor profiler to a WebAssembly application. The profiler is enabled only in the Development environment
    /// unless <see cref="ProfilerOptions.Enabled"/> is set explicitly. Place a <c>&lt;BlazorProfilerClient /&gt;</c> component in
    /// the app to collect browser metrics and connect the dashboard.
    /// </summary>
    public static IBlazorProfilerBuilder AddBlazorProfiler(this WebAssemblyHostBuilder builder, Action<ProfilerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var profiler = builder.Services.AddBlazorProfiler(options =>
        {
            configure?.Invoke(options);
            options.Enabled ??= builder.HostEnvironment.IsDevelopment();
        });

        profiler.Hosting.IsWebAssembly = true;
        profiler.Hosting.Source = "webassembly";
        profiler.Hosting.EnvironmentName = builder.HostEnvironment.Environment;
        profiler.Hosting.DashboardUrl = "_content/BlazorProfiler/dashboard/index.html?source=bridge";

        if (profiler.Options.Enabled != false && profiler.Options.Collectors.JSInterop)
        {
            DecorateJSRuntime(builder.Services);
        }

        return profiler;
    }

    /// <summary>
    /// Replaces the <see cref="IJSRuntime"/> registration with a profiled decorator. Safe in WebAssembly, where framework
    /// services use the runtime instance directly and components receive it through dependency injection.
    /// </summary>
    internal static void DecorateJSRuntime(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IJSRuntime));
        if (existing?.ImplementationInstance is not IJSRuntime runtime || runtime is ProfiledJSRuntime)
        {
            return;
        }

        services.Remove(existing);
        services.AddSingleton<IJSRuntime>(sp => ProfiledJSRuntime.Create(runtime, sp.GetRequiredService<JSInteropCollector>()));
    }
}
