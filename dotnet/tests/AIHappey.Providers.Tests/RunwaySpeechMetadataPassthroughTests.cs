using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Providers.Runway;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class RunwaySpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Text_to_speech_preserves_keyed_contract_and_forwards_unknown_fields()
    {
        var metadata = ParseElement(
            """
            {
              "voice": {
                "type": "custom",
                "presetId": "metadata-voice",
                "future_voice": {"array": [true, null, 4]}
              },
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "soundEffects": {"duration": 3}
            }
            """);
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/text_to_speech")
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"id":"task-1"}""");
            }

            return CompleteTask(request);
        });

        var speechRequest = CreateRequest("tts", metadata, voice: "request-voice");
        var boundary = JsonSerializer.SerializeToElement(speechRequest, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(metadata, boundary.GetProperty("providerOptions").GetProperty("runway")));

        await CreateProvider(client).SpeechRequest(speechRequest, CancellationToken.None);

        Assert.Equal("tts", payload.GetProperty("model").GetString());
        Assert.Equal("Hello", payload.GetProperty("promptText").GetString());
        Assert.Equal("runway-preset", payload.GetProperty("voice").GetProperty("type").GetString());
        Assert.Equal("request-voice", payload.GetProperty("voice").GetProperty("presetId").GetString());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("voice").GetProperty("future_voice"),
            payload.GetProperty("voice").GetProperty("future_voice")));
        AssertUnknownFields(metadata, payload,
            "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("runway", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
        Assert.False(payload.TryGetProperty("soundEffects", out _));

        await CreateProvider(client).SpeechRequest(CreateRequest("tts", metadata), CancellationToken.None);
        Assert.Equal("metadata-voice", payload.GetProperty("voice").GetProperty("presetId").GetString());
    }

    [Fact]
    public async Task Sound_effects_unwraps_known_options_and_preserves_nested_and_root_unknown_fields()
    {
        var metadata = ParseElement(
            """
            {
              "soundEffects": {
                "duration": 4,
                "loop": true,
                "future_object": {"nested": [1, {"value": null}]},
                "future_array": [false, {"deep": ["text", 7]}],
                "future_string": "unchanged",
                "future_number": 12345678901234567890.123456789,
                "future_boolean": false,
                "future_null": null
              },
              "root_future": {"array": [null, true]}
            }
            """);
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/sound_effect")
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"id":"task-1"}""");
            }

            return CompleteTask(request);
        });

        var speechRequest = CreateRequest("eleven_text_to_sound_v2", metadata);
        var boundary = JsonSerializer.SerializeToElement(speechRequest, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(metadata, boundary.GetProperty("providerOptions").GetProperty("runway")));

        await CreateProvider(client).SpeechRequest(speechRequest, CancellationToken.None);

        Assert.Equal("eleven_text_to_sound_v2", payload.GetProperty("model").GetString());
        Assert.Equal("Hello", payload.GetProperty("promptText").GetString());
        Assert.Equal(4, payload.GetProperty("duration").GetDouble());
        Assert.True(payload.GetProperty("loop").GetBoolean());
        AssertUnknownFields(metadata.GetProperty("soundEffects"), payload,
            "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null");
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("root_future"), payload.GetProperty("root_future")));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("runway", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
        Assert.False(payload.TryGetProperty("soundEffects", out _));
        Assert.Equal(11, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Sound_effects_preserves_omissions_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/sound_effect")
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"id":"task-1"}""");
            }

            return CompleteTask(request);
        });

        await CreateProvider(client).SpeechRequest(CreateRequest("eleven_text_to_sound_v2", default), CancellationToken.None);
        Assert.False(payload.TryGetProperty("duration", out _));
        Assert.False(payload.TryGetProperty("loop", out _));
        Assert.Equal(2, payload.EnumerateObject().Count());

        var requestsSent = 0;
        using var unusedClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreateProvider(unusedClient).SpeechRequest(
                CreateRequest("eleven_text_to_sound_v2", ParseElement("""{"soundEffects":{"duration":0}}""")),
                CancellationToken.None));
        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ => JsonResponse("upstream failure", HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).SpeechRequest(
                CreateRequest("eleven_text_to_sound_v2", default),
                CancellationToken.None));
        Assert.Equal("Runway sound_effect failed (400): upstream failure", error.Message);
    }

    private static void AssertUnknownFields(JsonElement source, JsonElement payload, params string[] names)
    {
        foreach (var name in names)
            Assert.True(JsonElement.DeepEquals(source.GetProperty(name), payload.GetProperty(name)), name);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
    }

    private static SpeechRequest CreateRequest(string model, JsonElement metadata, string? voice = null)
        => new()
        {
            Model = model,
            Text = "Hello",
            Voice = voice,
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement>
                {
                    ["runway"] = metadata,
                    ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
                }
        };

    private static RunwayProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static HttpResponseMessage CompleteTask(HttpRequestMessage request)
        => request.RequestUri!.AbsolutePath switch
        {
            "/v1/tasks/task-1" => JsonResponse("""{"status":"SUCCEEDED","output":["https://media.test/audio.mp3"]}"""),
            "/audio.mp3" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            },
            _ => throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}")
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
