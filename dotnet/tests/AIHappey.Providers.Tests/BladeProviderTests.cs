using System.Net;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Blade;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class BladeProviderTests
{
    [Fact]
    public async Task Chat_authorization_is_request_scoped()
    {
        string? authorization = null;
        using var client = CreateClient(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("unauthorized")
            });
        });
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CompleteChatAsync(new ChatCompletionOptions { Model = "test-model" }));

        Assert.Equal("Bearer test-api-key", authorization);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Speech_sends_documented_fields_and_raw_provider_options()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/v1/audio/speech", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-api-key", request.Headers.Authorization?.ToString());

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Hello", body.RootElement.GetProperty("input").GetString());
            Assert.Equal("test-speech-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("tara", body.RootElement.GetProperty("voice").GetString());
            Assert.Equal(1.25, body.RootElement.GetProperty("speed").GetDouble());

            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var provider = CreateProvider(client);
        using var metadata = JsonDocument.Parse("""{"speed":1.25,"future_option":{"enabled":true}}""");

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "test-speech-model",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["blade"] = metadata.RootElement.Clone()
            }
        });

        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Model_listing_maps_limits_and_supported_parameters()
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "data": [{
                    "id": "chat-model",
                    "name": "Chat Model",
                    "context_length": 8192,
                    "max_output_tokens": 1024,
                    "output_modalities": ["text"],
                    "supported_parameters": ["temperature"],
                    "pricing": { "input": 0.1, "output": 0.4 }
                  }]
                }
                """)
        }));
        var provider = CreateProvider(client);

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("blade/chat-model", model.Id);
        Assert.Equal("language", model.Type);
        Assert.Equal(8192, model.ContextWindow);
        Assert.Equal(1024, model.MaxTokens);
        Assert.Contains("parameter:temperature", model.Tags!);
        Assert.Equal(0.1m, model.Pricing!.Input);
        Assert.Equal(0.4m, model.Pricing.Output);
    }

    private static BladeProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), new TestHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
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
