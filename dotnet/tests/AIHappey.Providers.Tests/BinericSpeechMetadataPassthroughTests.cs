using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Bineric;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class BinericSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_preserves_provider_options_and_merges_unknown_fields_at_request_root()
    {
        JsonElement payload = default;
        var metadata = ParseElement("""
            {
              "language": "fr",
              "voice": "metadata-voice",
              "pitch": "high",
              "rate": 1.5,
              "volume": "loud",
              "future_object": {"nested": [{"value": true}, [null, 2]]},
              "future_array": [1, {"value": "kept"}],
              "future_string": "unchanged",
              "future_number": 12.5,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "text-to-speech",
            Text = "Hello",
            Language = "en",
            Voice = "request-voice",
            Speed = 2,
            ProviderOptions = new() { ["bineric"] = metadata }
        };
        var clientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientRequest.GetProperty("providerOptions").GetProperty("bineric")));

        await CreateProvider(message =>
        {
            Assert.Equal("/api/v1/ai/chat/completions", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SpeechResponse();
        }).SpeechRequest(request);

        Assert.Equal("text-to-speech", payload.GetProperty("model").GetString());
        Assert.Equal("Hello", payload.GetProperty("messages")[0].GetProperty("content").GetString());
        var options = payload.GetProperty("options");
        Assert.Equal("en", options.GetProperty("language").GetString());
        Assert.Equal("request-voice", options.GetProperty("voice").GetString());
        Assert.Equal("high", options.GetProperty("pitch").GetString());
        Assert.Equal(2, options.GetProperty("rate").GetDouble());
        Assert.Equal("loud", options.GetProperty("volume").GetString());

        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string",
                     "future_number", "future_boolean", "future_null"
                 })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("bineric", out _));
        Assert.Equal(
            new[]
            {
                "model", "messages", "options", "future_object", "future_array",
                "future_string", "future_number", "future_boolean", "future_null"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(
            new[] { "language", "voice", "pitch", "rate", "volume" }.Order(),
            options.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Speech_keeps_known_option_omission_defaults_validation_and_error_behavior()
    {
        JsonElement payload = default;
        var provider = CreateProvider(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SpeechResponse();
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "text-to-speech",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["bineric"] = ParseElement(
                    """{"language":null,"voice":null,"pitch":null,"rate":null,"volume":null,"future_null":null}""")
            }
        });

        Assert.Equal("text-to-speech", payload.GetProperty("model").GetString());
        var options = payload.GetProperty("options");
        foreach (var name in new[] { "language", "voice", "pitch", "rate", "volume" })
        {
            if (options.TryGetProperty(name, out var value))
                Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(4, payload.EnumerateObject().Count());

        var unusedProvider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => unusedProvider.SpeechRequest(new SpeechRequest
        {
            Model = "text-to-speech",
            Text = "Hello",
            ProviderOptions = new() { ["bineric"] = ParseElement("""{"rate":"invalid"}""") }
        }));
        await Assert.ThrowsAsync<JsonException>(() => unusedProvider.SpeechRequest(new SpeechRequest
        {
            Model = "text-to-speech",
            Text = "Hello",
            ProviderOptions = new() { ["bineric"] = ParseElement("[]") }
        }));

        var failedProvider = CreateProvider(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => failedProvider.SpeechRequest(
            new SpeechRequest { Model = "text-to-speech", Text = "Hello" }));
        Assert.Equal("Bineric TTS failed (400): upstream failure", error.Message);
    }

    private static BinericProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new CapturingHandler(responder))));

    private static HttpResponseMessage SpeechResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":{"audioContent":"data:audio/mpeg;base64,AQID","metadata":{"format":"mp3"}}}}]}""")
        };

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

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
