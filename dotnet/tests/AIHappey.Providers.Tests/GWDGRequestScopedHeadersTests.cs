using System.Net;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.GWDG;

namespace AIHappey.Tests;

public sealed class GWDGRequestScopedHeadersTests
{
    [Fact]
    public async Task Chat_authorization_is_attached_to_the_request_not_the_shared_client()
    {
        string? authorization = null;
        using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("unauthorized")
            });
        }));
        var provider = new GWDGProvider(
            new TestKeyResolver(),
            new TestHttpClientFactory(client),
            new AsyncCacheHelper());

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(
            new ChatCompletionOptions { Model = "test-model" }));

        Assert.Equal("Bearer test-api-key", authorization);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

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
