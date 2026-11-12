using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Nabrah;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class NabrahProviderTests
{
    [Fact]
    public async Task Speech_sends_native_metadata_and_request_scoped_key()
    {
        using var client = Client(request =>
        {
            Assert.Equal("/api/ext/tts/generations", request.RequestUri!.AbsolutePath);
            Assert.Equal("secret", request.Headers.GetValues("X-API-Key").Single());
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            Assert.Equal("nabrah_tts_v1", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("hello", body.RootElement.GetProperty("input").GetString());
            Assert.Equal("voice-id", body.RootElement.GetProperty("voice").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.True(body.RootElement.GetProperty("use_cache").GetBoolean());
            Assert.Equal(42, body.RootElement.GetProperty("future").GetProperty("value").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        var metadata = JsonSerializer.Deserialize<JsonElement>("""{"use_cache":true,"future":{"value":42}}""");
        var response = await Provider(client).SpeechRequest(new SpeechRequest
        {
            Model = "nabrah/nabrah_tts_v1", Text = "hello", Voice = "voice-id", OutputFormat = "wav",
            ProviderOptions = new() { ["nabrah"] = metadata }
        });
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
        Assert.False(client.DefaultRequestHeaders.Contains("X-API-Key"));
    }

    [Fact]
    public async Task Openai_streaming_sends_options_and_streams_audio()
    {
        using var client = Client(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(body.RootElement.GetProperty("use_cache").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7, 8]) };
        });
        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in Provider(client).OpenAISpeechStreamingAsync(new AudioSpeechRequest
        {
            Model = "nabrah_tts_v1", Input = "hello", Voice = "voice",
            AdditionalProperties = new() { ["use_cache"] = JsonSerializer.SerializeToElement(false) }
        })) events.Add(item);
        Assert.Equal(Convert.ToBase64String([7, 8]), Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.IsType<AudioSpeechStreamDone>(events[1]);
    }

    [Fact]
    public async Task Transcription_uploads_repeated_options_and_returns_native_result()
    {
        using var client = Client(request =>
        {
            Assert.Equal("/api/ext/stt/async-transcribe", request.RequestUri!.AbsolutePath);
            Assert.Equal("secret", request.Headers.GetValues("X-API-Key").Single());
            var form = request.Content!.ReadAsStringAsync().Result;
            Assert.Contains("audio_file", form);
            Assert.Contains("priority_words", form);
            Assert.Contains("Nabrah", form);
            Assert.Contains("Olaya", form);
            Assert.Contains("end_of_utterance_silence_ms", form);
            return Json("""{"id":"job-1","status":"completed","result":{"text_result":"hello","audio_duration":1.5,"timestamps":{"word":[]}}}""");
        });
        var response = await Provider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "nabrah/transcription", Audio = Convert.ToBase64String([1, 2]), MediaType = "audio/wav",
            ProviderOptions = new() { ["nabrah"] = JsonSerializer.Deserialize<JsonElement>("""{"priority_words":["Nabrah","Olaya"],"end_of_utterance_silence_ms":500}""") }
        });
        Assert.Equal("hello", response.Text);
        Assert.Equal(1.5, response.DurationInSeconds);
        Assert.Equal("job-1", response.ProviderMetadata!["nabrah"].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Transcription_reports_failed_jobs()
    {
        using var client = Client(_ => Json("""{"id":"bad","status":"failed","error":"bad audio"}"""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "transcription", Audio = Convert.ToBase64String([1]), MediaType = "audio/wav"
        }));
        Assert.Contains("bad audio", error.Message);
    }

    [Fact]
    public async Task Transcription_polls_queued_jobs_with_authentication()
    {
        var calls = 0;
        using var client = Client(request =>
        {
            Assert.Equal("secret", request.Headers.GetValues("X-API-Key").Single());
            calls++;
            if (calls == 1)
                return Json("""{"id":"job-1","status":"queued"}""");
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/ext/stt/jobs/job-1", request.RequestUri!.AbsolutePath);
            return Json("""{"id":"job-1","status":"completed","result":{"text_result":"done","audio_duration":2}}""");
        });
        var response = await Provider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "transcription", Audio = Convert.ToBase64String([1]), MediaType = "audio/wav"
        });
        Assert.Equal(2, calls);
        Assert.Equal("done", response.Text);
    }

    private static NabrahProvider Provider(HttpClient client) => new(new Key(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(new Handler(handler));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Key : IApiKeyResolver { public string? Resolve(string provider) => "secret"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
