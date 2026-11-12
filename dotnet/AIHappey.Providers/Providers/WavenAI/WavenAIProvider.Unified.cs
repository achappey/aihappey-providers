using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WavenAI;

public sealed partial class WavenAIProvider
{
    private async Task<AIResponse> ExecuteNativeTranscription(AIRequest request, CancellationToken cancellationToken)
    {
        var audio = request.Input?.Items?.LastOrDefault(i => i.Role == "user")?.Content?
            .OfType<AIFileContentPart>().LastOrDefault(f => f.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new ArgumentException("WavenAI transcription requires audio in the last user message.");
        Dictionary<string, JsonElement>? options = null;
        if (request.Metadata?.TryGetValue(ProviderId, out var native) == true)
            options = new() { [ProviderId] = JsonSerializer.SerializeToElement(native) };
        var result = await TranscriptionRequest(new TranscriptionRequest
        {
            Model = LocalModel(request.Model),
            Audio = audio.Data ?? throw new ArgumentException("WavenAI transcription audio is empty."),
            MediaType = audio.MediaType!,
            ProviderOptions = options
        }, cancellationToken);
        return new AIResponse
        {
            ProviderId = ProviderId,
            Model = request.Model,
            Status = "completed",
            Metadata = new() { [ProviderId] = result.ProviderMetadata![ProviderId] },
            Output = new()
            {
                Items = [new()
                {
                    Role = "assistant",
                    Content = [new AITextContentPart { Type = "text", Text = result.Text }]
                }]
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Waven's HTTP audio routes return complete results, not SSE.
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        foreach (var part in response.Output?.Items?.SelectMany(i => i.Content ?? []) ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part is AITextContentPart text)
            {
                yield return Event("text-start", id, new AITextStartEventData(), response.Metadata);
                yield return Event("text-delta", id, new AITextDeltaEventData { Delta = text.Text }, response.Metadata);
                yield return Event("text-end", id, new AITextEndEventData(), response.Metadata);
            }
            else if (part is AIToolCallContentPart tool)
            {
                yield return Event("tool-input-available", tool.ToolCallId, new AIToolInputAvailableEventData
                {
                    ToolName = tool.ToolName!, Title = tool.Title, Input = tool.Input!, ProviderExecuted = true
                }, tool.Metadata);
                yield return Event("tool-output-available", tool.ToolCallId, new AIToolOutputAvailableEventData
                {
                    ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true, Preliminary = false
                }, tool.Metadata);
            }
        }
        yield return Event("finish", id, new AIFinishEventData
        {
            FinishReason = IsSpeech(request.Model) ? "tool-calls" : "stop",
            Model = request.Model
        }, response.Metadata);
    }

    private static AIStreamEvent Event(string type, string id, object data, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = ProviderId,
            Event = new() { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data },
            Metadata = metadata
        };
}
