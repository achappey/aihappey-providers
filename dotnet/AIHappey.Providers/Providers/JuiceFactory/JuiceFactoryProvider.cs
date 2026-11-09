using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.JuiceFactory;

public sealed partial class JuiceFactoryProvider(
    IApiKeyResolver keys, AsyncCacheHelper cache, IHttpClientFactory clients) : IModelProvider
{
    private const string BaseUrl = "https://api.juicefactory.ai/v1/";
    private readonly HttpClient _client = clients.CreateClient();

    public string GetIdentifier() => "juicefactory";

    private Dictionary<string, string> Headers(Dictionary<string, string>? supplied = null, string? key = null)
    {
        key ??= keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No JuiceFactory API key.");

        var headers = ProviderCredentialHeaders.ForModel(supplied, null);
        headers["Authorization"] = new AuthenticationHeaderValue("Bearer", key).ToString();
        return headers;
    }

    private JsonElement BuildPayload(ChatCompletionOptions options, bool stream)
    {
        var serialized = JsonSerializer.SerializeToElement(options, ResponseJson.Default);
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in serialized.EnumerateObject())
            if (property.Name != GetIdentifier())
                body[property.Name] = property.Value.Clone();

        // Preserve native provider options, including unknown fields and explicit nulls.
        if (serialized.TryGetProperty(GetIdentifier(), out var native) && native.ValueKind == JsonValueKind.Object)
            foreach (var property in native.EnumerateObject())
                body[property.Name] = property.Value.Clone();

        if (body.TryGetValue("model", out var model) && model.ValueKind == JsonValueKind.String)
            body["model"] = JsonSerializer.SerializeToElement(LocalModel(model.GetString()!));
        body["stream"] = JsonSerializer.SerializeToElement(stream);
        return JsonSerializer.SerializeToElement(body);
    }

    private string LocalModel(string model)
        => model.StartsWith(GetIdentifier() + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(GetIdentifier().Length + 1)..] : model;

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
                ?? throw new JsonException("Empty JuiceFactory chat chunk.");
            if (!string.IsNullOrEmpty(update.Model))
                update.Model = update.Model.ToModelId(GetIdentifier());
            yield return update;
        }
    }

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        var local = new OpenAIEmbeddingRequest
        {
            Model = LocalModel(request.Model), Input = request.Input, Dimensions = request.Dimensions,
            EncodingFormat = request.EncodingFormat, User = request.User, AdditionalProperties = request.AdditionalProperties
        };
        return (await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, local, Headers(), BaseUrl + "embeddings", cancellationToken)).Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        var local = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        local.Model = LocalModel(local.Model);
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, local, Headers(), BaseUrl + "embeddings", cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "models");
            foreach (var (name, value) in Headers(key: key))
                request.Headers.TryAddWithoutValidation(name, value);
            using var response = await _client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var models = new List<Model>();
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(id.GetString())) continue;
                models.Add(new Model
                {
                    Id = id.GetString()!.ToModelId(GetIdentifier()), Name = id.GetString()!,
                    OwnedBy = item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                        ? owner.GetString()! : "JuiceFactory",
                    Type = item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        ? type.GetString()! : "language"
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 0, cancellationToken: cancellationToken);
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToResponseStreamParts(cancellationToken)) yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(MessagesRequest(request), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(MessagesRequest(request), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken)) yield return part;
    }

    private AIRequest MessagesRequest(MessagesRequest request)
    {
        var unified = request.ToUnifiedRequest(GetIdentifier());
        unified.Metadata!.Remove(GetIdentifier());
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ToolChoice is { } choice)
            properties["tool_choice"] = choice.Type switch
            {
                "auto" or "none" => JsonSerializer.SerializeToElement(choice.Type),
                "any" => JsonSerializer.SerializeToElement("required"),
                "tool" => JsonSerializer.SerializeToElement(new { type = "function", function = new { name = choice.Name } }),
                _ => JsonSerializer.SerializeToElement(choice)
            };
        // Explicit native options take precedence over adapter defaults.
        if (request.AdditionalProperties?.TryGetValue(GetIdentifier(), out var native) == true
            && native.ValueKind == JsonValueKind.Object)
            foreach (var property in native.EnumerateObject())
                properties[property.Name] = property.Value.Clone();
        if (properties.Count > 0)
            unified.Metadata[GetIdentifier()] = JsonSerializer.SerializeToElement(properties);
        return unified;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier())) yield return uiPart;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
