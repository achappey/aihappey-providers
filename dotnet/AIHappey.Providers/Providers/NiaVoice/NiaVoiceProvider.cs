using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
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

namespace AIHappey.Core.Providers.NiaVoice;

public sealed partial class NiaVoiceProvider : IModelProvider
{
    private const string ProviderId = "niavoice";
    private readonly IApiKeyResolver _keys;
    private readonly HttpClient _client;

    public NiaVoiceProvider(IApiKeyResolver keys, IHttpClientFactory clients)
    {
        _keys = keys;
        _client = clients.CreateClient();
        _client.BaseAddress = new Uri("https://niavoices.com/");
    }

    public string GetIdentifier() => ProviderId;

    private static string LocalModel(string? model)
        => model?.StartsWith(ProviderId + "/", StringComparison.OrdinalIgnoreCase) == true
            ? model[(ProviderId.Length + 1)..] : model ?? "";

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var key = _keys.Resolve(ProviderId);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No NiaVoice API key.");
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private static JsonObject Options(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new JsonObject();
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("NiaVoice provider options must be a JSON object.");
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
                $"NiaVoice request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
    }

    private async Task<JsonElement> SendJson(string path, JsonObject payload, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, path, JsonContent(payload));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var result = document.RootElement.Clone();
        if (result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"NiaVoice request failed: {error.GetRawText()}");
        return result;
    }

    private static string RequiredString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()! : throw new JsonException($"NiaVoice response requires '{name}'.");

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

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
