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
    /// A request only counts as local when it arrives on a loopback connection (or from the machine's own address),
    /// addresses the app by a local host name, does not use HTTP/1.0, reports only this machine as the client in any
    /// forwarding headers (<c>Forwarded</c>, <c>X-Forwarded-For</c>, and similar; local hops such as IIS Express qualify),
    /// and, when sent by a browser, comes from a page on a local host name. Traffic that relays remote clients through a
    /// reverse proxy or a tunnel (for example Dev Tunnels, ngrok, or nginx) is therefore rejected, except for non-browser
    /// clients behind a relay that adds no forwarding headers and rewrites <c>Host</c>, which cannot be told apart from local
    /// ones. Configure <see cref="AuthorizationPolicy"/> when the app is reachable from other machines.
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
    /// <remarks>
    /// A retained session that a dashboard has shown within <see cref="RetainedSessionViewWindow"/> is never removed
    /// automatically, so it cannot disappear while someone inspects it; the limit is exceeded until it is no longer viewed.
    /// Sessions can always be removed explicitly.
    /// </remarks>
    public int RetainedCircuitSessions { get; set; } = 4;

    /// <summary>
    /// How long after a dashboard last read a retained circuit session it is protected from automatic removal (see
    /// <see cref="RetainedCircuitSessions"/>).
    /// </summary>
    public static TimeSpan RetainedSessionViewWindow { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The maximum size of an uploaded trace.</summary>
    public long MaxImportBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// A delay added to every message from the browser to Blazor Server circuits, to reproduce a slow connection during
    /// development (see <see cref="LatencySimulator"/>). Zero (the default) turns simulation off; it can also be changed at
    /// runtime with the <c>simulate-latency</c> command. Ignored in the Production environment.
    /// </summary>
    public TimeSpan SimulatedLatency { get; set; }
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
            services.TryAddSingleton<LatencySimulator>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IProfilerCommand, SimulateLatencyCommand>());
            services.AddSingleton<ProfilerHubFilter>(sp => new ProfilerHubFilter(sp.GetRequiredService<LatencySimulator>()));
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

    // Headers in which proxies, tunnels, and CDNs report the client's address (X-Original-For is written by
    // ForwardedHeadersMiddleware with the proxy's address after it consumed X-Forwarded-For).
    private static readonly string[] s_clientAddressHeaders =
    [
        "X-Forwarded-For",
        "X-Original-For",
        "X-Real-IP",
        "X-Client-IP",
        "X-Cluster-Client-IP",
        "True-Client-IP",
        "CF-Connecting-IP",
        "Fastly-Client-IP",
        "X-Azure-ClientIP",
        "X-Azure-SocketIP",
    ];

    // Headers in which relays report the host name the client used.
    private static readonly string[] s_clientHostHeaders = ["X-Forwarded-Host", "X-Original-Host"];

    // Headers that show a request was relayed without saying by whom it was sent.
    private static readonly string[] s_relayHeaders = ["Via", "X-Forwarded-Server", "X-Forwarded-Proto", "X-Original-Proto"];

    private static readonly Lazy<string?> s_fullyQualifiedHostName = new(GetFullyQualifiedHostName);

    /// <summary>
    /// Returns whether a request comes from a browser on this machine. A loopback connection alone is not enough: a local
    /// reverse proxy or tunnel (Dev Tunnels, ngrok, nginx) also connects over loopback while relaying remote clients.
    /// </summary>
    /// <remarks>
    /// <para>A request counts as local only when all of the following hold:</para>
    /// <list type="bullet">
    /// <item>It does not use HTTP/1.0, which browsers never send but proxies do by default (nginx's <c>proxy_pass</c>).</item>
    /// <item>It addresses the app by a local host name: <c>localhost</c>, the machine name, a loopback or local address.</item>
    /// <item>
    /// When it carries forwarding headers, every client address they report is this machine and every reported host is a
    /// local name. This accepts local hops such as IIS or IIS Express out-of-process hosting, and rejects relays of remote
    /// clients. A relay that reports no client address (<c>Via</c> alone, for example) is rejected.
    /// </item>
    /// <item>
    /// The <c>Origin</c> and, for same-origin requests, <c>Referer</c> headers sent by browsers name a local host, so a remote
    /// browser is rejected even behind a relay that strips forwarding headers and rewrites <c>Host</c>.
    /// </item>
    /// <item>The connection comes from a loopback address, or from the address it arrived on.</item>
    /// </list>
    /// <para>
    /// A non-browser client behind a relay that speaks HTTP/1.1, adds no forwarding headers, and rewrites <c>Host</c> to a
    /// local name remains indistinguishable from a local one; the grant only applies in Development for that reason.
    /// </para>
    /// </remarks>
    internal static bool IsLocal(HttpContext context)
    {
        var request = context.Request;
        if (HttpProtocol.IsHttp10(request.Protocol))
        {
            return false;
        }

        var local = Normalize(context.Connection.LocalIpAddress);
        if (!IsLocalHostName(request.Host.Host, local) || !IsRelayedFromLocalClient(request.Headers, local) || !IsLocalBrowserOrigin(request.Headers, local))
        {
            return false;
        }

        var remote = Normalize(context.Connection.RemoteIpAddress);
        if (remote is null)
        {
            // In-memory test servers have no remote address.
            return local is null;
        }

        return IsLocalAddress(remote, local);
    }

    /// <summary>
    /// Returns whether forwarding headers, if any, report only this machine as the client: the request is either direct or
    /// relayed by a local hop on behalf of a local browser.
    /// </summary>
    private static bool IsRelayedFromLocalClient(IHeaderDictionary headers, IPAddress? local)
    {
        var relayed = false;
        var clientReported = false;
        foreach (var name in s_clientAddressHeaders)
        {
            if (!headers.TryGetValue(name, out var values))
            {
                continue;
            }

            relayed = clientReported = true;
            foreach (var value in Split(values))
            {
                if (!TryParseAddress(value, out var address) || !IsLocalAddress(address, local))
                {
                    return false;
                }
            }
        }

        if (headers.TryGetValue("Forwarded", out var forwarded))
        {
            // RFC 7239: comma-separated elements of semicolon-separated pairs, for example for="[::1]:1234";host=localhost.
            relayed = true;
            foreach (var element in Split(forwarded))
            {
                foreach (var pair in element.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = pair.IndexOf('=', StringComparison.Ordinal);
                    var key = separator < 0 ? pair : pair[..separator].Trim();
                    var value = separator < 0 ? string.Empty : pair[(separator + 1)..].Trim().Trim('"');
                    if (key.Equals("for", StringComparison.OrdinalIgnoreCase))
                    {
                        clientReported = true;
                        if (!TryParseAddress(value, out var address) || !IsLocalAddress(address, local))
                        {
                            return false;
                        }
                    }
                    else if (key.Equals("host", StringComparison.OrdinalIgnoreCase) && !IsLocalHostName(HostWithoutPort(value), local))
                    {
                        return false;
                    }
                }
            }
        }

        foreach (var name in s_clientHostHeaders)
        {
            if (!headers.TryGetValue(name, out var values))
            {
                continue;
            }

            relayed = true;
            foreach (var value in Split(values))
            {
                if (!IsLocalHostName(HostWithoutPort(value), local))
                {
                    return false;
                }
            }
        }

        foreach (var name in s_relayHeaders)
        {
            relayed |= headers.ContainsKey(name);
        }

        // A relayed request must say who its client is; otherwise it may come from anywhere.
        return !relayed || clientReported;
    }

    /// <summary>
    /// Returns whether the page that sent the request (as reported by the browser) was loaded from a local host name. Absent
    /// headers pass: non-browser clients do not send them, and a navigation from another site carries that site's referrer.
    /// </summary>
    private static bool IsLocalBrowserOrigin(IHeaderDictionary headers, IPAddress? local)
    {
        if (headers.TryGetValue("Origin", out var origin) && !IsLocalUrl(origin.ToString(), local))
        {
            return false;
        }

        // The referrer of a same-origin request is the page's own URL, so its host is the one the browser used. Cross-site
        // navigations (for example a link to the dashboard from another site) are not checked.
        var site = headers.TryGetValue("Sec-Fetch-Site", out var fetchSite) ? fetchSite.ToString() : null;
        if ((site is null || site.Equals("same-origin", StringComparison.OrdinalIgnoreCase))
            && headers.TryGetValue("Referer", out var referer)
            && !IsLocalUrl(referer.ToString(), local))
        {
            return false;
        }

        return true;
    }

    private static bool IsLocalUrl(string value, IPAddress? local) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && IsLocalHostName(uri.Host, local);

    private static bool IsLocalHostName(string? host, IPAddress? localAddress)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        host = host.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || (s_fullyQualifiedHostName.Value is { } fqdn && host.Equals(fqdn, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Browsers connect to the unspecified address (0.0.0.0, [::]) on the local machine.
        return IPAddress.TryParse(host.Trim('[', ']'), out var address)
            && (IsLocalAddress(Normalize(address)!, localAddress) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));
    }

    private static bool IsLocalAddress(IPAddress address, IPAddress? local) =>
        IPAddress.IsLoopback(address) || (local is not null && address.Equals(local));

    private static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    private static IEnumerable<string> Split(Microsoft.Extensions.Primitives.StringValues values)
    {
        foreach (var value in values)
        {
            foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                yield return part;
            }
        }
    }

    /// <summary>Parses an address as reported by a proxy: <c>1.2.3.4</c>, <c>1.2.3.4:80</c>, <c>::1</c>, or <c>[::1]:80</c>.</summary>
    private static bool TryParseAddress(string value, out IPAddress address)
    {
        value = value.Trim().Trim('"');
        if (IPEndPoint.TryParse(value, out var endpoint))
        {
            address = Normalize(endpoint.Address)!;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private static string HostWithoutPort(string value)
    {
        value = value.Trim().Trim('"');
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']', StringComparison.Ordinal);
            return end < 0 ? value : value[..(end + 1)];
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 || value.IndexOf(':', colon + 1) >= 0 ? value : value[..colon];
    }

    private static string? GetFullyQualifiedHostName()
    {
        try
        {
            var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            return string.IsNullOrEmpty(properties.DomainName) ? null : properties.HostName + "." + properties.DomainName.TrimEnd('.');
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return null;
        }
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
