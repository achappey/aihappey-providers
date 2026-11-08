using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AIBramha;

namespace AIHappey.Tests;

public sealed class AIBramhaModelCatalogTests
{
    [Fact]
    public async Task ListModels_maps_the_documented_category_provider_and_context_length()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("www.aibramha.in", request.RequestUri!.Host);
            Assert.Equal("/api/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(
                """{"success":true,"models":[{"id":"gpt-4o","name":"GPT-4o","category":"text","provider":"OpenAI","context_length":128000},{"id":"dall-e-3","name":"DALL-E 3","category":"image","provider":"OpenAI","context_length":4000}]}"""));
        });
        Assert.Empty(client.DefaultRequestHeaders);

        var provider = new AIBramhaProvider(
            new TestKeyResolver(),
            new AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        var models = await provider.ListModels();

        var textModel = Assert.Single(models, model => model.Id == "aibramha/gpt-4o");
        Assert.Equal("text", textModel.Type);
        Assert.Equal("OpenAI", textModel.OwnedBy);
        Assert.Equal(128000, textModel.ContextWindow);

        var imageModel = Assert.Single(models, model => model.Id == "aibramha/dall-e-3");
        Assert.Equal("image", imageModel.Type);
        Assert.Equal("OpenAI", imageModel.OwnedBy);
        Assert.Equal(4000, imageModel.ContextWindow);
    }

    [Fact]
    public async Task Chat_completion_uses_the_documented_host_with_request_scoped_auth_and_raw_options()
    {
        JsonElement payload = default;
        using var client = CreateClient(async request =>
        {
            Assert.Equal("www.aibramha.in", request.RequestUri!.Host);
            Assert.Equal("/api/v1/chat/completions", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);

            payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return JsonResponse(
                """{"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}]}""");
        });

        Assert.Empty(client.DefaultRequestHeaders);
        var provider = new AIBramhaProvider(
            new TestKeyResolver(),
            new AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "aibramha/gpt-4o",
            Messages = [new() { Role = "user", Content = "Hello" }],
            AdditionalProperties = new()
            {
                ["future_option"] = JsonSerializer.SerializeToElement("preserved")
            }
        });

        Assert.Equal("Hello", Assert.Single(response.Choices).Message.Content);
        Assert.Equal("preserved", payload.GetProperty("future_option").GetString());
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Streaming_chat_authentication_is_attached_to_the_request()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("www.aibramha.in", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            });
        });
        Assert.Empty(client.DefaultRequestHeaders);
        var provider = new AIBramhaProvider(
            new TestKeyResolver(),
            new AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        await foreach (var _ in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "aibramha/gpt-4o",
            Messages = [new() { Role = "user", Content = "Hello" }]
        }))
        {
        }

        Assert.Empty(client.DefaultRequestHeaders);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }
}
