using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlazorProfiler.Events;
using BlazorProfiler.Server;
using BlazorProfiler.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
    public async Task DevelopmentAllowsLocalRequestsWithoutPolicy()
    {
        await using var app = await CreateAppAsync("Development", enabled: null);
        using var response = await Client(app).GetAsync("/_blazor-profiler/api/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(info.GetProperty("canViewAllCircuits").GetBoolean());
        Assert.Equal("server", info.GetProperty("source").GetString());
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
