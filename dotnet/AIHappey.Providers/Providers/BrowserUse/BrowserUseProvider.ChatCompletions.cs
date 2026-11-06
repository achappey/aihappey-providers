using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = StreamUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        await foreach (var update in stream.ToChatCompletionUpdates(cancellationToken))
        {
            yield return update;
        }
    }
}
