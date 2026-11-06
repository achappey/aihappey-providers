using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class CohereRerankPricingTests
{
    [Theory]
    [InlineData("rerank-v3.5", "0.001", """{"search_units":3}""", "0.003")]
    [InlineData("rerank-4-fast", "0.002", """{"search_units":3}""", "0.006")]
    [InlineData("rerank-v5.0-fast", "0.000001", """{"input_tokens":1000}""", "0.001")]
    [InlineData("rerank-v5.0-pro", "0.000002", """{"input_tokens":1000}""", "0.002")]
    public async Task Uses_model_pricing_with_the_billing_units_reported_by_Cohere(
        string model,
        string inputPrice,
        string billedUnits,
        string expectedCost)
    {
        using var catalog = CreateCatalog(model, inputPrice);
        using var client = CreateClient(model,
            """{"results":[],"meta":{"billed_units":""" + billedUnits + "}}");
        var provider = new CohereProvider(new TestKeyResolver(), new AsyncCacheHelper(), new TestClientFactory(client));

        var response = await provider.RerankingRequest(CreateRequest(model));

        Assert.Equal(
            decimal.Parse(expectedCost, System.Globalization.CultureInfo.InvariantCulture),
            response.ProviderMetadata["gateway"].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task V5_does_not_fall_back_to_search_units_when_input_tokens_are_missing()
    {
        using var catalog = CreateCatalog("rerank-v5.0-fast", "0.000001");
        using var client = CreateClient("rerank-v5.0-fast",
            """{"results":[],"meta":{"billed_units":{"search_units":3}}}""");
        var provider = new CohereProvider(new TestKeyResolver(), new AsyncCacheHelper(), new TestClientFactory(client));

        var response = await provider.RerankingRequest(CreateRequest("rerank-v5.0-fast"));

        Assert.False(response.ProviderMetadata.ContainsKey("gateway"));
    }

    private static ProviderCatalogScope CreateCatalog(string model, string inputPrice)
    {
        var pricing = $$"""{"cohere/{{model}}":{"pricing":{"input":"{{inputPrice}}"} } }""";
        return ProviderCatalogScope.Begin(new TestCatalogSource("cohere", "{}", pricing));
    }

    private static HttpClient CreateClient(string model, string rerankResponse)
        => new(new TestHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse($$"""{"models":[{"name":"{{model}}"}]}""");

            return JsonResponse(rerankResponse);
        }));

    private static RerankingRequest CreateRequest(string model)
        => new()
        {
            Model = $"cohere/{model}",
            Query = "query",
            Documents = new()
            {
                Type = "text",
                Values = JsonDocument.Parse("""["first","second"]""").RootElement.Clone()
            }
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
