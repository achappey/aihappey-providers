using AIHappey.Core.AI;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "openai/v1/chat/completions",
             requestHeaders: GetAuthHeaders(),
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "openai/v1/chat/completions",
                    requestHeaders: GetAuthHeaders(),
                    cancellationToken: cancellationToken);
    }
}