using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Verbatik;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class VerbatikApiTests
{
    [Fact]
    public async Task Speech_uses_current_async_endpoint_and_preserves_raw_options()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal("app.verbatik.com", request.RequestUri!.Host);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);

            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/v1/speech", request.RequestUri.AbsolutePath);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var root = payload.RootElement;
                using var expectedOptions = JsonDocument.Parse(
                    """{"future_option":{"enabled":true,"nested":[1,null,{"label":"keep"}]},"future_array":[false,2,"three"],"future_count":7,"future_null":null,"pitch":1.5}""");
                Assert.Equal("verbatik/speech-lite", root.GetProperty("model").GetString());
                Assert.Equal("Hello", root.GetProperty("blocks")[0].GetProperty("text").GetString());
                Assert.Equal("voice-1", root.GetProperty("blocks")[0].GetProperty("voice").GetString());
                Assert.Equal(1.25, root.GetProperty("settings").GetProperty("speed").GetDouble());
                using var expectedSettings = JsonDocument.Parse("""{"array":[2,null,{"nested":true}]}""");
                Assert.True(JsonElement.DeepEquals(
                    expectedSettings.RootElement,
                    root.GetProperty("settings").GetProperty("future_setting")));
                foreach (var property in expectedOptions.RootElement.EnumerateObject())
                    Assert.True(JsonElement.DeepEquals(property.Value, root.GetProperty(property.Name)), property.Name);
                Assert.False(root.TryGetProperty("providerOptions", out _));
                Assert.False(root.TryGetProperty("speed", out _));
                return JsonResponse("""{"id":"speech-1","status":"queued"}""", HttpStatusCode.Accepted);
            }

            Assert.Equal("/api/v1/speech/speech-1", request.RequestUri.AbsolutePath);
            return JsonResponse(
                """{"id":"speech-1","status":"completed","outputs":[{"url":"https://audio.example/result.wav","media_type":"audio/wav"}]}""");
        }, async request =>
        {
            Assert.Equal("audio.example", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var provider = CreateProvider(client);

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "verbatik/tts/voice-1",
            Text = "Hello",
            Speed = 1.25f,
            ProviderOptions = new()
            {
                ["verbatik"] = JsonDocument.Parse(
                    """{"future_option":{"enabled":true,"nested":[1,null,{"label":"keep"}]},"future_array":[false,2,"three"],"future_count":7,"future_null":null,"pitch":1.5,"speed":1.75,"settings":{"speed":1.8,"future_setting":{"array":[2,null,{"nested":true}]}}}""").RootElement.Clone()
            }
        });

        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Speech_preserves_volume_validation_before_sending_the_request()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var provider = CreateProvider(client);
        using var providerOptions = JsonDocument.Parse("""{"volume":10.1}""");

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "verbatik/tts",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["verbatik"] = providerOptions.RootElement.Clone()
            }
        }));

        Assert.Equal("Volume", error.ParamName);
    }

    [Fact]
    public async Task Voice_models_use_the_documented_preset_catalog()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("app.verbatik.com", request.RequestUri!.Host);
            Assert.Equal("/api/v1/voices/catalog", request.RequestUri.AbsolutePath);
            Assert.Equal("?model=verbatik%2Fspeech-lite", request.RequestUri.Query);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(
                """{"object":"list","model":"verbatik/speech-lite","data":[{"id":"voice-1","label":"Wise Woman","language":"en-US","gender":"female"}]}"""));
        });
        var provider = CreateProvider(client);

        var models = await provider.ListModels(CancellationToken.None);

        Assert.Contains(models, model => model.Name!.Contains("Wise Woman", StringComparison.Ordinal));
        Assert.DoesNotContain(models, model => model.Id.EndsWith("text-to-music/voice-1", StringComparison.Ordinal));
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Music_uses_current_audio_generation_contract_and_downloads_signed_output()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal("app.verbatik.com", request.RequestUri!.Host);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);

            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/v1/generations", request.RequestUri!.AbsolutePath);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var root = payload.RootElement;
                Assert.Equal("audio", root.GetProperty("mode").GetString());
                Assert.Equal("fal-ai/minimax-music/v2", root.GetProperty("model").GetString());
                Assert.Equal("Warm instrumental", root.GetProperty("prompt").GetString());
                using var expectedObject = JsonDocument.Parse("""{"enabled":true,"nested":[1,null,{"label":"keep"}]}""");
                using var expectedArray = JsonDocument.Parse("""[false,2,"three"]""");
                Assert.True(JsonElement.DeepEquals(
                    expectedObject.RootElement,
                    root.GetProperty("future_option")));
                Assert.True(JsonElement.DeepEquals(
                    expectedArray.RootElement,
                    root.GetProperty("future_array")));
                Assert.Equal(7, root.GetProperty("future_count").GetInt32());
                Assert.True(root.GetProperty("future_null").ValueKind == JsonValueKind.Null);
                Assert.Equal(19, root.GetProperty("seed").GetInt32());
                Assert.Equal("5", root.GetProperty("parameters").GetProperty("duration").GetString());
                Assert.Equal("lyrics here", root.GetProperty("parameters").GetProperty("lyrics").GetString());
                return JsonResponse("""{"id":"generation-1","status":"queued"}""", HttpStatusCode.Accepted);
            }

            Assert.Equal("/api/v1/generations/generation-1", request.RequestUri!.AbsolutePath);
            return JsonResponse(
                """{"id":"generation-1","status":"completed","outputs":[{"url":"https://audio.example/music.mp3","media_type":"audio/mpeg"}]}""");
        }, async request =>
        {
            Assert.Equal("audio.example", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([4, 5, 6])
            };
        });
        var provider = CreateProvider(client);

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "verbatik/text-to-music",
            Text = "Warm instrumental",
            Instructions = "lyrics here",
            ProviderOptions = new()
            {
                ["verbatik"] = JsonDocument.Parse(
                    """{"parameters":{"duration":"5"},"future_option":{"enabled":true,"nested":[1,null,{"label":"keep"}]},"future_array":[false,2,"three"],"future_count":7,"future_null":null,"seed":19}""").RootElement.Clone()
            }
        });

        Assert.Equal(Convert.ToBase64String([4, 5, 6]), response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    private static VerbatikProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), new TestHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> apiResponder,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? mediaResponder = null)
        => new(new DelegateHttpMessageHandler(request =>
            request.RequestUri!.Host == "audio.example"
                ? (mediaResponder ?? apiResponder)(request)
                : apiResponder(request)));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
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
