using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SmallestAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class SmallestAISpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_preserves_provider_keyed_options_and_forwards_unknown_root_fields()
    {
        JsonElement payload = default;
        var metadata = ParseElement("""
            {
              "sampleRate": 24000,
              "language": "fr",
              "outputFormat": "wav",
              "pronunciationDicts": ["dict-1"],
              "numberPronunciationLanguage": "hi",
              "sessionId": "session-1",
              "requestId": "request-1",
              "wordTimestamps": true,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var provider = CreateProvider(async request =>
        {
            Assert.Equal("/waves/v1/tts", request.RequestUri!.AbsolutePath);
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio")) };
        });
        var speechRequest = new SpeechRequest
        {
            Model = "smallestai/lightning-v3.1/voice-1",
            Text = "Hello",
            Language = "en",
            OutputFormat = "mp3",
            ProviderOptions = new()
            {
                ["smallestai"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await provider.SpeechRequest(speechRequest);

        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voice_id").GetString());
        Assert.Equal("lightning_v3.1", payload.GetProperty("model").GetString());
        Assert.Equal(24000, payload.GetProperty("sample_rate").GetInt32());
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.Equal("mp3", payload.GetProperty("output_format").GetString());
        Assert.True(JsonElement.DeepEquals(ParseElement("""["dict-1"]"""), payload.GetProperty("pronunciation_dicts")));
        Assert.Equal("hi", payload.GetProperty("number_pronunciation_language").GetString());
        Assert.Equal("session-1", payload.GetProperty("session_id").GetString());
        Assert.Equal("request-1", payload.GetProperty("request_id").GetString());
        Assert.True(payload.GetProperty("word_timestamps").GetBoolean());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string",
                     "future_number", "future_boolean", "future_null"
                 })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("smallestai", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.True(JsonElement.DeepEquals(metadata, speechRequest.ProviderOptions["smallestai"]));
        Assert.Equal(18, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Speech_preserves_defaults_omission_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        var provider = CreateProvider(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio")) };
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "smallestai/lightning-v3.1/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["smallestai"] = ParseElement("""{"sampleRate":null,"outputFormat":null,"language":null,"future_null":null}""")
            }
        });

        Assert.Equal(44100, payload.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1, payload.GetProperty("speed").GetDouble());
        Assert.Equal("wav", payload.GetProperty("output_format").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("language", out _));
        Assert.False(payload.TryGetProperty("pronunciation_dicts", out _));
        Assert.False(payload.TryGetProperty("number_pronunciation_language", out _));
        Assert.False(payload.TryGetProperty("session_id", out _));
        Assert.False(payload.TryGetProperty("request_id", out _));
        Assert.False(payload.TryGetProperty("word_timestamps", out _));
        Assert.Equal(7, payload.EnumerateObject().Count());

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "smallestai/lightning-v3.1/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["smallestai"] = ParseElement("""{"sampleRate":"invalid"}""")
            }
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "smallestai/lightning-v3.1/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["smallestai"] = ParseElement("""{"sampleRate":32000}""")
            }
        }));

        var failedProvider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failedProvider.SpeechRequest(new SpeechRequest
        {
            Model = "smallestai/lightning-v3.1/voice-1",
            Text = "Hello"
        }));
        Assert.Contains("SmallestAI TTS failed (400): upstream failure", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"sampleRate":null,"outputFormat":null,"language":null,"pronunciationDicts":null,"numberPronunciationLanguage":null,"sessionId":null,"requestId":null,"wordTimestamps":null}""")]
    public async Task Speech_omits_unset_known_options_and_preserves_defaults(string? metadataJson)
    {
        JsonElement payload = default;
        var provider = CreateProvider(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio")) };
        });
        var speechRequest = new SpeechRequest
        {
            Model = "smallestai/lightning-v3.1/voice-1",
            Text = "Hello"
        };
        if (metadataJson is not null)
            speechRequest.ProviderOptions = new() { ["smallestai"] = ParseElement(metadataJson) };

        await provider.SpeechRequest(speechRequest);

        Assert.True(JsonElement.DeepEquals(ParseElement("""
            {
              "text": "Hello",
              "voice_id": "voice-1",
              "model": "lightning_v3.1",
              "sample_rate": 44100,
              "speed": 1,
              "output_format": "wav"
            }
            """), payload));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("\"invalid\"")]
    public async Task Speech_rejects_non_object_metadata(string metadataJson)
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No upstream request expected."));

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "smallestai/lightning-v3.1/voice-1",
            Text = "Hello",
            ProviderOptions = new() { ["smallestai"] = ParseElement(metadataJson) }
        }));
    }

    [Fact]
    public async Task Speech_preserves_raw_nulls_after_omitting_unset_known_options()
    {
        JsonElement payload = default;
        var provider = CreateProvider(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio")) };
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "smallestai/lightning-v3.1/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["smallestai"] = ParseElement("""{"sessionId":"session-1","session_id":null,"future_null":null}""")
            }
        });

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("session_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("language", out _));
        Assert.Equal(8, payload.EnumerateObject().Count());
    }

    private static SmallestAIProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new CapturingHandler(responder))));

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

    private sealed class CapturingHandler(
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
