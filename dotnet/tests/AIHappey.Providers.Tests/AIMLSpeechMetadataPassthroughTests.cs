using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AIML;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class AIMLSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_preserves_provider_key_and_forwards_unknown_json_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/tts", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });

        var metadata = ParseElement(
            """
            {
              "model": "metadata-model",
              "text": "metadata-text",
              "voice": "metadata-voice",
              "response_format": "metadata-response-format",
              "output_format": "metadata-output-format",
              "format": "metadata-format",
              "future_object": {"nested": [{"value": true}, [null, 1]]},
              "future_array": [1, {"more": [false, 2.5]}],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "request-model",
            Text = "request text",
            Voice = "request-voice",
            OutputFormat = "wav",
            ProviderOptions = new()
            {
                ["aiml"] = metadata,
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("aiml")));

        var response = await CreateProvider(client).SpeechRequest(request, CancellationToken.None);

        Assert.Equal("YXVkaW8=", response.Audio.Base64);
        Assert.Equal("request-model", payload.GetProperty("model").GetString());
        Assert.Equal("request text", payload.GetProperty("text").GetString());
        Assert.Equal("request-voice", payload.GetProperty("voice").GetString());
        Assert.Equal("wav", payload.GetProperty("response_format").GetString());
        Assert.Equal("wav", payload.GetProperty("output_format").GetString());
        Assert.Equal("wav", payload.GetProperty("format").GetString());

        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number",
                     "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("aiml", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(12, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Speech_omits_missing_provider_options_and_preserves_validation_and_errors()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });
        var provider = CreateProvider(client);

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "request-model",
            Text = "request text",
            ProviderOptions = new()
            {
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        }, CancellationToken.None);

        Assert.Equal("YXVkaW8=", response.Audio.Base64);
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"model":"request-model","text":"request text"}"""),
            payload));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(
            new SpeechRequest { Model = "request-model" },
            CancellationToken.None));

        using var failedClient = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(failedClient)
            .SpeechRequest(new SpeechRequest { Model = "request-model", Text = "request text" }, CancellationToken.None));

        Assert.Equal("AIML TTS failed (400): upstream failure", error.Message);
    }

    private static AIMLProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static HttpResponseMessage AudioResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return response;
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
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
