using System.Net;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Novita;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class NovitaRequestScopedHeadersTests
{
    [Fact]
    public async Task Fish_speech_auth_and_model_headers_are_attached_to_the_request()
    {
        string? authorization = null;
        string? model = null;
        using var client = new HttpClient(new DelegateHttpMessageHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            model = request.Headers.GetValues("model").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        }));
        var provider = new NovitaProvider(
            new TestKeyResolver(),
            new AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "s1",
            Text = "Hello"
        });

        Assert.Equal("Bearer test-api-key", authorization);
        Assert.Equal("s1", model);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("model"));
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
