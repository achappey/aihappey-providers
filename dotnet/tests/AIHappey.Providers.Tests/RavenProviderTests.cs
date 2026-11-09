using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Raven;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class RavenProviderTests
{
    private const string BaseUrl = "https://raven.graphn.ai/api/inference/";
    private const string Native = """{"future_null":null,"future_empty":{},"future_nested":{"values":[true,null,[],{}],"large":9007199254740993}}""";
    private const string ChatReply = """
        {"id":"chat_1","object":"chat.completion","created":1,"model":"org/model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15},"future":{"large":9007199254740993}}
        """;
    private const string ChatEvents = """
        : keepalive

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"org/model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}],"future":{"large":9007199254740993}}

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"org/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}

        data: [DONE]

        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<RavenProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "raven"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_preserves_raw_options_and_replies_and_uses_request_scoped_auth(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(BaseUrl + "v1/chat/completions", http.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, http.Method);
            AssertAuth(http);
            Assert.True(http.Headers.Contains("X-Trace"));
            Assert.False(http.Headers.Contains("X-Other-Key"));
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal("org/model", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.Equal(42, body.GetProperty("unknown_top_level").GetInt32());
            return Reply(streaming ? ChatEvents : ChatReply, streaming);
        });
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"raven/org/model","messages":[{"role":"user","content":"Hi"}],"unknown_top_level":42,"raven":
            """ + Native + "}")!;
        options.Headers = new() { ["authorization"] = "******", ["X-Trace"] = "trace", ["X-Other-Key"] = "hidden" };
        var before = JsonSerializer.Serialize(options);
        var provider = Provider(client);
        if (streaming)
        {
            var updates = await Collect(provider.CompleteChatStreamingAsync(options));
            Assert.Equal(2, updates.Count);
            Assert.Equal("raven/org/model", updates[0].Model);
            Assert.Equal(9007199254740993L, updates[0].AdditionalProperties!["future"].GetProperty("large").GetInt64());
            Assert.Equal(15, updates[1].Usage!.TotalTokens);
        }
        else
        {
            var result = await provider.CompleteChatAsync(options);
            Assert.Equal("raven/org/model", result.Model);
            Assert.Equal(9007199254740993L, result.AdditionalProperties!["future"].GetProperty("large").GetInt64());
        }
        Assert.Equal(before, JsonSerializer.Serialize(options));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Null(client.BaseAddress);
    }

    [Theory]
    [InlineData("unified")]
    [InlineData("unified-stream")]
    [InlineData("messages")]
    [InlineData("messages-stream")]
    [InlineData("responses")]
    [InlineData("responses-stream")]
    [InlineData("ui")]
    public async Task Mapped_adapters_use_chat_not_unpublished_native_endpoints(string adapter)
    {
        var streaming = adapter.EndsWith("stream") || adapter == "ui";
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(BaseUrl + "v1/chat/completions", http.RequestUri!.AbsoluteUri);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal("org/model", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            return Reply(streaming ? ChatEvents : ChatReply, streaming);
        });
        var provider = Provider(client);
        var unified = new AIRequest
        {
            ProviderId = "raven", Model = "raven/org/model", Input = new() { Text = "Hi" },
            Metadata = new() { ["raven"] = Element(Native) }
        };
        var messages = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"raven/org/model","max_tokens":20,"messages":[{"role":"user","content":"Hi"}],"raven":
            """ + Native + "}")!;
        var responses = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"raven/org/model","input":"Hi","raven":
            """ + Native + "}")!;
        switch (adapter)
        {
            case "unified": Assert.NotNull((await provider.ExecuteUnifiedAsync(unified)).Output); break;
            case "unified-stream": Assert.NotEmpty(await Collect(provider.StreamUnifiedAsync(unified))); break;
            case "messages": Assert.NotEmpty((await provider.MessagesAsync(messages, [])).Content); break;
            case "messages-stream": Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, []))); break;
            case "responses": Assert.NotNull((await provider.ResponsesAsync(responses)).Output); break;
            case "responses-stream": Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(responses))); break;
            default:
                Assert.NotEmpty(await Collect(provider.StreamAsync(JsonSerializer.Deserialize<ChatRequest>("""
                    {"model":"raven/org/model","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}],
                     "providerMetadata":{"raven":
                    """ + Native + "}}")!)));
                break;
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Embeddings_use_shared_transport_with_raw_options(bool native)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(BaseUrl + "v1/embeddings", http.RequestUri!.AbsoluteUri);
            AssertAuth(http);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal("org/embedding", body.GetProperty("model").GetString());
            Assert.Equal("Hello", body.GetProperty("input")[0].GetString());
            Assert.Equal(2, body.GetProperty("dimensions").GetInt32());
            return Reply("""
                {"object":"list","model":"org/embedding","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
                 "usage":{"prompt_tokens":1,"total_tokens":1},"future":{"large":9007199254740993}}
                """);
        });
        var provider = Provider(client);
        if (native)
        {
            var request = JsonSerializer.Deserialize<OpenAIEmbeddingRequest>("""
                {"model":"org/embedding","input":["Hello"],"dimensions":2,
                """ + Native[1..])!;
            var result = await provider.OpenAIEmbeddingRequestAsync(request);
            Assert.Equal("raven/org/embedding", result.Model);
            Assert.Single(result.Data);
        }
        else
        {
            var result = await provider.EmbeddingRequestAsync(new EmbeddingRequest
            {
                Model = "org/embedding", Values = ["Hello"],
                ProviderOptions = new() { ["raven"] = Element("""{"dimensions":2,""" + Native[1..]) }
            });
            Assert.Single(result.Embeddings);
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Null(client.BaseAddress);
    }

    [Theory]
    [InlineData("""{"id":"rank_1","results":[{"index":1,"relevance_score":0.9}],"future":null}""")]
    [InlineData("""[{"index":1,"score":0.9,"text":"second"}]""")]
    public async Task Rerank_preserves_metadata_and_raw_body_and_accepts_TEI_rankings(string response)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(BaseUrl + "v1/rerank", http.RequestUri!.AbsoluteUri);
            AssertAuth(http);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal("org/reranker", body.GetProperty("model").GetString());
            Assert.Equal("query", body.GetProperty("query").GetString());
            Assert.Equal("second", body.GetProperty("documents")[1].GetString());
            Assert.Equal(1, body.GetProperty("top_n").GetInt32());
            var reply = Reply(response);
            reply.Headers.Add("X-Request-Id", "rank-request");
            return reply;
        });
        var result = await Provider(client).RerankingRequest(RerankRequest());
        var ranking = Assert.Single(result.Ranking);
        Assert.Equal(1, ranking.Index);
        Assert.Equal(0.9f, ranking.RelevanceScore);
        Assert.True(JsonElement.DeepEquals(Element(response), JsonSerializer.SerializeToElement(result.Response!.Body)));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Model_discovery_is_key_scoped_and_qualifies_ids()
    {
        var key = "first";
        var calls = 0;
        using var client = Client((http, _) =>
        {
            calls++;
            Assert.Equal(BaseUrl + "v1/models", http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer " + key, http.Headers.Authorization?.ToString());
            return Task.FromResult(Reply("""
                {"data":[{"id":"org/model","owned_by":"org","created":1},{"id":"org/model"},{"id":""}]}
                """));
        });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var provider = new RavenProvider(new Keys(() => key), new AsyncCacheHelper(memory), new Factory(client));
        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("raven/org/model", model.Id);
        Assert.Equal("org", model.OwnedBy);
        Assert.Single(await provider.ListModels());
        Assert.Equal(1, calls);
        key = "second";
        Assert.Single(await provider.ListModels());
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Missing_auth_unsupported_operations_and_invalid_rerank_do_not_send_requests()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Must not send"));
        var provider = new RavenProvider(new Keys(() => null), new AsyncCacheHelper(), new Factory(client));
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new() { Model = "model" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAISpeechRequestAsync(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAITranscriptionRequestAsync(null!));
        var request = RerankRequest();
        request.TopN = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RerankingRequest(request));
    }

    [Fact]
    public async Task Cancellation_and_upstream_errors_are_not_hidden()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"bad Raven request"}}""")
        }));
        var provider = Provider(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new() { Model = "model" }));
        Assert.Contains("bad Raven request", error.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RerankingRequest(RerankRequest()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteChatAsync(new() { Model = "model" }, cancellation.Token));
    }

    private static RerankingRequest RerankRequest() => new()
    {
        Model = "raven/org/reranker", Query = "query", TopN = 1,
        Documents = new() { Type = "text", Values = Element("""["first",{"text":"second"}]""") },
        ProviderOptions = new() { ["raven"] = Element(Native) }
    };

    private static void AssertNative(JsonElement body)
    {
        foreach (var property in Element(Native).EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
        Assert.False(body.TryGetProperty("raven", out _));
    }

    private static void AssertAuth(HttpRequestMessage http)
    {
        Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
    }

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Reply(string value, bool streaming = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
    };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static RavenProvider Provider(HttpClient client) => new(new Keys(), new AsyncCacheHelper(), new Factory(client));
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var results = new List<T>();
        await foreach (var part in stream) results.Add(part);
        return results;
    }
    private sealed class Keys(Func<string?>? resolve = null) : IApiKeyResolver
    {
        public string? Resolve(string provider) => resolve is null ? "test-key" : resolve();
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
