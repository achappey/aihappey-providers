using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.JuiceFactory;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class JuiceFactoryProviderTests
{
    private const string Reply = """
        {"id":"chat_1","object":"chat.completion","created":1,"model":"qwen3.8-27b",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15},"future":{"enabled":true}}
        """;
    private const string Native = """
        {"reasoning_effort":"future-effort","future_null":null,"future_empty":{},
         "future_array":[true,null,{},[]],"large":9007199254740993}
        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(() => "test-key"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<JuiceFactoryProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "juicefactory"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_preserves_vision_and_native_options_without_mutating_request(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("https://api.juicefactory.ai/v1/chat/completions", http.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, http.Method);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal("qwen3.8-27b", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.Equal("image_url", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.True(body.GetProperty("top_level_unknown").GetBoolean());
            Assert.True(http.Headers.Contains("X-Trace"));
            return streaming ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"qwen3.8-27b","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}],"future":{"enabled":true}}

                    data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"qwen3.8-27b","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]

                    """, Encoding.UTF8, "text/event-stream")
            } : Json(Reply);
        });
        var request = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"juicefactory/qwen3.8-27b","messages":[{"role":"user","content":[
             {"type":"text","text":"Read this invoice"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AA=="}}]}],
             "top_level_unknown":true,"juicefactory":
            """ + Native + "}")!;
        request.Headers = new() { ["Authorization"] = "untrusted", ["X-Trace"] = "trace" };
        var before = JsonSerializer.Serialize(request);
        var provider = Provider(client);
        if (streaming)
        {
            var updates = await Collect(provider.CompleteChatStreamingAsync(request));
            Assert.Equal(2, updates.Count);
            Assert.Equal("juicefactory/qwen3.8-27b", updates[0].Model);
            Assert.True(updates[0].AdditionalProperties!["future"].GetProperty("enabled").GetBoolean());
        }
        else
        {
            var result = await provider.CompleteChatAsync(request);
            Assert.Equal("juicefactory/qwen3.8-27b", result.Model);
            Assert.True(result.AdditionalProperties!["future"].GetProperty("enabled").GetBoolean());
        }
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Null(client.BaseAddress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_preserves_raw_provider_metadata(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            AssertNative(Element(await http.Content!.ReadAsStringAsync(ct)));
            return streaming ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"qwen3.8-27b","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

                    data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"qwen3.8-27b","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]

                    """,
                    Encoding.UTF8, "text/event-stream")
            } : Json(Reply);
        });
        var request = new AIRequest
        {
            Model = "qwen3.8-27b",
            Input = new() { Text = "Hello" },
            Metadata = new() { ["juicefactory"] = Element(Native) }
        };
        var before = JsonSerializer.Serialize(request);
        if (streaming) await Collect(Provider(client).StreamUnifiedAsync(request));
        else await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Embeddings_preserve_native_fields_and_map_response(bool vercel)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("https://api.juicefactory.ai/v1/embeddings", http.RequestUri!.AbsoluteUri);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("embedding-model", body.GetProperty("model").GetString());
            Assert.Equal(2, body.GetProperty("dimensions").GetInt32());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("future_null").ValueKind);
            Assert.True(body.GetProperty("future").GetProperty("enabled").GetBoolean());
            Assert.False(body.TryGetProperty("juicefactory", out _));
            return Json("""
                {"object":"list","model":"embedding-model","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
                 "usage":{"prompt_tokens":3,"total_tokens":3},"future":{"enabled":true}}
                """);
        });
        var provider = Provider(client);
        if (vercel)
        {
            var request = new EmbeddingRequest
            {
                Model = "juicefactory/embedding-model", Values = ["Hello"],
                ProviderOptions = new()
                {
                    ["juicefactory"] = Element("""{"dimensions":2,"future_null":null,"future":{"enabled":true}}""")
                }
            };
            var result = await provider.EmbeddingRequestAsync(request);
            Assert.Equal(3, result.Usage!.Tokens);
            Assert.Single(result.Embeddings);
        }
        else
        {
            var request = JsonSerializer.Deserialize<OpenAIEmbeddingRequest>("""
                {"model":"juicefactory/embedding-model","input":["Hello"],"dimensions":2,
                 "future_null":null,"future":{"enabled":true}}
                """)!;
            var before = JsonSerializer.Serialize(request);
            var result = await provider.OpenAIEmbeddingRequestAsync(request);
            Assert.Equal("juicefactory/embedding-model", result.Model);
            Assert.Single(result.Data);
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Models_use_authenticated_endpoint_and_cache_results()
    {
        var calls = 0;
        using var client = Client((http, _) =>
        {
            Assert.Equal("https://api.juicefactory.ai/v1/models", http.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, http.Method);
            calls++;
            return Task.FromResult(Json("""{"data":[{"id":"qwen3.8-27b","owned_by":"qwen"},{"id":"embed","type":"embedding"}]}"""));
        });
        var provider = Provider(client);
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["juicefactory/qwen3.8-27b", "juicefactory/embed"], models.Select(model => model.Id));
        Assert.Equal("embedding", models[1].Type);
        await provider.ListModels();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Missing_key_skips_discovery_and_rejects_requests_before_network()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network call")));
        var provider = Provider(client, () => null);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new() { Model = "model" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(provider.CompleteChatStreamingAsync(new() { Model = "model" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIEmbeddingRequestAsync(new()
        {
            Model = "model", Input = Element("\"hello\"")
        }));
    }

    [Fact]
    public async Task Credentials_are_resolved_per_call_without_mutating_shared_client()
    {
        var key = "first";
        using var client = new HttpClient(new Handler((http, _) =>
        {
            Assert.Equal(key, http.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(Reply));
        }));
        client.BaseAddress = new Uri("https://unrelated.example/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "unrelated");
        var provider = Provider(client, () => key);
        await provider.CompleteChatAsync(new() { Model = "model" });
        key = "second";
        await provider.CompleteChatAsync(new() { Model = "model" });
        Assert.Equal("unrelated", client.DefaultRequestHeaders.Authorization.Parameter);
        Assert.Equal("https://unrelated.example/", client.BaseAddress.AbsoluteUri);
    }

    private static void AssertNative(JsonElement body)
    {
        foreach (var property in Element(Native).EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
        Assert.False(body.TryGetProperty("juicefactory", out _));
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }

    private static JuiceFactoryProvider Provider(HttpClient client, Func<string?>? key = null)
        => new(new Keys(key ?? (() => "test-key")), new AsyncCacheHelper(), new Factory(client));
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler((request, ct) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return send(request, ct);
        }));
    private sealed class Keys(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string provider)
        {
            Assert.Equal("juicefactory", provider);
            return key();
        }
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
