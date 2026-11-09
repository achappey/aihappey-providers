using System.Runtime.CompilerServices;
using System.Text.Json;
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

namespace AIHappey.Core.Providers.PaperInstruments;

public sealed class PaperInstrumentsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public PaperInstrumentsProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.paperinstruments.com/");
    }

    public string GetIdentifier() => "paperinstruments";

    private Dictionary<string, string> CreateAuthenticatedHeaders(
        IEnumerable<KeyValuePair<string, string>>? requestHeaders = null,
        bool includeAnthropicVersion = false)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No PaperInstruments API key.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (requestHeaders is not null)
        {
            foreach (var (name, value) in requestHeaders)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(value)
                    || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("api-key", StringComparison.OrdinalIgnoreCase)
                    || ProviderCredentialHeaders.IsProviderKey(name))
                    continue;

                headers[name] = value;
            }
        }

        if (includeAnthropicVersion)
            headers.TryAdd("anthropic-version", "2023-06-01");

        headers["Authorization"] = "Bearer " + key;
        return headers;
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // PaperInstruments documents this model, but no model-discovery endpoint.
        return Task.FromResult<IEnumerable<Model>>(
        [
            new Model
            {
                Id = "paperinstruments/ultramarine-1-flash",
                Name = "ultramarine-1-flash",
                OwnedBy = "PaperInstruments",
                Type = "chat"
            }
        ]);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => _client.GetChatCompletion(options, GetIdentifier(),
            headers: CreateAuthenticatedHeaders(options.Headers), ct: cancellationToken);

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => _client.GetChatCompletionUpdates(options, GetIdentifier(),
            headers: CreateAuthenticatedHeaders(options.Headers), ct: cancellationToken);

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => _client.GetResponses(PrepareResponses(options, stream: false), GetIdentifier(),
            headers: CreateAuthenticatedHeaders(options.Headers), ct: cancellationToken);

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options, CancellationToken cancellationToken = default)
        => _client.GetResponsesUpdates(PrepareResponses(options, stream: true), GetIdentifier(),
            headers: CreateAuthenticatedHeaders(options.Headers), ct: cancellationToken);

    private static ResponseRequest PrepareResponses(ResponseRequest request, bool stream)
    {
        var copy = JsonSerializer.SerializeToElement(request, ResponseJson.Default)
            .Deserialize<ResponseRequest>(ResponseJson.Default)!;
        // The shared transport merges request.Headers again; only pass sanitized headers separately.
        copy.Headers = null;
        copy.Stream = stream;
        return copy;
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
        => _client.PostMessages(PrepareMessages(request, stream: false), GetIdentifier(),
            headers: CreateAuthenticatedHeaders(headers, includeAnthropicVersion: true), ct: cancellationToken);

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => _client.PostMessagesStreaming(PrepareMessages(request, stream: true), GetIdentifier(),
            headers: CreateAuthenticatedHeaders(headers, includeAnthropicVersion: true), ct: cancellationToken);

    private static MessagesRequest PrepareMessages(MessagesRequest request, bool stream)
    {
        var copy = JsonSerializer.SerializeToElement(request).Deserialize<MessagesRequest>()!;
        copy.Stream = stream;
        return copy;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaResponsesAsync(request, cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
