using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Lutheos;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class LutheosProviderTests
{
    private const string ChatReply = """
        {"id":"chat_1","object":"chat.completion","created":1,"model":"vendor/model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3},
         "future_reply":{"large":9007199254740993,"values":[null,{},[]]}}
        """;
    private const string MessageReply = """
        {"id":"msg_1","type":"message","role":"assistant","model":"vendor/model",
         "content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn",
         "usage":{"input_tokens":2,"output_tokens":1},
         "future_reply":{"large":9007199254740993,"values":[null,{},[]]}}
        """;
    private const string ChatEvents = """
        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}],"future_reply":{"large":9007199254740993,"values":[null,{},[]]}}

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

        data: [DONE]


        """;
    private const string NativeFields = """
        "metadata":{"user_id":"user","lutheos":"application"},
        "future_null":null,"future_empty":{},"future_array":[],
        "future_nested":{"large":9007199254740993,"values":[true,null,{},[]]}
        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<LutheosProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "lutheos"));
    }

    [Fact]
    public async Task Discovery_uses_authenticated_key_scoped_cache_and_preserves_vendor_prefix()
    {
        var calls = 0;
        using var client = Client((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://lutheos.com/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal(calls++ == 0 ? "first" : "second", request.Headers.Authorization!.Parameter);
            return Task.FromResult(Reply("""
                {"data":[{"id":"vendor/model","owned_by":"vendor","description":"Available"},
                         {"id":""},{"object":"model"}]}
                """));
        });
        var keys = new Keys("first");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = Provider(client, keys, cache);
        var first = Assert.Single(await provider.ListModels());
        Assert.Equal("lutheos/vendor/model", first.Id);
        Assert.Equal("vendor/model", first.Name);
        Assert.Equal("vendor", first.OwnedBy);
        Assert.Equal("Available", first.Description);
        Assert.Single(await provider.ListModels());
        keys.Key = "second";
        Assert.Single(await provider.ListModels());
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_endpoints_preserve_raw_metadata_and_use_request_scoped_auth(bool messages, bool streaming)
    {
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://lutheos.com/v1/" + (messages ? "messages" : "chat/completions"),
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("lutheos-test-key", request.Headers.Authorization.Parameter);
            Assert.Equal(streaming ? "text/event-stream" : "application/json",
                Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("keep", Assert.Single(request.Headers.GetValues("X-Request")));
            var body = Element(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("vendor/model", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            foreach (var field in Element("{" + NativeFields + "}").EnumerateObject())
                Assert.True(JsonElement.DeepEquals(field.Value, body.GetProperty(field.Name)), field.Name);
            return Reply(streaming
                ? messages
                    ? "event: message_start\ndata: {\"type\":\"message_start\",\"message\":" + MessageReply.Replace("\n", "")
                        + "}\n\nevent: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
                    : ChatEvents
                : messages ? MessageReply : ChatReply, streaming);
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "unchanged");
        var provider = Provider(client);
        if (messages)
        {
            var headers = new Dictionary<string, string> { ["authorization"] = "Basic caller", ["X-Request"] = "keep" };
            var request = JsonSerializer.Deserialize<MessagesRequest>(
                "{\"model\":\"vendor/model\",\"max_tokens\":100,\"messages\":[]," + NativeFields + "}")!;
            if (streaming)
            {
                var parts = await Collect(provider.MessagesStreamingAsync(request, headers));
                Assert.Equal("lutheos/vendor/model", parts[0].Message!.Model);
                Assert.Contains("9007199254740993", JsonSerializer.Serialize(parts[0]));
            }
            else
            {
                var response = await provider.MessagesAsync(request, headers);
                Assert.Equal("lutheos/vendor/model", response.Model);
                Assert.Contains("9007199254740993", JsonSerializer.Serialize(response));
            }
            Assert.Equal("Basic caller", headers["authorization"]);
        }
        else
        {
            var request = JsonSerializer.Deserialize<ChatCompletionOptions>(
                "{\"model\":\"vendor/model\",\"messages\":[]," + NativeFields + "}")!;
            request.Headers = new Dictionary<string, string> { ["authorization"] = "Basic caller", ["X-Request"] = "keep" };
            if (streaming)
            {
                var parts = await Collect(provider.CompleteChatStreamingAsync(request));
                Assert.Equal("lutheos/vendor/model", parts[0].Model);
                Assert.Contains("9007199254740993", JsonSerializer.Serialize(parts[0]));
            }
            else
            {
                var response = await provider.CompleteChatAsync(request);
                Assert.Equal("lutheos/vendor/model", response.Model);
                Assert.Contains("9007199254740993", JsonSerializer.Serialize(response));
            }
            Assert.Equal("Basic caller", request.Headers["authorization"]);
        }
        Assert.Equal("Basic", client.DefaultRequestHeaders.Authorization.Scheme);
        Assert.Equal("unchanged", client.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Theory]
    [InlineData("unified")]
    [InlineData("responses")]
    [InlineData("unified-stream")]
    [InlineData("responses-stream")]
    [InlineData("ui")]
    public async Task Adapters_only_use_chat_completions(string adapter)
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            calls++;
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("lutheos-test-key", request.Headers.Authorization!.Parameter);
            var body = Element(await request.Content!.ReadAsStringAsync(ct));
            var streaming = body.GetProperty("stream").GetBoolean();
            return Reply(streaming ? ChatEvents : ChatReply, streaming);
        });
        var provider = Provider(client);
        var unified = new AIRequest
        {
            ProviderId = "lutheos", Model = "vendor/model", Input = new AIInput { Text = "Hello" }
        };
        switch (adapter)
        {
            case "unified":
                Assert.NotNull(await provider.ExecuteUnifiedAsync(unified));
                break;
            case "responses":
                Assert.NotNull(await provider.ResponsesAsync(new ResponseRequest { Model = "vendor/model", Input = "Hello" }));
                break;
            case "unified-stream":
                Assert.NotEmpty(await Collect(provider.StreamUnifiedAsync(unified)));
                break;
            case "responses-stream":
                Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(new ResponseRequest { Model = "vendor/model", Input = "Hello" })));
                break;
            default:
                Assert.NotEmpty(await Collect(provider.StreamAsync(new ChatRequest
                {
                    Model = "vendor/model",
                    Messages = [new UIMessage { Id = "u", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] }]
                })));
                break;
        }
        Assert.Equal(1, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Missing_key_never_sends_HTTP(string? key)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected."));
        var provider = Provider(client, new Keys(key));
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.CompleteChatAsync(new ChatCompletionOptions { Model = "model" }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Collect(provider.CompleteChatStreamingAsync(new ChatCompletionOptions { Model = "model" })));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.MessagesAsync(new MessagesRequest { Model = "model", MaxTokens = 100, Messages = [] }, []));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Collect(provider.MessagesStreamingAsync(new MessagesRequest { Model = "model", MaxTokens = 100, Messages = [] }, [])));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Discovery_propagates_HTTP_errors()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ListModels());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private static LutheosProvider Provider(HttpClient client, Keys? keys = null, IMemoryCache? cache = null)
        => new(keys ?? new Keys(), new AsyncCacheHelper(cache), new Clients(client));
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Reply(string value, bool streaming = false) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
    private sealed class Keys(string? key = "lutheos-test-key") : IApiKeyResolver
    {
        public string? Key { get; set; } = key;
        public string? Resolve(string provider)
        {
            Assert.Equal("lutheos", provider);
            return Key;
        }
    }
    private sealed class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
