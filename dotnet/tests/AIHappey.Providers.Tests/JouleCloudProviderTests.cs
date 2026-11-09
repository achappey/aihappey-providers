using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.JouleCloud;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class JouleCloudProviderTests
{
    private const string Models = """
        {"data":[{"id":"org/chat","owned_by":"org","context_length":4096},{"id":"draw","type":"image"},
        {"id":"talk","type":"speech"},{"id":"listen","type":"transcription"},{"id":"embed","type":"embedding"}]}
        """;
    private const string ChatReply = """
        {"id":"chat_1","object":"chat.completion","created":1,"model":"org/chat",
        "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
        "usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3},"future":{"large":9007199254740993}}
        """;
    private const string ChatEvents = """
        : keepalive

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"org/chat","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}],"future":{"large":9007199254740993}}

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"org/chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

        data: [DONE]

        """;
    private const string Extra = """{"future":{"nested":[true,null,{},[]],"large":9007199254740993},"temperature":0.2}""";

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(() => "jc_test"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<JouleCloudProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "joulecloud"));
    }

    [Fact]
    public async Task Models_are_dynamic_authenticated_and_include_auto()
    {
        var calls = 0;
        using var provider = Provider((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("https://api.greenjoules.cloud/v1/models", http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
            Assert.Equal("jc_test", http.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(calls++ == 0 ? Models : """{"data":[{"id":"new-model"}]}"""));
        });
        var first = (await provider.ListModels()).ToArray();
        Assert.Equal(6, first.Length);
        Assert.Equal(4096, first.Single(m => m.Id == "joulecloud/org/chat").ContextWindow);
        Assert.Equal("speech", first.Single(m => m.Id == "joulecloud/talk").Type);
        var second = (await provider.ListModels()).ToArray();
        Assert.Equal(["joulecloud/new-model", "joulecloud/auto"], second.Select(m => m.Id));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"unknown\"")]
    [InlineData("{}")]
    public async Task Non_numeric_context_lengths_are_ignored(string context)
    {
        using var provider = Provider((_, _) => Task.FromResult(Json(
            """{"data":[{"id":"chat","context_length":""" + context + "}]}")));
        var models = await provider.ListModels();
        Assert.Null(models.Single(model => model.Id == "joulecloud/chat").ContextWindow);
    }

    [Fact]
    public async Task Concurrent_keys_are_request_local_even_on_shared_transport()
    {
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new AsyncLocal<string?>();
        var seen = new List<string?>();
        var transport = new Factory(async (http, ct) =>
        {
            var before = http.Headers.Authorization?.Parameter;
            if (before == "jc_first")
            {
                firstArrived.SetResult();
                await releaseFirst.Task.WaitAsync(ct);
            }
            Assert.Equal(before, http.Headers.Authorization?.Parameter);
            lock (seen) seen.Add(before);
            return Json(Models);
        });
        using var provider = new JouleCloudProvider(new Keys(() => current.Value), transport);
        current.Value = "jc_first";
        var first = provider.ListModels();
        await firstArrived.Task;
        current.Value = "jc_second";
        await provider.ListModels();
        releaseFirst.SetResult();
        await first;
        Assert.Equal(["jc_first", "jc_second"], seen.Order());
        current.Value = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListModels());
        using var other = new HttpClient(transport.CreateHandler("other"), disposeHandler: false);
        Assert.Null(other.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_chat_uses_standard_route_and_preserves_metadata(bool streaming)
    {
        using var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("/v1/chat/completions", http.RequestUri!.AbsolutePath);
            Assert.Equal("jc_test", http.Headers.Authorization?.Parameter);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("auto", body.GetProperty("model").GetString());
            Assert.False(body.TryGetProperty("joulecloud", out _));
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.True(JsonElement.DeepEquals(Element(Extra).GetProperty("future"), body.GetProperty("future")));
            return streaming ? Events(ChatEvents) : Json(ChatReply);
        });
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>(
            """{"model":"joulecloud/auto","messages":[{"role":"user","content":"Hi"}],"joulecloud":""" + Extra + "}")!;
        options.Headers = new() { ["Authorization"] = "******" };
        var before = JsonSerializer.Serialize(options);
        if (streaming)
        {
            var updates = await Collect(provider.CompleteChatStreamingAsync(options));
            Assert.Equal(2, updates.Count);
            Assert.Equal(9007199254740993L, updates[0].AdditionalProperties!["future"].GetProperty("large").GetInt64());
            Assert.Equal(3, updates[1].Usage!.TotalTokens);
        }
        else
        {
            var result = await provider.CompleteChatAsync(options);
            Assert.Equal("joulecloud/org/chat", result.Model);
            Assert.Equal(9007199254740993L, result.AdditionalProperties!["future"].GetProperty("large").GetInt64());
        }
        Assert.Equal(before, JsonSerializer.Serialize(options));
    }

    [Theory]
    [InlineData("unified")]
    [InlineData("responses")]
    [InlineData("messages")]
    [InlineData("vercel")]
    public async Task Conversation_surfaces_use_chat_adapters(string surface)
    {
        var posts = 0;
        using var provider = Provider(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get)
                return Json(Models);
            posts++;
            Assert.Equal("/v1/chat/completions", http.RequestUri!.AbsolutePath);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("auto", body.GetProperty("model").GetString());
            Assert.Equal(9007199254740993L, body.GetProperty("future").GetProperty("large").GetInt64());
            return body.TryGetProperty("stream", out var stream) && stream.GetBoolean() ? Events(ChatEvents) : Json(ChatReply);
        });
        if (surface == "unified")
        {
            var result = await provider.ExecuteUnifiedAsync(Request("auto"));
            Assert.Contains(result.Output!.Items!.SelectMany(i => i.Content!), p => p is AITextContentPart { Text: "Hello" });
            Assert.NotEmpty(await Collect(provider.StreamUnifiedAsync(Request("auto"))));
        }
        else if (surface == "responses")
        {
            var request = JsonSerializer.Deserialize<ResponseRequest>(
                """{"model":"auto","input":"Hi","joulecloud":""" + Extra + "}")!;
            Assert.NotNull(await provider.ResponsesAsync(request));
            Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(request)));
        }
        else if (surface == "messages")
        {
            var request = JsonSerializer.Deserialize<MessagesRequest>(
                """{"model":"auto","max_tokens":20,"messages":[{"role":"user","content":"Hi"}],"joulecloud":""" + Extra + "}")!;
            Assert.NotNull(await provider.MessagesAsync(request, []));
            Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(request, [])));
        }
        else
        {
            var request = JsonSerializer.Deserialize<ChatRequest>(
                """{"model":"auto","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}],"providerMetadata":{"joulecloud":""" + Extra + "}}")!;
            Assert.NotEmpty(await Collect(provider.StreamAsync(request)));
        }
        Assert.True(posts > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Embeddings_use_shared_compatibility_and_forward_options(bool native)
    {
        using var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("/v1/embeddings", http.RequestUri!.AbsolutePath);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("embed", body.GetProperty("model").GetString());
            Assert.Equal(9007199254740993L, body.GetProperty("future").GetProperty("large").GetInt64());
            return Json("""{"object":"list","model":"embed","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],"usage":{"prompt_tokens":2,"total_tokens":2}}""");
        });
        if (native)
            Assert.Equal("joulecloud/embed", (await provider.OpenAIEmbeddingRequestAsync(
                JsonSerializer.Deserialize<OpenAIEmbeddingRequest>("""{"model":"joulecloud/embed","input":"Hi","joulecloud":""" + Extra + "}")!)).Model);
        else
            Assert.Equal(2, (await provider.EmbeddingRequestAsync(new EmbeddingRequest
            {
                Model = "joulecloud/embed", Values = ["Hi"], ProviderOptions = new() { ["joulecloud"] = Element(Extra) }
            })).Usage!.Tokens);
    }

    [Theory]
    [InlineData("image", false)]
    [InlineData("image", true)]
    [InlineData("speech", false)]
    [InlineData("speech", true)]
    [InlineData("transcription", false)]
    [InlineData("transcription", true)]
    public async Task Native_and_unified_media_route_and_preserve_options(string type, bool unified)
    {
        var posts = 0;
        using var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("jc_test", http.Headers.Authorization?.Parameter);
            if (http.Method == HttpMethod.Get)
                return Json(Models);
            posts++;
            if (type == "transcription")
            {
                Assert.Equal("/v1/audio/transcriptions", http.RequestUri!.AbsolutePath);
                var form = Assert.IsType<MultipartFormDataContent>(http.Content);
                var future = Assert.Single(form, f => f.Headers.ContentDisposition!.Name!.Trim('"') == "future");
                Assert.True(JsonElement.DeepEquals(Element(Extra).GetProperty("future"), Element(await future.ReadAsStringAsync(ct))));
                Assert.Contains(form, f => f.Headers.ContentDisposition!.Name!.Trim('"') == "file");
                return Json("""{"text":"Heard","language":"en","future":{"preserved":true}}""");
            }
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal(9007199254740993L, body.GetProperty("future").GetProperty("large").GetInt64());
            if (type == "image")
            {
                Assert.Equal("/v1/images/generations", http.RequestUri!.AbsolutePath);
                Assert.False(body.GetProperty("stream").GetBoolean());
                return Json("""{"created":1,"data":[{"b64_json":"aW1hZ2U="}],"future":{"preserved":true}}""");
            }
            Assert.Equal("/v1/audio/speech", http.RequestUri!.AbsolutePath);
            Assert.Equal("audio", body.GetProperty("stream_format").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("audio", Encoding.UTF8, "audio/mpeg") };
        });
        if (unified)
        {
            var model = type switch { "image" => "draw", "speech" => "talk", _ => "listen" };
            var request = Request(model, type == "transcription");
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.NotEmpty(response.Output!.Items!);
            Assert.NotEmpty(await Collect(provider.StreamUnifiedAsync(request)));
            Assert.Equal(2, posts);
        }
        else if (type == "image")
        {
            var response = await provider.OpenAIImageGenerationRequestAsync(JsonSerializer.Deserialize<OpenAIImageGenerationRequest>(
                """{"model":"draw","prompt":"Hi","joulecloud":""" + Extra + "}")!);
            Assert.Equal("aW1hZ2U=", Assert.Single(response.Data!).B64Json);
        }
        else if (type == "speech")
        {
            var response = await provider.OpenAISpeechRequestAsync(JsonSerializer.Deserialize<AudioSpeechRequest>(
                """{"model":"talk","input":"Hi","voice":"alloy","joulecloud":""" + Extra + "}")!);
            Assert.Equal("audio/mpeg", response.MimeType);
        }
        else
        {
            using var audio = new MemoryStream(Encoding.UTF8.GetBytes("audio"));
            var request = JsonSerializer.Deserialize<OpenAITranscriptionRequest>(
                """{"model":"listen","joulecloud":""" + Extra + "}")!;
            request.File = new FormFile(audio, 0, audio.Length, "file", "audio.wav")
                { Headers = new HeaderDictionary(), ContentType = "audio/wav" };
            Assert.Equal("Heard", (await provider.OpenAITranscriptionRequestAsync(request)).Text);
        }
    }

    [Fact]
    public async Task Unified_image_edits_and_native_audio_streaming_are_not_claimed()
    {
        using var provider = Provider((_, _) => throw new InvalidOperationException("Unexpected network request."));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIImageEditRequestAsync(new()));
        Assert.Throws<NotSupportedException>(() => provider.OpenAISpeechStreamingAsync(new()));
        Assert.Throws<NotSupportedException>(() => provider.OpenAITranscriptionStreamingAsync(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "draw", Prompt = "edit", Files = [new ImageFile { Data = "aW1hZ2U=", MediaType = "image/png" }]
        }));
    }

    [Fact]
    public async Task Generic_transcription_preserves_native_options_and_timing()
    {
        using var provider = Provider(async (http, ct) =>
        {
            Assert.Equal("/v1/audio/transcriptions", http.RequestUri!.AbsolutePath);
            var form = Assert.IsType<MultipartFormDataContent>(http.Content);
            var fields = new Dictionary<string, string>();
            foreach (var field in form)
                fields[field.Headers.ContentDisposition!.Name!.Trim('"')] = await field.ReadAsStringAsync(ct);
            Assert.Equal("listen", fields["model"]);
            Assert.Equal("verbose_json", fields["response_format"]);
            Assert.Equal("fr", fields["language"]);
            Assert.True(JsonElement.DeepEquals(Element(Extra).GetProperty("future"), Element(fields["future"])));
            return Json("""{"text":"Bonjour","language":"fr","duration":1.5,"segments":[{"id":0,"seek":0,"start":0,"end":1.5,"text":"Bonjour","tokens":[],"temperature":0,"avg_logprob":0,"compression_ratio":1,"no_speech_prob":0}]}""");
        });
        var result = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "joulecloud/listen", MediaType = "audio/wav", Audio = "data:audio/wav;base64,YXVkaW8=",
            ProviderOptions = new() { ["joulecloud"] = Element("""{"language":"fr","future":""" + Element(Extra).GetProperty("future").GetRawText() + "}") }
        });
        Assert.Equal("Bonjour", result.Text);
        Assert.Equal("fr", result.Language);
        Assert.Equal(1.5, result.DurationInSeconds);
        Assert.Equal(1.5, Assert.Single(result.Segments).EndSecond);
        Assert.Equal(JsonValueKind.Object, result.ProviderMetadata!["joulecloud"].ValueKind);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("srt")]
    [InlineData("vtt")]
    [InlineData("diarized_json")]
    public async Task Generic_transcription_preserves_requested_response_format(string format)
    {
        using var provider = Provider(async (http, ct) =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(http.Content);
            var field = Assert.Single(form, item => item.Headers.ContentDisposition!.Name!.Trim('"') == "response_format");
            Assert.Equal(format, await field.ReadAsStringAsync(ct));
            if (format == "diarized_json")
                return Json("""{"text":"Heard","duration":1.5,"segments":[{"id":"s1","speaker":"speaker_1","start":0,"end":1.5,"text":"Heard"}]}""");
            return format == "json" ? Json("""{"text":"Heard"}""") : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("Heard", Encoding.UTF8, "text/plain") };
        });
        var result = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "listen", MediaType = "audio/wav", Audio = "YXVkaW8=",
            ProviderOptions = new() { ["joulecloud"] = JsonSerializer.SerializeToElement(new { response_format = format }) }
        });
        Assert.Equal("Heard", result.Text);
        if (format == "diarized_json")
        {
            Assert.Equal(1.5, result.DurationInSeconds);
            Assert.Equal(1.5, Assert.Single(result.Segments).EndSecond);
        }
        else
        {
            Assert.Empty(result.Segments);
            Assert.Null(result.DurationInSeconds);
        }
    }

    [Fact]
    public async Task Generic_speech_honors_raw_provider_audio_options()
    {
        using var provider = Provider(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("wav", body.GetProperty("response_format").GetString());
            Assert.Equal("voice-1", body.GetProperty("voice").GetString());
            Assert.Equal(1.2, body.GetProperty("speed").GetDouble());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("audio", Encoding.UTF8, "audio/wav") };
        });
        var result = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "talk", Text = "Hi", OutputFormat = "mp3",
            ProviderOptions = new() { ["joulecloud"] = Element("""{"voice":"voice-1","response_format":"wav","speed":1.2}""") }
        });
        Assert.Equal("wav", result.Audio.Format);
        Assert.Equal("audio/wav", result.Audio.MimeType);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("speech")]
    public async Task Unified_media_preserves_scoped_formats_against_adapter_defaults(string type)
    {
        var calls = 0;
        using var provider = Provider(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get)
                return Json(Models);
            calls++;
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            if (type == "image")
            {
                Assert.Equal("/v1/images/generations", http.RequestUri!.AbsolutePath);
                Assert.Equal("jpeg", body.GetProperty("output_format").GetString());
                return Json("""{"created":1,"data":[{"b64_json":"aW1hZ2U="}]}""");
            }
            Assert.Equal("/v1/audio/speech", http.RequestUri!.AbsolutePath);
            Assert.Equal("wav", body.GetProperty("response_format").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("audio", Encoding.UTF8, "audio/wav") };
        });
        var request = Request(type == "image" ? "draw" : "talk");
        request.Metadata!["joulecloud"] = type == "image"
            ? Element("""{"output_format":"jpeg"}""")
            : Element("""{"response_format":"wav"}""");
        var expectedMimeType = type == "image" ? "image/jpeg" : "audio/wav";
        Assert.Contains(expectedMimeType, JsonSerializer.Serialize(await provider.ExecuteUnifiedAsync(request)));
        Assert.Contains(expectedMimeType, JsonSerializer.Serialize(await Collect(provider.StreamUnifiedAsync(request))));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Provider_errors_and_cancellation_propagate()
    {
        using var provider = Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("invalid key") }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new() { Model = "auto", Messages = [] }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancelled.Token));
    }

    private static AIRequest Request(string model, bool audio = false) => new()
    {
        ProviderId = "joulecloud", Model = model,
        Metadata = new() { ["joulecloud"] = Element(Extra) },
        Input = new AIInput { Items = [new AIInputItem
        {
            Role = "user", Content = audio
                ? [new AIFileContentPart { Type = "file", MediaType = "audio/wav", Data = "data:audio/wav;base64,YXVkaW8=" }]
                : [new AITextContentPart { Type = "text", Text = "Hi" }]
        }] }
    };

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static JouleCloudProvider Provider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Keys(() => "jc_test"), new Factory(send));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Events(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }
    private sealed class Keys(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string provider) { Assert.Equal("joulecloud", provider); return key(); }
    }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpMessageHandlerFactory
    {
        private readonly HttpMessageHandler _handler = new Handler(send);
        public HttpMessageHandler CreateHandler(string name) => _handler;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
