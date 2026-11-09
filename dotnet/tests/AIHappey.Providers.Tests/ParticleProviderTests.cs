using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Particle;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class ParticleProviderTests
{
    private const string NativeOptions = """
        {"reasoning_effort":"particle-future-effort","top_k":17,"min_p":0.07,
         "prompt_cache_key":"cache-1","service_tier":"auto",
         "future_object":{"nested":[true,null,{},[]]},
         "future_array":[1,false,null],"future_null":null,
         "future_number":9007199254740993}
        """;

    private const string Reply = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"Particle-1",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}
        """;

    private const string Events = """
        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"Particle-1","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}

        data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"Particle-1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

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
        Assert.IsType<ParticleProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "particle"));
    }

    [Fact]
    public async Task Discovery_is_authenticated_cached_and_prefixes_complete_model_ids()
    {
        var calls = 0;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = Client((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.particle.ai/v1/models", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("""
                {"data":[{"id":"Particle-1","owned_by":"particle"},
                 {"id":"deepseek-ai/deepseek-v41-flash-1017","owned_by":"deepseek-ai"},
                 {"id":"glm-5.3-flash"},{},{"id":""},{"id":null}]}
                """));
        });
        var provider = Provider(client, new AsyncCacheHelper(cache));

        var first = (await provider.ListModels()).ToArray();
        var second = (await provider.ListModels()).ToArray();

        Assert.Equal(["particle/Particle-1", "particle/deepseek-ai/deepseek-v41-flash-1017",
            "particle/glm-5.3-flash"], first.Select(model => model.Id));
        Assert.Equal("Particle-1", first[0].Name);
        Assert.Equal("deepseek-ai", first[1].OwnedBy);
        Assert.Equal(first.Select(model => model.Id), second.Select(model => model.Id));
        Assert.Equal(1, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Discovery_cache_is_isolated_by_the_same_key_used_for_authorization()
    {
        var calls = new List<string>();
        var keys = new Keys();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var key = request.Headers.Authorization!.Parameter!;
            calls.Add(key);
            return Task.FromResult(Json(JsonSerializer.Serialize(new
            {
                data = new[] { new { id = key } }
            })));
        }));
        var provider = new ParticleProvider(keys, new AsyncCacheHelper(cache), new Factory(client));

        keys.Key = "first-key";
        Assert.Equal("particle/first-key", Assert.Single(await provider.ListModels()).Id);
        keys.Key = "second-key";
        Assert.Equal("particle/second-key", Assert.Single(await provider.ListModels()).Id);
        keys.Key = "first-key";
        Assert.Equal("particle/first-key", Assert.Single(await provider.ListModels()).Id);

        Assert.Equal(["first-key", "second-key"], calls);
        Assert.Equal(3, keys.ResolveCount);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Discovery_without_a_key_returns_no_models_without_HTTP(string? key)
    {
        using var client = new HttpClient(new Handler((_, _) =>
            throw new InvalidOperationException("No HTTP request should be sent.")));
        var keys = new Keys { Key = key };
        var provider = new ParticleProvider(keys, new AsyncCacheHelper(), new Factory(client));

        Assert.Empty(await provider.ListModels());
        Assert.Equal(1, keys.ResolveCount);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_preserves_all_particle_metadata_as_raw_native_fields(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("https://api.particle.ai/v1/chat/completions", http.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, http.Method);
            var payload = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNativeOptions(payload);
            Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
            Assert.False(payload.TryGetProperty("particle", out _));
            Assert.False(payload.TryGetProperty("other", out _));
            return streaming ? Sse() : Json(Reply);
        });
        var request = new AIRequest
        {
            Model = "Particle-1",
            ProviderId = "particle",
            Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "user",
                    Content = [new AITextContentPart { Type = "text", Text = "Hello" }]
                }]
            },
            Metadata = new Dictionary<string, object?>
            {
                ["particle"] = Element(NativeOptions),
                ["other"] = Element("""{"private_option":"must not leak"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);
        var provider = Provider(client);
        if (streaming)
        {
            var events = await Collect(provider.StreamUnifiedAsync(request));
            Assert.Equal("Hello", string.Concat(events.Select(item => item.Event.Data)
                .OfType<AITextDeltaEventData>().Select(delta => delta.Delta)));
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal("Hello", Assert.Single(response.Output!.Items![0].Content!
                .OfType<AITextContentPart>()).Text);
        }
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Native_adapters_use_chat_completions_and_preserve_raw_root_fields(
        string endpoint, bool streaming)
    {
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal("https://api.particle.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            var body = Element(await request.Content!.ReadAsStringAsync(ct));
            AssertNativeOptions(body);
            if (streaming)
                Assert.True(body.GetProperty("stream").GetBoolean());
            return streaming ? Sse() : Json(Reply);
        });
        var provider = Provider(client);
        var fields = "\"model\":\"Particle-1\"," + NativeOptions.Trim()[1..^1];
        switch (endpoint)
        {
            case "chat":
                var chat = JsonSerializer.Deserialize<ChatCompletionOptions>(
                    "{" + fields + ""","messages":[{"role":"user","content":"Hello"}]}""")!;
                if (streaming)
                    Assert.NotEmpty(await Collect(provider.CompleteChatStreamingAsync(chat)));
                else
                    Assert.Equal("particle/Particle-1", (await provider.CompleteChatAsync(chat)).Model);
                break;
            case "responses":
                var responses = JsonSerializer.Deserialize<ResponseRequest>(
                    "{" + fields + ""","input":"Hello"}""")!;
                if (streaming)
                    Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(responses)));
                else
                    Assert.NotNull(await provider.ResponsesAsync(responses));
                break;
            default:
                var messages = JsonSerializer.Deserialize<MessagesRequest>(
                    "{" + fields + ""","max_tokens":100,"messages":[{"role":"user","content":"Hello"}]}""")!;
                if (streaming)
                    Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, [])));
                else
                    Assert.NotNull(await provider.MessagesAsync(messages, []));
                break;
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task UI_adapter_preserves_namespaced_metadata_and_image_input()
    {
        using var client = Client(async (request, ct) =>
        {
            var body = Element(await request.Content!.ReadAsStringAsync(ct));
            AssertNativeOptions(body);
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Contains("image_url", body.GetProperty("messages").GetRawText());
            return Sse();
        });
        var request = JsonSerializer.Deserialize<ChatRequest>("""
            {"model":"Particle-1","messages":[{"id":"u1","role":"user","parts":[
             {"type":"text","text":"Describe this"},
             {"type":"file","mediaType":"image/png","url":"https://example.com/image.png"}]}],
             "providerMetadata":{"particle":
            """ + NativeOptions + "}}")!;

        Assert.NotEmpty(await Collect(Provider(client).StreamAsync(request)));
    }

    [Fact]
    public async Task Authorization_is_resolved_for_each_request_and_overrides_caller_headers()
    {
        var keys = new Keys();
        var seen = new List<string?>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            seen.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(Reply));
        }));
        var provider = new ParticleProvider(keys, new AsyncCacheHelper(), new Factory(client));
        for (var index = 0; index < 2; index++)
        {
            keys.Key = $"test-key-{index}";
            await provider.CompleteChatAsync(new ChatCompletionOptions
            {
                Model = "Particle-1",
                Headers = new Dictionary<string, string>
                {
                    ["authorization"] = new AuthenticationHeaderValue("Bearer", "caller-key").ToString()
                }
            });
        }
        Assert.Equal(["test-key-0", "test-key-1"], seen);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Missing_key_and_HTTP_errors_are_not_silently_ignored()
    {
        using var client = new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var keys = new Keys { Key = " " };
        var provider = new ParticleProvider(keys, new AsyncCacheHelper(), new Factory(client));
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(
            new ChatCompletionOptions { Model = "Particle-1" }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Collect(provider.CompleteChatStreamingAsync(new ChatCompletionOptions { Model = "Particle-1" })));
        keys.Key = "test-key";
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(
            new ChatCompletionOptions { Model = "Particle-1" }));
    }

    [Fact]
    public void Unsupported_modalities_throw_not_supported()
    {
        using var client = new HttpClient();
        var provider = Provider(client);
        Assert.Throws<NotSupportedException>(() => { _ = provider.ImageRequest(null!); });
        Assert.Throws<NotSupportedException>(() => { _ = provider.SpeechRequest(null!); });
        Assert.Throws<NotSupportedException>(() => { _ = provider.TranscriptionRequest(null!); });
        Assert.Throws<NotSupportedException>(() => { _ = provider.EmbeddingRequestAsync(null!); });
        Assert.Throws<NotSupportedException>(() => { _ = provider.RerankingRequest(null!); });
        Assert.Throws<NotSupportedException>(() => { _ = provider.StartVideoOperation(null!); });
        Assert.Throws<NotSupportedException>(() => { _ = provider.CreateRealtimeSessionAsync("Particle-1", null!); });
    }

    private static void AssertNativeOptions(JsonElement body)
    {
        foreach (var property in Element(NativeOptions).EnumerateObject())
        {
            Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.Single(body.EnumerateObject(), candidate => candidate.Name == property.Name);
        }
    }

    private static ParticleProvider Provider(HttpClient client, AsyncCacheHelper? cache = null)
        => new(new Keys(), cache ?? new AsyncCacheHelper(), new Factory(client));

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler((request, ct) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return send(request, ct);
        }));

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Events, Encoding.UTF8, "text/event-stream")
    };

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream)
            result.Add(item);
        return result;
    }

    private sealed class Keys : IApiKeyResolver
    {
        public string? Key { get; set; } = "test-key";
        public int ResolveCount { get; private set; }

        public string? Resolve(string provider)
        {
            ResolveCount++;
            return provider == "particle" ? Key : null;
        }
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return send(request, cancellationToken);
        }
    }
}
