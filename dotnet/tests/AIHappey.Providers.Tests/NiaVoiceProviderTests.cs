using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.NiaVoice;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class NiaVoiceProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    public async Task Chat_maps_native_history_and_usage_without_double_counting_cache(int? cached)
    {
        using var client = Client(async request =>
        {
            Assert.Equal("https://niavoices.com/v1/chat", request.RequestUri!.ToString());
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("nia-chat-1", payload.GetProperty("model").GetString());
            Assert.Equal("hello", payload.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.Equal("system", payload.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal(0.3, payload.GetProperty("temperature").GetDouble());
            Assert.True(payload.GetProperty("future").GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("explicit_null").ValueKind);
            Assert.False(payload.GetProperty("stream").GetBoolean());
            return Json(JsonSerializer.Serialize(new
            {
                reply = "Hi!", model = "nia-chat-1",
                usage = new { input_tokens = 12, output_tokens = 3, cached_tokens = cached }
            }));
        });
        var provider = Provider(client);
        var response = await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"niavoice/nia-chat-1","temperature":0.3,
             "messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"hello"}],
             "niavoice":{"future":{"enabled":true},"explicit_null":null}}
            """)!);

        Assert.Equal("Hi!", response.Choices.Single().Message.Content);
        Assert.Equal(15, response.Usage!.TotalTokens);
        Assert.Equal(cached, response.Usage.PromptTokensDetails?.CachedTokens);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Translation_stream_uses_native_options_and_returns_text_events()
    {
        using var client = Client(async request =>
        {
            Assert.Equal("/v1/translate", request.RequestUri!.AbsolutePath);
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("Good morning", payload.GetProperty("text").GetString());
            Assert.Equal("es", payload.GetProperty("target_language").GetString());
            Assert.Equal("en", payload.GetProperty("source_language").GetString());
            Assert.False(payload.TryGetProperty("model", out _));
            return Json("""{"translation":"Buenos días","sourceLanguage":"English","targetLanguageCode":"es","model":"nia-translate-1"}""");
        });
        var events = new List<AIStreamEvent>();
        await foreach (var part in Provider(client).StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "niavoice", Model = "niavoice/nia-translate-1", Input = new() { Text = "Good morning" },
            Metadata = new() { ["niavoice"] = JsonSerializer.SerializeToElement(new { target_language = "es", source_language = "en" }) }
        }))
            events.Add(part);
        Assert.Equal(["text-start", "text-delta", "text-end", "finish"], events.Select(part => part.Event.Type));
        Assert.Equal("Buenos días", Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("responses")]
    public async Task Text_adapters_preserve_native_options(string endpoint)
    {
        using var client = Client(async request =>
        {
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("hello", payload.GetProperty("message").GetString());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
            Assert.False(payload.TryGetProperty("niavoice", out _));
            return Json("""{"reply":"Hi","model":"nia-chat-1"}""");
        });
        var provider = Provider(client);
        const string body = """
            {"model":"niavoice/nia-chat-1","messages":[{"role":"user","content":"hello"}],
             "input":"hello","max_tokens":20,"niavoice":{"message":"hello","future_null":null}}
            """;
        if (endpoint == "messages")
            await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(body)!, []);
        else
            await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(body)!);
    }

    [Fact]
    public async Task Speech_preserves_native_persona_options_and_maps_voice_language_and_format()
    {
        using var client = Client(async request =>
        {
            Assert.Equal("/v1/speech", request.RequestUri!.AbsolutePath);
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("ta", payload.GetProperty("language").GetString());
            Assert.Equal("nia-maya-2", payload.GetProperty("voice").GetString());
            Assert.Equal("p_example", payload.GetProperty("persona_id").GetString());
            Assert.True(payload.GetProperty("future_option").GetBoolean());
            Assert.False(payload.TryGetProperty("model", out _));
            Assert.Equal("mp3", payload.GetProperty("format").GetString());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("audio/mpeg");
            response.Headers.Add("X-Nia-Sample-Rate", "16000");
            return response;
        });
        var response = await Provider(client).SpeechRequest(new SpeechRequest
        {
            Model = "niavoice/speech/ta/nia-maya-1", Text = "text", Voice = "nia-maya-2", OutputFormat = "mp3",
            ProviderOptions = new() { ["niavoice"] = JsonSerializer.SerializeToElement(new { persona_id = "p_example", future_option = true }) }
        });
        Assert.Equal("AQID", response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Contains(response.Response.Headers!, pair => pair.Key.Equals("X-Nia-Sample-Rate", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Transcription_uploads_audio_and_native_fields_and_converts_duration()
    {
        using var client = Client(async request =>
        {
            Assert.Equal("/v1/transcribe", request.RequestUri!.AbsolutePath);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var fields = form.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal(new byte[] { 1, 2, 3 }, await fields["file"].ReadAsByteArrayAsync());
            Assert.Equal("audio/wav", fields["file"].Headers.ContentType!.MediaType);
            Assert.Equal("ml", await fields["language"].ReadAsStringAsync());
            Assert.Equal("""{"enabled":true}""", await fields["future_option"].ReadAsStringAsync());
            Assert.False(fields.ContainsKey("model"));
            return Json("""{"text":"transcript","language":"ml","durationMs":3200,"model":"nia-scribe-1"}""");
        });
        var response = await Provider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "niavoice/nia-scribe-1", Audio = "data:audio/wav;base64,AQID", MediaType = "audio/wav",
            ProviderOptions = new() { ["niavoice"] = JsonSerializer.SerializeToElement(new { language = "ml", future_option = new { enabled = true } }) }
        });
        Assert.Equal("transcript", response.Text);
        Assert.Equal(3.2, response.DurationInSeconds);
        Assert.Equal("nia-scribe-1", response.ProviderMetadata!["niavoice"].GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_transcription_preserves_language_hint(bool streaming)
    {
        using var client = Client(async request =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var language = Assert.Single(form, part => part.Headers.ContentDisposition!.Name!.Trim('"') == "language");
            Assert.Equal("ml", await language.ReadAsStringAsync());
            return Json("""{"text":"transcript","language":"ml","durationMs":1000}""");
        });
        var request = new AIRequest
        {
            ProviderId = "niavoice", Model = "niavoice/nia-scribe-1",
            Input = new() { Items = [new()
            {
                Role = "user", Content = [new AIFileContentPart { Type = "file", MediaType = "audio/wav", Data = "AQID" }]
            }] },
            Metadata = new() { ["niavoice"] = new { language = "ml" } }
        };
        var provider = Provider(client);
        if (streaming)
        {
            var parts = new List<AIStreamEvent>();
            await foreach (var part in provider.StreamUnifiedAsync(request)) parts.Add(part);
            Assert.Contains(parts, part => part.Event.Data is AITextDeltaEventData { Delta: "transcript" });
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal("transcript", Assert.IsType<AITextContentPart>(response.Output!.Items![0].Content![0]).Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_speech_keeps_native_format_and_persona(bool streaming)
    {
        using var client = Client(async request =>
        {
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("wav", payload.GetProperty("format").GetString());
            Assert.Equal("p_example", payload.GetProperty("persona_id").GetString());
            Assert.Equal("ml", payload.GetProperty("language").GetString());
            Assert.False(payload.TryGetProperty("niavoice", out _));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("audio/wav");
            return response;
        });
        var request = new AIRequest
        {
            ProviderId = "niavoice", Model = "niavoice/speech",
            Input = new() { Items = [new()
            {
                Role = "user", Content = [new AITextContentPart { Type = "text", Text = "hello" }]
            }] },
            Metadata = new() { ["niavoice"] = new { niavoice = new { language = "ml", format = "wav", persona_id = "p_example" } } }
        };
        var provider = Provider(client);
        if (streaming)
        {
            var parts = new List<AIStreamEvent>();
            await foreach (var part in provider.StreamUnifiedAsync(request)) parts.Add(part);
            Assert.Contains(parts, part => part.Event.Type == "tool-output-available");
            Assert.DoesNotContain(parts, part => part.Event.Type == "tool-output-error");
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.IsType<AIToolCallContentPart>(response.Output!.Items![0].Content![0]);
        }
    }

    [Fact]
    public async Task Embeddings_map_native_shape_and_usage_and_support_openai_base64()
    {
        using var client = Client(async request =>
        {
            Assert.Equal("/v1/embeddings", request.RequestUri!.AbsolutePath);
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal(2, payload.GetProperty("input").GetArrayLength());
            Assert.True(payload.GetProperty("future_option").GetBoolean());
            Assert.False(payload.TryGetProperty("model", out _));
            return Json("""{"embeddings":[[0.5,-1],[1,0]],"dimensions":2,"model":"nia-embed-1","usage":{"input_tokens":24}}""");
        });
        var provider = Provider(client);
        var response = await provider.EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "niavoice/nia-embed-1", Values = ["a", "b"],
            ProviderOptions = new() { ["niavoice"] = JsonSerializer.SerializeToElement(new { future_option = true }) }
        });
        Assert.Equal(24, response.Usage!.Tokens);
        Assert.Equal(new double[] { 0.5, -1 }, response.Embeddings.First());
        var openai = await provider.OpenAIEmbeddingRequestAsync(new OpenAIEmbeddingRequest
        {
            Model = "niavoice/nia-embed-1", Input = JsonSerializer.SerializeToElement(new[] { "a", "b" }),
            EncodingFormat = "base64", AdditionalProperties = new() { ["future_option"] = JsonSerializer.SerializeToElement(true) }
        });
        Assert.Equal(24, openai.Usage.TotalTokens);
        Assert.Equal("AAAAPwAAgL8=", openai.Data.First().Embedding.GetString());
    }

    [Theory]
    [InlineData("nia-realtime-1", "session")]
    [InlineData("nia-conversational-en", "conversational")]
    [InlineData("nia-realtime-tts-1", "tts")]
    [InlineData("nia-realtime-stt-1", "stt")]
    [InlineData("nia-live-stt-auto-1", "stt/auto")]
    [InlineData("nia-live-stt-lang-1", "stt/lang")]
    public async Task Realtime_routes_all_native_handshakes_and_preserves_connection_details(string model, string route)
    {
        using var client = Client(async request =>
        {
            Assert.Equal("/v1/realtime/" + route, request.RequestUri!.AbsolutePath);
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("ml", payload.GetProperty("language").GetString());
            Assert.Equal("external", payload.GetProperty("mode").GetString());
            Assert.Equal(120, payload.GetProperty("ttl_seconds").GetInt32());
            return Json("""
                {"token":"nia_rt_test","expires_at":"2026-11-12T14:00:00Z","expires_in":120,
                 "session_id":"rts_test","url":"wss://api.niavoices.com/api/realtime/transcribe/lang?token=nia_rt_test&language=ml",
                 "ice_servers":[{"urls":"turn:relay.example.com","username":"u","credential":"c"}],
                 "settings":{"language":"ml"},"future_option":{"enabled":true}}
                """);
        });
        var response = await Provider(client).GetRealtimeToken(new RealtimeRequest
        {
            Model = "niavoice/" + model,
            ProviderOptions = new() { ["niavoice"] = JsonSerializer.SerializeToElement(new { language = "ml", mode = "external", ttl_seconds = 120 }) }
        });
        Assert.Equal("nia_rt_test", response.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T14:00:00Z").ToUnixTimeSeconds(), response.ExpiresAt);
        var metadata = response.ProviderMetadata!["niavoice"];
        Assert.False(metadata.TryGetProperty("token", out _));
        Assert.Equal("c", metadata.GetProperty("ice_servers")[0].GetProperty("credential").GetString());
        Assert.Equal("ml", metadata.GetProperty("settings").GetProperty("language").GetString());
        Assert.True(metadata.GetProperty("future_option").GetProperty("enabled").GetBoolean());
        Assert.Contains("token=nia_rt_test", metadata.GetProperty("url").GetString());
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Voice_discovery_keeps_language_specific_ids_for_shared_voices()
    {
        using var client = Client(request =>
        {
            Assert.Equal("/v1/voices", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""
                {"languages":[
                  {"code":"ta","label":"Tamil","voices":[{"id":"nia-maya-1","label":"Maya"}]},
                  {"code":"ml","label":"Malayalam","voices":[{"id":"nia-maya-1","label":"Maya"}]}]}
                """));
        });
        var models = (await Provider(client).ListModels()).ToArray();
        Assert.Contains(models, model => model.Id == "niavoice/speech/ta/nia-maya-1");
        Assert.Contains(models, model => model.Id == "niavoice/speech/ml/nia-maya-1");
    }

    [Fact]
    public async Task Errors_include_status_and_provider_body()
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"type":"forbidden","message":"Missing speech scope"}}""")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).SpeechRequest(
            new SpeechRequest { Model = "speech", Text = "hello" }));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("Missing speech scope", error.Message);
    }

    [Fact]
    public async Task Invalid_native_options_and_unsupported_tools_fail_before_http()
    {
        using var client = Client(_ => throw new InvalidOperationException("No HTTP expected."));
        var provider = Provider(client);
        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "speech", Text = "hello", ProviderOptions = new() { ["niavoice"] = JsonSerializer.SerializeToElement(1) }
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "niavoice", Model = "nia-chat-1", Tools = [new() { Name = "test" }]
        }));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(65, 1)]
    [InlineData(1, 32001)]
    public async Task Embedding_limits_are_enforced_before_http(int count, int length)
    {
        using var client = Client(_ => throw new InvalidOperationException("No HTTP expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).EmbeddingRequestAsync(new EmbeddingRequest
        {
            Model = "nia-embed-1", Values = Enumerable.Repeat(new string('x', length), count)
        }));
    }

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<ProviderRegistry>().HasModelProvider("niavoice"));
    }

    private static NiaVoiceProvider Provider(HttpClient client) => new(new Keys(), new Clients(client));
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
