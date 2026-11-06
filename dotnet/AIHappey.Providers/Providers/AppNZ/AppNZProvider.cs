using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
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
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AppNZ;

public partial class AppNZProvider : IModelProvider, IUnifiedModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClient _downloadClient;
    private readonly AsyncCacheHelper _memoryCache;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AppNZProvider(IApiKeyResolver keyResolver, AsyncCacheHelper memoryCache,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = memoryCache;
        _httpClientFactory = httpClientFactory;
        _downloadClient = httpClientFactory.CreateClient();
    }

    public string GetIdentifier() => "appnz";

    // Providers are singletons. Each operation gets its own client/header snapshot.
    private HttpClient CreateClient(string? key = null)
    {
        key ??= _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No AppNZ API key.");

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://app.nz/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static Dictionary<string, object?> Options(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return [];

        if (!metadata.TryGetValue("appnz", out var value) || value is null)
            return [];

        var element = JsonSerializer.SerializeToElement(value, Json);
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("AppNZ provider options must be an object.");

        return element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    private static async Task<JsonElement> SendJsonAsync(HttpClient client, HttpMethod method,
        string endpoint, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, endpoint);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AppNZ {endpoint} failed ({(int)response.StatusCode}): {raw}", null, response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var result = document.RootElement.Clone();
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException($"AppNZ {endpoint} failed: {raw}");
        return result;
    }

    private static bool IsAgentModel(string? model)
        => model is "agent" or "appnz/agent";

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (IsAgentModel(options.Model))
            return (await ExecuteAgentAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

        using var client = CreateClient();
        var headers = PrepareChatHeaders(options);
        return await client.GetChatCompletion(options, GetIdentifier(), headers: headers, ct: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsAgentModel(options.Model))
        {
            var stream = StreamAgentAsync(
                options.ToUnifiedRequest(GetIdentifier()),
                cancellationToken);

            await foreach (var update in stream.ToChatCompletionUpdates(cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        using var client = CreateClient();
        var headers = PrepareChatHeaders(options);
        await foreach (var part in client.GetChatCompletionUpdates(options, GetIdentifier(), headers: headers, ct: cancellationToken))
            yield return part;
    }

    private Dictionary<string, string> PrepareChatHeaders(ChatCompletionOptions options)
    {
        var headers = options.Headers ?? new Dictionary<string, string>();
        var merged = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        foreach (var header in options.Headers ?? [])
            merged[header.Key] = header.Value;
        merged.Remove("Authorization");
        merged.Remove("X-Api-Key");
        merged.Remove("X-AppNZ-Key");
        return merged;
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        if (IsAgentModel(request.Model))
            return (await ExecuteAgentAsync(ToAgentMessagesRequest(request), cancellationToken)).ToMessagesResponse();

        using var client = CreateClient();
        return await client.PostMessages(request, GetIdentifier(), FilterMessagesHeaders(headers), ct: cancellationToken);
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsAgentModel(request.Model))
        {
            await foreach (var part in StreamAgentAsync(ToAgentMessagesRequest(request), cancellationToken)
                .ToMessageStreamParts(request.Model, cancellationToken))
                yield return part;
            yield break;
        }

        using var client = CreateClient();
        await foreach (var part in client.PostMessagesStreaming(request, GetIdentifier(), FilterMessagesHeaders(headers), ct: cancellationToken))
            yield return part;
    }

    private AIRequest ToAgentMessagesRequest(MessagesRequest request)
    {
        return request.ToUnifiedRequest(GetIdentifier());
    }

    private static Dictionary<string, string> FilterMessagesHeaders(Dictionary<string, string> headers)
        => headers.Where(h => h.Key.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase)
            || h.Key.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsAgentModel(request.Model))
            return await ExecuteAgentAsync(request, cancellationToken);

        var type = (await this.GetModel(request.Model, cancellationToken)).Type;
        return type switch
        {
            "image" => await this.ExecuteUnifiedImageAsync(request, cancellationToken),
            "speech" => await this.ExecuteUnifiedSpeechAsync(request, cancellationToken),
            "transcription" => await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken),
            "video" => await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken),
            _ => await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsAgentModel(request.Model))
        {
            await foreach (var part in StreamAgentAsync(request, cancellationToken))
                yield return part;
            yield break;
        }

        var type = (await this.GetModel(request.Model, cancellationToken)).Type;
        var stream = type switch
        {
            "image" => this.StreamUnifiedImageAsync(request, cancellationToken),
            "speech" => this.StreamUnifiedSpeechAsync(request, cancellationToken),
            "transcription" => this.StreamUnifiedTranscriptionAsync(request, cancellationToken),
            "video" => this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken),
            _ => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
        };
        await foreach (var part in stream)
            yield return part;
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("AppNZ does not document a reranking endpoint.");

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken)
        => throw new NotSupportedException("AppNZ does not document a realtime endpoint.");

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
