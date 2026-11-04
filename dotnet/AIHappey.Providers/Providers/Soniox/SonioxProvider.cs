using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Messages.Mapping;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses.Mapping;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;
using AIHappey.Common.Model.Providers.Soniox;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Common.Model.Providers.OpenAI.Live;

namespace AIHappey.Core.Providers.Soniox;

public partial class SonioxProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public SonioxProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.soniox.com/");
    }

    private void ApplyAuthHeader(HttpRequestMessage request)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Soniox)} API key.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
            .ToChatCompletion();

    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
        {
            yield return part.ToChatCompletionUpdate();
        }

    }

    public string GetIdentifier() => nameof(Soniox).ToLowerInvariant();




    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(
        Responses.ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
            .ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
                           unifiedRequest,
                           cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;

        yield break;
    }

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);

        SonioxRealtimeProviderMetadata? metadata = null;
        var config = new JsonObject();
        if (realtimeRequest.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw new JsonException("Soniox realtime provider options must be a JSON object.");
            metadata = options.Deserialize<SonioxRealtimeProviderMetadata>(SonioxJsonOptions);
            if (options.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in options.EnumerateObject())
                {
                    if (property.Name.ToLowerInvariant() is
                        "expires_in_seconds" or "max_session_duration_seconds" or
                        "usage_type" or "single_use" or "api_key" or "model")
                        continue;

                    config[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            }
        }

        config["model"] = string.IsNullOrWhiteSpace(realtimeRequest.Model)
            ? "stt-rt-v6"
            : NormalizeSonioxModel(realtimeRequest.Model);
        if (!config.ContainsKey("audio_format"))
            config["audio_format"] = "auto";

        var expiresInSeconds = metadata?.ExpiresInSeconds ?? 1800;
        if (expiresInSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(realtimeRequest), "Soniox expires_in_seconds must be between 1 and 3600.");
        if (metadata?.MaxSessionDurationSeconds is < 1 or > 18000)
            throw new ArgumentOutOfRangeException(nameof(realtimeRequest), "Soniox max_session_duration_seconds must be between 1 and 18000.");
        if (metadata?.ClientReferenceId?.Length > 256)
            throw new ArgumentException("Soniox client_reference_id cannot exceed 256 characters.", nameof(realtimeRequest));

        var payload = new Dictionary<string, object?>
        {
            ["usage_type"] = "transcribe_websocket",
            ["expires_in_seconds"] = expiresInSeconds,
            ["client_reference_id"] = metadata?.ClientReferenceId,
            ["single_use"] = true,
            ["max_session_duration_seconds"] = metadata?.MaxSessionDurationSeconds
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/auth/temporary-api-key")
        {
            Content = JsonContent.Create(payload, options: SonioxJsonOptions)
        };
        ApplyAuthHeader(httpRequest);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Soniox temporary API key creation failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var apiKey = ReadString(document.RootElement, "api_key")
            ?? throw new InvalidOperationException("Soniox temporary API key response did not contain api_key.");
        var expiresAt = ReadString(document.RootElement, "expires_at");
        var expiry = DateTimeOffset.TryParse(expiresAt, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeSeconds();

        return new RealtimeResponse
        {
            Value = apiKey,
            ExpiresAt = expiry,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                websocket_url = "wss://stt-rt.soniox.com/transcribe-websocket",
                config
            })
        };
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();



    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;

        yield break;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
      => this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);


    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedTranscriptionAsync(request, cancellationToken);

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }


    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}