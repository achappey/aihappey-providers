using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.XALEN;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class XALENProviderTests
{
    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new KeyResolver(() => "test-key"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("xalen"));
        Assert.IsType<XALENProvider>(registry.GetModelProvider(scope.ServiceProvider, "xalen"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_preserves_native_options_and_response_metadata(bool streaming)
    {
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {
              "model":"vedika-standard",
              "messages":[{"role":"user","content":"Hello"}],
              "metadata":{"project":"test"},
              "modalities":["image","text"],
              "future_object":{"nested":[true,null,{},[]]},
              "future_number":9007199254740993,
              "future_null":null
            }
            """)!;
        options.Stream = streaming;
        options.Headers = new() { ["Authorization"] = "Bearer must-not-override", ["X-Test"] = "forwarded" };
        var rawMetadata = JsonSerializer.Deserialize<JsonElement>("""
            {"citations":[{"source":"classical","page":12}],"cost":0.001,"future":null}
            """);
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.xalen.io/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            Assert.Equal("forwarded", Assert.Single(request.Headers.GetValues("X-Test")));
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("vedika-standard", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            var original = JsonSerializer.SerializeToElement(options);
            foreach (var name in new[] { "metadata", "modalities", "future_object", "future_number", "future_null" })
                Assert.True(JsonElement.DeepEquals(original.GetProperty(name), body.GetProperty(name)), name);

            var response = $$"""
                {"id":"chat-1","object":"{{(streaming ? "chat.completion.chunk" : "chat.completion")}}",
                 "created":1,"model":"vedika-standard","choices":[],
                 "xalen_metadata":{{rawMetadata.GetRawText()}}}
                """;
            var compact = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(response));
            return Json(streaming ? $"data: {compact}\n\ndata: [DONE]\n\n" : response, streaming);
        }));
        var provider = CreateProvider(client);

        if (streaming)
        {
            var updates = new List<ChatCompletionUpdate>();
            await foreach (var update in provider.CompleteChatStreamingAsync(options))
                updates.Add(update);
            Assert.True(JsonElement.DeepEquals(rawMetadata, Assert.Single(updates).AdditionalProperties!["xalen_metadata"]));
        }
        else
        {
            var result = await provider.CompleteChatAsync(options);
            Assert.True(JsonElement.DeepEquals(rawMetadata, result.AdditionalProperties!["xalen_metadata"]));
        }

        Assert.Equal(1, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task UI_chat_forwards_only_XALEN_metadata_without_filtering_native_fields()
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>("""
            {"reasoning_effort":"high","future_object":{"nested":[true,null,{}]},
             "future_number":9007199254740993,"future_null":null}
            """);
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            foreach (var property in metadata.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.False(body.TryGetProperty("providerMetadata", out _));
            Assert.False(body.TryGetProperty("another-provider", out _));
            Assert.False(body.TryGetProperty("secret", out _));
            return Json("data: [DONE]\n\n", streaming: true);
        }));
        var request = new ChatRequest
        {
            Model = "vedika-standard",
            Messages =
            [
                new UIMessage
                {
                    Id = "user-1",
                    Role = Role.user,
                    Parts = [new TextUIPart { Text = "Hello" }]
                }
            ],
            ProviderMetadata = new()
            {
                ["xalen"] = metadata,
                ["another-provider"] = JsonSerializer.Deserialize<JsonElement>("""{"secret":"not-forwarded"}""")
            }
        };
        var original = JsonSerializer.Serialize(request);

        await foreach (var _ in CreateProvider(client).StreamAsync(request)) { }

        Assert.Equal(original, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Models_use_authenticated_discovery_and_credential_partitioned_cache()
    {
        var key = "first-key";
        var calls = 0;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.xalen.io/v1/models", request.RequestUri!.ToString());
            Assert.Equal($"Bearer {key}", request.Headers.Authorization?.ToString());
            return Task.FromResult(Json("""
                {"object":"list","data":[
                  {"id":"vedika-standard","object":"model","owned_by":"xalen","permission":[]},
                  {"id":"claude-opus-4.8","name":"Claude Opus","owned_by":"anthropic",
                   "created":1717200000,"description":"Frontier model","permission":[]}
                ]}
                """));
        }));
        var provider = new XALENProvider(
            new KeyResolver(() => key), new Factory(client), new AsyncCacheHelper(memory));

        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["xalen/vedika-standard", "xalen/claude-opus-4.8"], models.Select(model => model.Id));
        Assert.Equal("vedika-standard", models[0].Name);
        Assert.Equal("Claude Opus", models[1].Name);
        Assert.Equal("anthropic", models[1].OwnedBy);
        Assert.Equal(1717200000, models[1].Created);
        Assert.Equal("Frontier model", models[1].Description);
        Assert.All(models, model => Assert.Equal("language", model.Type));
        await provider.ListModels();
        Assert.Equal(1, calls);
        key = "second-key";
        await provider.ListModels();
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":[{}]}""")]
    [InlineData("""{"data":[{"id":" "}]}""")]
    public async Task Malformed_discovery_fails_explicitly(string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(client).ListModels());
    }

    [Fact]
    public async Task Discovery_surfaces_upstream_errors()
    {
        using var client = new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("invalid API key")
            })));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider(client).ListModels());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("invalid API key", error.Message);
    }

    [Fact]
    public async Task Missing_credentials_fail_before_sending_requests()
    {
        using var client = new HttpClient(new Handler((_, _) =>
            throw new InvalidOperationException("No HTTP request should be sent.")));
        var provider = new XALENProvider(new KeyResolver(() => null), new Factory(client), new AsyncCacheHelper());

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CompleteChatAsync(new ChatCompletionOptions { Model = "vedika-standard" }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(
                               new ChatCompletionOptions { Model = "vedika-standard" })) { }
        });
    }

    [Fact]
    public async Task Unavailable_production_endpoints_are_not_called()
    {
        using var client = new HttpClient(new Handler((_, _) =>
            throw new InvalidOperationException("No HTTP request should be sent.")));
        var provider = CreateProvider(client);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.EmbeddingRequestAsync(new EmbeddingRequest { Model = "text-embedding-3-small" }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.ImageRequest(new ImageRequest { Model = "gpt-image-2", Prompt = "Hello" }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.SpeechRequest(new SpeechRequest { Model = "tts-1", Text = "Hello" }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.TranscriptionRequest(new TranscriptionRequest { Model = "whisper-1" }));
    }

    private static XALENProvider CreateProvider(HttpClient client)
        => new(new KeyResolver(() => "test-key"), new Factory(client), new AsyncCacheHelper());

    private static HttpResponseMessage Json(string body, bool streaming = false)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
        };

    private sealed class KeyResolver(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string provider)
        {
            Assert.Equal("xalen", provider);
            return key();
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
            => send(request, cancellationToken);
    }
}
