using System.Net;
using System.Security.Claims;
using BlazorProfiler.Api;
using BlazorProfiler.Browser;
using BlazorProfiler.Collectors;
using BlazorProfiler.Correlation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
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
    /// <remarks>
    /// A request only counts as local when it arrives on a loopback connection, addresses the app by a local host name, and
    /// carries no forwarding headers (<c>Forwarded</c>, <c>X-Forwarded-*</c>, and similar). Traffic relayed by a reverse
    /// proxy or a tunnel (for example Dev Tunnels or ngrok) therefore never qualifies; configure
    /// <see cref="AuthorizationPolicy"/> when the app is reachable from other machines.
    /// </remarks>
    public bool AllowLoopbackInDevelopment { get; set; } = true;

    /// <summary>Creates a session per circuit automatically (subject to the session limit).</summary>
    public bool AutoCircuitSessions { get; set; }

    /// <summary>
    /// The number of sessions created by <see cref="AutoCircuitSessions"/> that are kept (stopped) after their circuit
    /// closes, so their data can still be viewed and exported by the circuit's owner. When more are closed, the oldest are
    /// removed; a retained session is also removed when a new circuit needs room under
    /// <see cref="RetentionOptions.MaxSessions"/>. Zero removes a circuit's session as soon as the circuit closes.
    /// Defaults to 4.
    /// </summary>
    public int RetainedCircuitSessions { get; set; } = 4;

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

        // Authorizes BlazorProfilerClient's dashboard bridge (when enabled) for the circuit's user, with the same policies
        // as the HTTP endpoints. Without it the bridge would stay disabled on the server.
        services.TryAddScoped<IProfilerBridgeAccessProvider, CircuitBridgeAccessProvider>();
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
        if (_options.AuthorizationPolicy is null && _options.AllowLoopbackInDevelopment && _environment.IsDevelopment() && IsLocal(context))
        {
            return ProfilerApiAccess.Full;
        }

        return await EvaluateUserAsync(context.User).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the access of a user from the configured policies alone, or <see langword="null"/> when the user has no
    /// access. Used where no network information is available (for example the dashboard bridge of a circuit), so the
    /// Development loopback grant never applies.
    /// </summary>
    public async Task<ProfilerApiAccess?> EvaluateUserAsync(ClaimsPrincipal? user)
    {
        user ??= new ClaimsPrincipal(new ClaimsIdentity());
        if (_options.AuthorizationPolicy is not null && await AuthorizeAsync(user, _options.AuthorizationPolicy).ConfigureAwait(false))
        {
            return ProfilerApiAccess.Full;
        }

        if (_options.OwnCircuitPolicy is not null && await AuthorizeAsync(user, _options.OwnCircuitPolicy).ConfigureAwait(false))
        {
            var key = CircuitRegistry.GetOwnerKey(user);
            if (key is not null)
            {
                return new ProfilerApiAccess { CanViewAllCircuits = false, OwnedCircuits = _circuits.GetOwned(key), CanControl = true };
            }
        }

        return null;
    }

    private async Task<bool> AuthorizeAsync(ClaimsPrincipal user, string policy)
    {
        var authorization = _services.GetService<IAuthorizationService>();
        if (authorization is null)
        {
            return false;
        }

        var result = await authorization.AuthorizeAsync(user, resource: null, policy).ConfigureAwait(false);
        return result.Succeeded;
    }

    // Headers added by reverse proxies, tunnels, and CDNs (and by ForwardedHeadersMiddleware after it consumed them).
    private static readonly string[] s_forwardingHeaders =
    [
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "X-Original-For",
        "X-Original-Host",
        "X-Original-Proto",
        "X-Real-IP",
        "X-Client-IP",
        "True-Client-IP",
        "CF-Connecting-IP",
    ];

    /// <summary>
    /// Returns whether a request comes from a browser on this machine. A loopback connection alone is not enough: a local
    /// reverse proxy or tunnel (Dev Tunnels, ngrok) also connects over loopback while relaying remote clients, so relayed
    /// requests (forwarding headers, or a Host that is not a local name) are rejected.
    /// </summary>
    internal static bool IsLocal(HttpContext context)
    {
        foreach (var header in s_forwardingHeaders)
        {
            if (context.Request.Headers.ContainsKey(header))
            {
                return false;
            }
        }

        var local = context.Connection.LocalIpAddress;
        if (!IsLocalHostName(context.Request.Host.Host, local))
        {
            return false;
        }

        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            // In-memory test servers have no remote address.
            return local is null;
        }

        return IPAddress.IsLoopback(remote) || remote.Equals(local);
    }

    private static bool IsLocalHostName(string? host, IPAddress? localAddress)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var address) && (IPAddress.IsLoopback(address) || address.Equals(localAddress));
    }
}

/// <summary>
/// Authorizes the dashboard bridge of <see cref="BlazorProfilerClient"/> on Blazor Server: requests get the access of the
/// circuit's user under <see cref="ProfilerServerOptions.AuthorizationPolicy"/> and
/// <see cref="ProfilerServerOptions.OwnCircuitPolicy"/>, evaluated per request.
/// </summary>
internal sealed class CircuitBridgeAccessProvider(ProfilerAccessEvaluator evaluator, IServiceProvider services) : IProfilerBridgeAccessProvider
{
    public async ValueTask<ProfilerApiAccess?> GetAccessAsync(CancellationToken cancellationToken = default)
    {
        ClaimsPrincipal? user = null;
        var authentication = services.GetService<AuthenticationStateProvider>();
        if (authentication is not null)
        {
            try
            {
                user = (await authentication.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            }
            catch (InvalidOperationException)
            {
                // No authentication state outside an interactive circuit: treat the caller as anonymous.
            }
        }

        return await evaluator.EvaluateUserAsync(user).ConfigureAwait(false);
    }
}
