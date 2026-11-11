using System.Runtime.CompilerServices;
using System.Text.Json;
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

namespace AIHappey.Core.Providers.Porten;

public sealed partial class PortenProvider : IModelProvider
{
    private readonly IApiKeyResolver _keys;
    private readonly HttpClient _client;

    public PortenProvider(IApiKeyResolver keys, IHttpClientFactory factory)
    {
        _keys = keys;
        _client = factory.CreateClient();
        _client.BaseAddress = new Uri("https://porten.ai/");
    }

    public string GetIdentifier() => "porten";

    private string Key() => _keys.Resolve(GetIdentifier()) is { } key && !string.IsNullOrWhiteSpace(key)
        ? key : throw new InvalidOperationException("No Porten API key.");

    private Dictionary<string, string> Headers() => new() { ["Authorization"] = "Bearer " + Key() };

    private string NativeModel(string model)
        => model.StartsWith("porten/", StringComparison.OrdinalIgnoreCase) ? model[7..] : model;

    private Dictionary<string, JsonElement> NativeFields<T>(T request)
    {
        var fields = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        if (fields.Remove("providerMetadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty(GetIdentifier(), out var scoped) && scoped.ValueKind == JsonValueKind.Object)
            foreach (var property in scoped.EnumerateObject())
                fields[property.Name] = property.Value.Clone();
        return fields;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keys.Resolve(GetIdentifier())))
            return [];
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        request.Headers.Authorization = new("Bearer", Key());
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Porten model discovery must contain a data array.");
        var models = new List<Model>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString()))
                continue;
            var id = idElement.GetString()!;
            var features = item.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
            var type = features.Contains("chat") ? "language"
                : features.Any(x => x is "image_generation" or "image-generation" or "image") ? "image"
                : features.Any(x => x is "embedding" or "embeddings") ? "embedding"
                : id.GuessModelType();
            models.Add(new Model
            {
                Id = $"{GetIdentifier()}/{NativeModel(id)}",
                Name = id,
                OwnedBy = item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                    ? owner.GetString()! : "porten",
                Type = type,
                Tags = features,
                ContextWindow = item.TryGetProperty("context_length", out var context)
                    && context.ValueKind == JsonValueKind.Number && context.TryGetInt32(out var length) && length > 0
                    ? length : null
            });
        }
        return models;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Porten request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => this.GetChatCompletion(_client, NativeChat(options, false), Headers(), cancellationToken: cancellationToken);

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => this.GetChatCompletions(_client, NativeChat(options, true), Headers(), cancellationToken: cancellationToken);

    private ChatCompletionOptions NativeChat(ChatCompletionOptions options, bool stream)
    {
        ArgumentNullException.ThrowIfNull(options);
        var copy = JsonSerializer.SerializeToElement(NativeFields(options), JsonSerializerOptions.Web)
            .Deserialize<ChatCompletionOptions>(JsonSerializerOptions.Web)
            ?? throw new JsonException("Invalid Porten chat request.");
        if (!string.IsNullOrWhiteSpace(copy.Model)) copy.Model = NativeModel(copy.Model);
        copy.Headers = options.Headers;
        copy.Stream = stream;
        return copy;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken).ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken).ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await this.OpenAICompatibleEmbeddingRequestAsync(_client, NativeEmbedding(request), Headers(), cancellationToken: cancellationToken)).Response;

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await this.OpenAICompatibleEmbeddingRequestAsync(_client, NativeEmbedding(request.ToOpenAIEmbeddingRequest(GetIdentifier())), Headers(),
            cancellationToken: cancellationToken)).ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());

    private OpenAIEmbeddingRequest NativeEmbedding(OpenAIEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var copy = JsonSerializer.SerializeToElement(NativeFields(request), JsonSerializerOptions.Web)
            .Deserialize<OpenAIEmbeddingRequest>(JsonSerializerOptions.Web)
            ?? throw new JsonException("Invalid Porten embedding request.");
        if (!string.IsNullOrWhiteSpace(copy.Model)) copy.Model = NativeModel(copy.Model);
        return copy;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
