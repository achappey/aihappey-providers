using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infer;

public partial class InferProvider(
    IApiKeyResolver keyResolver,
    AsyncCacheHelper asyncCacheHelper,
    IHttpClientFactory httpClientFactory) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);
    private readonly AsyncCacheHelper _memoryCache = asyncCacheHelper;

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://inferexchange.com/api/v1/");
        return client;
    }

    public string GetIdentifier() => "infer";

    private string GetApiKey()
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No INFER API key.");

        return key;
    }

    private Dictionary<string, string> WithAuthorization(IDictionary<string, string>? headers = null)
    {
        var result = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        result[nameof(HttpRequestHeaders.Authorization)] =
            new AuthenticationHeaderValue("Bearer", GetApiKey()).ToString();
        return result;
    }

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
        => await this.GetChatCompletion(
            _client,
            options,
            WithAuthorization(),
            relativeUrl: "chat/completions",
            cancellationToken: cancellationToken);

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
        => this.GetChatCompletions(
            _client,
            options,
            WithAuthorization(),
            relativeUrl: "chat/completions",
            cancellationToken: cancellationToken);

    public async Task<Responses.ResponseResult> ResponsesAsync(
        Responses.ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);
        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(
            request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);
        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken)
                           .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
