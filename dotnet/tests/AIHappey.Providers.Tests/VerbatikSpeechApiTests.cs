using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Verbatik;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class VerbatikSpeechApiTests
{
    [Fact]
    public async Task Speech_uses_current_async_api_and_preserves_provider_options_without_leaking_auth()
    {
        JsonElement payload = default;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            if (request.RequestUri!.Host == "app.verbatik.com"
                && request.RequestUri.AbsolutePath == "/api/v1/speech"
                && request.Method == HttpMethod.Post)
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse(
                    """{"id":"speech-1","object":"speech_generation","status":"queued"}""",
                    HttpStatusCode.Accepted);
            }

            if (request.RequestUri.Host == "app.verbatik.com"
                && request.RequestUri.AbsolutePath == "/api/v1/speech/speech-1")
            {
                Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
                return JsonResponse(
                    """{"id":"speech-1","object":"speech_generation","status":"completed","outputs":[{"url":"https://media.test/audio.mp3","media_type":"audio/mpeg"}]}""");
            }

            if (request.RequestUri.Host == "media.test")
            {
                Assert.Null(request.Headers.Authorization);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-bytes"))
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }));
        using var metadataDocument = JsonDocument.Parse(
            """
            {
              "delivery": "signed_url",
              "loudness_normalization": true,
              "volume": 0.8,
              "future_option": {"nested": [1, true, null]}
            }
            """);
        var provider = new VerbatikProvider(
            new TestKeyResolver(),
            new AIHappey.Core.AI.AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "verbatik/tts",
            Text = "Hello",
            Voice = "Wise_Woman",
            Speed = 1.25f,
            ProviderOptions = new Dictionary<string, object?>
            {
                ["verbatik"] = metadataDocument.RootElement.Clone(),
                ["other-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        });

        Assert.Equal("verbatik/speech-lite", payload.GetProperty("model").GetString());
        var block = Assert.Single(payload.GetProperty("blocks").EnumerateArray());
        Assert.Equal("Hello", block.GetProperty("text").GetString());
        Assert.Equal("Wise_Woman", block.GetProperty("voice").GetString());
        Assert.Equal(1.25f, payload.GetProperty("settings").GetProperty("speed").GetSingle());
        Assert.Equal(0.8, payload.GetProperty("settings").GetProperty("volume").GetDouble());
        Assert.True(payload.GetProperty("settings").GetProperty("loudness_normalization").GetBoolean());
        Assert.Equal("signed_url", payload.GetProperty("delivery").GetString());
        Assert.True(JsonElement.DeepEquals(
            metadataDocument.RootElement.GetProperty("future_option"),
            payload.GetProperty("future_option")));
        Assert.False(payload.TryGetProperty("other-provider", out _));
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("audio-bytes")), response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
