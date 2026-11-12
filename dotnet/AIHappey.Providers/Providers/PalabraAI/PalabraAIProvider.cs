using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PalabraAI;

public sealed class PalabraAIProvider : IModelProvider
{
    private const string ProviderId = "palabraai";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public PalabraAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.palabra.ai/");
    }

    public string GetIdentifier() => ProviderId;

    public async Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);

        var metadata = realtimeRequest.GetRealtimeProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("Palabra provider options must be a JSON object.");

        var payload = metadata.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(metadata.GetRawText())!.AsObject()
            : new JsonObject();

        if (!payload.ContainsKey("intent"))
            payload["intent"] = ResolveIntent(realtimeRequest.Model);

        var intent = ReadOptionalString(payload, "intent");
        if (intent is not ("s2s" or "stt" or "tts"))
            throw new ArgumentException("Palabra intent must be s2s, stt, or tts.", nameof(realtimeRequest));

        var connectionType = ReadOptionalString(payload, "connection_type") ?? "ws";
        if (connectionType is not ("ws" or "webrtc"))
            throw new ArgumentException("Palabra connection_type must be ws or webrtc.", nameof(realtimeRequest));
        if (connectionType == "webrtc" && intent != "s2s")
            throw new ArgumentException("Palabra WebRTC sessions are only available for s2s.", nameof(realtimeRequest));

        using var request = new HttpRequestMessage(HttpMethod.Post, "session-storage/v2/sessions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ApplyAuthHeader(request);

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Palabra session creation failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Palabra session response did not contain data.");
        }

        var token = ReadString(data, "token");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Palabra session response did not contain a token.");

        intent = ReadString(data, "intent") ?? intent;
        connectionType = ReadString(data, "connection_type") ?? connectionType;
        var sessionMetadata = JsonNode.Parse(data.GetRawText())!.AsObject();
        sessionMetadata.Remove("token");

        if (connectionType == "ws")
        {
            sessionMetadata["ws_url"] = GetWebSocketUrl(intent);
            sessionMetadata["token_parameter"] = "token";
        }

        var expiresAt = ReadString(data, "connect_by") is { } connectBy
            && DateTimeOffset.TryParse(connectBy, out var connectByTime)
                ? connectByTime.ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.AddSeconds(ReadInt32(payload, "expires_in_seconds") ?? 3600).ToUnixTimeSeconds();

        return new RealtimeResponse
        {
            Value = token,
            ExpiresAt = expiresAt,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(sessionMetadata)
            }
        };
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>(
        [
            new() { Id = "palabraai/s2s", Name = "Speech-to-Speech Translation", OwnedBy = "PalabraAI", Type = "audio", Tags = ["real-time", "realtime-token", "translation"] },
            new() { Id = "palabraai/stt", Name = "Realtime Speech-to-Text", OwnedBy = "PalabraAI", Type = "transcription", Tags = ["real-time", "realtime-token"] },
            new() { Id = "palabraai/tts", Name = "Realtime Text-to-Speech", OwnedBy = "PalabraAI", Type = "audio", Tags = ["real-time", "realtime-token"] }
        ]);

    private void ApplyAuthHeader(HttpRequestMessage request)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No PalabraAI API key.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static string ResolveIntent(string? model)
    {
        var localModel = model?.Trim();
        if (localModel?.StartsWith("palabraai/", StringComparison.OrdinalIgnoreCase) == true)
            localModel = localModel["palabraai/".Length..];

        return localModel?.ToLowerInvariant() switch
        {
            "s2s" or "speech-to-speech" or "translation" => "s2s",
            "stt" or "speech-to-text" or "transcription" => "stt",
            "tts" or "text-to-speech" => "tts",
            _ => throw new ArgumentException(
                "Palabra realtime models must select s2s, stt, or tts (or provide providerOptions.palabraai.intent).",
                nameof(model))
        };
    }

    private static string GetWebSocketUrl(string intent)
        => intent switch
        {
            "s2s" => $"wss://streaming.palabra.ai/streaming-api/{Guid.NewGuid():N}/v1/speech-to-speech/stream",
            "stt" => "wss://stream.palabra.ai/asr/v1/speech-to-text/stream",
            "tts" => "wss://stream.palabra.ai/tts-api/v1/text-to-speech/stream",
            _ => throw new ArgumentOutOfRangeException(nameof(intent))
        };

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadOptionalString(JsonObject payload, string propertyName)
    {
        if (!payload.TryGetPropertyValue(propertyName, out var node))
            return null;

        if (node is JsonValue value && value.TryGetValue<string>(out var result))
            return result;

        throw new ArgumentException($"Palabra '{propertyName}' must be a string.");
    }

    private static int? ReadInt32(JsonObject payload, string propertyName)
        => payload[propertyName] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
