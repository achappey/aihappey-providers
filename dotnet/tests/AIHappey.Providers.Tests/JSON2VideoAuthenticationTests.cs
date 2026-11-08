using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.JSON2Video;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class JSON2VideoAuthenticationTests
{
    [Fact]
    public async Task Video_api_key_is_scoped_to_api_requests_and_not_sent_to_the_download_host()
    {
        using var client = CreateClient(async request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.json2video.com")
            {
                Assert.Equal("test-api-key", request.Headers.GetValues("x-api-key").Single());
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal("/v2/movies", uri.AbsolutePath);
                    using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    Assert.True(payload.RootElement.GetProperty("future_option").GetProperty("nested").GetBoolean());
                    return JsonResponse("""{"success":true,"project":"project-123"}""");
                }

                if (request.Method == HttpMethod.Get)
                {
                    Assert.Equal("?project=project-123&format=simple", uri.Query);
                    return JsonResponse("""{"success":true,"movie":{"status":"done","url":"https://assets.example/render.mp4"}}""");
                }

                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal("?project=project-123", uri.Query);
                return JsonResponse("""{"success":true,"project":"project-123"}""");
            }

            Assert.Equal("assets.example", uri.Host);
            Assert.False(request.Headers.Contains("x-api-key"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var provider = new JSON2VideoProvider(new TestKeyResolver(), new TestHttpClientFactory(client));
        using var options = JsonDocument.Parse(
            """{"future_option":{"nested":true},"scenes":[{"elements":[{"type":"text","text":"Hello","duration":5}]}]}""");

        var operation = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "json2video/render",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["json2video"] = options.RootElement.Clone()
            }
        });
        var result = await provider.GetVideoOperationStatus(operation.Operation);

        var video = Assert.IsType<VideoOperationCompletedResult>(result);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), Assert.Single(video.Videos).Data);
        Assert.Empty(client.DefaultRequestHeaders);
    }

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
