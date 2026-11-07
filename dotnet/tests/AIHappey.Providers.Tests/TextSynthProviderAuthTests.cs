using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.TextSynth;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class TextSynthProviderAuthTests
{
    [Fact]
    public async Task Chat_authentication_is_attached_to_the_request_without_mutating_client_defaults()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/engines/mistral_7B/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse("""{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"mistral_7B","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}]}"""));
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "existing-key");
        var provider = CreateProvider(client);

        await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "mistral_7B",
            Messages = [new() { Role = "user", Content = "Hi" }]
        });

        Assert.Equal("existing-key", client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public async Task Speech_authentication_is_attached_to_the_request_without_mutating_client_defaults()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/engines/parler_tts_large/speech", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "existing-key");
        var provider = CreateProvider(client);

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "textsynth/parler_tts_large/Will",
            Text = "Hello"
        });

        Assert.Equal("existing-key", client.DefaultRequestHeaders.Authorization?.Parameter);
    }

    private static TextSynthProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new AIHappey.Core.AI.AsyncCacheHelper(), new TestHttpClientFactory(client));

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
