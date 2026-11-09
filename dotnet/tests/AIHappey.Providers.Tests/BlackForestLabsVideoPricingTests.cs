using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.BlackForestLabs;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class BlackForestLabsVideoPricingTests
{
    [Fact]
    public async Task Video_uses_settled_poll_cost_instead_of_submission_cost()
    {
        using var client = CreateClient(request => request.Method == HttpMethod.Post
            ? JsonResponse("""{"id":"task-123","cost":900,"polling_url":"https://api.bfl.ai/v1/get_result?id=task-123"}""")
            : JsonResponse("""{"status":"Ready","cost":1234,"result":{"sample":"data:video/mp4;base64,AQID"}}"""));
        var provider = CreateProvider(client);

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "flux-3-video",
            Prompt = "A short video"
        });

        Assert.False(started.ProviderMetadata!.ContainsKey("gateway"));
        var result = Assert.IsType<VideoOperationCompletedResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.Equal(12.34m, result.ProviderMetadata!["gateway"].GetProperty("cost").GetDecimal());
        Assert.Equal(1234, result.ProviderMetadata["blackforestlabs"].GetProperty("cost").GetInt32());
    }

    [Fact]
    public async Task Video_pending_poll_does_not_report_estimated_cost_as_billable()
    {
        using var client = CreateClient(request => request.Method == HttpMethod.Post
            ? JsonResponse("""{"id":"task-123","cost":900,"polling_url":"https://api.bfl.ai/v1/get_result?id=task-123"}""")
            : JsonResponse("""{"status":"Pending","cost":900}"""));
        var provider = CreateProvider(client);
        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "flux-3-video",
            Prompt = "A short video"
        });

        var result = Assert.IsType<VideoOperationPendingResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        Assert.False(result.ProviderMetadata!.ContainsKey("gateway"));
    }

    private static BlackForestLabsProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new TestHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

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
