using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Exa;
using AIHappey.Unified.Models;

namespace AIHappey.Tests;

public sealed class ExaSearchTypeTests
{
    [Theory]
    [InlineData("keyword")]
    [InlineData("hybrid")]
    public async Task Supported_search_types_are_forwarded_with_native_options(string type)
    {
        JsonElement payload = default;
        using var client = new HttpClient(new TestHttpMessageHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.exa.ai/search", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-api-key", request.Headers.GetValues("x-api-key").Single());
            using var requestDocument = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            payload = requestDocument.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[]}""", Encoding.UTF8, "application/json")
            };
        }));
        using var options = JsonDocument.Parse("""{"numResults":7,"contents":{"text":true}}""");
        var provider = new ExaProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "exa",
            Model = $"exa/{type}",
            Input = new AIInput { Text = "latest search" },
            Metadata = new Dictionary<string, object?>
            {
                ["exa"] = options.RootElement.Clone()
            }
        });

        Assert.Equal(type, payload.GetProperty("type").GetString());
        Assert.Equal("latest search", payload.GetProperty("query").GetString());
        Assert.Equal(7, payload.GetProperty("numResults").GetInt32());
        Assert.True(payload.GetProperty("contents").GetProperty("text").GetBoolean());
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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
