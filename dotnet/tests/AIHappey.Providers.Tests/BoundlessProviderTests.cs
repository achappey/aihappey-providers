using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Boundless;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class BoundlessProviderTests
{
    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<BoundlessProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "boundless"));
    }

    [Fact]
    public async Task Discovery_uses_live_list_and_identifies_decisions()
    {
        using var client = Client((message, _) =>
        {
            Assert.Equal("/v1/models", message.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer key", message.Headers.Authorization!.ToString());
            return Task.FromResult(Json("""{"data":[{"id":"vendor/model","context_length":4096},{"id":"convaiinnovations/laya","context_length":8192}]}"""));
        });
        var models = (await Provider(client).ListModels()).ToArray();
        Assert.Equal("boundless/vendor/model", models[0].Id);
        Assert.Equal(4096, models[0].ContextWindow);
        Assert.Equal("decision", models[1].Type);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Unified_chat_passes_native_metadata_without_leaking_credentials()
    {
        using var client = Client(async (message, ct) =>
        {
            Assert.Equal("https://api.inference.boundless.network/v1/chat/completions", message.RequestUri!.ToString());
            Assert.Equal("Bearer key", message.Headers.Authorization!.ToString());
            Assert.False(message.Headers.Contains("x-api-key"));
            Assert.False(message.Headers.Contains("x-other-key"));
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            Assert.Equal("vendor/model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.Equal(9007199254740993L, body.RootElement.GetProperty("future").GetProperty("number").GetInt64());
            return Json("""{"id":"c1","object":"chat.completion","created":1,"model":"vendor/model","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""");
        });
        var request = new AIRequest
        {
            ProviderId = "boundless",
            Model = "boundless/vendor/model",
            Metadata = new() { ["boundless"] = Element("""{"reasoning_effort":"low","future":{"number":9007199254740993}}""") },
            Headers = new() { ["x-api-key"] = "wrong", ["x-other-key"] = "secret" },
            Input = new AIInput { Items = [new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] }] }
        };
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.NotNull(result.Output);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_streams_use_matching_endpoints_and_bearer_auth(string surface)
    {
        using var client = Client(async (message, ct) =>
        {
            Assert.Equal("Bearer key", message.Headers.Authorization!.ToString());
            Assert.False(message.Headers.Contains("x-api-key"));
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            Assert.Equal("vendor/model", body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            var (path, data) = surface switch
            {
                "chat" => ("/v1/chat/completions", "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"vendor/model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n"),
                "responses" => ("/v1/responses", "data: {\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"msg1\",\"output_index\":0,\"content_index\":0,\"delta\":\"hi\"}\n\ndata: [DONE]\n\n"),
                _ => ("/v1/messages", "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n")
            };
            Assert.Equal(path, message.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        });
        var provider = Provider(client);
        var count = 0;
        if (surface == "chat")
        {
            var request = new ChatCompletionOptions { Model = "boundless/vendor/model", Messages = [new() { Role = "user", Content = "Hello" }] };
            await foreach (var _ in provider.CompleteChatStreamingAsync(request)) count++;
        }
        else if (surface == "responses")
        {
            var request = new ResponseRequest { Model = "boundless/vendor/model", Input = "Hello", Headers = new() { ["x-api-key"] = "wrong" } };
            await foreach (var _ in provider.ResponsesStreamingAsync(request)) count++;
            Assert.Equal("wrong", request.Headers["x-api-key"]);
            Assert.False(request.Headers.ContainsKey("Authorization"));
            Assert.Equal("boundless/vendor/model", request.Model);
        }
        else
        {
            var request = new MessagesRequest { Model = "boundless/vendor/model", MaxTokens = 1024, Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }] };
            await foreach (var _ in provider.MessagesStreamingAsync(request, new() { ["x-api-key"] = "wrong" })) count++;
        }
        Assert.True(count > 0);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_requests_flatten_provider_options_without_replacing_standard_fields(string surface)
    {
        using var client = Client(async (message, ct) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            Assert.Equal("vendor/model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(9007199254740993L, body.RootElement.GetProperty("future").GetProperty("number").GetInt64());
            Assert.False(body.RootElement.TryGetProperty("boundless", out _));
            return surface switch
            {
                "chat" => Json("""{"id":"c1","object":"chat.completion","created":1,"model":"vendor/model","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}"""),
                "responses" => Json("""{"id":"r1","object":"response","created_at":1,"model":"vendor/model","status":"completed","output":[]}"""),
                _ => Json("""{"id":"m1","type":"message","role":"assistant","model":"vendor/model","content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}""")
            };
        });
        var provider = Provider(client);
        const string fields = """
            "model":"boundless/vendor/model","boundless":{"model":"wrong","future":{"number":9007199254740993}}
            """;
        if (surface == "chat")
            await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>("{" + fields + ""","messages":[{"role":"user","content":"Hello"}]}""")!);
        else if (surface == "responses")
            await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>("{" + fields + ""","input":"Hello"}""")!);
        else
            await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>("{" + fields + ""","max_tokens":1024,"messages":[{"role":"user","content":"Hello"}]}""")!, []);
    }

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static BoundlessProvider Provider(HttpClient client) => new(new Keys(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
