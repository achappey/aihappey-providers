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

namespace AIHappey.Core.Providers.FrontiereAI;

public sealed partial class FrontiereAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keys;
    private readonly AsyncCacheHelper _cache;
    private readonly HttpClient _client;

    public FrontiereAIProvider(IApiKeyResolver keys, IHttpClientFactory clients, AsyncCacheHelper cache)
    {
        _keys = keys;
        _cache = cache;
        _client = clients.CreateClient();
        _client.BaseAddress = new Uri("https://getfrontiereai.eu/api/");
    }

    public string GetIdentifier() => "frontiereai";

    private Dictionary<string, string> AuthHeaders()
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No FrontiereAI API key.");
        return new() { ["Authorization"] = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key).ToString() };
    }

    private HttpRequestMessage PublicRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        // An explicitly empty request header suppresses credentials inherited from a factory-configured client.
        if (_client.DefaultRequestHeaders.Contains("Authorization"))
            request.Headers.TryAddWithoutValidation("Authorization", string.Empty);
        return request;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => this.GetChatCompletion(_client, ChatOptions(options, false), AuthHeaders(), cancellationToken: cancellationToken);

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => this.GetChatCompletions(_client, ChatOptions(options, true), AuthHeaders(), cancellationToken: cancellationToken);

    private ChatCompletionOptions ChatOptions(ChatCompletionOptions options, bool stream)
    {
        var copy = NormalizeOptions(options);
        copy.Headers = options.Headers;
        copy.Stream = stream;
        return copy;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => await this.IsImageModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedImageAsync(ImageOptions(request), cancellationToken)
            : await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken);

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = await this.IsImageModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedImageAsync(ImageOptions(request), cancellationToken)
            : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
        await foreach (var part in stream)
            yield return part;
    }

    private AIRequest ImageOptions(AIRequest request)
    {
        Dictionary<string, object?>? metadata = null;
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var native) == true)
        {
            var json = native is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(native);
            metadata = new() { [GetIdentifier()] = json };
            if (json.ValueKind == JsonValueKind.Object)
                foreach (var property in json.EnumerateObject())
                    metadata.TryAdd(property.Name, property.Value.Clone());
        }
        return new()
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Id = request.Id,
            Input = request.Input,
            Metadata = metadata
        };
    }

    // Responses and Messages are adapters, not undocumented native endpoints.
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await this.OpenAICompatibleEmbeddingRequestAsync(_client, NormalizeOptions(request), AuthHeaders(), cancellationToken: cancellationToken)).Response;

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await this.OpenAICompatibleEmbeddingRequestAsync(_client, NormalizeOptions(request.ToOpenAIEmbeddingRequest(GetIdentifier())),
            AuthHeaders(), cancellationToken: cancellationToken)).ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
