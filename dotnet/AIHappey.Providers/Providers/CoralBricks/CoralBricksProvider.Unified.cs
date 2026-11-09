using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.CoralBricks;

public sealed partial class CoralBricksProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => (await CompleteChatAsync(UnifiedOptions(request), cancellationToken)).ToUnifiedResponse(GetIdentifier());

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, UnifiedOptions(request), cancellationToken: cancellationToken);

    private ChatCompletionOptions UnifiedOptions(AIRequest request)
    {
        var metadata = request.Metadata is null ? new Dictionary<string, object?>() : new(request.Metadata);
        metadata.Remove(GetIdentifier(), out var native);
        // Keep native JSON out of typed mappers so future provider options and explicit nulls survive.
        var mapped = new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Id = request.Id,
            Instructions = request.Instructions, Input = request.Input, Temperature = request.Temperature,
            TopP = request.TopP, MaxOutputTokens = request.MaxOutputTokens, MaxToolCalls = request.MaxToolCalls,
            Stream = request.Stream, ParallelToolCalls = request.ParallelToolCalls, ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat, Tools = request.Tools, Headers = request.Headers,
            Verbosity = request.Verbosity, Metadata = metadata
        }.ToChatCompletionOptions(GetIdentifier());
        if (native is not null)
        {
            mapped.AdditionalProperties ??= [];
            mapped.AdditionalProperties[GetIdentifier()] = native is JsonElement element
                ? element.Clone() : JsonSerializer.SerializeToElement(native);
        }
        return mapped;
    }
}
