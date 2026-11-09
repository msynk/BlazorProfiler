using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorProfiler.Api;
using BlazorProfiler.Browser;
using BlazorProfiler.Events;
using BlazorProfiler.Server;
using BlazorProfiler.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlazorProfiler.Server.Tests;

public sealed class ServerAccessTests
{
    private static async Task<WebApplication> CreateAppAsync(string environment, bool? enabled, Action<ProfilerServerOptions>? server = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("Developers", p => p.RequireRole("Developer"));
            o.AddPolicy("Users", p => p.RequireAuthenticatedUser());
        });
        builder.Services.AddScoped<FixedAuthenticationStateProvider>();
        builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<FixedAuthenticationStateProvider>());
        builder.AddBlazorProfiler(
            o =>
            {
                o.Enabled = enabled;
                o.Collectors.Runtime = false;
                o.Pipeline.SynchronousProcessing = true;
            },
            server);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBlazorProfiler();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, string? user = null, string? role = null)
    {
        var client = app.GetTestClient();
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User", user);
        }

        if (role is not null)
        {
            client.DefaultRequestHeaders.Add("X-Test-Role", role);
        }

        return client;
    }

    private static HttpRequestMessage Post(string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add(ProfilerEndpoints.RequestHeader, "1");
        return request;
    }

    [Fact]
    public async Task CommandsRunOverHttpAndLatencySimulationIsValidated()
    {
        await using var app = await CreateAppAsync("Development", enabled: null);

        // SignalR builds the hub filter's factory when hub options are created; it rejects ambiguous constructors.
        Assert.NotNull(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.SignalR.HubOptions>>().Value);
        var client = Client(app);
        var info = await client.GetFromJsonAsync<JsonElement>("/_blazor-profiler/api/info");
        var names = info.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("check-leaks", names);
        Assert.Contains("simulate-latency", names);

        using (var response = await client.SendAsync(Post("/_blazor-profiler/api/commands/simulate-latency?argument=150")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("150", result.GetProperty("values").GetProperty("delayMs").GetString());
        }

        Assert.Equal(TimeSpan.FromMilliseconds(150), app.Services.GetRequiredService<LatencySimulator>().Delay);
        using (var invalid = await client.SendAsync(Post("/_blazor-profiler/api/commands/simulate-latency?argument=99999")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        using (var unknown = await client.SendAsync(Post("/_blazor-profiler/api/commands/nope")))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        // Commands change state: like every other mutation they require the CSRF header.
        using var withoutHeader = await client.PostAsync("/_blazor-profiler/api/commands/simulate-latency?argument=0", null);
        Assert.Equal(HttpStatusCode.BadRequest, withoutHeader.StatusCode);
        Assert.Equal(TimeSpan.FromMilliseconds(150), app.Services.GetRequiredService<LatencySimulator>().Delay);
    }

    [Fact]
    public void LatencySimulationIsNeverActiveInProduction()
    {
        var simulator = new LatencySimulator(new ProfilerServerOptions { SimulatedLatency = TimeSpan.FromMilliseconds(200) }, new TestEnvironment("Production"));
        Assert.False(simulator.IsAllowed);
        Assert.Equal(TimeSpan.Zero, simulator.Delay);

        var development = new LatencySimulator(new ProfilerServerOptions { SimulatedLatency = TimeSpan.FromMilliseconds(200) }, new TestEnvironment("Development"));
        Assert.Equal(TimeSpan.FromMilliseconds(200), development.Delay);
        Assert.Throws<ArgumentOutOfRangeException>(() => development.Delay = TimeSpan.FromSeconds(10));
    }

    private sealed class TestEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task DevelopmentAllowsLocalRequestsWithoutPolicy()
    {
        await using var app = await CreateAppAsync("Development", enabled: null);
        using var response = await Client(app).GetAsync("/_blazor-profiler/api/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(info.GetProperty("canViewAllCircuits").GetBoolean());
        Assert.Equal("server", info.GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.7")]
    [InlineData("Forwarded", "for=203.0.113.7")]
    [InlineData("X-Forwarded-Host", "abc.devtunnels.ms")]
    public async Task DevelopmentLoopbackGrantIgnoresRelayedRequests(string header, string value)
    {
        // Tunnels and local reverse proxies connect over loopback while relaying remote clients.
        await using var app = await CreateAppAsync("Development", enabled: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_blazor-profiler/api/info");
        request.Headers.TryAddWithoutValidation(header, value);
        using var response = await Client(app).SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DevelopmentLoopbackGrantRequiresALocalHostName()
    {
        await using var app = await CreateAppAsync("Development", enabled: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_blazor-profiler/api/info");
        request.Headers.Host = "my-app.ngrok.app";
        using var response = await Client(app).SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BridgeAccessFollowsThePoliciesOfTheCircuitUser()
    {
        await using var app = await CreateAppAsync("Development", enabled: true, s =>
        {
            s.AuthorizationPolicy = "Developers";
            s.OwnCircuitPolicy = "Users";
        });
        app.Services.GetRequiredService<CircuitRegistry>().Register("circuit-alice", null, "alice", DateTimeOffset.UtcNow);

        static async Task<ProfilerApiAccess?> AccessForAsync(WebApplication app, ClaimsPrincipal user)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var state = scope.ServiceProvider.GetRequiredService<FixedAuthenticationStateProvider>();
            state.User = user;
            return await scope.ServiceProvider.GetRequiredService<IProfilerBridgeAccessProvider>().GetAccessAsync();
        }

        // Anonymous users get nothing, even in Development: the bridge cannot prove a request is local.
        Assert.Null(await AccessForAsync(app, new ClaimsPrincipal(new ClaimsIdentity())));

        var alice = await AccessForAsync(app, User("alice"));
        Assert.NotNull(alice);
        Assert.False(alice.CanViewAllCircuits);
        Assert.Equal(["circuit-alice"], alice.OwnedCircuits);

        var developer = await AccessForAsync(app, User("dev", "Developer"));
        Assert.True(developer!.CanViewAllCircuits);
    }

    [Fact]
    public void ClosedCircuitSessionsAreRetainedWithinTheLimitAndStayVisibleToTheirOwner()
    {
        var registry = new CircuitRegistry();
        var live = new HashSet<string>(StringComparer.Ordinal) { "s1", "s2", "s3" };
        static bool NotInUse(string id) => false;
        Assert.Empty(registry.RetainClosedCircuit("c1", "alice", "s1", 2, live.Contains, NotInUse));
        Assert.Empty(registry.RetainClosedCircuit("c2", "bob", "s2", 2, live.Contains, NotInUse));
        Assert.Equal(["c1"], registry.GetOwned("alice"));

        // Over the limit: the oldest retained session is returned for removal and its owner loses access to it.
        Assert.Equal(["s1"], registry.RetainClosedCircuit("c3", "alice", "s3", 2, live.Contains, NotInUse));
        Assert.Equal(["c3"], registry.GetOwned("alice"));

        // Entries of sessions removed elsewhere are discarded instead of counting toward the limit.
        live.Remove("s2");
        live.Add("s4");
        Assert.Empty(registry.RetainClosedCircuit("c4", "bob", "s4", 2, live.Contains, NotInUse));
        Assert.Equal("s3", registry.TakeOldestRetainedSession(NotInUse));
        Assert.Equal("s4", registry.TakeOldestRetainedSession(NotInUse));
        Assert.Null(registry.TakeOldestRetainedSession(NotInUse));
    }

    [Fact]
    public void RetainingNoClosedCircuitSessionsRemovesThemImmediately()
    {
        var registry = new CircuitRegistry();
        Assert.Equal(["s1"], registry.RetainClosedCircuit("c1", "alice", "s1", 0, _ => true, _ => false));
        Assert.Empty(registry.GetOwned("alice"));
    }

    [Fact]
    public void RetainedSessionsInUseAreNeverRemovedAutomatically()
    {
        // A session open in a dashboard must not disappear while someone inspects it: the limit is exceeded instead, and
        // room for new circuits is made from other sessions.
        var registry = new CircuitRegistry();
        var viewed = new HashSet<string>(StringComparer.Ordinal) { "s1" };
        Assert.Empty(registry.RetainClosedCircuit("c1", "alice", "s1", 1, _ => true, viewed.Contains));
        Assert.Equal(["s2"], registry.RetainClosedCircuit("c2", "bob", "s2", 1, _ => true, viewed.Contains));
        Assert.Equal(["c1"], registry.GetOwned("alice"));

        Assert.Empty(registry.RetainClosedCircuit("c3", "bob", "s3", 0, _ => true, id => id is "s1" or "s3"));
        Assert.Equal("s3", registry.TakeOldestRetainedSession(id => id == "s1"));
        Assert.Null(registry.TakeOldestRetainedSession(id => id == "s1"));

        // Once no longer viewed, it is removed like any other.
        Assert.Equal("s1", registry.TakeOldestRetainedSession(_ => false));
    }

    [Fact]
    public async Task ReadingASessionThroughTheApiMarksItAsViewed()
    {
        await using var app = await CreateAppAsync("Development", enabled: true);
        var profiler = app.Services.GetRequiredService<IProfiler>();
        var session = (ProfilerSession)profiler.StartSession();
        Assert.False(session.WasViewedWithin(ProfilerServerOptions.RetainedSessionViewWindow));

        using var response = await Client(app).GetAsync($"/_blazor-profiler/api/sessions/{session.Id}/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(session.WasViewedWithin(ProfilerServerOptions.RetainedSessionViewWindow));
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.7")]
    [InlineData("X-Forwarded-For", "127.0.0.1, 203.0.113.7")] // a spoofed local entry followed by the real client
    [InlineData("Forwarded", "for=203.0.113.7")]
    [InlineData("Forwarded", "proto=https")] // relayed, but the client is not reported
    [InlineData("X-Forwarded-Host", "abc.devtunnels.ms")]
    [InlineData("X-Cluster-Client-IP", "203.0.113.7")]
    [InlineData("Via", "1.1 proxy")]
    [InlineData("X-Forwarded-Server", "proxy.example.com")]
    [InlineData("X-Forwarded-For", "unknown")]
    [InlineData("Origin", "https://my-app.example.com")] // a remote browser behind a relay that rewrote Host
    [InlineData("Referer", "https://my-app.example.com/_content/BlazorProfiler/dashboard/index.html")]
    public void LoopbackRequestsRelayedFromRemoteClientsAreNotLocal(string header, string value)
    {
        var context = LoopbackContext();
        context.Request.Headers[header] = value;
        Assert.False(ProfilerAccessEvaluator.IsLocal(context));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("X-Forwarded-For", "[::1]:54321")] // IIS / IIS Express out-of-process, before ForwardedHeadersMiddleware
    [InlineData("X-Original-For", "127.0.0.1:54321")] // the same, after ForwardedHeadersMiddleware consumed the header
    [InlineData("Forwarded", "for=\"[::1]:1234\";host=localhost:5000;proto=http")]
    [InlineData("Origin", "http://localhost:5000")]
    [InlineData("Referer", "http://127.0.0.1:5000/_content/BlazorProfiler/dashboard/index.html")]
    public void LoopbackRequestsFromLocalBrowsersAreLocal(string? header, string? value)
    {
        var context = LoopbackContext();
        if (header is not null)
        {
            context.Request.Headers[header] = value;
        }

        if (header == "X-Original-For")
        {
            context.Request.Headers["X-Original-Proto"] = "http";
        }

        Assert.True(ProfilerAccessEvaluator.IsLocal(context));
    }

    [Fact]
    public void CrossSiteNavigationsAreNotJudgedByTheirReferrer()
    {
        // A link to the dashboard from another site carries that site's referrer; the browser marks it as cross-site.
        var context = LoopbackContext();
        context.Request.Headers.Referer = "https://github.com/";
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.True(ProfilerAccessEvaluator.IsLocal(context));
    }

    [Fact]
    public void Http10RequestsAreNotLocal()
    {
        // Browsers never use HTTP/1.0, but nginx's proxy_pass does by default, without forwarding headers and with Host
        // rewritten to the upstream (localhost).
        var context = LoopbackContext();
        context.Request.Protocol = "HTTP/1.0";
        Assert.False(ProfilerAccessEvaluator.IsLocal(context));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    [InlineData("192.0.2.10")] // the address the connection arrived on
    public void LocalAddressesAsHostAreLocal(string host)
    {
        var context = LoopbackContext();
        context.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.10");
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Host = new HostString(host, 5000);
        Assert.True(ProfilerAccessEvaluator.IsLocal(context));
    }

    [Fact]
    public void RemoteConnectionsAreNotLocal()
    {
        var context = LoopbackContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        Assert.False(ProfilerAccessEvaluator.IsLocal(context));
    }

    private static DefaultHttpContext LoopbackContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Host = new HostString("localhost", 5000);
        context.Connection.LocalIpAddress = IPAddress.IPv6Loopback;
        context.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        return context;
    }

    private static ClaimsPrincipal User(string name, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, name), new(ClaimTypes.Name, name) };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task ProfilerIsDisabledOutsideDevelopmentByDefault()
    {
        await using var app = await CreateAppAsync("Production", enabled: null, s => s.AuthorizationPolicy = "Developers");
        Assert.False(app.Services.GetRequiredService<IProfiler>().IsEnabled);
        using var response = await Client(app, "dev", "Developer").GetAsync("/_blazor-profiler/api/info");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EnabledInProductionWithoutPolicyIsNotAccessible()
    {
        await using var app = await CreateAppAsync("Production", enabled: true);
        Assert.True(app.Services.GetRequiredService<IProfiler>().IsEnabled);
        using var response = await Client(app, "dev", "Developer").GetAsync("/_blazor-profiler/api/info");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PolicyGrantsFullAccessOnlyToAuthorizedUsers()
    {
        await using var app = await CreateAppAsync("Production", enabled: true, s => s.AuthorizationPolicy = "Developers");
        using (var anonymous = await Client(app).GetAsync("/_blazor-profiler/api/info"))
        {
            Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);
        }

        using (var user = await Client(app, "alice").GetAsync("/_blazor-profiler/api/info"))
        {
            Assert.Equal(HttpStatusCode.NotFound, user.StatusCode);
        }

        using var developer = await Client(app, "dev", "Developer").GetAsync("/_blazor-profiler/api/info");
        Assert.Equal(HttpStatusCode.OK, developer.StatusCode);
    }

    [Fact]
    public async Task OwnCircuitAccessIsolatesUsers()
    {
        await using var app = await CreateAppAsync("Production", enabled: true, s =>
        {
            s.AuthorizationPolicy = "Developers";
            s.OwnCircuitPolicy = "Users";
        });
        var registry = app.Services.GetRequiredService<CircuitRegistry>();
        registry.Register("circuit-alice", null, "alice", DateTimeOffset.UtcNow);
        registry.Register("circuit-bob", null, "bob", DateTimeOffset.UtcNow);
        var profiler = app.Services.GetRequiredService<IProfiler>();
        var aliceSession = profiler.StartSession(new ProfilerSessionOptions { CircuitId = "circuit-alice" });
        var bobSession = profiler.StartSession(new ProfilerSessionOptions { CircuitId = "circuit-bob" });
        profiler.Emit(new CircuitEvent { CircuitId = "circuit-alice", Change = CircuitChange.Opened });
        profiler.Emit(new CircuitEvent { CircuitId = "circuit-bob", Change = CircuitChange.Opened });

        var alice = Client(app, "alice");
        var info = await alice.GetFromJsonAsync<JsonElement>("/_blazor-profiler/api/info");
        var sessions = info.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Equal([aliceSession.Id], sessions);
        Assert.False(info.GetProperty("canViewAllCircuits").GetBoolean());
        // Process-wide counters are hidden from callers restricted to their own circuits.
        Assert.Equal(0, info.GetProperty("health").GetProperty("emitted").GetInt64());

        var page = await alice.GetFromJsonAsync<JsonElement>($"/_blazor-profiler/api/sessions/{aliceSession.Id}/events");
        Assert.All(page.GetProperty("events").EnumerateArray(), e => Assert.Equal("circuit-alice", e.GetProperty("circuitId").GetString()));

        using (var bobs = await alice.GetAsync($"/_blazor-profiler/api/sessions/{bobSession.Id}/events"))
        {
            Assert.Equal(HttpStatusCode.NotFound, bobs.StatusCode);
        }

        var circuits = await alice.GetFromJsonAsync<JsonElement>("/_blazor-profiler/api/circuits");
        Assert.Equal(["circuit-alice"], circuits.EnumerateArray().Select(c => c.GetProperty("circuitId").GetString()));

        using (var processWide = await alice.SendAsync(Post("/_blazor-profiler/api/sessions", JsonContent.Create(new { name = "all" }))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, processWide.StatusCode);
        }

        using (var own = await alice.SendAsync(Post("/_blazor-profiler/api/sessions", JsonContent.Create(new { name = "mine", circuitId = "circuit-alice" }))))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }

        using (var other = await alice.SendAsync(Post("/_blazor-profiler/api/sessions", JsonContent.Create(new { name = "theirs", circuitId = "circuit-bob" }))))
        {
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }

        using var import = await alice.SendAsync(Post("/_blazor-profiler/api/import", new ByteArrayContent("{}"u8.ToArray())));
        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);
    }

    [Fact]
    public async Task StateChangingRequestsRequireTheProfilerHeader()
    {
        await using var app = await CreateAppAsync("Development", enabled: true);
        using var response = await Client(app).PostAsync("/_blazor-profiler/api/sessions", JsonContent.Create(new { name = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImportRejectsInvalidAndOversizedTraces()
    {
        await using var app = await CreateAppAsync("Development", enabled: true, s => s.MaxImportBytes = 1024);
        var client = Client(app);
        using (var invalid = await client.SendAsync(Post("/_blazor-profiler/api/import", new StringContent("{\"format\":\"nope\"}"))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("Unknown trace format", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var large = await client.SendAsync(Post("/_blazor-profiler/api/import", new ByteArrayContent(new byte[4096])));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
    }

    [Fact]
    public async Task ControlEndpointsManageSessions()
    {
        await using var app = await CreateAppAsync("Development", enabled: true);
        var client = Client(app);
        using var created = await client.SendAsync(Post("/_blazor-profiler/api/sessions", JsonContent.Create(new { name = "Manual" })));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        using (var pause = await client.SendAsync(Post($"/_blazor-profiler/api/sessions/{id}/pause")))
        {
            Assert.Equal("Paused", (await pause.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        }

        using (var resume = await client.SendAsync(Post($"/_blazor-profiler/api/sessions/{id}/resume")))
        {
            Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        }

        using (var bookmark = await client.SendAsync(Post($"/_blazor-profiler/api/sessions/{id}/bookmark?name=checkpoint")))
        {
            Assert.Equal(HttpStatusCode.OK, bookmark.StatusCode);
        }

        using (var stop = await client.SendAsync(Post($"/_blazor-profiler/api/sessions/{id}/stop")))
        {
            Assert.Equal("Stopped", (await stop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        }

        using (var invalid = await client.SendAsync(Post($"/_blazor-profiler/api/sessions/{id}/pause")))
        {
            Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        }

        using (var unknown = await client.SendAsync(Post($"/_blazor-profiler/api/sessions/{id}/explode")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        using (var summary = await client.GetAsync($"/_blazor-profiler/api/sessions/{id}/summary"))
        {
            Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        }

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/_blazor-profiler/api/sessions/{id}");
        delete.Headers.Add(ProfilerEndpoints.RequestHeader, "1");
        using (var removed = await client.SendAsync(delete))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var missing = await client.GetAsync($"/_blazor-profiler/api/sessions/{id}/summary");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task DashboardRedirectHonorsPathBase()
    {
        await using var app = await CreateAppAsync("Development", enabled: true);
        var client = app.GetTestClient();
        using var response = await client.GetAsync("/_blazor-profiler/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/_content/BlazorProfiler/dashboard/index.html?source=api&api=%2F_blazor-profiler%2Fapi", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public void CircuitCorrelationUsesTheRendererSynchronizationContext()
    {
        var registry = new CircuitRegistry();
        var context = new SynchronizationContext();
        registry.Register("c1", context, null, DateTimeOffset.UtcNow);
        var provider = new CircuitCorrelationProvider(registry);
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var batch = new RenderBatchEvent();
            provider.Enrich(batch);
            Assert.Equal("c1", batch.CircuitId);
            Assert.True(batch.IncludesClientRoundTrip);

            SynchronizationContext.SetSynchronizationContext(null);
            var ssr = new RenderBatchEvent();
            provider.Enrich(ssr);
            Assert.Null(ssr.CircuitId);
            Assert.False(ssr.IncludesClientRoundTrip);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class FixedAuthenticationStateProvider : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; set; } = new(new ClaimsIdentity());

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()), new(ClaimTypes.Name, user.ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Role", out var role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }
}
