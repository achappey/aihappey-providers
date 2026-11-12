using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => ToTextApiResponse(await ExecuteUnifiedAsync(
            NativeOptions(options.ToUnifiedRequest(GetIdentifier()), options.AdditionalProperties), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in TextApiStream(NativeOptions(options.ToUnifiedRequest(GetIdentifier()), options.AdditionalProperties), cancellationToken)
            .ToChatCompletionUpdates(cancellationToken))
            yield return part;
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(NativeOptions(options.ToUnifiedRequest(GetIdentifier()), options.AdditionalProperties), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(NativeOptions(options.ToUnifiedRequest(GetIdentifier()), options.AdditionalProperties), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => ToTextApiResponse(await ExecuteUnifiedAsync(
            NativeOptions(request.ToUnifiedRequest(GetIdentifier()), request.AdditionalProperties, headers), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in TextApiStream(NativeOptions(request.ToUnifiedRequest(GetIdentifier()), request.AdditionalProperties, headers), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    // Native metadata is application data. Only native extension fields are DeepL options;
    // the general-purpose mappers also capture standard endpoint-specific root fields.
    private static AIRequest NativeOptions(AIRequest request, Dictionary<string, JsonElement>? additional,
        Dictionary<string, string>? headers = null)
        => new()
        {
            ProviderId = request.ProviderId, Model = request.Model, Id = request.Id,
            Input = request.Input, Instructions = request.Instructions, Tools = request.Tools,
            Metadata = new() { ["deepl"] = JsonSerializer.SerializeToElement(additional ?? []) },
            Headers = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase)
                .Concat(request.Headers ?? []).GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase)
        };

    // Chat Completions and Messages have no general binary assistant-output block.
    // Use standard text containing an inline download link, never a remote result URL.
    private static string FileLink(string? filename, string data)
        => $"\n[{(filename ?? "translated-document").Replace("[", "\\[").Replace("]", "\\]")}]({data})\n";

    private static AIResponse ToTextApiResponse(AIResponse response)
        => new()
        {
            ProviderId = response.ProviderId, Model = response.Model, Status = response.Status,
            Metadata = response.Metadata, Usage = response.Usage,
            Output = new AIOutput
            {
                Items = response.Output?.Items?.Select(item => new AIOutputItem
                {
                    Type = item.Type, Role = item.Role, Metadata = item.Metadata,
                    Content = item.Content?.Select(part => part is AIFileContentPart file
                        ? (AIContentPart)new AITextContentPart { Type = "text", Text = FileLink(file.Filename, (string)file.Data!), Metadata = file.Metadata }
                        : part).ToList()
                }).ToList()
            }
        };

    private async IAsyncEnumerable<AIStreamEvent> TextApiStream(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
        {
            if (part.Event.Data is not AIFileEventData file)
            {
                yield return part;
                continue;
            }
            yield return StreamEvent(part.Event.Id!, "text-start", new AITextStartEventData());
            yield return StreamEvent(part.Event.Id!, "text-delta", new AITextDeltaEventData { Delta = FileLink(file.Filename, file.Url) });
            yield return StreamEvent(part.Event.Id!, "text-end", new AITextEndEventData());
        }
    }
}
