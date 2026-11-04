using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Freepik;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class FreepikSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Freepik_speech_flattens_known_options_and_forwards_unknown_json()
    {
        var metadata = ParseElement("""
            {
              "sound_effects": {
                "duration_seconds": 7,
                "prompt_influence": 0.25,
                "loop": false,
                "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                "future_array": [1, {"more": [false, 2.5]}, []],
                "future_string": "unchanged",
                "future_number": 12345678901234567890.123456789,
                "future_boolean": false,
                "future_null": null
              },
              "future_root": {"nested": [null, {"enabled": true}]}
            }
            """);
        JsonElement payload = default;
        using var client = CreateClient(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/ai/sound-effects")
            {
                payload = ParseElement(await request.Content!.ReadAsStringAsync(cancellationToken));
                return JsonResponse("""{"data":{"task_id":"task-1"}}""");
            }
            if (request.RequestUri.AbsolutePath == "/v1/ai/sound-effects/task-1")
                return JsonResponse(
                    """{"data":{"status":"COMPLETED","task_id":"task-1","generated":["https://audio.test/result.mp3"]}}""");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var request = new SpeechRequest
        {
            Model = "sound-effects",
            Text = "Rain in a forest",
            ProviderOptions = new()
            {
                ["freepik"] = metadata,
                ["other-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("Rain in a forest", payload.GetProperty("text").GetString());
        Assert.Equal(7, payload.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(0.25f, payload.GetProperty("prompt_influence").GetSingle());
        Assert.False(payload.GetProperty("loop").GetBoolean());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number",
                     "future_boolean", "future_null", "future_root"
                 })
        {
            var expected = metadata.GetProperty("sound_effects").TryGetProperty(name, out var nested)
                ? nested
                : metadata.GetProperty(name);
            Assert.True(JsonElement.DeepEquals(expected, payload.GetProperty(name)), name);
        }

        Assert.False(payload.TryGetProperty("sound_effects", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("freepik", out _));
        Assert.False(payload.TryGetProperty("other-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(11, payload.EnumerateObject().Count());
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["freepik"]));
    }

    [Fact]
    public async Task Freepik_speech_uses_default_duration_and_omits_absent_optional_values()
    {
        JsonElement payload = default;
        using var client = CreateClient(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/ai/sound-effects")
            {
                payload = ParseElement(await request.Content!.ReadAsStringAsync(cancellationToken));
                return JsonResponse("""{"data":{"task_id":"task-1"}}""");
            }
            if (request.RequestUri.AbsolutePath == "/v1/ai/sound-effects/task-1")
                return JsonResponse(
                    """{"data":{"status":"COMPLETED","task_id":"task-1","generated":["https://audio.test/result.mp3"]}}""");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
        });

        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "sound-effects",
            Text = "Wind"
        });

        Assert.Equal("Wind", payload.GetProperty("text").GetString());
        Assert.Equal(5, payload.GetProperty("duration_seconds").GetInt32());
        Assert.False(payload.TryGetProperty("prompt_influence", out _));
        Assert.False(payload.TryGetProperty("loop", out _));
        Assert.False(payload.TryGetProperty("sound_effects", out _));
        Assert.Equal(2, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Freepik_speech_omits_null_known_options_and_preserves_unknown_nulls()
    {
        JsonElement payload = default;
        using var client = CreateClient(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/ai/sound-effects")
            {
                payload = ParseElement(await request.Content!.ReadAsStringAsync(cancellationToken));
                return JsonResponse("""{"data":{"task_id":"task-1"}}""");
            }
            if (request.RequestUri.AbsolutePath == "/v1/ai/sound-effects/task-1")
                return JsonResponse(
                    """{"data":{"status":"COMPLETED","task_id":"task-1","generated":["https://audio.test/result.mp3"]}}""");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
        });

        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "sound-effects",
            Text = "Wind",
            ProviderOptions = new()
            {
                ["freepik"] = ParseElement(
                    """{"sound_effects":{"duration_seconds":5,"prompt_influence":null,"loop":null,"future_null":null}}""")
            }
        });

        Assert.Equal(5, payload.GetProperty("duration_seconds").GetInt32());
        Assert.False(payload.TryGetProperty("prompt_influence", out _));
        Assert.False(payload.TryGetProperty("loop", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("sound_effects", out _));
        Assert.Equal(3, payload.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("""{"sound_effects":{"duration_seconds":0}}""", "duration_seconds")]
    [InlineData("""{"sound_effects":{"duration_seconds":23}}""", "duration_seconds")]
    [InlineData("""{"sound_effects":{"duration_seconds":5,"prompt_influence":1.1}}""", "prompt_influence")]
    public async Task Freepik_speech_preserves_metadata_validation(string metadata, string invalidField)
    {
        using var client = CreateClient((_, _) =>
            throw new InvalidOperationException("Request should not be sent."));

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreateProvider(client).SpeechRequest(new SpeechRequest
            {
                Model = "sound-effects",
                Text = "Wind",
                ProviderOptions = new() { ["freepik"] = ParseElement(metadata) }
            }));

        Assert.Equal("SoundEffects", error.ParamName);
        Assert.Contains(invalidField, error.Message);
    }

    [Fact]
    public async Task Freepik_speech_preserves_upstream_error_behavior()
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));

        var error = await Assert.ThrowsAsync<Exception>(() =>
            CreateProvider(client).SpeechRequest(new SpeechRequest
            {
                Model = "sound-effects",
                Text = "Wind"
            }));

        Assert.Contains("Freepik sound-effects start error: 400", error.Message);
        Assert.Contains("upstream failure", error.Message);
    }

    private static FreepikProvider CreateProvider(HttpClient client)
        => new(client, new StaticApiKeyResolver());

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new CapturingHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
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

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
