using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OPInference;

public sealed partial class OPInferenceProvider(
    IApiKeyResolver keyResolver,
    AsyncCacheHelper cache,
    IHttpClientFactory httpClientFactory) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.opinference.com/");
        return client;
    }

    public string GetIdentifier() => "opinference";

    private string ResolveKey()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        return !string.IsNullOrWhiteSpace(key) ? key
            : throw new InvalidOperationException("No OPInference API key.");
    }

    private string NativeModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var prefix = GetIdentifier() + "/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            model = model[prefix.Length..];
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return model;
    }

    private Dictionary<string, string> Headers(Dictionary<string, string>? original)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in original ?? [])
            if (!ProviderCredentialHeaders.IsProviderKey(name)
                && !name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith("-api-key", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("host", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                headers[name] = value;
        headers["Authorization"] = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ResolveKey()).ToString();
        return headers;
    }

    private JsonElement BuildPayload(ChatCompletionOptions options, bool stream)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = JsonSerializer.SerializeToElement(options, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }).EnumerateObject()
            .GroupBy(property => property.Name)
            .ToDictionary(group => group.Key, group => group.Last().Value.Clone());
        if (root.Remove(GetIdentifier(), out var native))
        {
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("OPInference provider metadata must be a JSON object.");
            foreach (var property in native.EnumerateObject())
                root[property.Name] = property.Value.Clone();
        }
        root["model"] = JsonSerializer.SerializeToElement(NativeModel(options.Model));
        root["stream"] = JsonSerializer.SerializeToElement(stream);
        if (stream && !root.ContainsKey("stream_options"))
            root["stream_options"] = JsonSerializer.SerializeToElement(new { include_usage = true });
        return JsonSerializer.SerializeToElement(root);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => _client.GetChatCompletion(BuildPayload(options, false), GetIdentifier(),
            headers: Headers(options.Headers), ct: cancellationToken);

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        return StreamChatAsync(BuildPayload(options, true), Headers(options.Headers), cancellationToken);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> StreamChatAsync(JsonElement payload,
        Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var part in _client.GetChatCompletionSseEvents(payload, headers: headers, ct: cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(part.Data))
                continue;
            using var document = JsonDocument.Parse(part.Data);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException($"OPInference stream failed: {error.GetRawText()}");
            var update = JsonSerializer.Deserialize<ChatCompletionUpdate>(part.Data)
                ?? throw new JsonException("OPInference returned an empty chat chunk.");
            if (!string.IsNullOrEmpty(update.Model))
                update.Model = $"{GetIdentifier()}/{NativeModel(update.Model)}";
            if (update.Created == 0)
                update.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            yield return update;
        }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => (await CompleteChatAsync(UnifiedOptions(request), cancellationToken)).ToUnifiedResponse(GetIdentifier());

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, UnifiedOptions(request), cancellationToken: cancellationToken);

    private ChatCompletionOptions UnifiedOptions(AIRequest request)
    {
        var metadata = request.Metadata is null ? new Dictionary<string, object?>() : new(request.Metadata);
        metadata.Remove(GetIdentifier(), out var native);
        // Keep native JSON out of typed mappers so future options and explicit nulls survive.
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

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    private static NotSupportedException Unsupported() => new("This capability is not implemented for OPInference.");
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken) => throw Unsupported();
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
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
}
