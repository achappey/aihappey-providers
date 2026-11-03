using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Picsart;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class PicsartImageTests
{
    [Fact]
    public async Task Logo_generation_forwards_documented_options_and_reference_image()
    {
        var requests = new List<(string Method, string Path, string? Body)>();
        using var client = CreateClient(async request =>
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));

            return request.RequestUri.AbsolutePath switch
            {
                "/v1/logo" => JsonResponse("""{"inference_id":"inference-1"}"""),
                "/v1/logo/inferences/inference-1" => JsonResponse("""{"status":"completed","data":[{"url":"https://picsart.test/generated.png"}]}"""),
                "/generated.png" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                },
                _ => throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}")
            };
        });
        var options = JsonDocument.Parse("""
            {
              "brand_name": "Acme",
              "business_description": "Outdoor gear",
              "logo_description": "A simple mountain emblem",
              "color_tone": "Blue"
            }
            """).RootElement.Clone();
        var request = new ImageRequest
        {
            Model = "picsart/logo/urn:air:fluxai:model:fluxai:flux-2-pro@1",
            N = 2,
            Files =
            [
                new ImageFile
                {
                    Data = "data:image/png;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("reference image")),
                    MediaType = "image/png"
                }
            ],
            ProviderOptions = new()
            {
                ["picsart"] = options
            }
        };
        var requestBefore = JsonSerializer.Serialize(request);
        var provider = new PicsartProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

        var response = await provider.ImageRequest(request);

        Assert.Equal(3, requests.Count);
        var submit = requests[0];
        Assert.Equal("POST", submit.Method);
        Assert.Equal("/v1/logo", submit.Path);
        Assert.Contains("name=\"brand_name\"", submit.Body);
        Assert.Contains("Acme", submit.Body);
        Assert.Contains("name=\"business_description\"", submit.Body);
        Assert.Contains("Outdoor gear", submit.Body);
        Assert.Contains("name=\"logo_description\"", submit.Body);
        Assert.Contains("A simple mountain emblem", submit.Body);
        Assert.Contains("name=\"color_tone\"", submit.Body);
        Assert.Contains("Blue", submit.Body);
        Assert.Contains("name=\"reference_image\"", submit.Body);
        Assert.Contains("reference image", submit.Body);
        Assert.Contains("name=\"count\"", submit.Body);
        Assert.Equal(1, response.Images.Count);
        Assert.EndsWith(Convert.ToBase64String([1, 2, 3]), response.Images[0]);
        Assert.Equal(requestBefore, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Logo_generation_rejects_multiple_reference_sources_before_sending()
    {
        var sentRequests = 0;
        using var client = CreateClient(_ =>
        {
            sentRequests++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        var provider = new PicsartProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));
        var request = new ImageRequest
        {
            Model = "picsart/logo/model",
            Prompt = "A prompt",
            Files = [new ImageFile { Data = "AQID", MediaType = "image/png" }],
            ProviderOptions = new()
            {
                ["picsart"] = JsonDocument.Parse("""{"reference_image_url":"https://example.com/logo.png"}""").RootElement.Clone()
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(request));

        Assert.Equal(0, sentRequests);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
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
