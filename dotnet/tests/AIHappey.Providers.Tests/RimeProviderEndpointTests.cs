using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Rime;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class RimeProviderEndpointTests
{
    [Theory]
    [InlineData("coda", "https://coda.api.rime.ai/")]
    [InlineData("mistv3", "https://users.rime.ai/v1/rime-tts")]
    public async Task Speech_uses_documented_model_endpoint_and_preserves_unknown_options(
        string model,
        string expectedUri)
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(expectedUri, request.RequestUri!.AbsoluteUri);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });

        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = model,
            Text = "Hello",
            ProviderOptions = new()
            {
                ["rime"] = ParseElement("""{"futureOption":{"nested":[true,null,"value"]}}""")
            }
        });

        Assert.Equal(model, payload.GetProperty("modelId").GetString());
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"nested":[true,null,"value"]}"""),
            payload.GetProperty("futureOption")));
    }

    private static RimeProvider CreateProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
