using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.WavenAI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests;

public sealed class WavenAIProviderTests
{
    [Fact]
    public async Task Speech_preserves_native_options_and_authenticates_the_audio_download()
    {
        var calls = 0;
        using var client = Client(async request =>
        {
            AssertAuth(request);
            if (++calls == 1)
            {
                Assert.Equal("/api/v1/generate", request.RequestUri!.AbsolutePath);
                Assert.Equal("false", Assert.Single(request.Headers.GetValues("X-Waven-Retain-Audio")));
                var fields = await Fields(request);
                Assert.Equal("kokoro", fields["model"]);
                Assert.Equal("af_heart", fields["speaker"]);
                Assert.Equal("1.25", fields["speed"]);
                Assert.Equal("es", fields["language"]);
                Assert.Equal("Hello", fields["text"]);
                Assert.Equal("""{"nested":[1,null,true]}""", fields["future_option"]);
                Assert.Equal("null", fields["future_null"]);
                Assert.False(fields.ContainsKey("headers"));
                return Json("""{"file":"/api/v1/audio/user/clip.wav","sample_rate":24000}""");
            }
            Assert.Equal("/api/v1/audio/user/clip.wav", request.RequestUri!.AbsolutePath);
            return Audio();
        });
        var response = await Provider(client).SpeechRequest(new()
        {
            Model = "wavenai/kokoro/af_heart", Text = "Hello", Speed = 1.25, Language = "es",
            ProviderOptions = Options("""{"speed":2,"future_option":{"nested":[1,null,true]},"future_null":null,"headers":{"X-Waven-Retain-Audio":false}}""")
        });
        Assert.Equal(2, calls);
        Assert.Equal("AQID", response.Audio.Base64);
        Assert.Equal(24000, response.ProviderMetadata!["wavenai"].GetProperty("sample_rate").GetInt32());
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Cloning_sends_reference_audio_as_a_file_and_preserves_consent_fields()
    {
        using var client = Client(async request =>
        {
            if (request.Method == HttpMethod.Get) return Audio();
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var reference = Assert.Single(form, f => Name(f) == "reference_audio");
            Assert.Equal(new byte[] { 1, 2, 3 }, await reference.ReadAsByteArrayAsync());
            Assert.Equal("audio/wav", reference.Headers.ContentType!.MediaType);
            var fields = await Fields(request);
            Assert.Equal("true", fields["consent_attested"]);
            Assert.Equal("own-voice-v1", fields["consent_version"]);
            Assert.Equal("0.3", fields["cfg_weight"]);
            Assert.False(fields.ContainsKey("speaker"));
            return Json("""{"file":"/api/v1/audio/u/clone.wav"}""");
        });
        await Provider(client).SpeechRequest(new()
        {
            Model = "chatterbox", Text = "Hello",
            ProviderOptions = Options("""{"reference_audio":"data:audio/wav;base64,AQID","consent_attested":true,"consent_version":"own-voice-v1","cfg_weight":0.3}""")
        });
    }

    [Fact]
    public async Task Chatterbox_voice_maps_to_gallery_not_stock_speaker()
    {
        using var client = Client(async request =>
        {
            if (request.Method == HttpMethod.Get) return Audio();
            var fields = await Fields(request);
            Assert.Equal("owned-voice", fields["gallery_voice_id"]);
            Assert.False(fields.ContainsKey("speaker"));
            return Json("""{"file":"/api/v1/audio/u/clone.wav"}""");
        });
        await Provider(client).SpeechRequest(new() { Model = "chatterbox", Text = "Hello", Voice = "owned-voice" });
    }

    [Fact]
    public async Task Long_speech_polls_and_surfaces_partial_results()
    {
        var polls = 0;
        using var client = Client(async request =>
        {
            AssertAuth(request);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/v1/generate-long", request.RequestUri!.AbsolutePath);
                Assert.Equal("retry-key", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
                var fields = await Fields(request);
                Assert.False(fields.ContainsKey("long_form"));
                Assert.Equal("true", fields["allow_partial"]);
                return Json("""{"job_id":"job-1","status":"pending"}""", HttpStatusCode.Accepted);
            }
            if (request.RequestUri!.AbsolutePath.Contains("/jobs/"))
            {
                polls++;
                Assert.Equal("/api/v1/generate-long/jobs/job-1", request.RequestUri.AbsolutePath);
                return Json("""{"status":"completed","result":{"file":"/api/v1/audio/u/long.wav","truncated":true,"chunks_failed":1}}""");
            }
            return Audio();
        });
        var result = await Provider(client).SpeechRequest(new()
        {
            Model = "kokoro", Text = new string('a', 5001),
            ProviderOptions = Options("""{"allow_partial":true,"background":true,"headers":{"Idempotency-Key":"retry-key"}}""")
        });
        Assert.Equal(1, polls);
        Assert.Single(result.Warnings);
        Assert.Equal(1, result.ProviderMetadata!["wavenai"].GetProperty("result").GetProperty("chunks_failed").GetInt32());
    }

    [Theory]
    [InlineData("https://other.example/audio.wav")]
    [InlineData("https://waven.ai.evil.example/api/v1/audio/u/a.wav")]
    [InlineData("http://waven.ai/api/v1/audio/u/a.wav")]
    [InlineData("/api/v1/user")]
    public async Task Speech_rejects_download_locations_that_could_leak_credentials(string file)
    {
        var calls = 0;
        using var client = Client(_ =>
        {
            calls++;
            return Task.FromResult(Json(JsonSerializer.Serialize(new { file })));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(client).SpeechRequest(new() { Model = "kokoro", Text = "Hello" }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("base64")]
    [InlineData("data-url")]
    public async Task Transcription_preserves_native_features_and_returns_full_metadata(string encoding)
    {
        using var client = Client(async request =>
        {
            AssertAuth(request);
            Assert.Equal("/api/v1/stt/transcribe", request.RequestUri!.AbsolutePath);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            Assert.Equal(new byte[] { 1, 2, 3 }, await Assert.Single(form, f => Name(f) == "file").ReadAsByteArrayAsync());
            var fields = await Fields(request);
            Assert.Equal("auto", fields["model"]);
            Assert.Equal("word", fields["granularity"]);
            Assert.Equal("false", fields["apply_dictation_profile"]);
            Assert.Equal("true", fields["redact_pii"]);
            Assert.Equal("true", fields["summary"]);
            Assert.Equal("de", fields["translate_to"]);
            Assert.Equal("""{"nested":[1,null,true]}""", fields["future_option"]);
            return Json("""{"text":"Hallo","language":null,"detected_language":"de","duration_seconds":2.5,"segments":[{"start":0.1,"end":2,"text":"Hallo"}],"words":null,"translation":"Hello","translation_status":"ok","correction_denied":"allowance_exhausted","audio_intelligence":{"errors":{"summary":"tier_not_entitled"}}}""");
        });
        var response = await Provider(client).TranscriptionRequest(new()
        {
            Model = "wavenai/auto", MediaType = "audio/wav",
            Audio = encoding switch { "bytes" => new byte[] { 1, 2, 3 }, "base64" => "AQID", _ => "data:audio/wav;base64,AQID" },
            ProviderOptions = Options("""{"granularity":"word","apply_dictation_profile":false,"redact_pii":true,"summary":true,"translate_to":"de","future_option":{"nested":[1,null,true]}}""")
        });
        Assert.Equal("Hallo", response.Text);
        Assert.Equal("de", response.Language);
        Assert.Equal(2.5, response.DurationInSeconds);
        Assert.Equal(0.1, Assert.Single(response.Segments).StartSecond);
        Assert.Single(response.Warnings);
        Assert.Equal("ok", response.ProviderMetadata!["wavenai"].GetProperty("translation_status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diarization_supports_inline_and_queued_results(bool queued)
    {
        using var client = Client(async request =>
        {
            AssertAuth(request);
            const string result = """{"utterances":[{"start":1,"end":2,"text":"Hello","speaker":"SPEAKER_00"}],"num_speakers":1,"low_confidence_merge":true}""";
            if (request.Method == HttpMethod.Get)
                return Json($$"""{"status":"completed","kind":"stt_diarize","diarization":{{result}},"result":null}""");
            var fields = await Fields(request);
            Assert.Equal(queued ? "/api/v1/stt/transcribe/jobs" : "/api/v1/stt/transcribe/diarize", request.RequestUri!.AbsolutePath);
            Assert.Equal("2", fields["max_speakers"]);
            Assert.Equal(queued, fields.ContainsKey("diarize"));
            Assert.False(fields.ContainsKey("queued"));
            return queued ? Json("""{"job_id":"stt-1","status":"pending"}""") : Json(result);
        });
        var response = await Provider(client).TranscriptionRequest(new()
        {
            Model = "parakeet", Audio = "AQID", MediaType = "audio/wav",
            ProviderOptions = Options(JsonSerializer.Serialize(new { queued, diarize = true, max_speakers = 2 }))
        });
        Assert.Equal("Hello", response.Text);
        Assert.Equal(1, Assert.Single(response.Segments).StartSecond);
        var metadata = response.ProviderMetadata!["wavenai"];
        if (queued) metadata = metadata.GetProperty("diarization");
        Assert.Equal("SPEAKER_00", metadata.GetProperty("utterances")[0].GetProperty("speaker").GetString());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("dead_letter")]
    [InlineData("unexpected")]
    public async Task Queued_transcription_errors_are_not_success_shaped(string status)
    {
        using var client = Client(_ => Task.FromResult(Json(JsonSerializer.Serialize(new { job_id = "job", status, error = "failure" }))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).TranscriptionRequest(new()
        {
            Model = "auto", Audio = "AQID", MediaType = "audio/wav", ProviderOptions = Options("""{"queued":true}""")
        }));
        Assert.Contains(status, error.Message);
    }

    [Fact]
    public async Task Job_polling_observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = Client(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(Json("""{"job_id":"job","status":"pending"}"""));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).TranscriptionRequest(new()
        {
            Model = "auto", Audio = "AQID", MediaType = "audio/wav", ProviderOptions = Options("""{"queued":true}""")
        }, cancellation.Token));
    }

    [Fact]
    public async Task Queued_plain_transcription_retains_word_timings_and_detected_language()
    {
        using var client = Client(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var fields = await Fields(request);
                Assert.Equal("word", fields["granularity"]);
                Assert.False(fields.ContainsKey("format"));
                return Json("""{"job_id":"stt-1","status":"pending"}""", HttpStatusCode.Accepted);
            }
            Assert.Equal("/api/v1/stt/transcribe/jobs/stt-1", request.RequestUri!.AbsolutePath);
            return Json("""{"status":"completed","result":{"text":"Hello","detected_language":"en","duration_seconds":1.6,"segments":[{"text":"Hello","start":0,"end":1}],"words":[{"word":"Hello","start":0,"end":1,"confidence":0.99}],"granularity":"word"}}""");
        });
        var result = await Provider(client).TranscriptionRequest(new()
        {
            Model = "auto", Audio = "AQID", MediaType = "audio/wav",
            ProviderOptions = Options("""{"queued":true,"granularity":"word"}""")
        });
        Assert.Equal("Hello", result.Text);
        Assert.Equal("en", result.Language);
        Assert.Equal(0.99, result.ProviderMetadata!["wavenai"].GetProperty("result").GetProperty("words")[0].GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task Malformed_transcription_success_is_not_returned_as_an_empty_transcript()
    {
        using var client = Client(_ => Task.FromResult(Json("""{"unexpected":"response"}""")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).TranscriptionRequest(new()
        {
            Model = "auto", Audio = "AQID", MediaType = "audio/wav"
        }));
        Assert.Contains("no transcript", error.Message);
    }

    [Fact]
    public async Task Native_errors_preserve_the_provider_status_and_detail()
    {
        using var client = Client(_ => Task.FromResult(Json(
            """{"detail":{"error":"model_retired"}}""", HttpStatusCode.BadRequest)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(client).SpeechRequest(new() { Model = "omnivoice", Text = "Hello" }));
        Assert.Contains("400", error.Message);
        Assert.Contains("model_retired", error.Message);
    }

    [Theory]
    [InlineData("flac", "audio/flac")]
    [InlineData("opus", "audio/ogg")]
    [InlineData("pcm", "audio/pcm")]
    [InlineData("aac", "audio/aac")]
    public async Task OpenAI_speech_uses_the_shim_for_extended_formats(string format, string mediaType)
    {
        using var client = Client(async request =>
        {
            AssertAuth(request);
            Assert.Equal("/api/v1/audio/speech", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("kokoro", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("af_heart", json.RootElement.GetProperty("voice").GetString());
            Assert.Equal(format, json.RootElement.GetProperty("response_format").GetString());
            Assert.Equal("keep", json.RootElement.GetProperty("future_field").GetString());
            return Audio(mediaType);
        });
        var result = await Provider(client).OpenAISpeechRequestAsync(new()
        {
            Model = "wavenai/kokoro/af_heart", Input = "Hello", ResponseFormat = format,
            AdditionalProperties = new() { ["future_field"] = JsonSerializer.SerializeToElement("keep") }
        });
        Assert.Equal(mediaType, result.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Audio);
    }

    [Fact]
    public async Task OpenAI_transcription_preserves_wire_options_without_mutating_the_request()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        var options = new OpenAITranscriptionRequest
        {
            Model = "wavenai/auto", File = new FormFile(stream, 0, stream.Length, "file", "clip.wav")
            { Headers = new HeaderDictionary(), ContentType = "audio/wav" },
            ResponseFormat = "verbose_json", TimestampGranularities = ["word"], Prompt = "Waven,Parakeet",
            Temperature = 0, AdditionalProperties = new() { ["future_field"] = JsonSerializer.SerializeToElement("keep") }
        };
        using var client = Client(async request =>
        {
            AssertAuth(request);
            Assert.Equal("/api/v1/audio/transcriptions", request.RequestUri!.AbsolutePath);
            var fields = await Fields(request);
            Assert.Equal("auto", fields["model"]);
            Assert.Equal("word", fields["timestamp_granularities[]"]);
            Assert.Equal("Waven,Parakeet", fields["prompt"]);
            Assert.Equal("keep", fields["future_field"]);
            return Json("""{"text":"Hello","language":"en","duration":1.6,"words":[{"word":"Hello","start":0,"end":1}]}""");
        });
        var result = await Provider(client).OpenAITranscriptionRequestAsync(options);
        Assert.Equal("Hello", result.Text);
        Assert.Equal("Hello", Assert.Single(Assert.IsType<OpenAITranscriptionVerboseResponse>(result).Words!).Word);
        Assert.Equal("wavenai/auto", options.Model);
    }

    [Fact]
    public async Task Discovery_combines_catalog_models_and_public_stock_voices()
    {
        using var scope = ProviderCatalogScope.Begin(new TestCatalogSource("wavenai",
            """{"data":[{"id":"wavenai/kokoro","type":"speech"},{"id":"wavenai/auto","type":"transcription"}]}"""));
        using var client = Client(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("/api/v1/voices", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"voices":[{"id":"af_heart","display_name":"Heart","label":"Heart - English","engine":"kokoro","language":"en","gender":"female"}]}"""));
        });
        var models = (await Provider(client).ListModels()).ToArray();
        Assert.Equal(3, models.Length);
        Assert.Contains(models, m => m.Id == "wavenai/auto" && m.Type == "transcription");
        Assert.Contains(models, m => m.Id == "wavenai/kokoro/af_heart" && m.Tags!.Contains("en"));
    }

    [Fact]
    public async Task Conversational_speech_stream_buffers_native_audio_without_attempting_upstream_SSE()
    {
        using var client = Client(async request =>
        {
            if (request.Method == HttpMethod.Get) return Audio("audio/mpeg");
            Assert.Equal("/api/v1/generate", request.RequestUri!.AbsolutePath);
            var fields = await Fields(request);
            Assert.Equal("0.3", fields["cfg_weight"]);
            Assert.False(fields.ContainsKey("stream_format"));
            return Json("""{"file":"/api/v1/audio/u/speech.mp3"}""");
        });
        var request = new AIRequest
        {
            ProviderId = "wavenai", Model = "wavenai/chatterbox",
            Input = new() { Items = [new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] }] },
            Metadata = new() { ["wavenai"] = JsonSerializer.Deserialize<JsonElement>("""{"cfg_weight":0.3}""") }
        };
        var events = new List<AIStreamEvent>();
        await foreach (var part in Provider(client).StreamUnifiedAsync(request)) events.Add(part);
        Assert.Equal(new[] { "tool-input-available", "tool-output-available", "finish" }, events.Select(e => e.Event.Type));
        var output = Assert.IsType<AIToolOutputAvailableEventData>(events[1].Event.Data);
        Assert.Contains("AQID", JsonSerializer.Serialize(output.Output));
    }

    [Fact]
    public async Task Conversational_transcription_stream_uses_native_options_and_returns_the_transcript_once()
    {
        using var client = Client(async request =>
        {
            Assert.Equal("/api/v1/stt/transcribe", request.RequestUri!.AbsolutePath);
            var fields = await Fields(request);
            Assert.Equal("true", fields["summary"]);
            Assert.False(fields.ContainsKey("stream"));
            return Json("""{"text":"Hello","detected_language":"en","segments":[]}""");
        });
        var request = new AIRequest
        {
            ProviderId = "wavenai", Model = "wavenai/auto",
            Input = new() { Items = [new()
            {
                Role = "user",
                Content = [new AIFileContentPart { Type = "file", MediaType = "audio/wav", Data = "AQID" }]
            }] },
            Metadata = new() { ["wavenai"] = new { summary = true } }
        };
        var events = new List<AIStreamEvent>();
        await foreach (var part in Provider(client).StreamUnifiedAsync(request)) events.Add(part);
        Assert.Equal(new[] { "text-start", "text-delta", "text-end", "finish" }, events.Select(e => e.Event.Type));
        Assert.Equal("Hello", Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
        Assert.NotNull(events[1].Metadata!["wavenai"]);
    }

    private static WavenAIProvider Provider(HttpClient client) => new(new Keys(), new AsyncCacheHelper(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static Dictionary<string, JsonElement> Options(string json) => new() { ["wavenai"] = JsonSerializer.Deserialize<JsonElement>(json) };
    private static string Name(HttpContent content) => content.Headers.ContentDisposition!.Name!.Trim('"');
    private static async Task<Dictionary<string, string>> Fields(HttpRequestMessage request)
    {
        var fields = new Dictionary<string, string>();
        foreach (var field in Assert.IsType<MultipartFormDataContent>(request.Content))
            if (field.Headers.ContentDisposition!.FileName == null) fields.Add(Name(field), await field.ReadAsStringAsync());
        return fields;
    }
    private static void AssertAuth(HttpRequestMessage request)
    {
        Assert.Equal("https", request.RequestUri!.Scheme);
        Assert.Equal("waven.ai", request.RequestUri.Host);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Audio(string mediaType = "audio/wav")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
