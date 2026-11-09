using System.Diagnostics.CodeAnalysis;
using BlazorProfiler.Analysis;
using BlazorProfiler.Analysis.Rules;
using BlazorProfiler.Api;
using BlazorProfiler.Browser;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using BlazorProfiler.Http;
using BlazorProfiler.Instrumentation;
using BlazorProfiler.Interop;
using BlazorProfiler.Pipeline;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace BlazorProfiler;

/// <summary>A builder returned by <see cref="BlazorProfilerServiceCollectionExtensions.AddBlazorProfiler"/>.</summary>
public interface IBlazorProfilerBuilder
{
    /// <summary>The service collection.</summary>
    IServiceCollection Services { get; }

    /// <summary>The profiler options. Changes made after the host has been created have no effect.</summary>
    ProfilerOptions Options { get; }

    /// <summary>Information about the hosting environment.</summary>
    ProfilerHostingInfo Hosting { get; }
}

/// <summary>Dependency injection registration for the Blazor profiler.</summary>
public static class BlazorProfilerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the profiler core and the hosting-independent Blazor collectors. Prefer the hosting-specific entry points
    /// (<c>AddBlazorProfiler</c> on <c>WebApplicationBuilder</c> or <c>WebAssemblyHostBuilder</c>), which also decide whether
    /// the profiler is enabled for the current environment.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures options.</param>
    /// <returns>A builder for further configuration.</returns>
    public static IBlazorProfilerBuilder AddBlazorProfiler(this IServiceCollection services, Action<ProfilerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(BlazorProfilerBuilder))?.ImplementationInstance as BlazorProfilerBuilder;
        if (existing is not null)
        {
            configure?.Invoke(existing.Options);
            return existing;
        }

        var options = new ProfilerOptions();
        configure?.Invoke(options);
        var hosting = new ProfilerHostingInfo();
        var builder = new BlazorProfilerBuilder(services, options, hosting);
        services.AddSingleton(builder);
        services.TryAddSingleton(options);
        services.TryAddSingleton(hosting);

        services.TryAddSingleton<JSInteropCollector>();
        services.TryAddSingleton(sp => CreateHost(sp, options));
        services.TryAddSingleton<IProfiler>(sp => sp.GetRequiredService<ProfilerHost>());
        services.TryAddSingleton(sp => new ProfilerApi(sp.GetRequiredService<IProfiler>(), hosting.Source, null, sp.GetServices<IProfilerCommand>()));
        services.TryAddScoped<IProfiledJSRuntime>(sp => ProfiledJSRuntime.Create(sp.GetRequiredService<IJSRuntime>(), sp.GetRequiredService<JSInteropCollector>()));

        if (options.Enabled == false)
        {
            // Disabled: keep injectable services resolvable but add no interception points.
            return builder;
        }

        RegisterEnabledServices(services, options);
        return builder;
    }

    /// <summary>Registers an additional diagnostic rule.</summary>
    public static IBlazorProfilerBuilder AddRule<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRule>(this IBlazorProfilerBuilder builder)
        where TRule : class, IDiagnosticRule
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IDiagnosticRule, TRule>();
        return builder;
    }

    /// <summary>Registers an additional event processor.</summary>
    public static IBlazorProfilerBuilder AddProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(this IBlazorProfilerBuilder builder)
        where TProcessor : class, IProfilerEventProcessor
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IProfilerEventProcessor, TProcessor>();
        return builder;
    }

    /// <summary>Registers an additional collector.</summary>
    public static IBlazorProfilerBuilder AddCollector<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCollector>(this IBlazorProfilerBuilder builder)
        where TCollector : class, IProfilerCollector
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IProfilerCollector, TCollector>();
        return builder;
    }

    internal static void RegisterEnabledServices(IServiceCollection services, ProfilerOptions options)
    {
        if (services.Any(d => d.ServiceType == typeof(ComponentInstrumentation)))
        {
            return;
        }

        services.TryAddSingleton<ComponentInstanceRegistry>();
        AddCollector<ComponentMetricsCollector>(services);
        AddCollector<ComponentActivityCollector>(services);
        AddCollector<ComponentActivationCollector>(services);
        AddCollector<ComponentInstrumentationCollector>(services);
        AddCollector<RuntimeCollector>(services);
        AddCollector<HttpCollector>(services);
        AddCollector<BrowserCollector>(services);
        AddCollector<ComponentRetentionCollector>(services);
        AddCollector<DatabaseCollector>(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProfilerCommand, LeakCheckCommand>());
        services.TryAddSingleton<DevToolsTrackProcessor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProfilerEventProcessor, DevToolsTrackProcessor>(sp => sp.GetRequiredService<DevToolsTrackProcessor>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProfilerCollector, JSInteropCollector>(sp => sp.GetRequiredService<JSInteropCollector>()));
        services.TryAddSingleton<ComponentInstrumentation>();

        if (options.Collectors.ComponentMetrics)
        {
            // Idempotent; required for WebAssembly, where the host does not register component metrics by default.
            ComponentsMetricsServiceCollectionExtensions.AddComponentsMetrics(services);
        }

        if (options.Collectors.ComponentActivities)
        {
            ComponentsMetricsServiceCollectionExtensions.AddComponentsTracing(services);
        }

        if (options.Collectors.Http)
        {
            services.TryAddTransient<ProfilerHttpMessageHandler>();
            services.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler<ProfilerHttpMessageHandler>());
        }

        if (options.Collectors.ComponentActivator)
        {
            DecorateComponentActivator(services);
        }
    }

    private static void AddCollector<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCollector>(IServiceCollection services)
        where TCollector : class, IProfilerCollector
    {
        services.TryAddSingleton<TCollector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProfilerCollector, TCollector>(sp => sp.GetRequiredService<TCollector>()));
    }

    private static ProfilerHost CreateHost(IServiceProvider services, ProfilerOptions options)
    {
        var customRules = services.GetServices<IDiagnosticRule>().ToList();
        var host = new ProfilerHost(
            options,
            clock: services.GetService<IProfilerClock>(),
            collectors: services.GetServices<IProfilerCollector>(),
            correlationProviders: services.GetServices<IProfilerCorrelationProvider>(),
            processors: services.GetServices<IProfilerEventProcessor>(),
            redactionPolicy: services.GetService<IProfilerRedactionPolicy>(),
            rules: [.. BuiltInRules.Create(), .. customRules],
            recommendationProviders: services.GetServices<IDiagnosticRecommendationProvider>(),
            loggerFactory: services.GetService<ILoggerFactory>());
        host.Start();
        return host;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The activator type comes from an existing registration whose implementation type is preserved by the container.")]
    private static void DecorateComponentActivator(IServiceCollection services)
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IComponentActivator));
        if (existing is not null)
        {
            services.Remove(existing);
        }

        var lifetime = existing?.Lifetime ?? ServiceLifetime.Scoped;
        services.Add(ServiceDescriptor.Describe(
            typeof(IComponentActivator),
            sp => new ProfilerComponentActivator(
                sp,
                existing is null ? null : CreateFromDescriptor(sp, existing),
                sp.GetRequiredService<ComponentInstanceRegistry>(),
                sp.GetRequiredService<ComponentActivationCollector>(),
                sp.GetRequiredService<IProfiler>(),

                // Scope-end retention tracking needs one activator per scope (circuit or request).
                lifetime == ServiceLifetime.Scoped ? sp.GetService<ComponentRetentionCollector>() : null),
            lifetime));
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "The implementation type is preserved by the original registration.")]
    private static IComponentActivator CreateFromDescriptor(IServiceProvider services, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IComponentActivator instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (IComponentActivator)descriptor.ImplementationFactory(services);
        }

        return (IComponentActivator)ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!);
    }

    private sealed class BlazorProfilerBuilder(IServiceCollection services, ProfilerOptions options, ProfilerHostingInfo hosting) : IBlazorProfilerBuilder
    {
        public IServiceCollection Services { get; } = services;

        public ProfilerOptions Options { get; } = options;

        public ProfilerHostingInfo Hosting { get; } = hosting;
    }
}
