using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Soniox;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests;

public sealed class SonioxTests
{
    [Theory]
    [InlineData("soniox/stt-rt-v6", "stt-rt-v6")]
    [InlineData("stt-rt-v5", "stt-rt-v5")]
    [InlineData("stt-rt-v4", "stt-rt-v4")]
    public async Task Realtime_separates_key_restrictions_from_raw_websocket_configuration(string model, string expectedModel)
    {
        var calls = 0;
        using var client = CreateClient(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/auth/temporary-api-key", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.Equal(5, payload.EnumerateObject().Count());
            Assert.Equal("transcribe_websocket", payload.GetProperty("usage_type").GetString());
            Assert.True(payload.GetProperty("single_use").GetBoolean());
            Assert.Equal(60, payload.GetProperty("expires_in_seconds").GetInt32());
            Assert.Equal(120, payload.GetProperty("max_session_duration_seconds").GetInt32());
            Assert.Equal("call-1", payload.GetProperty("client_reference_id").GetString());
            return JsonResponse("""{"api_key":"temporary-key","expires_at":"2026-11-04T06:00:00Z"}""");
        });
        var options = JsonSerializer.Deserialize<JsonElement>("""
            {
              "expires_in_seconds":60,"max_session_duration_seconds":120,"client_reference_id":"call-1",
              "usage_type":"tts_rt","single_use":false,"api_key":"must-not-be-returned","model":"must-not-win",
              "audio_format":"pcm_s16le","sample_rate":16000,"num_channels":1,
              "enable_agent_text_input":true,"enable_endpoint_detection":true,"max_endpoint_delay_ms":1000,
              "language_hints":["en","es"],"language_hints_strict":true,
              "enable_speaker_diarization":true,"enable_language_identification":true,
              "context":{"terms":["Faro"],"future_context_option":{"enabled":true}},
              "translation":{"type":"one_way","target_language":"es"},"future_option":{"value":7},
              "future_number":42,"future_string":"unchanged","future_null":null
            }
            """);

        var response = await CreateProvider(client).GetRealtimeToken(new RealtimeRequest
        {
            Model = model,
            ProviderOptions = new() { ["soniox"] = options }
        }, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("temporary-key", response.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T06:00:00Z").ToUnixTimeSeconds(), response.ExpiresAt);
        var metadata = response.ProviderMetadata!["soniox"];
        Assert.Equal("wss://stt-rt.soniox.com/transcribe-websocket", metadata.GetProperty("websocket_url").GetString());
        var config = metadata.GetProperty("config");
        Assert.Equal(expectedModel, config.GetProperty("model").GetString());
        foreach (var name in new[] { "expires_in_seconds", "max_session_duration_seconds", "usage_type", "single_use", "api_key" })
            Assert.False(config.TryGetProperty(name, out _));
        foreach (var property in options.EnumerateObject().Where(p => p.Name is not
                     ("expires_in_seconds" or "max_session_duration_seconds" or "usage_type" or "single_use" or "api_key" or "model")))
            Assert.Equal(property.Value.GetRawText(), config.GetProperty(property.Name).GetRawText());
        Assert.Equal("must-not-win", options.GetProperty("model").GetString());
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Realtime_defaults_preserve_single_use_key_contract(string? options)
    {
        using var client = CreateClient(async request =>
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.Equal(1800, payload.GetProperty("expires_in_seconds").GetInt32());
            Assert.True(payload.GetProperty("single_use").GetBoolean());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("max_session_duration_seconds").ValueKind);
            return JsonResponse("""{"api_key":"temporary-key"}""");
        });
        var before = DateTimeOffset.UtcNow.AddSeconds(1800).ToUnixTimeSeconds();
        var response = await CreateProvider(client).GetRealtimeToken(new RealtimeRequest
        {
            ProviderOptions = options is null ? null : new() { ["soniox"] = JsonSerializer.Deserialize<JsonElement>(options) }
        }, CancellationToken.None);

        Assert.InRange(response.ExpiresAt, before, DateTimeOffset.UtcNow.AddSeconds(1800).ToUnixTimeSeconds());
        var config = response.ProviderMetadata!["soniox"].GetProperty("config");
        Assert.Equal("stt-rt-v6", config.GetProperty("model").GetString());
        Assert.Equal("auto", config.GetProperty("audio_format").GetString());
    }

