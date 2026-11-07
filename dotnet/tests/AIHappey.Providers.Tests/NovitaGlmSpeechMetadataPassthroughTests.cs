using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Novita;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class NovitaGlmSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Glm_speech_forwards_unknown_keyed_options_and_keeps_known_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v3/glm-tts", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });

        var metadata = ParseElement("""
            {
              "glm": {
                "voice": "metadata-voice",
                "response_format": "mp3",
                "speed": 0.6,
                "volume": 1.75,
                "watermark_enabled": false,
                "future_object": {"nested": [{"value": true}, [null, 1]]},
                "future_array": [1, {"value": "kept"}],
                "future_string": "unchanged",
                "future_number": 12345678901234567890.123456789,
                "future_boolean": false,
                "future_null": null
              },
              "future_root": {"must_not_be_forwarded": true}
            }
            """);
        var request = new SpeechRequest
        {
            Model = "glm-tts",
            Text = "Hello",
            Voice = "request-voice",
            Speed = 1.5f,
            OutputFormat = "WAV",
            ProviderOptions = new()
            {
                ["novita"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("novita")));

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("Hello", payload.GetProperty("input").GetString());
        Assert.Equal("request-voice", payload.GetProperty("voice").GetString());
        Assert.Equal("wav", payload.GetProperty("response_format").GetString());
        Assert.Equal(1.5, payload.GetProperty("speed").GetDouble());
        Assert.Equal(1.75, payload.GetProperty("volume").GetDouble());
        Assert.False(payload.GetProperty("watermark_enabled").GetBoolean());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number",
                     "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(
                metadata.GetProperty("glm").GetProperty(name),
                payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("glm", out _));
        Assert.False(payload.TryGetProperty("future_root", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("novita", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "future_object", "future_array", "future_string", "future_number", "future_boolean",
                "future_null", "input", "voice", "speed", "volume", "response_format", "watermark_enabled"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Glm_speech_keeps_defaults_null_omission_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return AudioResponse();
        });

        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "glm-tts",
            Text = "Hello"
        });

        Assert.Equal("tongtong", payload.GetProperty("voice").GetString());
        Assert.Equal("pcm", payload.GetProperty("response_format").GetString());
        Assert.Equal(1, payload.GetProperty("speed").GetDouble());
        Assert.Equal(1, payload.GetProperty("volume").GetDouble());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("watermark_enabled").ValueKind);

        payload = default;
        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "glm-tts",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["novita"] = ParseElement(
                    """{"glm":{"voice":"metadata-voice","response_format":"wav","speed":3,"volume":11}}""")
            }
        });

        Assert.Equal("metadata-voice", payload.GetProperty("voice").GetString());
        Assert.Equal("wav", payload.GetProperty("response_format").GetString());
        Assert.Equal(2, payload.GetProperty("speed").GetDouble());
        Assert.Equal(10, payload.GetProperty("volume").GetDouble());

        var requestCount = 0;
        using var invalidClient = CreateClient(_ =>
        {
            requestCount++;
            throw new InvalidOperationException("Request should not be sent.");
        });

        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).SpeechRequest(new SpeechRequest
        {
            Model = "glm-tts",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["novita"] = ParseElement("""{"glm":{"speed":"invalid"}}""")
            }
        }));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).SpeechRequest(new SpeechRequest
        {
            Model = "glm-tts",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["novita"] = ParseElement("""{"glm":[]}""")
            }
        }));
        Assert.Equal(0, requestCount);

        using var failedClient = CreateClient(_ =>
            new(HttpStatusCode.BadRequest) { Content = new StringContent("upstream failure") });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).SpeechRequest(new SpeechRequest
            {
                Model = "glm-tts",
                Text = "Hello",
                ProviderOptions = new()
                {
                    ["novita"] = ParseElement("""{"glm":{"future_parameter":true}}""")
                }
            }));
        Assert.Equal("Novita GLM-TTS failed (400): upstream failure", error.Message);
    }

    private static NovitaProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(), new StaticHttpClientFactory(client));

    private static HttpResponseMessage AudioResponse()
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
