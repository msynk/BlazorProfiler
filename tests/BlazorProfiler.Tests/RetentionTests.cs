using System.Runtime.CompilerServices;
using BlazorProfiler.Analysis;
using BlazorProfiler.Api;
using BlazorProfiler.Events;
using BlazorProfiler.Tests.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BlazorProfiler.Tests;

// Uses static holders, so the tests in this class must not run in parallel with each other.
[Collection(nameof(RetentionTests))]
public class RetentionTests
{
    private static ProfilerFixture CreateFixture() => ProfilerFixture.Create(o =>
    {
        o.Collectors.RetentionMinimumAge = TimeSpan.Zero;
        o.Collectors.RetentionGcCycles = 1;
    });

    [Fact]
    public async Task ReportsLeakingInstrumentedComponentsAndSeparatesRendererRetention()
    {
        LeakyChild.Holder.Clear();
        await using var f = CreateFixture();
        var id = await RenderAndForget<LeakHost>(f.Renderer);

        // Show and remove the children three times: the leaking child accumulates, the other child does not.
        await f.Renderer.SetParametersAsync(id, new() { ["Show"] = false });
        await f.Renderer.SetParametersAsync(id, new() { ["Show"] = true });
        await f.Renderer.SetParametersAsync(id, new() { ["Show"] = false });
        await f.Renderer.SetParametersAsync(id, new() { ["Show"] = true });
        await f.Renderer.SetParametersAsync(id, new() { ["Show"] = false });

        var result = await RunLeakCheck(f);

        var retained = f.All<ComponentRetainedEvent>();
        var leaky = retained.Where(e => e.ComponentType == typeof(LeakyChild).FullName).ToList();
        Assert.Equal(3, leaky.Count);
        Assert.All(leaky, e => Assert.Equal(RetentionReason.Disposed, e.Reason));
        Assert.All(leaky, e => Assert.True(e.ForcedCollection));

        // Blazor's renderer keeps the parent's previous render tree, which can reference the last removed child: at most one.
        Assert.True(retained.Count(e => e.ComponentType == typeof(ProfiledChild).FullName) <= 1);
        Assert.Equal(retained.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Values["retained"]);
        Assert.Equal(3, f.Session.GetSummary().Components.Single(c => c.ComponentType == typeof(LeakyChild).FullName).Retained);

        var findings = f.Host.Diagnostics.Analyze(f.Session).Findings.Where(x => x.RuleId == "BP5004").ToList();
        var leak = Assert.Single(findings, x => x.ComponentType == typeof(LeakyChild).FullName);
        Assert.Equal(DiagnosticConfidence.High, leak.Confidence);
        Assert.True(leak.Severity >= DiagnosticSeverity.Medium);
        Assert.All(findings.Where(x => x.ComponentType == typeof(ProfiledChild).FullName), x => Assert.Equal(DiagnosticSeverity.Info, x.Severity));
        LeakyChild.Holder.Clear();
    }

    [Fact]
    public async Task ReportsComponentsOfAnEndedScopeThatAreStillReachable()
    {
        PlainLeaky.Holder.Clear();
        await using var f = CreateFixture();
        await RenderInOwnScopeAndDispose(f);

        await RunLeakCheck(f);

        var retained = Assert.Single(f.All<ComponentRetainedEvent>());
        Assert.Equal(typeof(PlainLeaky).FullName, retained.ComponentType);
        Assert.Equal(RetentionReason.ScopeEnded, retained.Reason);
        var finding = Assert.Single(f.Host.Diagnostics.Analyze(f.Session).Findings, x => x.RuleId == "BP5004");
        Assert.NotEqual(DiagnosticSeverity.Info, finding.Severity);
        PlainLeaky.Holder.Clear();
    }

    [Fact]
    public async Task ReportsEachInstanceOnlyOnce()
    {
        LeakyChild.Holder.Clear();
        await using var f = CreateFixture();
        var id = await RenderAndForget<LeakHost>(f.Renderer);
        await f.Renderer.SetParametersAsync(id, new() { ["Show"] = false });

        await RunLeakCheck(f);
        var first = f.All<ComponentRetainedEvent>().Count;
        await RunLeakCheck(f);

        Assert.True(first >= 1);
        Assert.Equal(first, f.All<ComponentRetainedEvent>().Count);
        LeakyChild.Holder.Clear();
    }

    [Fact]
    public async Task DoesNothingWhenRetentionTrackingIsDisabled()
    {
        LeakyChild.Holder.Clear();
        await using var f = ProfilerFixture.Create(o => o.Collectors.ComponentRetention = false);
        var id = await RenderAndForget<LeakHost>(f.Renderer);
        await f.Renderer.SetParametersAsync(id, new() { ["Show"] = false });

        var api = f.Root.GetRequiredService<ProfilerApi>();
        await Assert.ThrowsAsync<ProfilerApiException>(() => api.ExecuteCommandAsync(ProfilerApiAccess.Full, "check-leaks", null));
        Assert.Empty(f.All<ComponentRetainedEvent>());
        LeakyChild.Holder.Clear();
    }

    [Fact]
    public async Task LeakCheckIsListedAndRequiresAccessToAllCircuits()
    {
        await using var f = CreateFixture();
        var api = f.Root.GetRequiredService<ProfilerApi>();
        Assert.Contains(api.GetInfo(ProfilerApiAccess.Full).Commands, c => c.Name == "check-leaks");

        var restricted = new ProfilerApiAccess { CanViewAllCircuits = false, CanControl = true };
        Assert.DoesNotContain(api.GetInfo(restricted).Commands, c => c.Name == "check-leaks");
        var error = await Assert.ThrowsAsync<ProfilerApiException>(() => api.ExecuteCommandAsync(restricted, "check-leaks", null));
        Assert.Equal(403, error.StatusCode);

        var readOnly = new ProfilerApiAccess { CanViewAllCircuits = true, CanControl = false };
        Assert.Empty(api.GetInfo(readOnly).Commands);
    }

    private static async Task<ProfilerCommandResult> RunLeakCheck(ProfilerFixture f)
    {
        var api = f.Root.GetRequiredService<ProfilerApi>();
        var result = await api.ExecuteCommandAsync(ProfilerApiAccess.Full, "check-leaks", null);
        await f.Host.FlushAsync();
        return result;
    }

    // The component reference stays in this frame only, so it cannot keep the instance alive in the caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RenderAndForget<T>(TestRenderer renderer)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        var (_, id) = await renderer.RenderAsync<T>();
        return id;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RenderInOwnScopeAndDispose(ProfilerFixture f)
    {
        var scope = f.Root.CreateScope();
        var renderer = new TestRenderer(scope.ServiceProvider);
        await RenderAndForget<PlainLeaky>(renderer);
        await renderer.DisposeAsync();
        scope.Dispose();
    }
}
