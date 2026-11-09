using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HatzAI;

public sealed class HatzAIProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper cache) : IModelProvider
{
    private const string ResponsesPath = "openai/responses";
    private const string MessagesPath = "anthropic/messages";
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://ai.hatz.ai/v1/");
        return client;
    }

    public string GetIdentifier() => "hatzai";

    private Dictionary<string, string> RequestHeaders(
        IEnumerable<KeyValuePair<string, string>>? incoming = null,
        bool messages = false)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No HatzAI API key.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in incoming ?? [])
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)
                || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("api-key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase)
                || ProviderCredentialHeaders.IsProviderKey(name))
                continue;
            headers[name] = value;
        }
        if (messages)
            headers.TryAdd("anthropic-version", "2023-06-01");
        headers["X-API-Key"] = key;
        return headers;
    }

    // Hatz-native chat uses a different response/NDJSON contract, not OpenAI chat SSE.
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(ToUnifiedChatRequest(options), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in StreamUnifiedAsync(ToUnifiedChatRequest(options), cancellationToken)
                           .ToChatCompletionUpdates(cancellationToken))
            yield return update;
    }

    private AIRequest ToUnifiedChatRequest(ChatCompletionOptions options)
    {
        var request = options.ToUnifiedRequest(GetIdentifier());
        return new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Id = request.Id,
            Instructions = request.Instructions, Input = request.Input, Temperature = request.Temperature,
            TopP = request.TopP, MaxOutputTokens = request.MaxOutputTokens,
            MaxToolCalls = request.MaxToolCalls, Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls, ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat, Tools = request.Tools, Metadata = request.Metadata,
            Headers = options.Headers, Verbosity = request.Verbosity
        };
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => _client.GetResponses(PrepareResponses(options, false), GetIdentifier(), relativeUrl: ResponsesPath,
            headers: RequestHeaders(options.Headers), ct: cancellationToken);

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options, CancellationToken cancellationToken = default)
        => _client.GetResponsesUpdates(PrepareResponses(options, true), GetIdentifier(), relativeUrl: ResponsesPath,
            headers: RequestHeaders(options.Headers), ct: cancellationToken);

    private static ResponseRequest PrepareResponses(ResponseRequest request, bool stream)
    {
        var copy = JsonSerializer.SerializeToElement(request, ResponseJson.Default)
            .Deserialize<ResponseRequest>(ResponseJson.Default)!;
        // The shared transports can merge the request's headers again.
        copy.Headers = null;
        copy.Stream = stream;
        return copy;
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
        => _client.PostMessages(PrepareMessages(request, false), GetIdentifier(), relativeUrl: MessagesPath,
            headers: RequestHeaders((request.Headers ?? []).Concat(headers ?? []), messages: true), ct: cancellationToken);

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => _client.PostMessagesStreaming(PrepareMessages(request, true), GetIdentifier(), relativeUrl: MessagesPath,
            headers: RequestHeaders((request.Headers ?? []).Concat(headers ?? []), messages: true), ct: cancellationToken);

    private static MessagesRequest PrepareMessages(MessagesRequest request, bool stream)
    {
        var copy = JsonSerializer.SerializeToElement(request).Deserialize<MessagesRequest>()!;
        copy.Headers = null;
        copy.Stream = stream;
        return copy;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "chat/models");
            request.Headers.Add("X-API-Key", key);
            using var response = await _client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"HatzAI model discovery failed with status {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}",
                    null, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var models = document.RootElement;
            if (models.ValueKind == JsonValueKind.Object)
            {
                if (models.TryGetProperty("models", out var nativeModels))
                    models = nativeModels;
                else if (models.TryGetProperty("data", out var data))
                    models = data;
            }
            if (models.ValueKind != JsonValueKind.Array)
                throw new JsonException("HatzAI model discovery must contain an array, models array, or data array.");

            return (IEnumerable<Model>)models.EnumerateArray().Select(ToModel).OfType<Model>()
                .DistinctBy(model => model.Id).ToArray();
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 480, cancellationToken: cancellationToken);
    }

    private Model? ToModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var id = GetString(item, "name") ?? GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = GetString(item, "display_name") ?? GetString(item, "name") ?? id,
            Description = GetString(item, "description"),
            OwnedBy = GetString(item, "developer") ?? GetString(item, "owned_by") ?? "HatzAI",
            Type = "language"
        };
    }

    private static string? GetString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    private static NotSupportedException Unsupported() => new("HatzAI does not document this gateway capability.");
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
}
