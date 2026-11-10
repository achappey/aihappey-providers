using System.Net.Http.Headers;
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
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inferel;

public sealed partial class InferelProvider : IModelProvider
{
    private const string QueueUrl = "https://console.inferel.ai/api/v1/queue/apikey/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IApiKeyResolver _keys;
    private readonly AsyncCacheHelper _cache;
    private readonly HttpClient _client;
    private readonly HttpClient _mediaClient;

    public InferelProvider(IApiKeyResolver keys, AsyncCacheHelper cache, IHttpClientFactory clients)
    {
        _keys = keys;
        _cache = cache;
        _client = clients.CreateClient();
        _client.BaseAddress = new Uri("https://inference.inferel.ai/");
        _mediaClient = clients.CreateClient();
    }

    public string GetIdentifier() => "inferel";

    private string Key() => _keys.Resolve(GetIdentifier()) is { } key && !string.IsNullOrWhiteSpace(key)
        ? key : throw new InvalidOperationException("No Inferel API key.");

    private static string NativeModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var native = model.StartsWith("inferel/", StringComparison.OrdinalIgnoreCase) ? model[8..] : model;
        ArgumentException.ThrowIfNullOrWhiteSpace(native);
        return native;
    }

    private Dictionary<string, string> Headers(Dictionary<string, string>? original = null, bool messages = false)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in original ?? [])
            if (!ProviderCredentialHeaders.IsProviderKey(name)
                && !name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith("-api-key", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("host", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                headers[name] = value;
        headers["Authorization"] = new AuthenticationHeaderValue("Bearer", Key()).ToString();
        if (messages) headers.TryAdd("anthropic-version", "2023-06-01");
        return headers;
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Inferel provider metadata must be a JSON object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    private static JsonElement TextPayload<T>(T options, string? model, bool stream)
    {
        var body = Properties(JsonSerializer.SerializeToElement(options, Json));
        if (body.Remove("inferel", out var native))
            foreach (var (name, value) in Properties(native))
                body[name] = value;
        body.Remove("headers");
        body["model"] = JsonSerializer.SerializeToElement(NativeModel(model));
        body["stream"] = JsonSerializer.SerializeToElement(stream);
        if (stream && options is ChatCompletionOptions && !body.ContainsKey("stream_options"))
            body["stream_options"] = JsonSerializer.SerializeToElement(new { include_usage = true });
        return JsonSerializer.SerializeToElement(body);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _client.GetChatCompletion(TextPayload(options, options.Model, false), GetIdentifier(),
            headers: Headers(options.Headers), ct: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var raw in StreamText(TextPayload(options, options.Model, true),
            "v1/chat/completions", Headers(options.Headers), cancellationToken))
        {
            var part = raw.Deserialize<ChatCompletionUpdate>(Json) ?? throw new JsonException("Empty Inferel chat chunk.");
            if (!string.IsNullOrEmpty(part.Model)) part.Model = $"inferel/{NativeModel(part.Model)}";
            yield return part;
        }
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _client.PostChatCompletionJson(TextPayload(options, options.Model, false),
            "v1/responses", Headers(options.Headers), cancellationToken);
        var result = raw.Deserialize<ResponseResult>(Json) ?? throw new JsonException("Empty Inferel response.");
        result.Model = $"inferel/{NativeModel(result.Model)}";
        return result;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var raw in StreamText(TextPayload(options, options.Model, true),
            "v1/responses", Headers(options.Headers), cancellationToken))
        {
            var part = raw.Deserialize<ResponseStreamPart>(Json) ?? throw new JsonException("Empty Inferel response event.");
            var response = part switch
            {
                ResponseCreated created => created.Response,
                ResponseInProgress progress => progress.Response,
                ResponseCompleted completed => completed.Response,
                _ => null
            };
            if (response is not null) response.Model = $"inferel/{NativeModel(response.Model)}";
            yield return part;
        }
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await _client.PostChatCompletionJson(TextPayload(request, request.Model, false),
            "v1/messages", Headers(headers, messages: true), cancellationToken);
        var result = raw.Deserialize<MessagesResponse>(Json) ?? throw new JsonException("Empty Inferel message.");
        result.Model = $"inferel/{NativeModel(result.Model)}";
        return result;
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var raw in StreamText(TextPayload(request, request.Model, true),
            "v1/messages", Headers(headers, messages: true), cancellationToken))
        {
            var part = raw.Deserialize<MessageStreamPart>(Json) ?? throw new JsonException("Empty Inferel message event.");
            if (part.Message is { } message) message.Model = $"inferel/{NativeModel(message.Model)}";
            yield return part;
        }
    }

    private async IAsyncEnumerable<JsonElement> StreamText(JsonElement payload, string path,
        Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await foreach (var part in _client.GetChatCompletionSseEvents(payload, path, headers, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(part.Data)) continue;
            var raw = JsonSerializer.Deserialize<JsonElement>(part.Data);
            if (raw.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException($"Inferel stream failed: {error.GetRawText()}");
            yield return raw;
        }
    }

    private ChatCompletionOptions UnifiedOptions(AIRequest request)
    {
        var metadata = request.Metadata is null ? new Dictionary<string, object?>() : new(request.Metadata);
        metadata.Remove(GetIdentifier(), out var native);
        // Keep native JSON out of typed mappers so explicit nulls and future options survive.
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

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var type = await ModelType(request.Model, cancellationToken);
        return type == "video" ? await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : type == "image" ? await this.ExecuteUnifiedImageAsync(request, cancellationToken)
            : (await CompleteChatAsync(UnifiedOptions(request), cancellationToken)).ToUnifiedResponse(GetIdentifier());
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var type = await ModelType(request.Model, cancellationToken);
        var stream = type == "video" ? this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : type == "image" ? this.StreamUnifiedImageAsync(request, cancellationToken)
            : this.StreamUnifiedViaChatCompletionsAsync(request, UnifiedOptions(request), cancellationToken: cancellationToken);
        await foreach (var part in stream.WithCancellation(cancellationToken)) yield return part;
    }

    private async Task<string> ModelType(string? model, CancellationToken ct)
    {
        var native = NativeModel(model);
        return (await ListModels(ct)).FirstOrDefault(candidate =>
            NativeModel(candidate.Id).Equals(native, StringComparison.OrdinalIgnoreCase))?.Type ?? "language";
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var ui in part.Event.ToUIMessagePart(GetIdentifier())) yield return ui;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
