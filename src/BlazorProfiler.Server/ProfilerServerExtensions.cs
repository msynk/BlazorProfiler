using System.Net;
using BlazorProfiler.Api;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BlazorProfiler.Server;

/// <summary>Server integration options.</summary>
public sealed class ProfilerServerOptions
{
    /// <summary>The path prefix of the dashboard and its API. Defaults to <c>/_blazor-profiler</c>.</summary>
    public string RoutePrefix { get; set; } = "/_blazor-profiler";

    /// <summary>
    /// An authorization policy whose holders get full access: every session, every circuit, process-wide metrics, import,
    /// and control. Required for any access outside Development on the local machine.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// An authorization policy whose holders may profile only their own circuits (circuits opened by the same user). When
    /// <see langword="null"/>, per-user access is disabled.
    /// </summary>
    public string? OwnCircuitPolicy { get; set; }

    /// <summary>
    /// Grants full access to requests from the local machine when the environment is Development and no
    /// <see cref="AuthorizationPolicy"/> is configured. Never applies in other environments.
    /// </summary>
    public bool AllowLoopbackInDevelopment { get; set; } = true;

    /// <summary>Creates a session per circuit automatically (subject to the session limit).</summary>
    public bool AutoCircuitSessions { get; set; }

    /// <summary>The maximum size of an uploaded trace.</summary>
    public long MaxImportBytes { get; set; } = 64L * 1024 * 1024;
}

/// <summary>Registration of the Blazor Server / Blazor Web App integration.</summary>
public static class ProfilerServerExtensions
{
    /// <summary>
    /// Adds the Blazor profiler to a server application. The profiler is enabled only in the Development environment
    /// unless <see cref="ProfilerOptions.Enabled"/> is set explicitly.
    /// </summary>
    public static IBlazorProfilerBuilder AddBlazorProfiler(
        this IHostApplicationBuilder builder,
        Action<ProfilerOptions>? configure = null,
        Action<ProfilerServerOptions>? configureServer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var serverOptions = new ProfilerServerOptions();
        configureServer?.Invoke(serverOptions);
        serverOptions.RoutePrefix = "/" + serverOptions.RoutePrefix.Trim('/');

        var profiler = builder.Services.AddBlazorProfiler(options =>
        {
            configure?.Invoke(options);
            options.Enabled ??= builder.Environment.IsDevelopment();
        });

        profiler.Hosting.IsWebAssembly = false;
        profiler.Hosting.Source = "server";
        profiler.Hosting.EnvironmentName = builder.Environment.EnvironmentName;
        profiler.Hosting.DashboardUrl = serverOptions.RoutePrefix.TrimStart('/') + "/";

        var services = builder.Services;
        services.TryAddSingleton(serverOptions);
        services.TryAddSingleton<CircuitRegistry>();
        if (profiler.Options.Enabled == false)
        {
            return profiler;
        }

        services.TryAddSingleton<CircuitCollector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProfilerCollector, CircuitCollector>(sp => sp.GetRequiredService<CircuitCollector>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProfilerCorrelationProvider, CircuitCorrelationProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, ProfilerCircuitHandler>());
        if (!services.Any(d => d.ServiceType == typeof(ProfilerHubFilter)))
        {
            // Global hub filters are copied to every hub's options, including Blazor's component hub.
            services.AddSingleton<ProfilerHubFilter>();
            services.Configure<HubOptions>(o => o.AddFilter<ProfilerHubFilter>());
        }
        services.AddHostedService<ProfilerHostedService>();
        services.TryAddSingleton<ProfilerAccessEvaluator>();
        return profiler;
    }

    /// <summary>Starts the profiler with the application and logs its access configuration.</summary>
    private sealed class ProfilerHostedService(IProfiler profiler, ProfilerServerOptions options, IHostEnvironment environment, ILogger<ProfilerHostedService> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            (profiler as ProfilerHost)?.Start();
            if (profiler.IsEnabled && !environment.IsDevelopment() && options.AuthorizationPolicy is null && options.OwnCircuitPolicy is null)
            {
                logger.LogWarning(
                    "The Blazor profiler is enabled in the {Environment} environment without an authorization policy. Profiling data is collected, but the dashboard API is not accessible. Configure ProfilerServerOptions.AuthorizationPolicy to allow access.",
                    environment.EnvironmentName);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>Computes the profiler access of an HTTP request.</summary>
public sealed class ProfilerAccessEvaluator
{
    private readonly ProfilerServerOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly IServiceProvider _services;
    private readonly CircuitRegistry _circuits;

    /// <summary>Creates the evaluator.</summary>
    public ProfilerAccessEvaluator(ProfilerServerOptions options, IHostEnvironment environment, IServiceProvider services, CircuitRegistry circuits)
    {
        _options = options;
        _environment = environment;
        _services = services;
        _circuits = circuits;
    }

    /// <summary>Returns the access of the request, or <see langword="null"/> when the caller has no access.</summary>
    public async Task<ProfilerApiAccess?> EvaluateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_options.AuthorizationPolicy is null)
        {
            if (_options.AllowLoopbackInDevelopment && _environment.IsDevelopment() && IsLocal(context))
            {
                return ProfilerApiAccess.Full;
            }
        }
        else if (await AuthorizeAsync(context, _options.AuthorizationPolicy).ConfigureAwait(false))
        {
            return ProfilerApiAccess.Full;
        }

        if (_options.OwnCircuitPolicy is not null && await AuthorizeAsync(context, _options.OwnCircuitPolicy).ConfigureAwait(false))
        {
            var key = CircuitRegistry.GetOwnerKey(context.User);
            if (key is not null)
            {
                return new ProfilerApiAccess { CanViewAllCircuits = false, OwnedCircuits = _circuits.GetOwned(key), CanControl = true };
            }
        }

        return null;
    }

    private async Task<bool> AuthorizeAsync(HttpContext context, string policy)
    {
        var authorization = _services.GetService<IAuthorizationService>();
        if (authorization is null)
        {
            return false;
        }

        var result = await authorization.AuthorizeAsync(context.User, resource: null, policy).ConfigureAwait(false);
        return result.Succeeded;
    }

    private static bool IsLocal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            // In-memory test servers have no remote address.
            return context.Connection.LocalIpAddress is null;
        }

        return IPAddress.IsLoopback(remote) || remote.Equals(context.Connection.LocalIpAddress);
    }
}
