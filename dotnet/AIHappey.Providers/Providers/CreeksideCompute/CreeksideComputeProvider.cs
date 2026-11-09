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
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.CreeksideCompute;

public sealed partial class CreeksideComputeProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper cache) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.creeksidecompute.ai/");
        return client;
    }

    public string GetIdentifier() => "creeksidecompute";

    private string ResolveKey()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        return !string.IsNullOrWhiteSpace(key) ? key
            : throw new InvalidOperationException("No CreeksideCompute API key.");
    }

    private string NativeModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var prefix = GetIdentifier() + "/";
        while (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            model = model[prefix.Length..];
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return model;
    }

    private Dictionary<string, string> Headers(
        Dictionary<string, string>? original,
        Dictionary<string, string>? additional = null,
        bool messages = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in (original ?? []).Concat(additional ?? []))
            if (!ProviderCredentialHeaders.IsProviderKey(name)
                && !name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith("-api-key", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("host", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                result[name] = value;
        result["Authorization"] = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ResolveKey()).ToString();
        if (messages)
            result["anthropic-version"] = "2023-06-01";
        return result;
    }

    private T Prepare<T>(T request, string? model, bool stream, JsonSerializerOptions? json = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        json ??= JsonSerializerOptions.Web;
        // Preserve extension JSON (including large integers) while isolating caller-owned requests.
        var root = JsonSerializer.SerializeToElement(request, json).EnumerateObject()
            .GroupBy(property => property.Name)
            .ToDictionary(group => group.Key, group => group.Last().Value.Clone());
        root["model"] = JsonSerializer.SerializeToElement(NativeModel(model));
        root["stream"] = JsonSerializer.SerializeToElement(stream);
        return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(root), json)!;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => _client.GetChatCompletion(Prepare(options, options.Model, false), GetIdentifier(),
            headers: Headers(options.Headers), ct: cancellationToken);

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(options, options.Model, true);
        prepared.StreamOptions ??= new StreamOptions { IncludeUsage = true };
        return _client.GetChatCompletionUpdates(prepared, GetIdentifier(),
            headers: Headers(options.Headers), ct: cancellationToken);
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => _client.GetResponses(Prepare(options, options.Model, false, ResponseJson.Default), GetIdentifier(),
            headers: Headers(options.Headers), ct: cancellationToken);

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => _client.GetResponsesUpdates(Prepare(options, options.Model, true, ResponseJson.Default), GetIdentifier(),
            headers: Headers(options.Headers), ct: cancellationToken);

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => _client.PostMessages(Prepare(request, request.Model, false), GetIdentifier(),
            headers: Headers(request.Headers, headers, messages: true), ct: cancellationToken);

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => _client.PostMessagesStreaming(Prepare(request, request.Model, true), GetIdentifier(),
            headers: Headers(request.Headers, headers, messages: true), ct: cancellationToken);

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

    private static NotSupportedException Unsupported() => new("CreeksideCompute does not document this capability.");
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
