using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Heabsy;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Providers.Tests;

public sealed class HeabsyProviderTests
{
    private const string Reply = """{"id":"chat","object":"chat.completion","created":1,"model":"qwen","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    [Fact]
    public void ProviderIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(() => "test-key"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("heabsy"));
        Assert.IsType<HeabsyProvider>(registry.GetModelProvider(scope.ServiceProvider, "heabsy"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeRequestsPreserveRawOptionsAndUseRequestAuthentication(bool messages, bool streaming)
    {
        var raw = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"qwen","messages":[],"max_tokens":100,
             "metadata":{"user_id":"user"},
             "future_nested":{"values":[true,null,{},[]]},
             "future_null":null,"future_number":9007199254740993}
            """);
        using var client = Client(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.heabsy.com/v1/" + (messages ? "messages" : "chat/completions"), request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            if (messages) Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            foreach (var name in new[] { "metadata", "future_nested", "future_null", "future_number" })
                Assert.True(JsonElement.DeepEquals(raw.GetProperty(name), body.GetProperty(name)), name);
            Assert.Equal(streaming, body.TryGetProperty("stream", out var value) && value.GetBoolean());
            if (streaming && !messages)
                Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            return Json(streaming
                ? messages
                    ? "data: {\"type\":\"message_stop\"}\n\n"
                    : $"data: {Reply}\n\ndata: [DONE]\n\n"
                : messages
                    ? """{"id":"msg","type":"message","role":"assistant","model":"qwen","content":[],"usage":{"input_tokens":1,"output_tokens":1}}"""
                    : Reply, streaming);
        });
        var provider = Provider(client);
        if (messages)
        {
            var request = raw.Deserialize<MessagesRequest>()!;
            request.Stream = streaming;
            var headers = new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" };
            if (streaming) Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(request, headers)));
            else Assert.Equal("heabsy/qwen", (await provider.MessagesAsync(request, headers)).Model);
        }
        else
        {
            var request = raw.Deserialize<ChatCompletionOptions>()!;
            request.Stream = streaming;
            if (streaming) Assert.NotEmpty(await Collect(provider.CompleteChatStreamingAsync(request)));
            else Assert.Equal("heabsy/qwen", (await provider.CompleteChatAsync(request)).Model);
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task UnifiedMetadataPassesThroughAtRequestRoot()
    {
        var native = JsonSerializer.Deserialize<JsonElement>("""{"reasoning_effort":"high","future":{"array":[true,null,{}]},"future_null":null}""");
        using var client = Client(async request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            foreach (var property in native.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            return Json(Reply);
        });
        await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "heabsy",
            Model = "qwen", Input = new AIInput { Text = "Hello" },
            Metadata = new() { ["heabsy"] = native }
        });
    }

    [Fact]
    public async Task ResponsesUsesChatCompletionsInsteadOfUnsupportedNativeEndpoint()
    {
        using var client = Client(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(Reply));
        });
        var response = await Provider(client).ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
            """{"model":"qwen","input":"Hello"}""")!);
        Assert.NotNull(response);
    }

    [Fact]
    public async Task DiscoveryIsAuthenticatedAndCacheIsPartitionedByKey()
    {
        string? key = "first";
        var calls = 0;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var client = Client(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.heabsy.com/v1/models", request.RequestUri!.ToString());
            Assert.Equal(key, request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json($$"""{"data":[{"id":"{{key}}","owned_by":"Heabsy"}]}"""));
        });
        var provider = new HeabsyProvider(new Keys(() => key), new AsyncCacheHelper(memory), new Factory(client));
        var first = Assert.Single(await provider.ListModels());
        Assert.Equal("heabsy/first", first.Id);
        Assert.Equal("language", first.Type);
        await provider.ListModels();
        Assert.Equal(1, calls);
        key = "second";
        Assert.Equal("heabsy/second", Assert.Single(await provider.ListModels()).Id);
        Assert.Equal(2, calls);
        key = null;
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new ChatCompletionOptions { Model = "qwen" }));
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task DiscoveryHonorsCancellationAndHttpErrors()
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var provider = Provider(client);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancellation.Token));
    }

    private static HeabsyProvider Provider(HttpClient client)
        => new(new Keys(() => "test-key"), new AsyncCacheHelper(), new Factory(client));

    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        => new(new Handler(send));

    private static HttpResponseMessage Json(string body, bool streaming = false)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }

    private sealed class Keys(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string providerId) => key();
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
