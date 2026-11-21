using BlazorProfiler.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BlazorProfiler.Server.Tests;

public sealed class FindingExplainerTests
{
    private sealed class FakeChatClient(string model) : IChatClient
    {
        public List<ChatMessage> Received { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Received.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"answer from {model}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake", defaultModelId: model) : null;

        public void Dispose()
        {
        }
    }

    private static async Task<WebApplication> CreateAppAsync(bool enabled, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddBlazorProfiler(o => o.Collectors.Runtime = false, server => server.AiExplanations = enabled);
        services?.Invoke(builder.Services);
        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task IsOffByDefaultEvenWithAChatClient()
    {
        await using var app = await CreateAppAsync(enabled: false, s => s.AddSingleton<IChatClient>(new FakeChatClient("app")));
        Assert.Null(app.Services.GetRequiredService<ProfilerApi>().Explainer);
    }

    [Fact]
    public async Task IsUnavailableWithoutAChatClient()
    {
        await using var app = await CreateAppAsync(enabled: true);
        var api = app.Services.GetRequiredService<ProfilerApi>();
        Assert.Null(api.Explainer);
        Assert.False(api.GetInfo(ProfilerApiAccess.Full).CanExplainFindings);
    }

    [Fact]
    public async Task PrefersTheProfilersKeyedChatClient()
    {
        var dedicated = new FakeChatClient("dedicated");
        await using var app = await CreateAppAsync(enabled: true, s =>
        {
            s.AddSingleton<IChatClient>(new FakeChatClient("app"));
            s.AddKeyedSingleton<IChatClient>(ProfilerServerOptions.ChatClientKey, dedicated);
        });
        var explainer = app.Services.GetRequiredService<ProfilerApi>().Explainer!;
        Assert.Equal("dedicated", explainer.Name);

        var answer = await explainer.ExplainAsync(
            new FindingExplanationRequest { Finding = new Analysis.DiagnosticFinding { RuleId = "BP1001", Title = "t", Summary = "s", Explanation = "e", Recommendation = "r" }, SystemPrompt = "system", Prompt = "prompt" },
            CancellationToken.None);
        Assert.Equal("answer from dedicated", answer);
        Assert.Equal([ChatRole.System, ChatRole.User], dedicated.Received.Select(m => m.Role));
        Assert.Equal(["system", "prompt"], dedicated.Received.Select(m => m.Text));
    }

    [Fact]
    public async Task FallsBackToTheAppsChatClient()
    {
        await using var app = await CreateAppAsync(enabled: true, s => s.AddSingleton<IChatClient>(new FakeChatClient("app")));
        Assert.Equal("app", app.Services.GetRequiredService<ProfilerApi>().Explainer!.Name);
    }
}
