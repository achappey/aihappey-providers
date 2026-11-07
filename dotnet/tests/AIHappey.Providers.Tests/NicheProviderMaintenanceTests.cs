using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Astica;
using AIHappey.Core.Providers.Audixa;
using AIHappey.Core.Providers.Brave;
using AIHappey.Core.Providers.FishAudio;
using AIHappey.Core.Providers.AIHorde;
using AIHappey.Core.Providers.JigsawStack;
using AIHappey.Core.Providers.MurfAI;
using AIHappey.Core.Providers.Tavily;
using AIHappey.Core.Providers.Valyu;
using AIHappey.Core.Providers.ZeroEntropy;
using AIHappey.Core.Models;
using AIHappey.Responses;
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

    [Fact]
    public async Task Jigsawstack_authentication_is_attached_to_each_request_not_the_shared_client()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/v1/ai/summary", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-api-key", request.Headers.GetValues("x-api-key").Single());
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Summarize this", payload.RootElement.GetProperty("text").GetString());
            Assert.Equal("text", payload.RootElement.GetProperty("type").GetString());
            return JsonResponse("""{"summary":"A summary"}""");
        });
        var provider = new JigsawStackProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "summary/text",
            Messages = [new() { Role = "user", Content = "Summarize this" }]
        }, CancellationToken.None);

        Assert.Equal("A summary", Assert.Single(response.Choices).Message.Content);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Brave_answers_authentication_is_request_scoped()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/res/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-api-key", request.Headers.GetValues("x-subscription-token").Single());
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var messages = payload.RootElement.GetProperty("messages");
            Assert.Equal(1, messages.GetArrayLength());
            Assert.Equal("user", messages[0].GetProperty("role").GetString());
            Assert.Equal("What is Brave Search?", messages[0].GetProperty("content").GetString());
            return JsonResponse("""{"id":"answer-1","object":"chat.completion","created":1,"model":"brave","choices":[{"index":0,"message":{"role":"assistant","content":"A search engine."},"finish_reason":"stop"}]}""");
        });
        var provider = new BraveProvider(
            new TestKeyResolver(),
            new AIHappey.Core.AI.AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "brave/answers",
            Messages =
            [
                new() { Role = "system", Content = "Answer concisely." },
                new() { Role = "user", Content = "What is Brave Search?" }
            ]
        });

        Assert.Equal("A search engine.", Assert.Single(response.Choices).Message.Content);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Valyu_answer_parses_documented_content_events_and_uses_request_scoped_authentication()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal("/v1/answer", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-api-key", request.Headers.GetValues("X-API-Key").Single());
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("user: What is Valyu?", payload.RootElement.GetProperty("query").GetString());
            Assert.Equal("all", payload.RootElement.GetProperty("search_type").GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: content\n" +
                    "data: {\"content\":\"Valyu answer\"}\n\n" +
                    "event: metadata\n" +
                    "data: {\"ai_usage\":{\"input_tokens\":3,\"output_tokens\":2}}\n\n" +
                    "data: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });
        var provider = new ValyuProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "answer/all",
            Messages = [new() { Role = "user", Content = "What is Valyu?" }]
        });

        Assert.Equal("Valyu answer", Assert.Single(response.Choices).Message.Content);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Valyu_answer_surfaces_documented_stream_errors()
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "event: error\n" +
                "data: {\"error\":\"Streaming failed\"}\n\n",
                Encoding.UTF8,
                "text/event-stream")
        }));
        var provider = new ValyuProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "answer/all",
            Messages = [new() { Role = "user", Content = "What is Valyu?" }]
        }));

        Assert.Contains("Streaming failed", error.Message);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task AIHorde_responses_use_the_documented_chat_completions_endpoint()
    {
        var requestCount = 0;
        using var client = CreateClient(async request =>
        {
            requestCount++;
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return JsonResponse("""{"data":[{"id":"test-model"}]}""");

            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Hello", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return JsonResponse("""{"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"Hello back"},"finish_reason":"stop"}]}""");
        });
        var provider = new AIHordeProvider(
            new TestKeyResolver(),
            new AIHappey.Core.AI.AsyncCacheHelper(),
            new TestHttpClientFactory(client));

        var response = await provider.ResponsesAsync(new ResponseRequest
        {
            Model = "aihorde/test-model",
            Input = "Hello"
        });

        Assert.NotNull(response);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task AIHorde_streaming_responses_use_the_documented_chat_completions_endpoint()
    {
        var requestCount = 0;
        using var client = CreateClient(async request =>
        {
            requestCount++;
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return JsonResponse("""{"data":[{"id":"test-model"}]}""");

            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello back\"},\"finish_reason\":null}]}\n\n" +
                    "data: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });
        var provider = new AIHordeProvider(
            new TestKeyResolver(),
            new AIHappey.Core.AI.AsyncCacheHelper(),
            new TestHttpClientFactory(client));
        var updates = new List<AIHappey.Responses.Streaming.ResponseStreamPart>();

        await foreach (var update in provider.ResponsesStreamingAsync(new ResponseRequest
        {
            Model = "aihorde/test-model",
            Input = "Hello"
        }))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task Audixa_api_authentication_is_request_scoped_and_not_sent_to_the_audio_host()
    {
        using var client = CreateClient(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v3/voices")
            {
                Assert.Equal("test-api-key", request.Headers.GetValues("x-api-key").Single());
                return JsonResponse("""{"voices":[{"voice_id":"voice-1","model":"base"}],"length":1}""");
            }

            if (request.RequestUri.AbsolutePath == "/v3/tts")
            {
                Assert.Equal("test-api-key", request.Headers.GetValues("x-api-key").Single());
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("Hello", payload.RootElement.GetProperty("text").GetString());
                Assert.Equal("base", payload.RootElement.GetProperty("model").GetString());
                Assert.Equal("voice-1", payload.RootElement.GetProperty("voice_id").GetString());
                Assert.Equal("preserved", payload.RootElement.GetProperty("future_option").GetString());
                return JsonResponse("""{"generation_id":"generation-1","status":"COMPLETED","audio_url":"https://audio.example/generated.wav"}""");
            }

            Assert.Equal("audio.example", request.RequestUri.Host);
            Assert.False(request.Headers.Contains("x-api-key"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var provider = new AudixaProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var models = await provider.ListModels(CancellationToken.None);
        Assert.Contains(models, model => model.Id == "audixa/base/voice-1");

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "audixa/base/voice-1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["audixa"] = JsonDocument.Parse("""{"future_option":"preserved"}""").RootElement.Clone()
            }
        });

        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Astica_streaming_speech_uses_documented_raw_audio_and_preserves_provider_options()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("voice.astica.ai", request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("X-API-Key"));
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());

            if (request.RequestUri.AbsolutePath == "/api/voice_list")
            {
                Assert.Equal("test-api-key", payload.RootElement.GetProperty("tkn").GetString());
                return JsonResponse("""{"voices":[{"voice":"expressive_ava","name":"Ava"}]}""");
            }

            Assert.Equal("/api/tts", request.RequestUri.AbsolutePath);
            Assert.Contains("*/*", request.Headers.Accept.Select(value => value.MediaType));
            Assert.Equal("test-api-key", payload.RootElement.GetProperty("tkn").GetString());
            Assert.Equal("Hello there", payload.RootElement.GetProperty("text").GetString());
            Assert.Equal("expressive_ava", payload.RootElement.GetProperty("voice").GetString());
            Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
            Assert.True(payload.RootElement.GetProperty("timestamps").GetBoolean());
            Assert.Equal("warm", payload.RootElement.GetProperty("prompt").GetString());
            Assert.True(payload.RootElement.GetProperty("future_option").GetProperty("enabled").GetBoolean());

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Content.Headers.ContentType = new("audio/wav");
            return response;
        });
        var provider = new AsticaProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        await provider.ListModels();
        var parts = new List<StreamingSpeechPart>();
        await foreach (var part in provider.SpeechStreamingAsync(new StreamingSpeechRequest
        {
            Model = "astica/expressive_ava",
            Text = [new() { Text = "Hello " }, new() { Text = "there" }],
            ProviderOptions = new()
            {
                ["astica"] = JsonDocument.Parse("""{"timestamps":true,"prompt":"warm","future_option":{"enabled":true}}""")
                    .RootElement.Clone()
            },
            IncludeRawChunks = true
        }))
        {
            parts.Add(part);
        }

        Assert.Single(parts.OfType<SpeechAudioPart>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), Assert.Single(parts.OfType<SpeechAudioPart>()).Audio);
        Assert.Equal("audio/wav", Assert.Single(parts.OfType<SpeechAudioPart>()).AudioFormat.Type);
        Assert.Single(parts.OfType<SpeechRawPart>());
        Assert.Single(parts.OfType<SpeechStreamStartPart>());
        Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.Empty(client.DefaultRequestHeaders);
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
