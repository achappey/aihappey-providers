using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

namespace AIHappey.Core.Providers.SpeakAI;

public sealed partial class SpeakAIProvider : IModelProvider
{
    private const string ProviderId = "speakai";
    private readonly IApiKeyResolver _keys;
    private readonly HttpClient _client;

    public SpeakAIProvider(IApiKeyResolver keys, IHttpClientFactory clients)
    {
        _keys = keys;
        _client = clients.CreateClient();
        _client.BaseAddress = new Uri("https://api.speakai.co/v1/");
    }

    public string GetIdentifier() => ProviderId;

    private static string LocalModel(string? model)
        => model?.StartsWith(ProviderId + "/", StringComparison.OrdinalIgnoreCase) == true
            ? model[(ProviderId.Length + 1)..] : model ?? "";

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var key = _keys.Resolve(ProviderId);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No SpeakAI API key.");

        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("x-speakai-key", key);
        return request;
    }

    // Provider options may be flat or scoped under the provider id; scoped values win.
    private static JsonObject Options(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new JsonObject();
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("SpeakAI provider options must be a JSON object.");

        var result = JsonNode.Parse(value.GetRawText())!.AsObject();
        if (result.Remove(ProviderId, out var scoped))
            foreach (var option in Options(JsonSerializer.SerializeToElement(scoped)))
                result[option.Key] = option.Value?.DeepClone();

        return result;
    }

    private static JsonObject Options(Dictionary<string, JsonElement>? options)
        => Options(options?.GetValueOrDefault(ProviderId) ?? default);

    private static JsonObject NativeOptions(Dictionary<string, JsonElement>? properties)
    {
        if (properties?.TryGetValue(ProviderId, out var native) == true)
            return Options(native);

        return properties is null ? new JsonObject() : Options(JsonSerializer.SerializeToElement(properties));
    }

    private static HttpContent JsonContent(JsonObject payload)
        => new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"SpeakAI request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
    }

    private async Task<JsonElement> Send(HttpMethod method, string path, JsonObject? payload, CancellationToken ct)
    {
        using var request = CreateRequest(method, path, payload is null ? null : JsonContent(payload));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return JsonDocument.Parse("{}").RootElement.Clone();

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    // Successful responses wrap the payload in {"status","data"}; older ones return it at the root.
    private static JsonElement Unwrap(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("data", out var data)
           && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? data
            : root;

    private static string? GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();

        return null;
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)
            .ToChatCompletionUpdates(cancellationToken))
            yield return part;
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(ProviderId))
                yield return uiPart;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keys.Resolve(ProviderId));

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
