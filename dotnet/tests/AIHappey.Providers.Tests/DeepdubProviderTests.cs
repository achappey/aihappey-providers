using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Deepdub;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class DeepdubProviderTests
{
    [Fact]
    public async Task Speech_maps_fields_and_preserves_native_options_and_generation_header()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("https://restapi.eu.deepdub.ai/api/v1/tts", message.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", message.Headers.GetValues("x-api-key").Single());
            payload = Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var response = AudioResponse();
            response.Headers.Add("x-generation-id", "generation-1");
            return response;
        });
        var metadata = Parse("""{"region":"eu","model":"wrong","targetText":"wrong","locale":"de-DE","voicePromptId":"old","format":"opus","accentControl":{"accentLocale":"fr-FR","accentRatio":0.3},"future":[null,{"x":true}]}""");
        var result = await Provider(client).SpeechRequest(new SpeechRequest
        {
            Model = "deepdub/dd-etts-4.0", Text = "Hello", Language = "en-US", Voice = "voice-1",
            ProviderOptions = new() { ["deepdub"] = metadata }
        });
        Assert.Equal("dd-etts-4.0", payload.GetProperty("model").GetString());
        Assert.Equal("Hello", payload.GetProperty("targetText").GetString());
        Assert.Equal("en-US", payload.GetProperty("locale").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voicePromptId").GetString());
        Assert.Equal("opus", payload.GetProperty("format").GetString());
        Assert.False(payload.TryGetProperty("region", out _));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("accentControl"), payload.GetProperty("accentControl")));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future"), payload.GetProperty("future")));
        Assert.Equal("generation-1", result.Response.Headers["x-generation-id"]);
        Assert.Equal("audio/ogg", result.Audio.MimeType);
        Assert.Equal("opus", result.Audio.Format);
        Assert.Equal("audio", Encoding.UTF8.GetString(Convert.FromBase64String(result.Audio.Base64)));
        Assert.False(client.DefaultRequestHeaders.Contains("x-api-key"));
    }

    [Fact]
    public async Task Streaming_forwards_audio_incrementally_and_preserves_headers()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedAudioStream(Encoding.UTF8.GetBytes("audio")))
            };
            response.Headers.Add("x-generation-id", "generation-2");
            return response;
        });
        var request = new StreamingSpeechRequest
        {
            Model = "dd-etts-3.0", Text = [new() { Text = "Hello " }, new() { Text = "world" }],
            OutputFormat = new() { Type = "mulaw", Rate = 8000 },
            ProviderOptions = new() { ["deepdub"] = Parse("""{"locale":"en-US","voicePromptId":"voice"}""") },
            IncludeRawChunks = true
        };
        var parts = new List<StreamingSpeechPart>();
        await foreach (var part in Provider(client).SpeechStreamingAsync(request)) parts.Add(part);
        Assert.Equal("Hello world", payload.GetProperty("targetText").GetString());
        Assert.Equal(8000, payload.GetProperty("sampleRate").GetInt32());
        Assert.Equal("generation-2", Assert.Single(parts.OfType<SpeechResponseMetadataPart>()).Headers["x-generation-id"]);
        var audio = parts.OfType<SpeechAudioPart>().ToArray();
        Assert.Equal(3, audio.Length);
        Assert.All(audio, part =>
        {
            Assert.Equal("audio/basic", part.AudioFormat.Type);
            Assert.Equal(8000, part.AudioFormat.Rate);
        });
        Assert.Equal("audio", Encoding.UTF8.GetString(audio.SelectMany(part => Convert.FromBase64String(part.Audio)).ToArray()));
        Assert.Equal(3, parts.OfType<SpeechRawPart>().Count());
        Assert.Single(parts.OfType<SpeechFinishPart>());
    }

    [Fact]
    public async Task OpenAI_streaming_accepts_native_options()
    {
        using var client = CreateClient(message =>
        {
            var payload = Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("en-US", payload.GetProperty("locale").GetString());
            Assert.Equal("voice", payload.GetProperty("voicePromptId").GetString());
            Assert.Equal(0.7, payload.GetProperty("temperature").GetDouble());
            return AudioResponse();
        });
        var request = new AudioSpeechRequest
        {
            Model = "dd-etts-4.0", Input = "Hello",
            AdditionalProperties = new() { ["deepdub"] = Parse("""{"locale":"en-US","voicePromptId":"voice","temperature":0.7}""") }
        };
        var parts = new List<IAudioSpeechStreamEvent>();
        await foreach (var part in Provider(client).OpenAISpeechStreamingAsync(request)) parts.Add(part);
        Assert.Single(parts.OfType<AudioSpeechStreamDelta>());
        Assert.Single(parts.OfType<AudioSpeechStreamDone>());
    }

    [Fact]
    public async Task Streaming_rejects_part_voice_changes_before_sending()
    {
        var calls = 0;
        using var client = CreateClient(_ => { calls++; return AudioResponse(); });
        var request = new StreamingSpeechRequest
        {
            Model = "dd-etts-4.0", Language = "en-US",
            Text = [new() { Text = "Hello", Voice = "one" }, new() { Text = " world", Voice = "two" }]
        };
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in Provider(client).SpeechStreamingAsync(request)) { }
        });
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Rejects_unsupported_format_and_invalid_options_and_reports_upstream_errors()
    {
        var calls = 0;
        using var client = CreateClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid request") };
        });
        var provider = Provider(client);
        var request = new SpeechRequest { Model = "dd-etts-4.0", Text = "hello", Language = "en-US", Voice = "voice", OutputFormat = "wav" };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(request));
        request.OutputFormat = null;
        request.ProviderOptions = new() { ["deepdub"] = Parse("[]") };
        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(request));
        request.ProviderOptions = null;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SpeechRequest(request));
        Assert.Contains("invalid request", error.Message);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal(1, calls);
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static HttpResponseMessage AudioResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio")) };
    private static DeepdubProvider Provider(HttpClient client) => new(new KeyResolver(), new ClientFactory(client));
    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class ChunkedAudioStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
