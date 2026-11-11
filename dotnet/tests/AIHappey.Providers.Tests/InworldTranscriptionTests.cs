using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Inworld;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class InworldTranscriptionTests
{
    [Fact]
    public async Task Raw_audio_uses_documented_multipart_form_and_preserves_transcription_options()
    {
        var audio = new byte[] { 0, 1, 2, 255 };
        using var client = CreateClient(request =>
        {
            Assert.Equal("https://api.inworld.ai/stt/v1/transcribe", request.RequestUri!.ToString());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);

            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToList();
            Assert.Equal(2, parts.Count);

            Assert.Equal("transcribeConfig", parts[0].Headers.ContentDisposition?.Name?.Trim('"'));
            Assert.Equal("application/json", parts[0].Headers.ContentType?.MediaType);
            using var configDocument = JsonDocument.Parse(parts[0].ReadAsStringAsync().GetAwaiter().GetResult());
            var config = configDocument.RootElement;
            Assert.Equal("inworld/inworld-stt-1", config.GetProperty("modelId").GetString());
            Assert.Equal("LINEAR16", config.GetProperty("audioEncoding").GetString());
            Assert.Equal("en-US", config.GetProperty("language").GetString());
            Assert.True(config.GetProperty("futureOption").GetProperty("enabled").GetBoolean());

            Assert.Equal("file", parts[1].Headers.ContentDisposition?.Name?.Trim('"'));
            Assert.Equal("audio", parts[1].Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.Equal("audio/wav", parts[1].Headers.ContentType?.MediaType);
            Assert.Equal(audio, parts[1].ReadAsByteArrayAsync().GetAwaiter().GetResult());

            return JsonResponse("""{"transcription":{"transcript":"recognized"}}""");
        });

        var response = await CreateProvider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "inworld/inworld-stt-1",
            Audio = audio,
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["inworld"] = ParseElement("""{"language":"en-US","futureOption":{"enabled":true}}""")
            }
        });

        Assert.Equal("recognized", response.Text);
    }

    [Fact]
    public async Task Base64_audio_keeps_existing_json_request_contract()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = payload.RootElement;
            Assert.Equal("inworld/inworld-stt-1", root.GetProperty("transcribeConfig").GetProperty("modelId").GetString());
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
                root.GetProperty("audioData").GetProperty("content").GetString());
            return JsonResponse("""{"transcription":{"transcript":"recognized"}}""");
        });

        var response = await CreateProvider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "inworld/inworld-stt-1",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav"
        });

        Assert.Equal("recognized", response.Text);
    }

    [Fact]
    public async Task Raw_audio_keeps_extended_audio_data_options_on_json_fallback()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = payload.RootElement;
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
                root.GetProperty("audioData").GetProperty("content").GetString());
            Assert.True(root.GetProperty("audioData").GetProperty("futureField").GetBoolean());
            return JsonResponse("""{"transcription":{"transcript":"recognized"}}""");
        });

        var response = await CreateProvider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "inworld/inworld-stt-1",
            Audio = Encoding.UTF8.GetBytes("audio"),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["inworld"] = ParseElement("""{"audioData":{"futureField":true}}""")
            }
        });

        Assert.Equal("recognized", response.Text);
    }

    private static InworldProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client), new NullEndUserIdResolver());

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class NullEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
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
