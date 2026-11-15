using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Models;
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

namespace AIHappey.Core.Providers.Boundless;

public sealed partial class BoundlessProvider : IModelProvider
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public BoundlessProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.inference.boundless.network/");
    }

    public string GetIdentifier() => "boundless";

    private string Authorization()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Boundless API key.");
        return $"Bearer {key}";
    }

    private Dictionary<string, string> AuthHeaders(IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers ?? [])
            if (!name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("host", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                && !ProviderCredentialHeaders.IsProviderKey(name))
                result[name] = value;
        result["Authorization"] = Authorization();
        return result;
    }

    private static string NativeModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var result = model.StartsWith("boundless/", StringComparison.OrdinalIgnoreCase) ? model["boundless/".Length..] : model;
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        return result;
    }

    private static T MergeNativeOptions<T>(T request, Dictionary<string, JsonElement>? properties, JsonSerializerOptions? json = null)
    {
        json ??= RequestJson;
        var body = JsonSerializer.SerializeToNode(request, json)!.AsObject();
        body.Remove("boundless");
        if (properties?.TryGetValue("boundless", out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Boundless provider options must be an object.", nameof(request));
            foreach (var field in options.EnumerateObject())
                if (!body.ContainsKey(field.Name) && field.Name is not ("model" or "stream" or "headers"))
                    body[field.Name] = JsonNode.Parse(field.Value.GetRawText());
        }
        return body.Deserialize<T>(json) ?? throw new JsonException("Invalid Boundless request.");
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var headers = AuthHeaders(options.Headers);
        options = MergeNativeOptions(options, options.AdditionalProperties);
        options.Model = NativeModel(options.Model);
        options.Stream = false;
        return _client.GetChatCompletion(options, GetIdentifier(), headers: headers, ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var headers = AuthHeaders(options.Headers);
        options = MergeNativeOptions(options, options.AdditionalProperties);
        options.Model = NativeModel(options.Model);
        options.Stream = true;
        return _client.GetChatCompletionUpdates(options, GetIdentifier(), headers: headers, ct: cancellationToken);
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var headers = AuthHeaders(options.Headers);
        var executionMetadata = options.ExecutionMetadata;
        options = MergeNativeOptions(options, options.AdditionalProperties, ResponseJson.Default);
        options.ExecutionMetadata = executionMetadata;
        options.Model = NativeModel(options.Model);
        options.Stream = false;
        return _client.GetResponses(options, GetIdentifier(), headers: headers, ct: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var headers = AuthHeaders(options.Headers);
        var executionMetadata = options.ExecutionMetadata;
        options = MergeNativeOptions(options, options.AdditionalProperties, ResponseJson.Default);
        options.ExecutionMetadata = executionMetadata;
        options.Model = NativeModel(options.Model);
        options.Stream = true;
        return _client.GetResponsesUpdates(options, GetIdentifier(), headers: headers, ct: cancellationToken);
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var requestHeaders = AuthHeaders((request.Headers ?? []).Concat(headers));
        requestHeaders.TryAdd("anthropic-version", "2023-06-01");
        request = MergeNativeOptions(request, request.AdditionalProperties);
        request.Model = NativeModel(request.Model);
        request.Stream = false;
        return this.GetMessage(_client, request, headers: requestHeaders, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var requestHeaders = AuthHeaders((request.Headers ?? []).Concat(headers));
        requestHeaders.TryAdd("anthropic-version", "2023-06-01");
        request = MergeNativeOptions(request, request.AdditionalProperties);
        request.Model = NativeModel(request.Model);
        request.Stream = true;
        return this.GetMessages(_client, request, headers: requestHeaders, cancellationToken: cancellationToken);
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", Authorization());
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Boundless model discovery failed ({(int)response.StatusCode}): {raw}", null, response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array)
            throw new JsonException("Boundless model discovery requires a data array.");
        return data.EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id))
                throw new JsonException("Boundless returned a model without an ID.");
            var model = JsonSerializer.Deserialize<Model>(item, JsonSerializerOptions.Web)
                ?? throw new JsonException("Boundless returned an invalid model.");
            model.Id = id.ToModelId(GetIdentifier());
            model.Name = string.IsNullOrWhiteSpace(model.Name) ? id : model.Name;
            model.Type = id.Equals("convaiinnovations/laya", StringComparison.OrdinalIgnoreCase)
                || id.Equals("laya", StringComparison.OrdinalIgnoreCase) ? "decision" : "language";
            if (item.TryGetProperty("context_length", out var context)
                && context.ValueKind == JsonValueKind.Number && context.TryGetInt32(out var length))
                model.ContextWindow = length;
            return model;
        }).ToArray();
    }
}
