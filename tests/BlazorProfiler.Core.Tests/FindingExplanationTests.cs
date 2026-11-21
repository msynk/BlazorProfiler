using BlazorProfiler.Api;
using BlazorProfiler.Events;

namespace BlazorProfiler.Core.Tests;

public sealed class FindingExplanationTests
{
    private sealed class RecordingExplainer(Func<FindingExplanationRequest, string>? answer = null) : IFindingExplainer
    {
        public FindingExplanationRequest? Last { get; private set; }

        public string Name => "test model";

        public Task<string> ExplainAsync(FindingExplanationRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(answer?.Invoke(request) ?? "Use @key.");
        }
    }

    private static (ProfilerHost Host, ProfilerApi Api, string SessionId, string Fingerprint) Create(IFindingExplainer? explainer)
    {
        var (host, session, _) = TestHost.WithSession(o => o.Instrumentation.TrackRenderOutput = true);

        // 200 renders of one component in a few seconds (BP1001, about the component).
        for (var i = 0; i < 200; i++)
        {
            host.Emit(Ev.Render("App.Shared.Row", i * 10, 1, instance: 1));
            host.Emit(Ev.Parameters("App.Shared.Row", i * 10, state: i % 2 == 0 ? ParameterChangeState.Changed : ParameterChangeState.ChangedByReferenceOnly, source: InstrumentationSource.ComponentInstrumentation, changed: ["Item"]));
        }

        var api = new ProfilerApi(host, "server") { Explainer = explainer };
        var finding = host.Diagnostics.Analyze(session).Findings.First(f => f.ComponentType == "App.Shared.Row");
        return (host, api, session.Id, finding.Fingerprint);
    }

    [Fact]
    public async Task SendsTheFindingAndItsComponentContext()
    {
        var explainer = new RecordingExplainer();
        var (host, api, sessionId, fingerprint) = Create(explainer);
        using var owner = host;

        Assert.True(api.GetInfo(ProfilerApiAccess.Full).CanExplainFindings);
        Assert.Equal("test model", api.GetInfo(ProfilerApiAccess.Full).FindingExplainer);

        var explanation = await api.ExplainFindingAsync(ProfilerApiAccess.Full, sessionId, fingerprint);
        Assert.Equal("Use @key.", explanation.Text);
        Assert.Equal("test model", explanation.Source);
        Assert.Equal(fingerprint, explanation.Fingerprint);

        var request = explainer.Last!;
        Assert.Equal(FindingPrompt.System, request.SystemPrompt);
        Assert.Contains("not instructions", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Blazor Server", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(request.Finding.RuleId, request.Prompt, StringComparison.Ordinal);
        Assert.Contains("## Component App.Shared.Row", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("changed by reference only", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Most often changed parameters: Item", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsUnavailableWithoutAnExplainerOrControlAccess()
    {
        var (host, api, sessionId, fingerprint) = Create(null);
        using var owner = host;
        Assert.False(api.GetInfo(ProfilerApiAccess.Full).CanExplainFindings);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProfilerApiException>(() => api.ExplainFindingAsync(ProfilerApiAccess.Full, sessionId, fingerprint))).StatusCode);

        var (host2, api2, sessionId2, fingerprint2) = Create(new RecordingExplainer());
        using var owner2 = host2;
        var viewer = new ProfilerApiAccess { CanViewAllCircuits = true, CanControl = false };
        Assert.False(api2.GetInfo(viewer).CanExplainFindings);
        Assert.Null(api2.GetInfo(viewer).FindingExplainer);
        Assert.Equal(403, (await Assert.ThrowsAsync<ProfilerApiException>(() => api2.ExplainFindingAsync(viewer, sessionId2, fingerprint2))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<ProfilerApiException>(() => api2.ExplainFindingAsync(ProfilerApiAccess.Full, sessionId2, "BP9999:none"))).StatusCode);
    }

    [Fact]
    public async Task HidesExplainerErrorsAndCutsLongAnswers()
    {
        var (host, api, sessionId, fingerprint) = Create(new RecordingExplainer(_ => throw new InvalidOperationException("https://secret.example/deployments/x returned 401")));
        using var owner = host;
        var error = await Assert.ThrowsAsync<ProfilerApiException>(() => api.ExplainFindingAsync(ProfilerApiAccess.Full, sessionId, fingerprint));
        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", error.Message, StringComparison.Ordinal);

        var (host2, api2, sessionId2, fingerprint2) = Create(new RecordingExplainer(_ => new string('x', ProfilerApi.MaxExplanationLength + 500)));
        using var owner2 = host2;
        Assert.Equal(ProfilerApi.MaxExplanationLength, (await api2.ExplainFindingAsync(ProfilerApiAccess.Full, sessionId2, fingerprint2)).Text.Length);

        // The explainer's own timeout (HttpClient reports it as a cancellation) is a failure, not a cancelled request.
        var (host3, api3, sessionId3, fingerprint3) = Create(new RecordingExplainer(_ => throw new TaskCanceledException("The request timed out.")));
        using var owner3 = host3;
        Assert.Equal(502, (await Assert.ThrowsAsync<ProfilerApiException>(() => api3.ExplainFindingAsync(ProfilerApiAccess.Full, sessionId3, fingerprint3))).StatusCode);

        // The caller's cancellation is not.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var (host4, api4, sessionId4, fingerprint4) = Create(new RecordingExplainer(_ => throw new OperationCanceledException(cancelled.Token)));
        using var owner4 = host4;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api4.ExplainFindingAsync(ProfilerApiAccess.Full, sessionId4, fingerprint4, cancelled.Token));
    }

    [Fact]
    public async Task BridgeRequestsReachTheExplainer()
    {
        var explainer = new RecordingExplainer();
        var (host, api, sessionId, fingerprint) = Create(explainer);
        using var owner = host;
        var response = await api.HandleAsync(new ProfilerApiRequest { Op = "explain", SessionId = sessionId, Fingerprint = fingerprint }, ProfilerApiAccess.Full);
        Assert.True(response.Ok);
        Assert.Equal("Use @key.", response.Explanation!.Text);
    }

    [Fact]
    public void PromptIsCutAtTheMaximumLength()
    {
        var finding = new Analysis.DiagnosticFinding
        {
            RuleId = "BP0000",
            Title = "Long",
            Summary = new string('s', FindingPrompt.MaxPromptLength * 2),
            Explanation = "e",
            Recommendation = "r",
        };
        var prompt = FindingPrompt.Build(finding, null, "webassembly");
        Assert.True(prompt.Length <= FindingPrompt.MaxPromptLength + 10);
        Assert.StartsWith("Hosting model: Blazor WebAssembly", prompt, StringComparison.Ordinal);
        Assert.EndsWith("[cut]", prompt, StringComparison.Ordinal);
    }
}
