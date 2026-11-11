using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Narakeet;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class NarakeetProviderTests
{
    [Fact]
    public async Task Long_text_uses_polling_and_preserves_query_options_without_authenticating_polls()
    {
        var audio = new byte[] { 1, 2, 3 };
        using var client = Client(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/text-to-speech/mp3", request.RequestUri!.AbsolutePath);
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.Contains("voice=amy", query);
                Assert.Contains("voice-volume=soft", query);
                Assert.Contains("future=a b", query);
                Assert.Empty(request.Headers.Accept);
                Assert.Equal("api-key", request.Headers.GetValues("x-api-key").Single());
                return Json("""{"statusUrl":"https://api.narakeet.com/status?token=task"}""");
            }

            Assert.False(request.Headers.Contains("x-api-key"));
            if (request.RequestUri!.AbsolutePath == "/status")
                return Json("""{"finished":true,"succeeded":true,"result":"https://api.narakeet.com/result?token=audio"}""");
            Assert.Equal("/result", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
        });

        var result = await Provider(client).SpeechRequest(new SpeechRequest
        {
            Model = "narakeet/amy",
            Text = new string('x', 1025),
            ProviderOptions = new()
            {
                ["narakeet"] = Element("""{"voice-volume":"soft","future":"a b"}""")
            }
        });

        Assert.Equal(Convert.ToBase64String(audio), result.Audio.Base64);
        Assert.Equal("audio/mpeg", result.Audio.MimeType);
        Assert.Equal("mp3", result.Audio.Format);
    }

    [Fact]
    public async Task Wav_uses_long_content_polling()
    {
        using var client = Client(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/text-to-speech/wav", request.RequestUri!.AbsolutePath);
                Assert.Empty(request.Headers.Accept);
                return Json("""{"statusUrl":"https://api.narakeet.com/status"}""");
            }

            Assert.False(request.Headers.Contains("x-api-key"));
            if (request.RequestUri!.AbsolutePath == "/status")
                return Json("""{"finished":true,"succeeded":true,"result":"https://api.narakeet.com/result"}""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });

        var result = await Provider(client).SpeechRequest(new SpeechRequest
        {
            Model = "narakeet/amy",
            Text = "Hello",
            OutputFormat = "wav"
        });

        Assert.Equal("audio/wav", result.Audio.MimeType);
        Assert.Equal("wav", result.Audio.Format);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), result.Audio.Base64);
    }

    [Fact]
    public async Task Streaming_rejects_content_that_requires_polling()
    {
        using var client = Client(_ => throw new InvalidOperationException("No request expected."));
        var provider = Provider(client);
        var options = new AudioSpeechRequest
        {
            Model = "narakeet/amy",
            Input = new string('x', 1025),
            StreamFormat = "sse"
        };

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.OpenAISpeechStreamingAsync(options)) { }
        });
    }

    private static NarakeetProvider Provider(HttpClient client)
        => new(new TestKeyResolver(), new TestHttpClientFactory(client));

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(new TestHttpMessageHandler(responseFactory));

    private static HttpResponseMessage Json(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private static JsonElement Element(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
