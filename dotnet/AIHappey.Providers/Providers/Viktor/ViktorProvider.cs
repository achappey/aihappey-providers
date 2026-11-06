using System.Net.Http.Headers;
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
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Viktor;

public sealed partial class ViktorProvider : IModelProvider
{
    private readonly IApiKeyResolver _keys;
    private readonly AsyncCacheHelper _cache;
    private readonly HttpClient _client;

    public ViktorProvider(IApiKeyResolver keys, AsyncCacheHelper cache, IHttpClientFactory clients)
    {
        _keys = keys;
        _cache = cache;
        _client = clients.CreateClient();
        _client.BaseAddress = new Uri("https://api.viktor.com/api/compat/");
        // Agent runs can take up to ten minutes.
        _client.Timeout = TimeSpan.FromMinutes(11);
    }

    public string GetIdentifier() => "viktor";

    private Dictionary<string, string> RequestHeaders(IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Viktor API key.");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers ?? [])
            result[header.Key] = header.Value;
        result["Authorization"] = new AuthenticationHeaderValue("Bearer", key).ToString();
        return result;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => _client.GetChatCompletion(options, GetIdentifier(), headers: RequestHeaders(options.Headers), ct: cancellationToken);

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => _client.GetChatCompletionUpdates(options, GetIdentifier(), headers: RequestHeaders(options.Headers), ct: cancellationToken);

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => _client.GetResponses(options, GetIdentifier(), headers: RequestHeaders(options.Headers), ct: cancellationToken);

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => _client.GetResponsesUpdates(options, GetIdentifier(), headers: RequestHeaders(options.Headers), ct: cancellationToken);

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        request.Stream = false;
        return this.GetMessage(_client, request, headers: RequestHeaders(headers), cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        request.Stream = true;
        return this.GetMessages(_client, request, headers: RequestHeaders(headers), cancellationToken: cancellationToken);
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaResponsesAsync(request, cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await _cache.GetOrCreateAsync<IEnumerable<Model>>(this.GetCacheKey(key), async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Viktor HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);

            using var document = JsonDocument.Parse(body);
            var models = new List<Model>();
            foreach (var entry in document.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id))
                    throw new JsonException("Viktor model is missing an ID.");
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = id,
                    OwnedBy = entry.TryGetProperty("owned_by", out var owner) ? owner.GetString() ?? "Viktor" : "Viktor",
                    Type = "chat",
                    Tags = ["agent"]
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }
}
