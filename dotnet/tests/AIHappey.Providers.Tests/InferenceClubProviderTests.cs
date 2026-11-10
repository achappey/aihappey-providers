using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.InferenceClub;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests;

public sealed class InferenceClubProviderTests
{
    [Fact]
    public async Task Discovery_uses_declared_capabilities_and_registration_supports_direct_clients()
    {
        using var factory = new Factory(request =>
        {
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            return Json("""
                {"data":[
                  {"id":"qwen/model","owned_by":"home","service_type":"llm","context_length":32768,"supported_features":["tools","vision"],"input_modalities":["text","image"]},
                  {"id":"flux","service_type":"image","context_length":null},
                  {"id":"asr","service_type":"stt"},
                  {"id":"tts","service_type":"tts"},
                  {"id":"song","service_type":"music"},
                  {"id":"ltx","service_type":"video"},
                  {"id":"mesh","service_type":"3d"}]}
                """);
        });
        var client = new ProviderClient(factory);
        var models = (await client.ExecuteAsync("inferenceclub",
            new Dictionary<string, string> { ["inferenceclub"] = "test-key" }, (p, ct) => p.ListModels(ct))).ToArray();

        Assert.Equal(["language", "image", "transcription", "speech", "speech", "video"], models.Select(m => m.Type));
        Assert.Equal("inferenceclub/qwen/model", models[0].Id);
        Assert.Equal("home", models[0].OwnedBy);
        Assert.Equal(32768, models[0].ContextWindow);
        Assert.Contains("tools", models[0].Tags!);
        Assert.Contains("vision", models[0].Tags!);
        Assert.Null(models[1].ContextWindow);
    }

