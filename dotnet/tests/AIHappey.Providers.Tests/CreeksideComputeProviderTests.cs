using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.CreeksideCompute;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Providers.Tests;

public sealed class CreeksideComputeProviderTests
{
    private const string ChatReply = """{"id":"chat","object":"chat.completion","created":1,"model":"model","choices":[]}""";
    private const string ResponsesReply = """{"id":"resp","model":"model","status":"completed","output":[]}""";
    private const string MessagesReply = """{"id":"msg","type":"message","role":"assistant","model":"model","content":[],"usage":{"input_tokens":1,"output_tokens":1}}""";

    [Fact]
    public void ProviderIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(() => "test-key"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<CreeksideComputeProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "creeksidecompute"));
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task NativeRoutesPreserveJsonToolsHeadersAndCallerState(string endpoint, bool streaming)
    {
        const string raw = """
            {"model":"CREEKSIDECOMPUTE/model","messages":[],"input":"Hello","max_tokens":100,
             "metadata":{"user_id":"user"},
             "reasoning":{"effort":"high"},"reasoning_effort":"future-effort","service_tier":"future-tier",
             "future_nested":{"values":[9007199254740993,true,null,{},[]]},
             "future_null":null,"future_empty":{},"future_number":9007199254740993,
             "response_format":{"type":"json_object"},
             "text":{"format":{"type":"json_schema","name":"result","schema":{"type":"object","properties":{}}}},
             "tools":[{"type":"function","name":"lookup","description":"Lookup",
               "parameters":{"type":"object","properties":{}},"input_schema":{"type":"object"},
               "function":{"name":"lookup","parameters":{"type":"object","properties":{}}}}],
             "tool_choice":"auto"}
            """;
        var original = Element(endpoint == "messages"
            ? raw.Replace("\"tool_choice\":\"auto\"", "\"tool_choice\":{\"type\":\"auto\"}")
                .Replace("\"type\":\"function\"", "\"type\":\"custom\"")
            : raw);
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://api.creeksidecompute.ai/v1/" + (endpoint == "chat" ? "chat/completions" : endpoint),
                http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
            Assert.Equal("trace", http.Headers.GetValues("x-trace-id").Single());
            Assert.Equal("none", http.Headers.GetValues("x-creekside-retention").Single());
            Assert.False(http.Headers.Contains("x-api-key"));
            Assert.False(http.Headers.Contains("x-goog-api-key"));
            Assert.False(http.Headers.Contains("openai-api-key"));
            Assert.Null(http.Headers.Host);
            if (endpoint == "messages")
                Assert.Equal("2023-06-01", http.Headers.GetValues("anthropic-version").Single());
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("model", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            if (endpoint == "chat" && streaming)
                Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            Assert.Equal("future-tier", body.GetProperty("service_tier").GetString());
            Assert.Equal("future-effort", body.GetProperty("reasoning_effort").GetString());
            foreach (var name in new[] { "metadata", "reasoning", "future_nested", "future_null", "future_empty", "future_number",
                "response_format", "text" })
                Assert.True(JsonElement.DeepEquals(original.GetProperty(name), body.GetProperty(name)), name);
            Assert.Single(body.GetProperty("tools").EnumerateArray());
            Assert.Equal("auto", endpoint == "messages"
                ? body.GetProperty("tool_choice").GetProperty("type").GetString()
                : body.GetProperty("tool_choice").GetString());
            return Json(streaming ? StreamReply(endpoint) : Reply(endpoint), streaming);
        });
        var provider = Provider(client);
        var headers = new Dictionary<string, string>
        {
            ["AUTHORIZATION"] = "untrusted",
            ["x-api-key"] = "untrusted",
            ["x-goog-api-key"] = "untrusted",
            ["openai-api-key"] = "untrusted",
            ["Host"] = "untrusted.test",
            ["x-creekside-retention"] = "none",
            ["x-trace-id"] = "trace"
        };
        switch (endpoint)
        {
            case "chat":
                var chat = original.Deserialize<ChatCompletionOptions>()!;
                chat.Headers = headers;
                chat.Stream = !streaming;
                chat.AdditionalProperties!["stream"] = JsonSerializer.SerializeToElement(!streaming);
                var chatBefore = JsonSerializer.Serialize(chat);
                if (streaming)
                {
                    var updates = await Collect(provider.CompleteChatStreamingAsync(chat));
                    Assert.NotEmpty(updates);
                    Assert.NotNull(updates[^1].Usage);
                }
                else Assert.NotNull(await provider.CompleteChatAsync(chat));
                Assert.Equal(chatBefore, JsonSerializer.Serialize(chat));
                Assert.Same(headers, chat.Headers);
                break;
            case "responses":
                var responses = original.Deserialize<ResponseRequest>(ResponseJson.Default)!;
                responses.Headers = headers;
                responses.Stream = !streaming;
                var responseBefore = JsonSerializer.Serialize(responses, ResponseJson.Default);
                if (streaming) Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(responses)));
                else Assert.NotNull(await provider.ResponsesAsync(responses));
                Assert.Equal(responseBefore, JsonSerializer.Serialize(responses, ResponseJson.Default));
                Assert.Same(headers, responses.Headers);
                break;
            default:
                var messages = original.Deserialize<MessagesRequest>()!;
                messages.Headers = headers;
                messages.Stream = !streaming;
                var messagesBefore = JsonSerializer.Serialize(messages);
                if (streaming) Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, headers)));
                else Assert.NotNull(await provider.MessagesAsync(messages, headers));
                Assert.Equal(messagesBefore, JsonSerializer.Serialize(messages));
                Assert.Same(headers, messages.Headers);
                break;
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Empty(client.DefaultRequestHeaders);
        Assert.Equal("untrusted", headers["AUTHORIZATION"]);
    }

    [Fact]
    public async Task DiscoveryCachesPerCredentialAndUsesOnlyReturnedLimits()
    {
        string? key = "first";
        var calls = 0;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var client = Client((http, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("https://api.creeksidecompute.ai/v1/models", http.RequestUri!.AbsoluteUri);
            Assert.Equal(key, http.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""
                {"data":[{"id":"creeksidecompute/model","name":"Returned name","owned_by":"owner",
                  "description":"Returned description","created":123,"context_length":9876,"max_output_tokens":456},
                 {"id":"bare"}]}
                """));
        });
        var provider = Provider(client, () => key, new AsyncCacheHelper(memory));
        var first = (await provider.ListModels()).ToArray();
        Assert.Equal(["creeksidecompute/model", "creeksidecompute/bare"], first.Select(model => model.Id));
        Assert.Equal("Returned name", first[0].Name);
        Assert.Equal("owner", first[0].OwnedBy);
        Assert.Equal("Returned description", first[0].Description);
        Assert.Equal(123, first[0].Created);
        Assert.Equal(9876, first[0].ContextWindow);
        Assert.Equal(456, first[0].MaxTokens);
        Assert.Null(first[1].ContextWindow);
        Assert.Null(first[1].MaxTokens);
        first[0].Name = "caller mutation";
        Assert.Equal("Returned name", (await provider.ListModels()).First().Name);
        Assert.Equal(1, calls);
        key = "second";
        await provider.ListModels();
        Assert.Equal(2, calls);
        key = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListModels());
        Assert.Equal(2, calls);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task MissingCredentialsFailWithoutNetworkAccess()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Network must not be used."));
        var provider = Provider(client, () => " ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new() { Model = "model" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResponsesAsync(
            Element("""{"model":"model","input":"hi"}""").Deserialize<ResponseRequest>()!));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.MessagesAsync(
            Element("""{"model":"model","messages":[],"max_tokens":10}""").Deserialize<MessagesRequest>()!, []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnifiedAndUiMetadataUsesNativeResponses(bool ui)
    {
        var native = Element("""{"future":{"large":9007199254740993,"array":[true,null,{},[]]},"future_null":null,"reasoning":{"effort":"high"}}""");
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("/v1/responses", http.RequestUri!.AbsolutePath);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            foreach (var property in native.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.Equal(ui, body.GetProperty("stream").GetBoolean());
            return Json(ui ? StreamReply("responses") : ResponsesReply, ui);
        });
        var provider = Provider(client);
        if (ui)
            await Collect(provider.StreamAsync(JsonSerializer.Deserialize<ChatRequest>("""
                {"model":"creeksidecompute/model","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hello"}]}],
                 "providerMetadata":{"creeksidecompute":{"future":{"large":9007199254740993,"array":[true,null,{},[]]},"future_null":null,"reasoning":{"effort":"high"}}}}
                """)!));
        else
            await provider.ExecuteUnifiedAsync(new AIRequest
            {
                Model = "creeksidecompute/model", Input = new AIInput { Text = "Hello" },
                Metadata = new() { ["creeksidecompute"] = native }
            });
    }

    [Fact]
    public async Task CancellationReachesTransport()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = Client(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(ChatReply);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).CompleteChatAsync(
            new() { Model = "model" }, cancellation.Token));
    }

    [Fact]
    public async Task DeferredStreamsCaptureTheirOwnCredentialsAndRequest()
    {
        string? key = "first";
        var credentials = new List<string?>();
        using var client = Client((http, _) =>
        {
            credentials.Add(http.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(StreamReply("chat"), true));
        });
        var provider = Provider(client, () => key);
        var request = new ChatCompletionOptions { Model = "creeksidecompute/model" };
        var first = provider.CompleteChatStreamingAsync(request);
        key = "second";
        var second = provider.CompleteChatStreamingAsync(request);
        request.Model = "caller-changed";
        await Collect(first);
        await Collect(second);
        Assert.Equal(["first", "second"], credentials);
        Assert.Equal("caller-changed", request.Model);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public void UnsupportedModalitiesAreExplicit()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Network must not be used."));
        var provider = Provider(client);
        Assert.Throws<NotSupportedException>(() => provider.ImageRequest(null!));
        Assert.Throws<NotSupportedException>(() => provider.EmbeddingRequestAsync(null!));
        Assert.Throws<NotSupportedException>(() => provider.SpeechRequest(null!));
        Assert.Throws<NotSupportedException>(() => provider.TranscriptionRequest(null!));
        Assert.Throws<NotSupportedException>(() => provider.RerankingRequest(null!));
        Assert.Throws<NotSupportedException>(() => provider.CreateLiveSessionAsync(null!));
    }

    [Fact]
    public async Task DiscoveryDoesNotCacheFailuresOrFabricateModels()
    {
        var calls = 0;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var client = Client((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Json("""{"data":[]}""")));
        var provider = Provider(client, cache: new AsyncCacheHelper(memory));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Empty(await provider.ListModels());
        Assert.Equal(2, calls);
    }

    private static string Reply(string endpoint) => endpoint switch
    {
        "chat" => ChatReply, "messages" => MessagesReply, _ => ResponsesReply
    };

    private static string StreamReply(string endpoint) => endpoint switch
    {
        "chat" => """
            data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call","type":"function","function":{"name":"lookup","arguments":"{}"}}]},"finish_reason":null}]}

            data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"model","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

            data: [DONE]

            """,
        "messages" => """
            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call","name":"lookup","input":{}}}

            event: message_stop
            data: {"type":"message_stop"}

            """,
        _ => """
            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp","model":"model","status":"completed","output":[]}}

            """
    };

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value, bool stream = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, stream ? "text/event-stream" : "application/json")
    };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));
    private static CreeksideComputeProvider Provider(HttpClient client, Func<string?>? key = null, AsyncCacheHelper? cache = null)
        => new(new Keys(key ?? (() => "test-key")), new Factory(client), cache ?? new AsyncCacheHelper());
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var parts = new List<T>();
        await foreach (var part in stream) parts.Add(part);
        return parts;
    }
    private sealed class Keys(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key();
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
