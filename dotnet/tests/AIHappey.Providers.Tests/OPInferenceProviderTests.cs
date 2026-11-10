using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OPInference;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Providers.Tests;

public sealed class OPInferenceProviderTests
{
    private const string Reply = """
        {"id":"chat","object":"chat.completion","created":1,"model":"vendor/model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
        """;
    private const string StreamReply = """
        data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}

        data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"vendor/model","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

        data: [DONE]

        """;
    private const string NativeMetadata = """
        {"provider":{"order":["endpoint"],"allow_fallbacks":false},"retention":"zdr",
         "models":["vendor/fallback"],"reasoning_effort":"future-effort",
         "future":{"large":9007199254740993,"array":[true,null,{},[]]},"future_null":null}
        """;

    [Fact]
    public void ProviderIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(() => "test-key"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<OPInferenceProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "opinference"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatPreservesNativeOptionsToolsAndStructuredOutputWithoutMutatingRequest(bool streaming)
    {
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"OPINFERENCE/vendor/model","messages":[{"role":"user","content":"Hello"}],
             "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object","properties":{}}}}],
             "tool_choice":"auto","response_format":{"type":"json_schema","json_schema":{"name":"result","schema":{"type":"object"}}},
             "future_root":{"value":9007199254740993},
             "opinference":{"provider":{"order":["endpoint"],"allow_fallbacks":false},"retention":"zdr",
               "models":["vendor/fallback"],"reasoning_effort":"future-effort",
               "future":{"large":9007199254740993,"array":[true,null,{},[]]},"future_null":null,
               "model":"must-not-replace-model","stream":false}}
            """)!;
        options.Stream = !streaming;
        options.Headers = new()
        {
            ["AUTHORIZATION"] = "untrusted",
            ["x-api-key"] = "untrusted",
            ["Host"] = "untrusted.test",
            ["Idempotency-Key"] = "request-key",
            ["x-request-id"] = "trace"
        };
        var before = JsonSerializer.Serialize(options);
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://api.opinference.com/v1/chat/completions", http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
            Assert.False(http.Headers.Contains("x-api-key"));
            Assert.Null(http.Headers.Host);
            Assert.Equal("request-key", http.Headers.GetValues("Idempotency-Key").Single());
            Assert.Equal("trace", http.Headers.GetValues("x-request-id").Single());
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("vendor/model", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.False(body.TryGetProperty("opinference", out _));
            foreach (var property in Element(NativeMetadata).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.Equal(9007199254740993L, body.GetProperty("future_root").GetProperty("value").GetInt64());
            Assert.Single(body.GetProperty("tools").EnumerateArray());
            Assert.Equal("auto", body.GetProperty("tool_choice").GetString());
            Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
            if (streaming)
                Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            return Json(streaming ? StreamReply : Reply, streaming);
        });
        var provider = Provider(client);
        if (streaming)
        {
            var updates = await Collect(provider.CompleteChatStreamingAsync(options));
            Assert.Equal(3, updates.Count);
            Assert.All(updates, update => Assert.Equal("opinference/vendor/model", update.Model));
            Assert.NotNull(updates[^1].Usage);
        }
        else
            Assert.Equal("opinference/vendor/model", (await provider.CompleteChatAsync(options)).Model);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnifiedAndUiMetadataPassThroughAsRawRootProperties(bool ui)
    {
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            foreach (var property in Element(NativeMetadata).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.False(body.TryGetProperty("opinference", out _));
            Assert.Equal(ui, body.GetProperty("stream").GetBoolean());
            return Json(ui ? StreamReply : Reply, ui);
        });
        var provider = Provider(client);
        if (ui)
        {
            var chat = JsonSerializer.Deserialize<ChatRequest>("""
                {"model":"opinference/vendor/model","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hello"}]}],
                 "providerMetadata":{"opinference":
                """ + NativeMetadata + "}}")!;
            Assert.NotEmpty(await Collect(provider.StreamAsync(chat)));
        }
        else
            Assert.NotNull(await provider.ExecuteUnifiedAsync(new AIRequest
            {
                ProviderId = "opinference", Model = "vendor/model",
                Input = new AIInput { Text = "Hello" },
                Metadata = new() { ["opinference"] = Element(NativeMetadata) }
            }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesAndMessagesCompatibilityUseChatCompletions(bool streaming)
    {
        var calls = 0;
        using var client = Client((http, _) =>
        {
            calls++;
            Assert.Equal("/v1/chat/completions", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(streaming ? StreamReply : Reply, streaming));
        });
        var provider = Provider(client);
        var responses = Element("""{"model":"vendor/model","input":"Hello"}""").Deserialize<ResponseRequest>()!;
        var messages = Element("""{"model":"vendor/model","messages":[{"role":"user","content":"Hello"}],"max_tokens":20}""")
            .Deserialize<MessagesRequest>()!;
        if (streaming)
        {
            Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(responses)));
            Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, [])));
        }
        else
        {
            Assert.NotNull(await provider.ResponsesAsync(responses));
            Assert.NotNull(await provider.MessagesAsync(messages, []));
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ModelsAreAuthenticatedCachedPerKeyAndNotFabricated()
    {
        string? key = "first";
        var calls = 0;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var client = Client((http, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("https://api.opinference.com/v1/models", http.RequestUri!.AbsoluteUri);
            Assert.Equal(key, http.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""{"data":[{"id":"vendor/model","owned_by":"vendor","created":123}]}"""));
        });
        var provider = Provider(client, () => key, new AsyncCacheHelper(memory));
        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("opinference/vendor/model", model.Id);
        Assert.Equal("vendor", model.OwnedBy);
        Assert.Equal(123, model.Created);
        model.Name = "changed";
        Assert.Equal("vendor/model", Assert.Single(await provider.ListModels()).Name);
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
    public async Task ErrorsAndCancellationAreNotConvertedIntoSuccessfulEmptyReplies()
    {
        using var denied = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"invalid key"}}""")
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(denied).CompleteChatAsync(new() { Model = "model" }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(denied).ListModels());

        using var failedStream = Client((_, _) => Task.FromResult(Json(
            StreamReply.Replace("data: [DONE]", """data: {"error":{"message":"endpoint failed"}}"""), true)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(
            Provider(failedStream).CompleteChatStreamingAsync(new() { Model = "model" })));
        Assert.Contains("endpoint failed", error.Message);

        using var cancellation = new CancellationTokenSource();
        using var waiting = Client(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(Reply);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(waiting).CompleteChatAsync(
            new() { Model = "model" }, cancellation.Token));
    }

    [Fact]
    public async Task DeferredStreamsCaptureCredentialsAndDoNotMutateSharedHeaders()
    {
        string? key = "first";
        var credentials = new List<string?>();
        using var client = Client((http, _) =>
        {
            credentials.Add(http.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(StreamReply, true));
        });
        var provider = Provider(client, () => key);
        var first = provider.CompleteChatStreamingAsync(new() { Model = "model" });
        key = "second";
        var second = provider.CompleteChatStreamingAsync(new() { Model = "model" });
        await Collect(first);
        await Collect(second);
        Assert.Equal(["first", "second"], credentials);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task MissingCredentialsAndUnimplementedMediaFailExplicitly()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Network must not be used."));
        var provider = Provider(client, () => " ");
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new() { Model = "model" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.StartVideoOperation(null!));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.EmbeddingRequestAsync(null!));
    }

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value, bool stream = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, stream ? "text/event-stream" : "application/json")
    };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));
    private static OPInferenceProvider Provider(HttpClient client, Func<string?>? key = null, AsyncCacheHelper? cache = null)
        => new(new Keys(key ?? (() => "test-key")), cache ?? new AsyncCacheHelper(), new Factory(client));
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
