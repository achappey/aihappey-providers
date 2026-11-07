using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Bria;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class BriaImageInputTests
{
    [Theory]
    [InlineData("data:image/png;base64,AQID", "AQID")]
    [InlineData("AQID", "AQID")]
    [InlineData("https://example.test/image.png", "https://example.test/image.png")]
    public async Task Edit_image_and_mask_use_raw_base64_or_public_urls(
        string input,
        string expected)
    {
        JsonElement payload = default;
        using var client = CreateClient(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/v2/image/edit/erase", request.RequestUri!.AbsolutePath);
                payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
                return JsonResponse("""{"result":{"image_url":"https://engine.prod.bria-api.com/result.png"}}""");
            }

            Assert.Equal("/result.png", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var provider = new BriaProvider(new TestApiKeyResolver(), new TestHttpClientFactory(client));
        var request = new ImageRequest
        {
            Model = "image/edit/erase",
            Files = [new ImageFile { Data = input, MediaType = "image/png" }],
            Mask = new ImageFile { Data = input, MediaType = "image/png" }
        };

        await provider.ImageRequest(request);

        Assert.Equal(expected, payload.GetProperty("image").GetString());
        Assert.Equal(expected, payload.GetProperty("mask").GetString());
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class TestApiKeyResolver : IApiKeyResolver
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
