using System.Net;
using System.Text;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Speechmatics;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class SpeechmaticsProviderAuthTests
{
    [Fact]
    public async Task Batch_transcription_attaches_auth_to_each_request_only()
    {
        var requests = new List<string>();
        using var client = CreateClient(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(request.Method switch
            {
                var method when method == HttpMethod.Post => JsonResponse("""{"id":"job-1"}"""),
                _ when request.RequestUri!.AbsolutePath.EndsWith("/transcript", StringComparison.Ordinal)
                    => JsonResponse("""{"results":[],"metadata":{"transcription_config":{"language":"en"}}}"""),
                _ => JsonResponse("""{"job":{"status":"done","duration":1}}""")
            });
        });
        var provider = CreateProvider(client);

        await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "speechmatics/en",
            Audio = Convert.ToBase64String([1, 2, 3]),
            MediaType = "audio/wav"
        });

        Assert.Equal(new[]
        {
            "POST /v2/jobs",
            "GET /v2/jobs/job-1",
            "GET /v2/jobs/job-1/transcript"
        }, requests);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Speech_attaches_auth_to_the_request_only()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        });
        var provider = CreateProvider(client);

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "speechmatics/en/voice/sarah",
            Voice = "sarah",
            Text = "Hello"
        });

        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static SpeechmaticsProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new TestHttpClientFactory(client));

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