    [Theory]
    [InlineData("""{"expires_in_seconds":0}""")]
    [InlineData("""{"expires_in_seconds":3601}""")]
    [InlineData("""{"max_session_duration_seconds":0}""")]
    [InlineData("""{"max_session_duration_seconds":18001}""")]
    [InlineData("""{"expires_in_seconds":"invalid"}""")]
    [InlineData("""{"client_reference_id":123}""")]
    [InlineData("""[]""")]
    public async Task Realtime_invalid_options_fail_before_http(string options)
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("No HTTP request expected."));
        var exception = await Record.ExceptionAsync(() => CreateProvider(client).GetRealtimeToken(new RealtimeRequest
        {
            Model = "stt-rt-v6",
            ProviderOptions = new() { ["soniox"] = JsonSerializer.Deserialize<JsonElement>(options) }
        }, CancellationToken.None));

        Assert.True(exception is ArgumentException or JsonException, exception?.ToString());
    }

    [Theory]
    [InlineData(1, 1, 256)]
    [InlineData(3600, 18000, 256)]
    [InlineData(60, 120, 257)]
    public async Task Realtime_restriction_boundaries_remain_compatible(int expiresIn, int maxSession, int referenceLength)
    {
        var calls = 0;
        using var client = CreateClient(_ =>
        {
            calls++;
            return Task.FromResult(JsonResponse("""{"api_key":"temporary-key"}"""));
        });
        var request = new RealtimeRequest
        {
            Model = "stt-rt-v6",
            ProviderOptions = new()
            {
                ["soniox"] = JsonSerializer.SerializeToElement(new
                {
                    expires_in_seconds = expiresIn,
                    max_session_duration_seconds = maxSession,
                    client_reference_id = new string('x', referenceLength)
                })
            }
        };
        if (referenceLength > 256)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(client).GetRealtimeToken(request, CancellationToken.None));
            Assert.Equal(0, calls);
        }
        else
        {
            await CreateProvider(client).GetRealtimeToken(request, CancellationToken.None);
            Assert.Equal(1, calls);
        }
    }

    [Theory]
    [InlineData("soniox/stt-async-v6", "stt-async-v6")]
    [InlineData("stt-async-v5", "stt-async-v5")]
    [InlineData("stt-async-v4", "stt-async-v4")]
    public async Task Async_preserves_raw_options_native_results_and_existing_response_contract(string model, string expectedModel)
    {
        var options = JsonSerializer.Deserialize<JsonElement>("""
            {
              "language_hints":["en","es"],"language_hints_strict":true,
              "enable_speaker_diarization":true,"enable_language_identification":true,
              "client_reference_id":"job-1","context":{"general":[{"key":"domain","value":"Travel"}],"terms":["Faro"]},
              "translation":{"type":"two_way","language_a":"en","language_b":"es"},
              "future_option":{"nested":[1,true,null]},"model":"must-not-win","file_id":"must-not-win"
            }
            """);
        var paths = new List<string>();
        const string transcript = """
            {"id":"transcription-1","text":"Hello Hola","tokens":[
              {"text":"Hello","start_ms":100,"end_ms":600,"confidence":0.99,"speaker":"1","language":"en","translation_status":"original"},
              {"text":" Hola","start_ms":100,"end_ms":600,"speaker":"1","language":"es","source_language":"en","translation_status":"translation","future_token_field":7}
            ],"future_result":{"value":true}}
            """;
        using var client = CreateClient(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var path = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            paths.Add(path);
            switch (path)
            {
                case "POST /v1/files":
                    var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                    Assert.Contains(form, part => part.Headers.ContentDisposition?.Name?.Trim('"') == "client_reference_id");
                    return JsonResponse("""{"id":"file-1"}""");
                case "POST /v1/transcriptions":
                    var payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
                    Assert.Equal(expectedModel, payload.GetProperty("model").GetString());
                    Assert.Equal("file-1", payload.GetProperty("file_id").GetString());
                    foreach (var property in options.EnumerateObject().Where(p => p.Name is not ("model" or "file_id")))
                        Assert.Equal(property.Value.GetRawText(), payload.GetProperty(property.Name).GetRawText());
                    return JsonResponse("""{"id":"transcription-1"}""");
                case "GET /v1/transcriptions/transcription-1":
                    return JsonResponse("""{"id":"transcription-1","status":"completed","audio_duration_ms":1250,"future_job_field":42}""");
                case "GET /v1/transcriptions/transcription-1/transcript":
                    return JsonResponse(transcript);
                default:
                    Assert.Equal(HttpMethod.Delete, request.Method);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        });

        var response = await CreateProvider(client).TranscriptionRequest(Transcription(model, options));

        Assert.Equal("Hello Hola", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal(1.25f, response.DurationInSeconds);
        Assert.Equal(2, response.Segments!.Count());
        Assert.Equal(0.1f, response.Segments.First().StartSecond);
        Assert.Equal(0.6f, response.Segments.First().EndSecond);
        Assert.Equal(expectedModel.ToModelId("soniox"), response.Response!.ModelId);
        var body = Assert.IsType<JsonElement>(response.Response.Body);
        Assert.Equal(transcript, body.GetRawText());
        var metadata = response.ProviderMetadata!["soniox"];
        Assert.True(JsonElement.DeepEquals(body, metadata.GetProperty("transcript")));
        Assert.Equal(42, metadata.GetProperty("transcription").GetProperty("future_job_field").GetInt32());
        Assert.Equal(new[]
        {
            "POST /v1/files", "POST /v1/transcriptions", "GET /v1/transcriptions/transcription-1",
            "GET /v1/transcriptions/transcription-1/transcript", "DELETE /v1/transcriptions/transcription-1", "DELETE /v1/files/file-1"
        }, paths);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("provider")]
    [InlineData("cancel")]
    [InlineData("create")]
    public async Task Async_always_attempts_file_cleanup_and_preserves_the_original_failure(string failure)
    {
        var deleted = new List<string>();
        using var client = CreateClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete)
            {
                deleted.Add(path);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("cleanup failed")
                });
            }

            if (request.Method == HttpMethod.Post)
                return Task.FromResult(path == "/v1/files"
                    ? JsonResponse("""{"id":"file-1"}""")
                    : failure == "create"
                        ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid model") }
                        : JsonResponse("""{"id":"transcription-1"}"""));
            if (failure == "cancel")
                throw new OperationCanceledException("cancelled");
            return Task.FromResult(path.EndsWith("/transcript", StringComparison.Ordinal)
                ? JsonResponse("""{"text":"Hello","tokens":[]}""")
                : failure == "provider"
                    ? JsonResponse("""{"status":"error","error_type":"invalid_audio","error_message":"bad recording"}""")
                    : JsonResponse("""{"status":"completed"}"""));
        });

        var exception = await Record.ExceptionAsync(() => CreateProvider(client).TranscriptionRequest(Transcription()));

        Assert.NotNull(exception);
        if (failure == "cancel")
            Assert.IsType<OperationCanceledException>(exception);
        else
            Assert.Contains(failure switch
            {
                "provider" => "invalid_audio: bad recording",
                "create" => "invalid model",
                _ => "cleanup of v1/transcriptions/transcription-1"
            }, exception.Message);
        Assert.Equal(failure == "create"
            ? new[] { "/v1/files/file-1" }
            : new[] { "/v1/transcriptions/transcription-1", "/v1/files/file-1" }, deleted);
    }

    [Theory]
    [InlineData("""{"language_hints":true}""")]
    [InlineData("""{"enable_speaker_diarization":"true"}""")]
    [InlineData("""["invalid"]""")]
    public async Task Async_invalid_known_options_fail_before_upload(string options)
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("No upload expected."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(client).TranscriptionRequest(
            Transcription(options: JsonSerializer.Deserialize<JsonElement>(options))));
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("text", false)]
    [InlineData("verbose_json", false)]
    [InlineData("json", true)]
    public async Task OpenAI_adapters_keep_completed_async_response_and_stream_shapes(string format, bool stream)
    {
        var deleted = 0;
        using var client = CreateClient(async request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deleted++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.RequestUri!.AbsolutePath == "/v1/transcriptions" && request.Method == HttpMethod.Post)
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
                Assert.Equal("stt-async-v6", payload.GetProperty("model").GetString());
                return JsonResponse("""{"id":"job-1"}""");
            }
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/files" => JsonResponse("""{"id":"file-1"}"""),
                "/v1/transcriptions/job-1" => JsonResponse("""{"status":"completed","audio_duration_ms":1000}"""),
                "/v1/transcriptions/job-1/transcript" => JsonResponse("""
                    {"text":"Hello","tokens":[{"text":"Hello","start_ms":0,"end_ms":1000,"language":"en","speaker":"1"}]}
                    """),
                _ => throw new InvalidOperationException("Unexpected path.")
            };
        });
        using var audio = new MemoryStream([1, 2, 3]);
        var request = new OpenAITranscriptionRequest
        {
            Model = "soniox/stt-async-v6",
            ResponseFormat = format,
            Stream = stream,
            File = new FormFile(audio, 0, audio.Length, "file", "audio.wav")
            {
                Headers = new HeaderDictionary(),
                ContentType = "audio/wav"
            }
        };
        var provider = CreateProvider(client);
        if (stream)
        {
            var events = new List<IOpenAITranscriptionStreamEvent>();
            await foreach (var item in provider.OpenAITranscriptionStreamingAsync(request))
                events.Add(item);
            Assert.Equal("Hello", Assert.IsType<OpenAITranscriptionTextDelta>(events[0]).Delta);
            Assert.Equal("Hello", Assert.IsType<OpenAITranscriptionTextDone>(events[1]).Text);
            Assert.Equal(2, events.Count);
        }
        else
        {
            var response = await provider.OpenAITranscriptionRequestAsync(request);
            Assert.Equal("Hello", response.Text);
            if (format == "verbose_json")
            {
                var verbose = Assert.IsType<OpenAITranscriptionVerboseResponse>(response);
                Assert.Equal("en", verbose.Language);
                Assert.Equal(1, verbose.Duration);
                Assert.Single(verbose.Segments!);
            }
            else
                Assert.IsType<OpenAITranscriptionResponse>(response);
        }
        Assert.Equal(2, deleted);
    }

    [Fact]
    public async Task Speech_keeps_request_scoped_authentication_after_stt_changes()
    {
        using var client = CreateClient(_ =>
        {
            Assert.Equal("Bearer", _.Headers.Authorization?.Scheme);
            Assert.Equal("tts-rt.soniox.com", _.RequestUri!.Host);
            Assert.Equal("/tts", _.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        });

        var response = await CreateProvider(client).SpeechRequest(new SpeechRequest
        {
            Model = "soniox/tts-rt-v2/Adrian",
            Text = "Hello",
            Language = "en"
        });

        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Discovery_does_not_advertise_async_models_as_realtime()
    {
        using var client = CreateClient(request => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => JsonResponse("""
                {"models":[
                  {"id":"stt-rt-v6","transcription_mode":"real_time","languages":[{"code":"en"}]},
                  {"id":"stt-async-v6","transcription_mode":"async","languages":[{"code":"es"}]},
                  {"id":"stt-rt-v5","transcription_mode":"real_time"},
                  {"id":"stt-async-v5","transcription_mode":"async"}
                ]}
                """),
            "/v1/tts-models" => JsonResponse("""
                {"models":[{"id":"tts-rt-v2","languages":[{"code":"en"}],"voices":[{"id":"Adrian","gender":"male"}]}]}
                """),
            "/v1/voices" => JsonResponse("""{"voices":[]}"""),
            _ => throw new InvalidOperationException("Unexpected path.")
        }));

        var models = (await CreateProvider(client).ListModels()).ToArray();

        Assert.Equal(6, models.Length);
        foreach (var model in models.Where(m => m.Type == "transcription"))
        {
            Assert.Equal("transcription", model.Type);
            if (model.Id.Contains("-rt-", StringComparison.Ordinal))
            {
                Assert.Contains("real-time", model.Tags!);
                Assert.Contains("realtime-token", model.Tags!);
            }
            else
            {
                Assert.DoesNotContain("real-time", model.Tags!);
                Assert.DoesNotContain("realtime-token", model.Tags!);
            }
        }
        Assert.Contains("en", models[0].Tags!);
        Assert.Contains("es", models[1].Tags!);
        var voice = Assert.Single(models, m => m.Id.EndsWith("/Adrian", StringComparison.Ordinal));
        Assert.Contains("voice", voice.Tags!);
        Assert.Contains("male", voice.Tags!);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static TranscriptionRequest Transcription(string model = "soniox/stt-async-v6", JsonElement? options = null)
        => new()
        {
            Model = model,
            Audio = Convert.ToBase64String([1, 2, 3]),
            MediaType = "audio/wav",
            ProviderOptions = options is null ? null : new() { ["soniox"] = options.Value }
        };

    private static SonioxProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), new TestHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }
}
