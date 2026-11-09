using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Conway;

public sealed partial class ConwayProvider
{
    private Dictionary<string, string> Headers(Dictionary<string, string>? supplied = null, string? key = null)
    {
        key ??= _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Conway API key.");

        var headers = ProviderCredentialHeaders.ForModel(supplied, null);
        headers["Authorization"] = new AuthenticationHeaderValue("Bearer", key).ToString();
        return headers;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => _client.GetChatCompletion(BuildPayload(options, false), GetIdentifier(),
            BaseUrl + "chat/completions", Headers(options.Headers), cancellationToken);

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in _client.GetChatCompletionSseEvents(BuildPayload(options, true),
            BaseUrl + "chat/completions", Headers(options.Headers), cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(part.Data)) continue;
            var update = JsonSerializer.Deserialize<ChatCompletionUpdate>(part.Data)
                ?? throw new JsonException("Empty Conway chat chunk.");
            if (!string.IsNullOrEmpty(update.Model))
                update.Model = update.Model.ToModelId(GetIdentifier());
            yield return update;
        }
    }

    private JsonElement BuildPayload(ChatCompletionOptions options, bool stream)
    {
        var serialized = JsonSerializer.SerializeToElement(options, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in serialized.EnumerateObject())
            if (property.Name != GetIdentifier())
                body[property.Name] = property.Value.Clone();

        if (serialized.TryGetProperty(GetIdentifier(), out var native) && native.ValueKind == JsonValueKind.Object)
            foreach (var property in native.EnumerateObject())
                body[property.Name] = property.Value.Clone();

        if (body.TryGetValue("model", out var model) && model.ValueKind == JsonValueKind.String)
        {
            var id = model.GetString()!;
            var prefix = GetIdentifier() + "/";
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                body["model"] = JsonSerializer.SerializeToElement(id[prefix.Length..]);
        }
        body["stream"] = JsonSerializer.SerializeToElement(stream);
        return JsonSerializer.SerializeToElement(body);
    }

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
