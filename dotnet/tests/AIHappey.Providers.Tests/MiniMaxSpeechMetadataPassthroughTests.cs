using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.MiniMax;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class MiniMaxSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Provider_options_preserve_known_mappings_and_forward_unknown_nested_and_root_values()
    {
        var metadata = ParseElement("""
            {
              "voice_setting": {
                "voice_id": "metadata-voice",
                "speed": 0.6,
                "vol": 1.75,
                "pitch": 4,
                "emotion": "calm",
                "text_normalization": false,
                "latex_read": true,
                "future_nested": {"items": [null, true, {"deep": "kept"}]}
              },
              "audio_setting": {
                "format": "flac",
                "sample_rate": 24000,
                "bitrate": 128000,
                "channel": 2,
                "force_cbr": false,
                "future_audio": [1, {"value": null}]
              },
              "pronunciation_dict": {"tone": ["foo(1)"], "future_dict": {"kept": true}},
              "voice_modify": {
                "pitch": 2, "intensity": 3, "timbre": 4,
                "sound_effects": null, "future_modify": [1, 2]
              },
              "language_boost": "Japanese",
              "subtitle_enable": false,
              "lyrics": "extra lyrics",
              "future_object": {"nested": [{"value": true}, [null, 1]]},
              "future_array": [1, {"value": "kept"}],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/t2a_v2", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"data":{"audio":"00"},"extra_info":{"usage_characters":1}}""");
        });

        var request = new SpeechRequest
        {
            Model = "speech-01",
            Text = "Hello",
            Voice = "request-voice",
            Speed = 1.5f,
            OutputFormat = "WAV",
            ProviderOptions = new()
            {
                ["minimax"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("minimax")));

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("speech-01", payload.GetProperty("model").GetString());
        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal("hex", payload.GetProperty("output_format").GetString());
        Assert.Equal("Japanese", payload.GetProperty("language_boost").GetString());
        Assert.False(payload.GetProperty("subtitle_enable").GetBoolean());
        Assert.Equal("request-voice", payload.GetProperty("voice_setting").GetProperty("voice_id").GetString());
        Assert.Equal(1.5, payload.GetProperty("voice_setting").GetProperty("speed").GetDouble());
        Assert.Equal(1.75, payload.GetProperty("voice_setting").GetProperty("vol").GetDouble());
        Assert.Equal("calm", payload.GetProperty("voice_setting").GetProperty("emotion").GetString());
        Assert.False(payload.GetProperty("voice_setting").GetProperty("text_normalization").GetBoolean());
        Assert.True(payload.GetProperty("voice_setting").GetProperty("latex_read").GetBoolean());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("voice_setting").GetProperty("future_nested"),
            payload.GetProperty("voice_setting").GetProperty("future_nested")));
        Assert.Equal("wav", payload.GetProperty("audio_setting").GetProperty("format").GetString());
        Assert.Equal(24000, payload.GetProperty("audio_setting").GetProperty("sample_rate").GetInt32());
        Assert.False(payload.GetProperty("audio_setting").GetProperty("force_cbr").GetBoolean());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("audio_setting").GetProperty("future_audio"),
            payload.GetProperty("audio_setting").GetProperty("future_audio")));
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("pronunciation_dict"),
            payload.GetProperty("pronunciation_dict")));
        Assert.Equal(2, payload.GetProperty("voice_modify").GetProperty("pitch").GetInt32());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("voice_modify").GetProperty("future_modify"),
            payload.GetProperty("voice_modify").GetProperty("future_modify")));
        Assert.False(payload.GetProperty("voice_modify").TryGetProperty("sound_effects", out _));

        foreach (var name in new[]
                 {
                     "lyrics", "future_object", "future_array", "future_string", "future_number",
                     "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("minimax", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.False(payload.GetProperty("stream").GetBoolean());
        Assert.Equal(
            new[]
            {
                "voice_setting", "audio_setting", "pronunciation_dict", "voice_modify",
                "language_boost", "subtitle_enable", "lyrics", "future_object", "future_array",
                "future_string", "future_number", "future_boolean", "future_null", "model",
                "text", "stream", "output_format"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Defaults_validation_null_omission_and_upstream_errors_remain_compatible()
    {
        JsonElement? payload = null;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"data":{"audio":"00"}}""");
        });

        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "speech-01",
            Text = "Hello"
        });

        Assert.NotNull(payload);
        Assert.Equal("English_Insightful_Speaker", payload.Value.GetProperty("voice_setting").GetProperty("voice_id").GetString());
        Assert.Equal(1, payload.Value.GetProperty("voice_setting").GetProperty("speed").GetDouble());
        Assert.Equal(1, payload.Value.GetProperty("voice_setting").GetProperty("vol").GetDouble());
        Assert.Equal(0, payload.Value.GetProperty("voice_setting").GetProperty("pitch").GetInt32());
        Assert.Equal("mp3", payload.Value.GetProperty("audio_setting").GetProperty("format").GetString());
        Assert.False(payload.Value.TryGetProperty("language_boost", out _));
        Assert.False(payload.Value.TryGetProperty("subtitle_enable", out _));
        Assert.False(payload.Value.TryGetProperty("pronunciation_dict", out _));
        Assert.False(payload.Value.TryGetProperty("voice_modify", out _));
        Assert.False(payload.Value.TryGetProperty("audio_base64", out _));

        payload = null;
        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "speech-01",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["minimax"] = ParseElement(
                    """{"lyrics":null,"lyrics_optimizer":null,"audio_url":null,"pronunciation_dict":{"tone":null,"future_null":null},"voice_modify":{"sound_effects":null,"future_null":null}}""")
            }
        });

        Assert.NotNull(payload);
        Assert.False(payload.Value.TryGetProperty("lyrics", out _));
        Assert.False(payload.Value.TryGetProperty("lyrics_optimizer", out _));
        Assert.False(payload.Value.TryGetProperty("audio_url", out _));
        Assert.False(payload.Value.GetProperty("pronunciation_dict").TryGetProperty("tone", out _));
        Assert.Equal(JsonValueKind.Null, payload.Value.GetProperty("pronunciation_dict").GetProperty("future_null").ValueKind);
        Assert.False(payload.Value.GetProperty("voice_modify").TryGetProperty("sound_effects", out _));
        Assert.Equal(JsonValueKind.Null, payload.Value.GetProperty("voice_modify").GetProperty("future_null").ValueKind);

        var requestsSent = 0;
        using var invalidClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        foreach (var invalidOptions in new[] { """{"voice_setting":{"speed":"invalid"}}""", "[]" })
        {
            await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).SpeechRequest(new SpeechRequest
            {
                Model = "speech-01",
                Text = "Hello",
                ProviderOptions = new() { ["minimax"] = ParseElement(invalidOptions) }
            }));
        }

        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).SpeechRequest(new SpeechRequest
            {
                Model = "speech-01",
                Text = "Hello"
            }));
        Assert.Equal("MiniMax t2a_v2 failed (400): upstream failure", error.Message);
    }

    private static MiniMaxProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
