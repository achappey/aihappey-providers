using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.KugelAudio;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class KugelAudioProviderTests
{
    [Fact]
    public async Task Tts_sends_native_fields_and_returns_streamed_audio_and_headers()
    {
        var metadata = ParseElement(
            """
            {
              "cfg_scale": 2.0,
              "normalize": true,
              "sample_rate": 24000,
              "future_option": {"nested": [1, null, true]}
            }
            """);
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/tts/generate", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Headers.Add("X-Sample-Rate", "24000");
            response.Headers.Add("X-Audio-Format", "pcm_s16le");
            response.Headers.Add("X-KugelAudio-AI-Generated", "true");
            return response;
        });

        var request = new SpeechRequest
        {
            Model = "kugelaudio/kugel-3.5",
            Text = "Hello KugelAudio",
            Voice = "979",
            ProviderOptions = new Dictionary<string, JsonElement> { ["kugelaudio"] = metadata }
        };
        var response = await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("Hello KugelAudio", payload.GetProperty("text").GetString());
        Assert.Equal("kugel-3.5", payload.GetProperty("model_id").GetString());
        Assert.Equal(979, payload.GetProperty("voice_id").GetInt32());
        Assert.Equal(2, payload.GetProperty("cfg_scale").GetDouble());
        Assert.True(payload.GetProperty("normalize").GetBoolean());
        Assert.Equal(24000, payload.GetProperty("sample_rate").GetInt32());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_option"), payload.GetProperty("future_option")));
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), response.Audio.Base64);
        Assert.Equal("pcm", response.Audio.Format);
        Assert.Equal("audio/pcm", response.Audio.MimeType);
        Assert.Equal("24000", response.Response.Headers!["X-Sample-Rate"]);
        Assert.Equal("true", response.Response.Headers["X-KugelAudio-AI-Generated"]);
    }

    [Fact]
    public async Task Tts_uses_default_model_and_provider_voice_id_without_filtering_options()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("pcm"))
            };
        });

        var options = ParseElement("""{"voice_id":1071,"output_format":"pcm","custom_flag":false}""");
        await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Text = "Default model",
            ProviderOptions = new Dictionary<string, JsonElement> { ["kugelaudio"] = options }
        });

        Assert.Equal("kugel-3.5", payload.GetProperty("model_id").GetString());
        Assert.Equal(1071, payload.GetProperty("voice_id").GetInt32());
        Assert.Equal("pcm", payload.GetProperty("output_format").GetString());
        Assert.False(payload.GetProperty("custom_flag").GetBoolean());
    }

    [Fact]
    public async Task Tts_eu_key_uses_direct_eu_endpoint_and_g711_mime_type()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("api.eu.kugelaudio.com", request.RequestUri!.Host);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Content.Headers.ContentType = new("audio/basic");
            response.Headers.Add("X-Audio-Format", "alaw");
            return response;
        });

        var metadata = ParseElement("""{"voice_id":979,"output_format":"alaw_8000"}""");
        var response = await CreateProvider(client, "eu-test-api-key").SpeechRequest(new SpeechRequest
        {
            Text = "EU KugelAudio",
            ProviderOptions = new Dictionary<string, JsonElement> { ["kugelaudio"] = metadata }
        });

        Assert.Equal("alaw", response.Audio.Format);
        Assert.Equal("audio/basic", response.Audio.MimeType);
    }

    private static KugelAudioProvider CreateProvider(HttpClient client, string apiKey = "test-api-key")
        => new(new StaticApiKeyResolver(apiKey), new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class StaticApiKeyResolver(string apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => apiKey;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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
