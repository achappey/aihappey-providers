using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cartesia;
using AIHappey.Core.Providers.ElevenLabs;
using AIHappey.Core.Providers.MurfAI;
using AIHappey.Core.Providers.Neuphonic;
using AIHappey.Core.Providers.ResembleAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderStreamingSpeechTests
{
    [Fact]
    public async Task Neuphonic_streams_SSE_audio_and_preserves_provider_options_and_event_metadata()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/sse/speak/en", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
            Assert.Contains("text/event-stream", request.Headers.Accept.Select(value => value.MediaType));
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"status_code\":200,\"data\":{\"audio\":\"AQI=\",\"sampling_rate\":24000,\"alignment\":{\"words\":[\"Hello\"]}}}\n\n" +
                    "data: {\"status_code\":200,\"data\":{\"audio\":\"AwQ=\",\"sampling_rate\":24000}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });
        var metadata = ParseElement("""{"encoding":"pcm_linear","sampling_rate":24000,"future":{"x":1}}""");
        var provider = CreateNeuphonicProvider(client);
        var request = new StreamingSpeechRequest
        {
            Model = "neuphonic/en/voice-1",
            Text = [new() { Text = "Hello" }, new() { Text = ", world!" }],
            Speed = 1.1f,
            OutputFormat = new() { Type = "pcm", Rate = 24000 },
            ProviderOptions = new() { ["neuphonic"] = metadata },
            IncludeRawChunks = true
        };
        var originalOptions = request.ProviderOptions["neuphonic"].Clone();
        var parts = await CollectAsync(provider.SpeechStreamingAsync(request));

        Assert.Equal("Hello, world!", payload.GetProperty("text").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voice_id").GetString());
        Assert.Equal("en", payload.GetProperty("lang_code").GetString());
        Assert.Equal(1.1f, payload.GetProperty("speed").GetSingle());
        Assert.Equal(24000, payload.GetProperty("sampling_rate").GetInt32());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future"), payload.GetProperty("future")));
        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["neuphonic"]));
        Assert.Equal(2, parts.OfType<SpeechAudioPart>().Count());
        Assert.All(parts.OfType<SpeechAudioPart>(), part =>
        {
            Assert.Equal("audio/pcm", part.AudioFormat.Type);
            Assert.Equal(24000, part.AudioFormat.Rate);
        });
        Assert.Equal(2, parts.OfType<SpeechRawPart>().Count());
        var finish = Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.Equal(2, finish.ProviderMetadata["neuphonic"].GetProperty("events").GetArrayLength());
        Assert.Contains(parts.OfType<SpeechAudioPart>().Select(part => part.ProviderMetadata["neuphonic"]),
            item => item.GetProperty("data").TryGetProperty("alignment", out _));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Cartesia_streams_SSE_audio_with_native_format_and_provider_options()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("https://api.cartesia.ai/tts/sse", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("2026-08-14", request.Headers.GetValues("Cartesia-Version").Single());
            Assert.Contains("text/event-stream", request.Headers.Accept.Select(value => value.MediaType));
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"chunk\",\"data\":\"AQI=\",\"context_id\":\"context-1\"}\n\n" +
                    "data: {\"type\":\"timestamps\",\"timestamps\":{\"words\":[\"Hello\"]}}\n\n" +
                    "data: {\"type\":\"done\",\"done\":true}\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });
        var metadata = ParseElement("""
            {
              "apiVersion":"2026-08-14",
              "sampleRate":24000,
              "speed":1.1,
              "volume":0.8,
              "emotion":"happy",
              "future":{"enabled":true}
            }
            """);
        var request = new StreamingSpeechRequest
        {
            Model = "tts/sonic-3.6/voice-1",
            Text = [new() { Text = "Hello " }, new() { Text = "world!" }],
            ProviderOptions = new() { ["cartesia"] = metadata },
            OutputFormat = new() { Type = "raw", Rate = 24000 },
            IncludeRawChunks = true
        };
        var originalOptions = request.ProviderOptions["cartesia"].Clone();

        var parts = await CollectAsync(CreateCartesiaProvider(client).SpeechStreamingAsync(request));

        Assert.Equal("sonic-3.6", payload.GetProperty("model_id").GetString());
        Assert.Equal("Hello world!", payload.GetProperty("transcript").GetString());
        Assert.Equal("id", payload.GetProperty("voice").GetProperty("mode").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voice").GetProperty("id").GetString());
        Assert.Equal("raw", payload.GetProperty("output_format").GetProperty("container").GetString());
        Assert.Equal(24000, payload.GetProperty("output_format").GetProperty("sample_rate").GetInt32());
        Assert.Equal(1.1, payload.GetProperty("generation_config").GetProperty("speed").GetDouble(), 3);
        Assert.Equal(0.8, payload.GetProperty("generation_config").GetProperty("volume").GetDouble(), 3);
        Assert.Equal("happy", payload.GetProperty("generation_config").GetProperty("emotion").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future"), payload.GetProperty("future")));
        Assert.False(payload.TryGetProperty("apiVersion", out _));
        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["cartesia"]));

        var responseMetadata = Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        Assert.Equal("cartesia/tts/sonic-3.6/voice-1", responseMetadata.ModelId);
        var audio = Assert.Single(parts.OfType<SpeechAudioPart>());
        Assert.Equal("AQI=", audio.Audio);
        Assert.Equal("audio/pcm", audio.AudioFormat.Type);
        Assert.Equal(24000, audio.AudioFormat.Rate);
        Assert.Equal(3, parts.OfType<SpeechRawPart>().Count());
        var finish = Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.Single(finish.ProviderMetadata["cartesia"].GetProperty("events").EnumerateArray());
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Cartesia_rejects_nonstreamable_formats_before_sending()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new StreamingSpeechRequest
        {
            Model = "tts/sonic-3.6/voice-1",
            Text = [new() { Text = "Hello" }],
            OutputFormat = new() { Type = "wav" }
        };

        var error = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await CollectAsync(CreateCartesiaProvider(client).SpeechStreamingAsync(request)));

        Assert.Contains("only raw PCM", error.Message);
    }

    [Fact]
    public async Task ResembleAI_streams_native_WAV_chunks_and_preserves_provider_options()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://f.cluster.resemble.ai/stream", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Contains("audio/wav", request.Headers.Accept.Select(value => value.MediaType));
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var content = new ByteArrayContent([1, 2, 3, 4]);
            content.Headers.ContentType = new("audio/wav");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var metadata = ParseElement("""
            {
              "project_uuid":"project-1",
              "precision":"PCM_16",
              "sample_rate":24000,
              "future":{"enabled":true}
            }
            """);
        var request = new StreamingSpeechRequest
        {
            Model = "resembleai/chatterbox/voice-1",
            Text = [new() { Text = "Hello " }, new() { Text = "world!" }],
            Voice = "ignored-voice",
            OutputFormat = new() { Type = "wav" },
            ProviderOptions = new() { ["resembleai"] = metadata },
            IncludeRawChunks = true
        };
        var originalMetadata = request.ProviderOptions["resembleai"].Clone();

        var parts = await CollectAsync(CreateResembleAIProvider(client).SpeechStreamingAsync(request));

        Assert.Equal("Hello world!", payload.GetProperty("data").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voice_uuid").GetString());
        Assert.Equal("project-1", payload.GetProperty("project_uuid").GetString());
        Assert.Equal("PCM_16", payload.GetProperty("precision").GetString());
        Assert.Equal(24000, payload.GetProperty("sample_rate").GetInt32());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future"), payload.GetProperty("future")));
        Assert.False(payload.TryGetProperty("output_format", out _));
        Assert.True(JsonElement.DeepEquals(originalMetadata, request.ProviderOptions["resembleai"]));
        var audio = Assert.Single(parts.OfType<SpeechAudioPart>());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), audio.Audio);
        Assert.Equal("audio/wav", audio.AudioFormat.Type);
        Assert.Equal(24000, audio.AudioFormat.Rate);
        Assert.Single(parts.OfType<SpeechRawPart>());
        Assert.Single(parts.OfType<SpeechStreamStartPart>());
        Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task MurfAI_streams_Falcon_audio_and_maps_provider_options_and_output_format()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://global.api.murf.ai/v1/speech/stream", request.RequestUri!.ToString());
            Assert.Equal("test-key", request.Headers.GetValues("api-key").Single());
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var content = new ByteArrayContent([5, 6, 7]);
            content.Headers.ContentType = new("audio/wav");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var metadata = ParseElement("""
            {
              "locale":"en-US",
              "sampleRate":24000,
              "pronunciationDictionary":{"API":{"type":"SAY_AS","pronunciation":"A P I"}},
              "future":{"enabled":true}
            }
            """);
        var request = new StreamingSpeechRequest
        {
            Model = "murfai/falcon-2.1/voice-1",
            Text = [new() { Text = "Hello " }, new() { Text = "world!" }],
            Speed = 1.2f,
            OutputFormat = new() { Type = "wav" },
            ProviderOptions = new() { ["murfai"] = metadata },
            IncludeRawChunks = true
        };
        var originalMetadata = request.ProviderOptions["murfai"].Clone();

        var parts = await CollectAsync(CreateMurfAIProvider(client).SpeechStreamingAsync(request));

        Assert.Equal("Hello world!", payload.GetProperty("text").GetString());
        Assert.Equal("falcon-2.1", payload.GetProperty("model").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voiceId").GetString());
        Assert.Equal("WAV", payload.GetProperty("format").GetString());
        Assert.Equal("en-US", payload.GetProperty("locale").GetString());
        Assert.Equal(10, payload.GetProperty("rate").GetInt32());
        Assert.Equal(24000, payload.GetProperty("sampleRate").GetInt32());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("pronunciationDictionary"),
            payload.GetProperty("pronunciationDictionary")));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future"), payload.GetProperty("future")));
        Assert.True(JsonElement.DeepEquals(originalMetadata, request.ProviderOptions["murfai"]));
        var audio = Assert.Single(parts.OfType<SpeechAudioPart>());
        Assert.Equal(Convert.ToBase64String(new byte[] { 5, 6, 7 }), audio.Audio);
        Assert.Equal("audio/wav", audio.AudioFormat.Type);
        Assert.Equal(24000, audio.AudioFormat.Rate);
        Assert.Single(parts.OfType<SpeechRawPart>());
        Assert.Single(parts.OfType<SpeechStreamStartPart>());
        Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.False(client.DefaultRequestHeaders.Contains("api-key"));
    }

    [Fact]
    public async Task ElevenLabs_streams_HTTP_audio_chunks_and_preserves_native_speech_options()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://api.elevenlabs.io/v1/text-to-speech/voice-1/stream?output_format=mp3_44100_128&optimize_streaming_latency=2",
                request.RequestUri!.ToString());
            Assert.Equal("test-key", request.Headers.GetValues("xi-api-key").Single());
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedReadStream([1, 2, 3, 4], 2))
            };
        });
        var metadata = ParseElement("""
            {
              "voice_settings":{"stability":0.42},
              "language_code":"en",
              "optimize_streaming_latency":2
            }
            """);
        var request = new StreamingSpeechRequest
        {
            Model = "eleven_multilingual_v2",
            Text = [new() { Text = "Hello " }, new() { Text = "world!" }],
            Voice = "voice-1",
            OutputFormat = new() { Type = "mp3_44100_128", Rate = 44100 },
            ProviderOptions = new() { ["elevenlabs"] = metadata },
            IncludeRawChunks = true
        };
        var originalOptions = request.ProviderOptions["elevenlabs"].Clone();

        var parts = await CollectAsync(CreateElevenLabsProvider(client).SpeechStreamingAsync(request));

        Assert.Equal("Hello world!", payload.GetProperty("text").GetString());
        Assert.Equal("eleven_multilingual_v2", payload.GetProperty("model_id").GetString());
        Assert.Equal(0.42, payload.GetProperty("voice_settings").GetProperty("stability").GetDouble(), 2);
        Assert.Equal("en", payload.GetProperty("language_code").GetString());
        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["elevenlabs"]));
        Assert.Equal(2, parts.OfType<SpeechAudioPart>().Count());
        Assert.All(parts.OfType<SpeechAudioPart>(), part =>
        {
            Assert.Equal("audio/mpeg", part.AudioFormat.Type);
            Assert.Equal(44100, part.AudioFormat.Rate);
        });
        Assert.Equal(2, parts.OfType<SpeechRawPart>().Count());
        Assert.Single(parts.OfType<SpeechStreamStartPart>());
        Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.False(client.DefaultRequestHeaders.Contains("xi-api-key"));
    }

    private static async Task<List<StreamingSpeechPart>> CollectAsync(
        IAsyncEnumerable<StreamingSpeechPart> stream)
    {
        var parts = new List<StreamingSpeechPart>();
        await foreach (var part in stream)
            parts.Add(part);
        return parts;
    }

    private static CartesiaProvider CreateCartesiaProvider(HttpClient client)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static NeuphonicProvider CreateNeuphonicProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static ResembleAIProvider CreateResembleAIProvider(HttpClient client)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static MurfAIProvider CreateMurfAIProvider(HttpClient client)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static ElevenLabsProvider CreateElevenLabsProvider(HttpClient client)
        => new(new StaticApiKeyResolver(),
            new StaticHttpClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
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

    private sealed class ChunkedReadStream(byte[] content, int maximumChunkSize) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkSize)], cancellationToken);
        }
    }
}