    [Fact]
    public async Task Chat_and_compatibility_adapters_only_use_chat_completions()
    {
        var calls = 0;
        using var factory = new Factory(request =>
        {
            calls++;
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            var payload = Body(request);
            Assert.False(payload.GetProperty("stream").GetBoolean());
            if (calls == 1)
            {
                Assert.Equal("request", request.Headers.GetValues("X-Request-Id").Single());
                Assert.True(payload.GetProperty("future").GetProperty("nested")[0].GetBoolean());
                Assert.False(payload.TryGetProperty("headers", out _));
            }
            return ChatResponse();
        });
        var provider = new InferenceClubProvider(new Keys(), factory);
        var chat = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "qwen/model",
            Stream = true,
            Headers = new() { ["X-Request-Id"] = "request", ["authorization"] = "Bearer wrong-key" },
            Messages = [new() { Role = "user", Content = "hello" }],
            AdditionalProperties = new() { ["future"] = Element("""{"nested":[true,null]}""") }
        });
        Assert.Equal("inferenceclub/qwen/model", chat.Model);
        var response = await provider.ResponsesAsync(new ResponseRequest { Model = "qwen/model", Input = "hello" });
        Assert.NotEmpty(response.Output);
        var messages = await provider.MessagesAsync(new MessagesRequest
        {
            Model = "qwen/model", MaxTokens = 10,
            Messages = [new() { Role = "user", Content = new MessagesContent("hello") }]
        }, []);
        Assert.NotNull(messages);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Streaming_chat_and_all_stream_adapters_use_the_native_SSE_route()
    {
        var sawMetadata = false;
        using var factory = new Factory(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            var payload = Body(request);
            Assert.True(payload.GetProperty("stream").GetBoolean());
            Assert.True(payload.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            if (payload.TryGetProperty("future", out var future))
            {
                Assert.True(future.GetProperty("deep").GetBoolean());
                sawMetadata = true;
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"c","object":"chat.completion.chunk","created":1,"model":"qwen/model","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"},"finish_reason":null}]}

                    data: {"id":"c","object":"chat.completion.chunk","created":1,"model":"qwen/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

                    data: [DONE]

                    """, Encoding.UTF8, "text/event-stream")
            };
        });
        var provider = new InferenceClubProvider(new Keys(), factory);
        Assert.NotEmpty(await Collect(provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "qwen/model", Messages = [new() { Role = "user", Content = "hello" }]
        })));
        Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(new ResponseRequest { Model = "qwen/model", Input = "hello" })));
        Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(new MessagesRequest
        {
            Model = "qwen/model", MaxTokens = 10,
            Messages = [new() { Role = "user", Content = new MessagesContent("hello") }]
        }, [])));
        Assert.NotEmpty(await Collect(provider.StreamAsync(new ChatRequest
        {
            Model = "qwen/model",
            Messages = [new UIMessage { Id = "u", Role = Role.user, Parts = [new TextUIPart { Text = "hello" }] }],
            ProviderMetadata = Options("""{"future":{"deep":true}}""")
        })));
        Assert.True(sawMetadata);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Images_preserve_flat_metadata_and_resolve_sync_or_queued_results(bool queued)
    {
        using var factory = new Factory(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/images/generations":
                    var payload = Body(request);
                    Assert.Equal(42, payload.GetProperty("seed").GetInt32());
                    Assert.Equal(17, payload.GetProperty("steps").GetInt32());
                    Assert.True(payload.GetProperty("future").GetProperty("enabled").GetBoolean());
                    Assert.False(payload.TryGetProperty("providerOptions", out _));
                    return queued
                        ? Json("""{"id":"42","object":"inference.job","status":"QUEUED"}""", HttpStatusCode.Accepted)
                        : Json("""{"created":1,"data":[{"b64_json":"AQID"}],"native":{"value":true}}""");
                case "/v1/jobs/42":
                    return Json("""{"id":42,"status":"PROCESSED","media":[{"kind":"image","url":"https://storage.example/image.png"}]}""");
                case "/image.png":
                    return Bytes("image/png");
                default:
                    throw new InvalidOperationException(request.RequestUri.ToString());
            }
        });
        var response = await new InferenceClubProvider(new Keys(), factory).ImageRequest(new ImageRequest
        {
            Model = "flux", Prompt = "fox", Seed = 42,
            ProviderOptions = Options("""{"steps":17,"future":{"enabled":true},"async":true}""")
        });
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
        Assert.Equal("inferenceclub/flux", response.Response.ModelId);
        Assert.Equal(JsonValueKind.Object, response.ProviderMetadata!["inferenceclub"].ValueKind);
    }

    [Fact]
    public async Task Image_edits_use_multipart_for_native_uploads_and_URL_references()
    {
        var edits = 0;
        using var factory = new Factory(request =>
        {
            if (request.RequestUri!.Host == "reference.example")
                return Bytes("image/png");
            edits++;
            Assert.Equal("/v1/images/edits", request.RequestUri.AbsolutePath);
            Assert.IsType<MultipartFormDataContent>(request.Content);
            var form = Form(request);
            Assert.Equal("fox", form["prompt"].Single());
            Assert.Equal("19", form["steps"].Single());
            Assert.Contains(form.Keys, name => name is "image" or "image[]");
            return Json("""{"created":1,"data":[{"b64_json":"AQID"}]}""");
        });
        var provider = new InferenceClubProvider(new Keys(), factory);
        await provider.ImageRequest(new ImageRequest
        {
            Model = "flux", Prompt = "fox",
            Files = [new ImageFileUrl { Url = "https://reference.example/input.png" }],
            Mask = new ImageFile { Data = "data:image/png;base64,AQID", MediaType = "image/png" },
            ProviderOptions = Options("""{"steps":19,"future":{"nested":[1,true]}}""")
        });
        var native = await provider.OpenAIImageEditRequestAsync(new OpenAIImageEditRequest
        {
            Model = "flux", Prompt = "fox", ImageFiles = [Upload()],
            AdditionalProperties = new() { ["steps"] = Element("19") }
        });
        Assert.Equal("AQID", Assert.Single(native.Data!).B64Json);
        var events = await Collect(provider.OpenAIImageEditStreamingAsync(new OpenAIImageEditRequest
        {
            Model = "flux", Prompt = "fox",
            Images = [new() { ImageUrl = "https://reference.example/input.png" }],
            AdditionalProperties = new() { ["steps"] = Element("19") }
        }));
        Assert.IsType<OpenAIImageEditCompleted>(Assert.Single(events));
        Assert.Equal(3, edits);
    }

    [Fact]
    public async Task Native_image_generation_preserves_extension_data_and_response_fields()
    {
        using var factory = new Factory(request =>
        {
            var payload = Body(request);
            Assert.False(payload.GetProperty("stream").GetBoolean());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
            Assert.Equal(23, payload.GetProperty("steps").GetInt32());
            Assert.Equal(2, payload.GetProperty("future").GetProperty("nested").GetArrayLength());
            return Json("""{"created":10,"data":[{"b64_json":"AQID","revised_prompt":"fox"}],"size":"1024x1024","native":{"seed":42}}""");
        });
        var response = await new InferenceClubProvider(new Keys(), factory).OpenAIImageGenerationRequestAsync(
            new OpenAIImageGenerationRequest
            {
                Model = "flux", Prompt = "fox", Stream = true,
                AdditionalProperties = new()
                {
                    ["steps"] = Element("23"), ["future_null"] = Element("null"),
                    ["future"] = Element("""{"nested":[true,null]}""")
                }
            });
        Assert.Equal(10, response.Created);
        Assert.Equal("1024x1024", response.Size);
        Assert.Equal("fox", Assert.Single(response.Data!).RevisedPrompt);
        Assert.Equal(42, response.AdditionalProperties!["native"].GetProperty("seed").GetInt32());
    }

    [Theory]
    [InlineData("https://storage.example/image.png", false)]
    [InlineData("https://api.inference.club/api/inference/assets/private/", true)]
    public async Task Media_downloads_only_authenticate_owner_gated_assets(string url, bool authenticated)
    {
        using var factory = new Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/images/generations")
                return Json($$"""{"created":1,"data":[{"url":"{{url}}"}]}""");
            Assert.Equal(url, request.RequestUri.ToString());
            Assert.Equal(authenticated, request.Headers.Authorization is not null);
            return Bytes("image/png");
        });
        var provider = new InferenceClubProvider(new Keys(), factory);
        var events = await Collect(provider.OpenAIImageGenerationStreamingAsync(
            new OpenAIImageGenerationRequest { Model = "flux", Prompt = "fox" }));
        Assert.Equal("AQID", Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events)).B64Json);
        Assert.All(factory.Clients, client => Assert.Null(client.DefaultRequestHeaders.Authorization));
    }

    [Theory]
    [InlineData("tts", "[]", "audio/speech", "input")]
    [InlineData("music", "[]", "music/generations", "prompt")]
    [InlineData("tts", """["voice-cloning"]""", "voice/generations", "input")]
    public async Task Audio_routes_by_discovered_service_and_passes_native_options(
        string service, string features, string endpoint, string textField)
    {
        using var factory = new Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return Json($$"""{"data":[{"id":"audio-model","service_type":"{{service}}","supported_features":{{features}}}]}""");
            Assert.Equal("/v1/" + endpoint, request.RequestUri.AbsolutePath);
            var payload = Body(request);
            Assert.Equal("hello", payload.GetProperty(textField).GetString());
            Assert.Equal(12, payload.GetProperty("speakers").GetProperty("S1").GetInt32());
            Assert.Equal("en-US", payload.GetProperty("language").GetString());
            Assert.True(payload.GetProperty("future").GetBoolean());
            return Bytes("audio/wav");
        });
        var response = await new InferenceClubProvider(new Keys(), factory).SpeechRequest(new SpeechRequest
        {
            Model = "audio-model", Text = "hello", Language = "en-US",
            ProviderOptions = Options("""{"speakers":{"S1":12},"future":true,"lyrics":"chorus"}""")
        });
        Assert.Equal("wav", response.Audio.Format);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("AQID", response.Audio.Base64);
    }

    [Fact]
    public async Task Native_speech_handles_URL_delivery_and_async_jobs_and_adapts_buffered_events()
    {
        using var factory = new Factory(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/models":
                    return Json("""{"data":[{"id":"tts","service_type":"tts"}]}""");
                case "/v1/audio/speech":
                    Assert.Equal("url", Body(request).GetProperty("delivery").GetString());
                    return Json("""{"object":"inference.job","id":"7","status":"QUEUED"}""", HttpStatusCode.Accepted);
                case "/v1/jobs/7":
                    return Json("""{"status":"PROCESSED","media":[{"kind":"audio","url":"https://storage.example/audio.wav"}]}""");
                case "/audio.wav":
                    return Bytes("audio/wav");
                default:
                    throw new InvalidOperationException(request.RequestUri.ToString());
            }
        });
        var events = await Collect(new InferenceClubProvider(new Keys(), factory).OpenAISpeechStreamingAsync(new AudioSpeechRequest
        {
            Model = "tts", Input = "hello",
            AdditionalProperties = new() { ["delivery"] = Element("\"url\""), ["async"] = Element("true") }
        }));
        Assert.Equal("AQID", Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.IsType<AudioSpeechStreamDone>(events[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Speech_reports_actual_output_format_for_direct_bytes_and_URL_delivery(bool deliveryUrl)
    {
        using var factory = new Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return Json("""{"data":[{"id":"tts","service_type":"tts"}]}""");
            if (request.RequestUri.AbsolutePath == "/v1/audio/speech")
            {
                Assert.Equal("mp3", Body(request).GetProperty("response_format").GetString());
                return deliveryUrl
                    ? Json("""{"object":"inference.result","data":[{"kind":"audio","url":"https://storage.example/audio.wav"}]}""")
                    : Bytes("audio/wav");
            }
            Assert.Null(request.Headers.Authorization);
            return Bytes("audio/wav");
        });
        var result = await new InferenceClubProvider(new Keys(), factory).SpeechRequest(new SpeechRequest
        {
            Model = "tts", Text = "hello", OutputFormat = "mp3",
            ProviderOptions = Options(deliveryUrl ? """{"delivery":"url"}""" : "{}")
        });
        Assert.Equal("audio/wav", result.Audio.MimeType);
        Assert.Equal("wav", result.Audio.Format);
    }

    [Fact]
    public async Task Transcription_preserves_metadata_timestamps_and_provider_downgrades()
    {
        var calls = 0;
        using var factory = new Factory(request =>
        {
            calls++;
            Assert.Equal("/v1/audio/transcriptions", request.RequestUri!.AbsolutePath);
            var form = Form(request);
            Assert.Equal("en", form["language"].Single());
            Assert.Equal("verbose_json", form["response_format"].Single());
            Assert.Equal(["word", "segment"], form["timestamp_granularities[]"]);
            Assert.Equal("""{"enabled":true}""", form["future"].Single());
            return calls == 1
                ? Json("""{"text":"hello","language":"en","duration":1.2,"segments":[{"text":"hello","start":0,"end":1.2}],"words":[{"word":"hello","start":0,"end":1.2}]}""")
                : Json("""{"text":"hello","usage":{"type":"duration","seconds":2}}""");
        });
        var provider = new InferenceClubProvider(new Keys(), factory);
        var result = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "asr", Audio = "data:audio/wav;base64,AQID", MediaType = "audio/wav",
            ProviderOptions = Options("""{"language":"en","response_format":"verbose_json","timestamp_granularities":["word","segment"],"future":{"enabled":true}}""")
        });
        Assert.Equal(1.2, result.DurationInSeconds);
        Assert.Equal("hello", Assert.Single(result.Segments).Text);
        Assert.Equal(1.2, Assert.Single(result.Segments).EndSecond);
        var native = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "asr", File = Upload("audio/wav"), Language = "en", ResponseFormat = "verbose_json",
            TimestampGranularities = ["word", "segment"],
            AdditionalProperties = new() { ["future"] = Element("""{"enabled":true}""") }
        });
        Assert.IsType<OpenAITranscriptionResponse>(native);
        Assert.Equal("hello", native.Text);
        Assert.Throws<NotSupportedException>(() => provider.OpenAITranscriptionStreamingAsync(new()));
    }

    [Theory]
    [InlineData("QUEUED", "pending", false)]
    [InlineData("PROCESSING", "pending", false)]
    [InlineData("FAILED", "error", false)]
    [InlineData("CANCELED", "error", false)]
    [InlineData("PROCESSED", "completed", false)]
    [InlineData("PROCESSED", "completed", true)]
    public async Task Video_maps_native_jobs_and_first_frame_inputs(string status, string expected, bool frame)
    {
        using var factory = new Factory(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v1/videos/generations":
                    var payload = Body(request);
                    Assert.True(payload.GetProperty("async").GetBoolean());
                    Assert.Equal("url", payload.GetProperty("delivery").GetString());
                    Assert.Equal("data:image/png;base64,AQID", payload.GetProperty("image").GetString());
                    Assert.Equal(1280, payload.GetProperty("width").GetInt32());
                    Assert.Equal(720, payload.GetProperty("height").GetInt32());
                    Assert.Equal(7, payload.GetProperty("duration").GetInt32());
                    Assert.Equal(33, payload.GetProperty("num_inference_steps").GetInt32());
                    return Json("""{"object":"inference.job","id":"42","status":"QUEUED"}""", HttpStatusCode.Accepted);
                case "/v1/jobs/42":
                    return Json($$"""{"status":"{{status}}","error":{"message":"failed"},"media":[{"kind":"video","url":"https://storage.example/video.mp4"}]}""");
                case "/video.mp4":
                    return Bytes("video/mp4");
                default:
                    throw new InvalidOperationException(request.RequestUri.ToString());
            }
        });
        var provider = new InferenceClubProvider(new Keys(), factory);
        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "ltx", Prompt = "boat", Duration = 7, Resolution = "1280x720",
            Image = frame ? null : new VideoFile { Data = "AQID", MediaType = "image/png" },
            FrameImages = frame ? [new() { FrameType = "first_frame", Image = new VideoFile { Data = "AQID", MediaType = "image/png" } }] : null,
            ProviderOptions = Options("""{"num_inference_steps":33,"future":{"enabled":true}}""")
        });
        var result = await provider.GetVideoOperationStatus(started.Operation);
        Assert.Equal(expected, result.Status);
        Assert.Equal("inferenceclub/ltx", result.Response.ModelId);
        if (result is VideoOperationCompletedResult completed)
            Assert.Equal("AQID", Assert.Single(completed.Videos).Data);
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("CANCELED")]
    [InlineData("UNKNOWN")]
    public async Task Failed_and_unknown_media_jobs_surface_errors(string status)
    {
        using var factory = new Factory(request => request.RequestUri!.AbsolutePath == "/v1/images/generations"
            ? Json("""{"object":"inference.job","id":"42"}""", HttpStatusCode.Accepted)
            : Json($$$"""{"status":"{{{status}}}","error":{"message":"failure"}}"""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InferenceClubProvider(new Keys(), factory).ImageRequest(new ImageRequest { Model = "flux", Prompt = "fox" }));
        Assert.Contains(status, error.Message);
    }

    [Fact]
    public async Task Errors_missing_credentials_and_invalid_tokens_are_explicit()
    {
        using var factory = new Factory(_ => Json("""{"error":{"message":"no provider"}}""", HttpStatusCode.NotFound));
        var provider = new InferenceClubProvider(new Keys(), factory);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains("no provider", error.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("invalid"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new InferenceClubProvider(new Keys(null), factory).ListModels());
    }

    [Fact]
    public async Task Queued_media_polling_honors_cancellation()
    {
        using var factory = new Factory(request => request.RequestUri!.AbsolutePath == "/v1/images/generations"
            ? Json("""{"object":"inference.job","id":"42"}""", HttpStatusCode.Accepted)
            : Json("""{"status":"PROCESSING"}"""));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InferenceClubProvider(new Keys(), factory).ImageRequest(new ImageRequest { Model = "flux", Prompt = "fox" }, cancellation.Token));
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream)
            result.Add(item);
        return result;
    }

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static Dictionary<string, JsonElement> Options(string json) => new() { ["inferenceclub"] = Element(json) };
    private static JsonElement Body(HttpRequestMessage request)
        => Element(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
    private static Dictionary<string, string[]> Form(HttpRequestMessage request)
        => ((MultipartFormDataContent)request.Content!).GroupBy(c => c.Headers.ContentDisposition!.Name!.Trim('"'))
            .ToDictionary(g => g.Key, g => g.Select(c => c.ReadAsStringAsync().GetAwaiter().GetResult()).ToArray());
    private static IFormFile Upload(string type = "image/png")
        => new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "input")
        { Headers = new HeaderDictionary(), ContentType = type };
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Bytes(string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new(type);
        return response;
    }
    private static HttpResponseMessage ChatResponse()
        => Json("""{"id":"c","object":"chat.completion","created":1,"model":"qwen/model","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""");

    private sealed class Keys(string? value = "test-key") : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "inferenceclub" ? value : null;
    }
    private sealed class Factory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory, IDisposable
    {
        public List<HttpClient> Clients { get; } = [];
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new Handler(respond));
            Clients.Add(client);
            return client;
        }
        public void Dispose()
        {
            foreach (var client in Clients)
                client.Dispose();
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            }
            return Task.FromResult(respond(request));
        }
    }
}
