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
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Pyrois;

public sealed class PyroisProvider(
    IApiKeyResolver keys, IHttpClientFactory clients, ProviderRequestOptions options) : IModelProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly HttpClient _client = clients.CreateClient();

    public string GetIdentifier() => "pyrois";

    private string ChatUrl()
    {
        if (!Uri.TryCreate(options.PyroisEndpointUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.EndsWith(".pyrois.run", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Length <= ".pyrois.run".Length
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.TrimEnd('/') is not ("" or "/v1"))
            throw new InvalidOperationException("PyroisEndpointUrl must be an HTTPS Pyrois workload URL, optionally ending in /v1.");
        return $"{uri.GetLeftPart(UriPartial.Authority)}/v1/chat/completions";
    }

    private Dictionary<string, string> Headers(Dictionary<string, string>? original)
    {
        var key = keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Pyrois endpoint key.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in original ?? [])
            if (!ProviderCredentialHeaders.IsProviderKey(name)
                && !name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith("-api-key", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("host", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                headers[name] = value;
        headers["Authorization"] = new AuthenticationHeaderValue("Bearer", key).ToString();
        return headers;
    }

    private static JsonElement Payload(ChatCompletionOptions options, bool stream)
    {
        var serialized = JsonSerializer.SerializeToElement(options, Json);
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in serialized.EnumerateObject())
            body[property.Name] = property.Value.Clone();
        if (body.Remove("pyrois", out var native))
        {
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Pyrois provider metadata must be a JSON object.");
            foreach (var property in native.EnumerateObject())
                body[property.Name] = property.Value.Clone();
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        var model = options.Model.StartsWith("pyrois/", StringComparison.OrdinalIgnoreCase)
            ? options.Model["pyrois/".Length..] : options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        body.Remove("headers");
        body["model"] = JsonSerializer.SerializeToElement(model);
        body["stream"] = JsonSerializer.SerializeToElement(stream);
        return JsonSerializer.SerializeToElement(body);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => _client.GetChatCompletion(Payload(options, false), GetIdentifier(), ChatUrl(), Headers(options.Headers), cancellationToken);

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _client.GetChatCompletionSseEvents(
            Payload(options, true), ChatUrl(), Headers(options.Headers), cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(evt.Data)) continue;
            using var document = JsonDocument.Parse(evt.Data);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException($"Pyrois stream failed: {error.GetRawText()}");
            var part = document.RootElement.Deserialize<ChatCompletionUpdate>(Json)
                ?? throw new JsonException("Empty Pyrois chat chunk.");
            if (!string.IsNullOrEmpty(part.Model)) part.Model = $"pyrois/{part.Model}";
            yield return part;
        }
    }

    private ChatCompletionOptions UnifiedOptions(AIRequest request)
    {
        var metadata = request.Metadata is null ? new Dictionary<string, object?>() : new(request.Metadata);
        metadata.Remove(GetIdentifier(), out var native);
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
        => (await CompleteChatAsync(UnifiedOptions(request), cancellationToken)).ToUnifiedResponse(GetIdentifier());

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, UnifiedOptions(request), cancellationToken: cancellationToken);

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest request, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToResponseStreamParts(cancellationToken)) yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken)) yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var ui in part.Event.ToUIMessagePart(GetIdentifier())) yield return ui;
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => this.ListModels(keys.Resolve(GetIdentifier()));
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
