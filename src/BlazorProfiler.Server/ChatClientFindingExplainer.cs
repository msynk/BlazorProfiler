using BlazorProfiler.Api;
using Microsoft.Extensions.AI;

namespace BlazorProfiler.Server;

/// <summary>Explains findings with a language model through <see cref="IChatClient"/> (Microsoft.Extensions.AI).</summary>
/// <remarks>Registered by <see cref="ProfilerServerOptions.AiExplanations"/>; see there for how the chat client is chosen.</remarks>
public sealed class ChatClientFindingExplainer : IFindingExplainer
{
    private readonly IChatClient _client;

    /// <summary>Creates the explainer.</summary>
    /// <param name="client">The chat client.</param>
    public ChatClientFindingExplainer(IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        var metadata = client.GetService<ChatClientMetadata>();
        Name = metadata?.DefaultModelId ?? metadata?.ProviderName ?? "language model";
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async Task<string> ExplainAsync(FindingExplanationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ChatMessage> messages = [new(ChatRole.System, request.SystemPrompt), new(ChatRole.User, request.Prompt)];
        var response = await _client.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 1_500, Temperature = 0.2f }, cancellationToken).ConfigureAwait(false);
        return response.Text;
    }
}
