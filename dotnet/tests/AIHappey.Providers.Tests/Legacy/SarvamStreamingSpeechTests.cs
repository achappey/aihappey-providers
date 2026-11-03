using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Sarvam;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class SarvamStreamingSpeechTests
{
    [Fact]
    public async Task Sarvam_streams_binary_audio_and_preserves_provider_options_without_mutating_headers()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.sarvam.ai/text-to-speech/stream", request.RequestUri!.ToString());
            Assert.Equal("test-key", request.Headers.GetValues("api-subscription-key").Single());
            Assert.Equal("audio/mpeg", request.Headers.Accept.Single().MediaType);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedReadStream([1, 2, 3, 4], 2))
            };
        });
        var metadata = ParseElement("""
            {
              "model":"bulbul:v4-flash",
              "language_code":"en-IN",
              "speaker":"shubh",
              "output_audio_codec":"mp3",
              "speech_sample_rate":16000,
              "dict_id":"dictionary-1",
              "future":{"enabled":true}
            }
            """);
        var request = new StreamingSpeechRequest
        {
            Model = "sarvam/bulbul:v3",
            Text = [new() { Text = "Hello " }, new() { Text = "world!" }],
            Speed = 1.1f,
            OutputFormat = new() { Type = "mp3", Rate = 24000 },
            ProviderOptions = new() { ["sarvam"] = metadata },
            IncludeRawChunks = true
        };
        var originalOptions = request.ProviderOptions["sarvam"].Clone();

        var parts = await CollectAsync(CreateProvider(client).SpeechStreamingAsync(request));

        Assert.Equal("Hello world!", payload.GetProperty("text").GetString());
        Assert.Equal("bulbul:v4-flash", payload.GetProperty("model").GetString());
        Assert.Equal("en-IN", payload.GetProperty("language_code").GetString());
        Assert.Equal("shubh", payload.GetProperty("speaker").GetString());
        Assert.Equal("mp3", payload.GetProperty("output_audio_codec").GetString());
        Assert.Equal(1.1, payload.GetProperty("pace").GetDouble(), 3);
        Assert.Equal(24000, payload.GetProperty("speech_sample_rate").GetInt32());
        Assert.Equal("dictionary-1", payload.GetProperty("dict_id").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future"), payload.GetProperty("future")));
        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["sarvam"]));
        Assert.Equal(2, parts.OfType<SpeechAudioPart>().Count());
        Assert.All(parts.OfType<SpeechAudioPart>(), part =>
        {
            Assert.Equal("audio/mpeg", part.AudioFormat.Type);
            Assert.Equal(24000, part.AudioFormat.Rate);
        });
        Assert.Equal(2, parts.OfType<SpeechRawPart>().Count());
        Assert.Single(parts.OfType<SpeechStreamStartPart>());
        Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.False(client.DefaultRequestHeaders.Contains("api-subscription-key"));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Sarvam_rejects_text_parts_with_different_native_configuration_before_sending()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new StreamingSpeechRequest
        {
            Model = "sarvam/bulbul:v3",
            Text =
            [
                new() { Text = "Hello", ProviderOptions = new() { ["sarvam"] = ParseElement("""{"speaker":"anushka"}""") } },
                new() { Text = "there", ProviderOptions = new() { ["sarvam"] = ParseElement("""{"speaker":"manisha"}""") } }
            ]
        };

        var error = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await CollectAsync(CreateProvider(client).SpeechStreamingAsync(request)));

        Assert.Contains("one effective voice", error.Message);
    }

    private static async Task<List<StreamingSpeechPart>> CollectAsync(
        IAsyncEnumerable<StreamingSpeechPart> stream)
    {
        var parts = new List<StreamingSpeechPart>();
        await foreach (var part in stream)
            parts.Add(part);
        return parts;
    }

    private static SarvamProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

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
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkSize)], cancellationToken);
        }
    }
}
