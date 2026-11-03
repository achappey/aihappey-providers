using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.FishAudio;
using AIHappey.Core.Providers.MurfAI;
using AIHappey.Core.Providers.Tavily;
using AIHappey.Core.Providers.ZeroEntropy;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class NicheProviderMaintenanceTests
{
    [Fact]
    public async Task Fishaudio_asr_alias_uses_the_current_transcription_model_header()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/asr", request.RequestUri!.AbsolutePath);
            Assert.Equal("transcribe-1-pro", request.Headers.GetValues("model").Single());
            Assert.IsType<MultipartFormDataContent>(request.Content);
            return Task.FromResult(JsonResponse("""{"text":"recognized words"}"""));
        });
        var provider = new FishAudioProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "fishaudio/asr",
            Audio = Convert.ToBase64String([1, 2, 3]),
            MediaType = "audio/wav"
        });

        Assert.Equal("recognized words", response.Text);
    }

    [Fact]
    public async Task Murf_falcon_legacy_model_uses_the_documented_falcon_21_stream_endpoint()
    {
        JsonElement payload = default;
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/v1/speech/stream", request.RequestUri!.AbsolutePath);
            Assert.Equal("global.api.murf.ai", request.RequestUri.Host);
            payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var provider = new MurfAIProvider(
            new TestKeyResolver(),
            new AIHappey.Core.AI.AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "murfai/falcon-2/voice-1",
            Text = "Hello",
            Language = "en-US",
            Speed = 1.2f
        });

        Assert.Equal("falcon-2.1", payload.GetProperty("model").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voiceId").GetString());
        Assert.Equal("en-US", payload.GetProperty("locale").GetString());
        Assert.Equal(10, payload.GetProperty("rate").GetInt32());
        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
    }

    [Fact]
    public async Task Zeroentropy_embedding_authentication_is_attached_to_the_request_not_the_shared_client()
    {
        using var client = CreateClient(_ =>
        {
            Assert.Equal("Bearer test-api-key", _.Headers.Authorization?.ToString());
            return Task.FromResult(JsonResponse("""{"results":[{"embedding":[0.1,0.2]}],"usage":{"total_tokens":1}}"""));
        });
        var provider = new ZeroEntropyProvider(new TestKeyResolver(), new TestHttpClientFactory(client));
        using var input = JsonDocument.Parse("""["hello"]""");

        await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "test-model",
            Input = input.RootElement.Clone()
        });

        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Tavily_research_authentication_is_attached_to_each_request_not_the_shared_client()
    {
        var requestCount = 0;
        using var client = CreateClient(_ =>
        {
            Assert.Equal("Bearer test-api-key", _.Headers.Authorization?.ToString());
            return Task.FromResult(Interlocked.Increment(ref requestCount) == 1
                ? JsonResponse("""{"request_id":"research-1"}""")
                : JsonResponse("""{"status":"completed","content":"answer","sources":[]}"""));
        });
        var provider = new TavilyProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            Model = "tavily/auto",
            ProviderId = "tavily",
            Input = new AIInput { Text = "question" }
        });

        Assert.Equal(2, requestCount);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
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
