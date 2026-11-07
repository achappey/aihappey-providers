using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.HumeAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class HumeAISpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Humeai_speech_preserves_keyed_options_and_forwards_unknown_values()
    {
        JsonElement payload = default;
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/v0/tts", request.RequestUri!.AbsolutePath);
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return JsonResponse("""{"generations":[{"audio":"YXVkaW8=","generation_id":"g1"}]}""");
        });

        var metadata = ParseElement(
            """
            {
              "voice_id": "metadata-voice",
              "description": "metadata description",
              "output_format": "wav",
              "version": "2",
              "temperature": 0.35,
              "trailing_silence": 0.2,
              "context_generation_id": "context-g1",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "octave",
            Text = "Hello",
            Voice = "request-voice",
            Instructions = "request description",
            ProviderOptions = new()
            {
                ["humeai"] = metadata,
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("humeai")));

        var response = await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("audio", Encoding.UTF8.GetString(Convert.FromBase64String(response.Audio.Base64)));
        Assert.Equal("wav", payload.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("2", payload.GetProperty("version").GetString());
        Assert.Equal(0.35, payload.GetProperty("temperature").GetDouble());
        Assert.Equal("request description", payload.GetProperty("utterances")[0].GetProperty("description").GetString());
        Assert.Equal("request-voice", payload.GetProperty("utterances")[0].GetProperty("voice").GetProperty("id").GetString());
        Assert.Equal("0.2", payload.GetProperty("utterances")[0].GetProperty("trailing_silence").GetRawText());
        Assert.Equal("context-g1", payload.GetProperty("context").GetProperty("generation_id").GetString());

        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("humeai", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
        Assert.False(payload.TryGetProperty("voice_id", out _));
        Assert.False(payload.TryGetProperty("output_format", out _));
        Assert.False(payload.TryGetProperty("description", out _));
    }

    [Fact]
    public async Task Humeai_speech_preserves_context_unknown_fields_and_known_omissions()
    {
        JsonElement payload = default;
        using var client = CreateClient(async request =>
        {
            payload = ParseElement(await request.Content!.ReadAsStringAsync());
            return JsonResponse("""{"generations":[{"audio":"YXVkaW8=","generation_id":"g1"}]}""");
        });

        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "octave",
            Text = "Hello",
            Voice = "voice-1",
            ProviderOptions = new()
            {
                ["humeai"] = ParseElement(
                    """{"voice_id":null,"output_format":null,"version":null,"context_utterances":[{"text":"Previous","voice_id":"context-voice","future_context":{"array":[1,null,{"nested":true}]}}],"future_null":null}""")
            }
        });

        Assert.Equal("mp3", payload.GetProperty("format").GetProperty("type").GetString());
        Assert.False(payload.TryGetProperty("version", out _));
        Assert.False(payload.TryGetProperty("context_generation_id", out _));
        Assert.Equal("context-voice", payload.GetProperty("context").GetProperty("utterances")[0].GetProperty("voice").GetProperty("id").GetString());
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"array":[1,null,{"nested":true}]}"""),
            payload.GetProperty("context").GetProperty("utterances")[0].GetProperty("future_context")));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.GetProperty("utterances")[0].TryGetProperty("description", out _));
        Assert.False(payload.GetProperty("utterances")[0].TryGetProperty("trailing_silence", out _));
        Assert.False(payload.TryGetProperty("instant_mode", out _));
        Assert.Equal(4, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Humeai_speech_preserves_validation_and_upstream_errors()
    {
        var requestsSent = 0;
        using var unusedClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });

        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).SpeechRequest(new SpeechRequest
        {
            Model = "octave",
            Text = "Hello",
            Voice = "voice-1",
            ProviderOptions = new() { ["humeai"] = ParseElement("""{"temperature":"invalid"}""") }
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(unusedClient).SpeechRequest(new SpeechRequest
        {
            Model = "octave",
            Text = "Hello",
            Voice = "voice-1",
            ProviderOptions = new() { ["humeai"] = ParseElement("""{"version":"3"}""") }
        }));
        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ =>
            Task.FromResult(JsonResponse("upstream failure", HttpStatusCode.BadRequest)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).SpeechRequest(new SpeechRequest
            {
                Model = "octave",
                Text = "Hello",
                Voice = "voice-1"
            }));
        Assert.Equal("HumeAI TTS failed (400): upstream failure", error.Message);
    }

    private static HumeAIProvider CreateProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHandler(responder));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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
