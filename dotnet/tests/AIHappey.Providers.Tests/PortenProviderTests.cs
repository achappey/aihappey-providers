using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Porten;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class PortenProviderTests
{
    [Fact]
    public async Task Discovery_uses_live_context_and_keeps_cold_models()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<PortenProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "porten"));

        using var client = Client(request =>
        {
            AssertRequest(request, HttpMethod.Get, "/v1/models");
            return Task.FromResult(Json("""
                {"data":[{"id":"qwen","owned_by":"porten","context_length":32768,"ready":false,"features":["chat","tools"]},
                {"id":"flux.2-klein","features":["image_generation"]},
                {"id":"nomic-embed-text","features":["embedding"]}]}
                """));
        });
        var models = (await Provider(client).ListModels()).ToArray();
        Assert.Equal(["porten/qwen", "porten/flux.2-klein", "porten/nomic-embed-text"], models.Select(m => m.Id));
        Assert.Equal(32768, models[0].ContextWindow);
        Assert.Equal(["language", "image", "embedding"], models.Select(m => m.Type));
        Assert.Contains("tools", models[0].Tags!);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_passes_native_fields_and_authenticates_each_request(bool streaming)
    {
        using var client = Client(async request =>
        {
            AssertRequest(request, HttpMethod.Post, "/v1/chat/completions");
            var body = Element(await request.Content!.ReadAsStringAsync());
            Assert.Equal("qwen", body.GetProperty("model").GetString());
            Assert.Equal(0, body.GetProperty("max_tokens").GetInt32());
            Assert.False(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.Equal(9007199254740993L, body.GetProperty("future").GetProperty("number").GetInt64());
            Assert.False(body.TryGetProperty("providerMetadata", out _));
            Assert.False(body.TryGetProperty("other", out _));
            return streaming
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") }
                : Json("""{"id":"chat","object":"chat.completion","created":1,"model":"qwen","choices":[]}""");
        });
        var provider = Provider(client);
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"porten/qwen","messages":[{"role":"user","content":"Hello"}],"max_tokens":0,
             "providerMetadata":{"porten":{"chat_template_kwargs":{"enable_thinking":false},"future":{"number":9007199254740993}},"other":{"other":true}}}
            """)!;
        if (streaming)
            await foreach (var _ in provider.CompleteChatStreamingAsync(options)) { }
        else
            await provider.CompleteChatAsync(options);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_chat_passes_provider_metadata_and_preserves_streamed_reasoning(bool streaming)
    {
        using var client = Client(async request =>
        {
            AssertRequest(request, HttpMethod.Post, "/v1/chat/completions");
            var body = Element(await request.Content!.ReadAsStringAsync());
            Assert.Equal("qwen", body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.Equal(9007199254740993L, body.GetProperty("future").GetProperty("number").GetInt64());
            Assert.False(body.TryGetProperty("other", out _));
            return streaming
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"model\":\"qwen\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"thinking\"},\"finish_reason\":null}]}\n\n" +
                    "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"model\":\"qwen\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream") }
                : Json("""{"id":"c","object":"chat.completion","created":1,"model":"qwen","choices":[{"index":0,"message":{"role":"assistant","content":"answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}""");
        });
        var request = new AIRequest
        {
            ProviderId = "porten", Model = "porten/qwen", Input = new AIInput { Text = "Hello" },
            Metadata = new()
            {
                ["porten"] = Element("""{"chat_template_kwargs":{"enable_thinking":false},"future":{"number":9007199254740993}}"""),
                ["other"] = Element("""{"other":true}""")
            }
        };
        var provider = Provider(client);
        if (streaming)
        {
            var events = new List<AIStreamEvent>();
            await foreach (var item in provider.StreamUnifiedAsync(request)) events.Add(item);
            Assert.Contains("thinking", JsonSerializer.Serialize(events));
            Assert.Contains("answer", JsonSerializer.Serialize(events));
        }
        else
            Assert.Contains("answer", JsonSerializer.Serialize(await provider.ExecuteUnifiedAsync(request)));
    }

    [Fact]
    public async Task Embeddings_forward_provider_options_and_strip_provider_prefix()
    {
        using var client = Client(async request =>
        {
            AssertRequest(request, HttpMethod.Post, "/v1/embeddings");
            var body = Element(await request.Content!.ReadAsStringAsync());
            Assert.Equal("nomic-embed-text", body.GetProperty("model").GetString());
            Assert.Equal("hello", body.GetProperty("input")[0].GetString());
            Assert.Equal(123, body.GetProperty("future").GetProperty("value").GetInt32());
            Assert.False(body.TryGetProperty("other", out _));
            return Json("""{"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],"model":"nomic-embed-text","usage":{"prompt_tokens":2,"total_tokens":2}}""");
        });
        var result = await Provider(client).EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "porten/nomic-embed-text", Values = ["hello"],
            ProviderOptions = new() { ["porten"] = Element("""{"future":{"value":123}}"""), ["other"] = Element("""{"other":true}""") }
        });
        Assert.Single(result.Embeddings);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task OpenAI_embeddings_forward_scoped_metadata()
    {
        using var client = Client(async request =>
        {
            AssertRequest(request, HttpMethod.Post, "/v1/embeddings");
            var body = Element(await request.Content!.ReadAsStringAsync());
            Assert.Equal("nomic-embed-text", body.GetProperty("model").GetString());
            Assert.Equal(123, body.GetProperty("future").GetInt32());
            Assert.False(body.TryGetProperty("providerMetadata", out _));
            Assert.False(body.TryGetProperty("other", out _));
            return Json("""{"object":"list","data":[{"index":0,"embedding":[0.1]}],"model":"nomic-embed-text","usage":{"prompt_tokens":1,"total_tokens":1}}""");
        });
        var response = await Provider(client).OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "porten/nomic-embed-text", Input = Element("\"hello\""),
            AdditionalProperties = new() { ["providerMetadata"] = Element("""{"porten":{"future":123},"other":{"other":true}}""") }
        });
        Assert.Equal("porten/nomic-embed-text", response.Model);
    }

    [Fact]
    public async Task Images_forward_native_options_and_return_png()
    {
        using var client = Client(async request =>
        {
            AssertRequest(request, HttpMethod.Post, "/v1/images/generations");
            var body = Element(await request.Content!.ReadAsStringAsync());
            Assert.Equal("flux.2-klein", body.GetProperty("model").GetString());
            Assert.Equal("a lake", body.GetProperty("prompt").GetString());
            Assert.Equal(42, body.GetProperty("seed").GetInt32());
            Assert.Equal(5, body.GetProperty("steps").GetInt32());
            Assert.True(JsonElement.DeepEquals(Element("""{"nested":[null,9007199254740993]}"""), body.GetProperty("future")));
            Assert.False(body.TryGetProperty("other", out _));
            return Json("""{"created":1730000000,"data":[{"b64_json":"AQID"}],"future":true}""");
        });
        var result = await Provider(client).ImageRequest(new ImageRequest
        {
            Model = "porten/flux.2-klein", Prompt = "a lake", Size = "1024x1024", Seed = 42,
            ProviderOptions = new() { ["porten"] = Element("""{"steps":5,"future":{"nested":[null,9007199254740993]}}"""), ["other"] = Element("""{"other":true}""") }
        });
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(result.Images!));
        Assert.True(result.ProviderMetadata!["porten"].GetProperty("future").GetBoolean());
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAI_images_forward_unknown_fields_and_adapt_non_streaming_output(bool streaming)
    {
        using var client = Client(async request =>
        {
            AssertRequest(request, HttpMethod.Post, "/v1/images/generations");
            var body = Element(await request.Content!.ReadAsStringAsync());
            Assert.Equal("flux.2-klein", body.GetProperty("model").GetString());
            Assert.Equal(7, body.GetProperty("steps").GetInt32());
            Assert.False(body.TryGetProperty("stream", out _));
            Assert.False(body.TryGetProperty("providerMetadata", out _));
            Assert.False(body.TryGetProperty("other", out _));
            return Json("""{"created":1730000000,"data":[{"b64_json":"AQID"}]}""");
        });
        var provider = Provider(client);
        var request = new OpenAIImageGenerationRequest
        {
            Model = "porten/flux.2-klein", Prompt = "a lake", Stream = streaming,
            AdditionalProperties = new() { ["providerMetadata"] = Element("""{"porten":{"steps":7},"other":{"other":true}}""") }
        };
        if (streaming)
        {
            var events = new List<IOpenAIImageStreamEvent>();
            await foreach (var item in provider.OpenAIImageGenerationStreamingAsync(request)) events.Add(item);
            Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events));
        }
        else
            Assert.Equal("AQID", Assert.Single((await provider.OpenAIImageGenerationRequestAsync(request)).Data!).B64Json);
    }

    [Fact]
    public async Task Porten_errors_are_not_hidden()
    {
        using var client = Client(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"error":{"code":"model_warming"}}""")
        }));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ImageRequest(
            new ImageRequest { Model = "flux.2-klein", Prompt = "a lake" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Contains("model_warming", exception.Message);
    }

    private static void AssertRequest(HttpRequestMessage request, HttpMethod method, string path)
    {
        Assert.Equal(method, request.Method);
        Assert.Equal("https://porten.ai" + path, request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
    }

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static PortenProvider Provider(HttpClient client) => new(new Keys(), new Factory(client));
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => provider == "porten" ? "test-key" : null; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
