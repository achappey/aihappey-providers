using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Pyrois;
using AIHappey.Unified.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Providers.Tests;

public sealed class PyroisProviderTests
{
    private const string Native = """{"temperature":null,"future":{"number":9007199254740993,"values":[null,{},[]]}}""";
    private const string Reply = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"qwen3-coder-30b-a3b-instruct",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}]}
        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<PyroisProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "pyrois"));
    }

    [Theory]
    [InlineData("https://workload.pyrois.run", false)]
    [InlineData("https://workload.pyrois.run/v1/", true)]
    public async Task Chat_uses_workload_url_key_and_raw_options(string url, bool stream)
    {
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal("https://workload.pyrois.run/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer endpoint-key", request.Headers.Authorization?.ToString());
            Assert.False(request.Headers.Contains("x-api-key"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("qwen3-coder-30b-a3b-instruct", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(stream, body.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(body.RootElement.TryGetProperty("pyrois", out _));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Native).GetProperty("future"),
                body.RootElement.GetProperty("future")));
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("temperature").ValueKind);
            return Response(stream
                ? "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"qwen3-coder-30b-a3b-instruct\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n"
                : Reply, stream ? "text/event-stream" : "application/json");
        });
        var provider = Provider(client, url);
        var request = JsonSerializer.Deserialize<ChatCompletionOptions>($$"""
            {"model":"pyrois/qwen3-coder-30b-a3b-instruct","messages":[{"role":"user","content":"Hello"}],
             "pyrois":{{Native}}}
            """)!;
        request.Headers = new() { ["Authorization"] = "Bearer wrong", ["x-api-key"] = "wrong" };
        var before = JsonSerializer.Serialize(request);
        if (stream)
        {
            var updates = new List<ChatCompletionUpdate>();
            await foreach (var update in provider.CompleteChatStreamingAsync(request)) updates.Add(update);
            Assert.Equal("pyrois/qwen3-coder-30b-a3b-instruct", Assert.Single(updates).Model);
        }
        else
            Assert.Equal("pyrois/qwen3-coder-30b-a3b-instruct", (await provider.CompleteChatAsync(request)).Model);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Unified_chat_preserves_provider_metadata()
    {
        using var client = Client(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Native).GetProperty("future"),
                body.RootElement.GetProperty("future")));
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("temperature").ValueKind);
            return Response(Reply, "application/json");
        });
        var request = new AIRequest
        {
            ProviderId = "pyrois",
            Model = "pyrois/qwen3-coder-30b-a3b-instruct",
            Input = new AIInput { Text = "Hello" },
            Metadata = new Dictionary<string, object?> { ["pyrois"] = JsonSerializer.Deserialize<JsonElement>(Native) }
        };
        await Provider(client, "https://workload.pyrois.run/v1").ExecuteUnifiedAsync(request);
    }

    [Theory]
    [InlineData("http://workload.pyrois.run")]
    [InlineData("https://workload.pyrois.run.evil.example/v1")]
    [InlineData("https://workload.pyrois.run/other")]
    [InlineData("https://workload.pyrois.run/v1?key=x")]
    public async Task Invalid_workload_url_fails_before_sending_credentials(string url)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Request should not be sent."));
        var request = JsonSerializer.Deserialize<ChatCompletionOptions>("""{"model":"test","messages":[]}""")!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client, url).CompleteChatAsync(request));
    }

    private static PyroisProvider Provider(HttpClient client, string url)
        => new(new Keys(), new Factory(client), new ProviderRequestOptions { PyroisEndpointUrl = url });

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));

    private static HttpResponseMessage Response(string text, string mediaType)
        => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => "endpoint-key";
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
