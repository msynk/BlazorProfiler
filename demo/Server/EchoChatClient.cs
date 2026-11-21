using Microsoft.Extensions.AI;

namespace BlazorProfiler.Demo.Server;

/// <summary>
/// A stand-in for a language model, used when <c>Demo:EchoAssistant</c> is <c>true</c> (for example in end-to-end tests):
/// it answers with the start of the prompt it received, so the dashboard's <em>Explain with AI</em> flow can be tried
/// without a model. In an app, register a real <see cref="IChatClient"/> instead (see the getting-started guide).
/// </summary>
internal sealed class EchoChatClient : IChatClient
{
    private static readonly ChatClientMetadata s_metadata = new("demo-echo", defaultModelId: "demo echo assistant");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var text = "This demo assistant repeats what it was asked instead of explaining it.\n\n" + (prompt.Length > 600 ? prompt[..600] + "…" : prompt);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ChatClientMetadata) ? s_metadata : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
