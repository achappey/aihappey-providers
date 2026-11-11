using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.VoyageAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class VoyageAIProviderTests
{
    [Fact]
    public async Task Requests_strip_catalog_prefix_and_keep_authentication_request_scoped()
    {
        var requests = new List<(string Path, string? Authorization, JsonElement Body)>();
        using var client = new HttpClient(new Handler(async request =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((path, request.Headers.Authorization?.ToString(), body));

            var response = path.EndsWith("/rerank", StringComparison.Ordinal)
                ? """{"data":[{"index":0,"relevance_score":0.9}],"model":"rerank-2.5-lite","usage":{"total_tokens":2}}"""
                : """{"data":[{"index":0,"embedding":[0.1]}],"model":"voyage-4","usage":{"total_tokens":1}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }));
        var provider = new VoyageAIProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "voyageai/voyage-4",
            Input = JsonSerializer.Deserialize<JsonElement>("\"hello\"")
        });
        await provider.RerankingRequest(new RerankingRequest
        {
            Model = "voyageai/rerank-2.5-lite",
            Query = "query",
            Documents = new()
            {
                Type = "text",
                Values = JsonSerializer.Deserialize<JsonElement>("""["document"]""")
            },
            ProviderOptions = new()
            {
                ["voyageai"] = JsonSerializer.Deserialize<JsonElement>(
                    """{"return_documents":true,"truncation":false,"future_option":{"value":123}}""")
            }
        });

        Assert.Equal(["/v1/embeddings", "/v1/rerank"], requests.Select(request => request.Path));
        Assert.All(requests, request => Assert.Equal("Bearer test-key", request.Authorization));
        Assert.Equal("voyage-4", requests[0].Body.GetProperty("model").GetString());
        Assert.Equal("rerank-2.5-lite", requests[1].Body.GetProperty("model").GetString());
        Assert.True(requests[1].Body.GetProperty("return_documents").GetBoolean());
        Assert.False(requests[1].Body.GetProperty("truncation").GetBoolean());
        Assert.Equal(123, requests[1].Body.GetProperty("future_option").GetProperty("value").GetInt32());
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
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
