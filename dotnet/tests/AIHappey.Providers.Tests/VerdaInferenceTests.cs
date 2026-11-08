using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Verda;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class VerdaInferenceTests
{
    [Fact]
    public async Task ListModels_uses_the_project_scoped_inference_endpoint()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://inference.verda.com/v1/models", request.RequestUri!.ToString());
            AssertBearerAuth(request);
            return JsonResponse("""{"object":"list","data":[{"id":"model-one","owned_by":"verda"}]}""");
        });

        var models = await CreateProvider(client).ListModels();
        var model = Assert.Single(models);

        Assert.Equal("verda/model-one", model.Id);
        Assert.Equal("language", model.Type);
        Assert.Equal("verda", model.OwnedBy);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Chat_completions_uses_native_endpoint_and_keeps_stream_usage_enabled()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("https://inference.verda.com/v1/chat/completions", request.RequestUri!.ToString());
            AssertBearerAuth(request);
            var payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.True(payload.GetProperty("stream").GetBoolean());
            Assert.True(payload.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

            return ChatStreamResponse();
        });
        var provider = CreateProvider(client);
        var updates = new List<ChatCompletionUpdate>();

        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "model-one",
            Messages = [new() { Role = "user", Content = "Say hello" }]
        }))
            updates.Add(update);

        Assert.Equal(3, updates.Count);
        Assert.Equal("verda/model-one", updates[0].Model);

        var uiParts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(new ChatRequest
        {
            Model = "model-one",
            Messages = [new UIMessage
            {
                Id = "user-1",
                Role = Role.user,
                Parts = [new TextUIPart { Text = "Say hello" }]
            }]
        }))
            uiParts.Add(part);
        Assert.NotEmpty(uiParts);

        var messageParts = new List<MessageStreamPart>();
        await foreach (var part in provider.MessagesStreamingAsync(new MessagesRequest
        {
            Model = "model-one",
            MaxTokens = 16,
            Messages = [new() { Role = "user", Content = new MessagesContent("Say hello") }]
        }, []))
            messageParts.Add(part);
        Assert.NotEmpty(messageParts);

        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Responses_and_messages_use_their_native_or_unified_routes()
    {
        using var client = CreateClient(request =>
        {
            AssertBearerAuth(request);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/responses")
            {
                var payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.False(payload.GetProperty("stream").GetBoolean());
                return JsonResponse(
                    """{"id":"resp-1","object":"response","created_at":1,"status":"completed","model":"model-one","output":[{"type":"message","id":"msg-1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"hello","annotations":[]}]}]}""");
            }

            Assert.Equal("/v1/chat/completions", path);
            var chatPayload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.False(chatPayload.GetProperty("stream").GetBoolean());
            return ChatResponse();
        });
        var provider = CreateProvider(client);

        var response = await provider.ResponsesAsync(new ResponseRequest { Model = "model-one", Input = "Say hello" });
        Assert.Equal("resp-1", response.Id);
        var chat = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "model-one",
            Messages = [new() { Role = "user", Content = "Say hello" }]
        });
        Assert.NotNull(chat);

        var messages = await provider.MessagesAsync(new MessagesRequest
        {
            Model = "model-one",
            MaxTokens = 16,
            Messages = [new() { Role = "user", Content = new MessagesContent("Say hello") }]
        }, []);

        Assert.NotNull(messages);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Responses_streaming_uses_native_streaming_route()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("https://inference.verda.com/v1/responses", request.RequestUri!.ToString());
            AssertBearerAuth(request);
            Assert.True(ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .GetProperty("stream").GetBoolean());
            return ResponsesStreamResponse();
        });
        var parts = new List<ResponseStreamPart>();

        await foreach (var part in CreateProvider(client).ResponsesStreamingAsync(
                           new ResponseRequest { Model = "model-one", Input = "Say hello" }))
            parts.Add(part);

        Assert.Collection(parts,
            part => Assert.Equal("verda/model-one", Assert.IsType<ResponseCreated>(part).Response.Model),
            part => Assert.Equal("hello", Assert.IsType<ResponseOutputTextDelta>(part).Delta),
            part => Assert.Equal("verda/model-one", Assert.IsType<ResponseCompleted>(part).Response.Model));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Chat_method_selects_streaming_mode_and_preserves_native_properties(bool streaming, bool? requestedStream)
    {
        var providerOptions = ParseElement("""{"enabled":true,"values":[1,false,null,{}]}""");
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://inference.verda.com/v1/chat/completions", request.RequestUri!.ToString());
            AssertBearerAuth(request);
            Assert.Equal("chat-request", Assert.Single(request.Headers.GetValues("X-Request-Id")));
            Assert.Equal(streaming ? "text/event-stream" : "application/json", Assert.Single(request.Headers.Accept).MediaType);

            var payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
            Assert.Single(payload.EnumerateObject(), property => property.Name == "stream");
            Assert.Equal("model-one", payload.GetProperty("model").GetString());
            Assert.True(JsonElement.DeepEquals(providerOptions, payload.GetProperty("provider_option")));
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
            Assert.Equal("application", payload.GetProperty("metadata").GetProperty("verda").GetString());
            Assert.False(payload.TryGetProperty("headers", out _));
            return streaming ? ChatStreamResponse() : ChatResponse();
        });
        var provider = CreateProvider(client);
        var options = new ChatCompletionOptions
        {
            Model = "model-one",
            Messages = [new() { Role = "user", Content = "Say hello" }],
            Stream = requestedStream,
            Headers = new() { ["X-Request-Id"] = "chat-request" },
            Metadata = new() { ["verda"] = "application" },
            AdditionalProperties = new()
            {
                ["provider_option"] = providerOptions,
                ["future_null"] = ParseElement("null")
            }
        };

        if (streaming)
        {
            var updates = new List<ChatCompletionUpdate>();
            await foreach (var update in provider.CompleteChatStreamingAsync(options))
                updates.Add(update);
            Assert.Equal(3, updates.Count);
            Assert.Equal("verda/model-one", updates[0].Model);
        }
        else
        {
            var result = await provider.CompleteChatAsync(options);
            Assert.Equal("verda/model-one", result.Model);
        }

        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("X-Request-Id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_streaming_defaults_usage_without_overwriting_existing_options(bool? includeUsage)
    {
        var extraOption = ParseElement("""{"values":[false,null,1]}""");
        using var client = CreateClient(request =>
        {
            var payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var streamOptions = payload.GetProperty("stream_options");
            Assert.Equal(includeUsage ?? true, streamOptions.GetProperty("include_usage").GetBoolean());
            Assert.False(streamOptions.GetProperty("include_obfuscation").GetBoolean());
            Assert.True(JsonElement.DeepEquals(extraOption, streamOptions.GetProperty("future_option")));
            return ChatStreamResponse();
        });
        var streamOptions = new StreamOptions
        {
            IncludeUsage = includeUsage,
            IncludeObfuscation = false,
            AdditionalProperties = new() { ["future_option"] = extraOption }
        };
        var options = new ChatCompletionOptions
        {
            Model = "model-one",
            Messages = [new() { Role = "user", Content = "Say hello" }],
            StreamOptions = streamOptions
        };

        await foreach (var _ in CreateProvider(client).CompleteChatStreamingAsync(options))
        {
        }

        Assert.Same(streamOptions, options.StreamOptions);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Responses_method_selects_streaming_mode_and_preserves_native_properties(bool streaming, bool? requestedStream)
    {
        var providerOptions = ParseElement("""{"enabled":true,"values":[1,false,null,{}]}""");
        var streamOptions = ParseElement("""{"include_obfuscation":false,"future_option":{}}""");
        using var client = CreateClient(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://inference.verda.com/v1/responses", request.RequestUri!.ToString());
            AssertBearerAuth(request);
            Assert.Equal("responses-request", Assert.Single(request.Headers.GetValues("X-Request-Id")));
            Assert.Equal(streaming ? "text/event-stream" : "application/json", Assert.Single(request.Headers.Accept).MediaType);

            var payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
            Assert.Single(payload.EnumerateObject(), property => property.Name == "stream");
            Assert.Equal("model-one", payload.GetProperty("model").GetString());
            Assert.Equal("Say hello", payload.GetProperty("input").GetString());
            Assert.True(JsonElement.DeepEquals(providerOptions, payload.GetProperty("provider_option")));
            Assert.True(JsonElement.DeepEquals(streamOptions, payload.GetProperty("stream_options")));
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
            Assert.Equal("application", payload.GetProperty("metadata").GetProperty("verda").GetString());
            Assert.False(payload.TryGetProperty("headers", out _));
            return streaming ? ResponsesStreamResponse() : JsonResponse(
                """{"id":"resp-1","object":"response","created_at":1,"status":"completed","model":"model-one","output":[]}""");
        });
        var provider = CreateProvider(client);
        var options = new ResponseRequest
        {
            Model = "model-one",
            Input = "Say hello",
            Stream = requestedStream,
            StreamOptions = streamOptions,
            Headers = new() { ["X-Request-Id"] = "responses-request" },
            Metadata = new() { ["verda"] = "application" },
            AdditionalProperties = new()
            {
                ["provider_option"] = providerOptions,
                ["future_null"] = ParseElement("null")
            }
        };

        if (streaming)
        {
            var parts = new List<ResponseStreamPart>();
            await foreach (var part in provider.ResponsesStreamingAsync(options))
                parts.Add(part);
            Assert.Collection(parts,
                part => Assert.IsType<ResponseCreated>(part),
                part => Assert.Equal("hello", Assert.IsType<ResponseOutputTextDelta>(part).Delta),
                part => Assert.Equal("verda/model-one", Assert.IsType<ResponseCompleted>(part).Response.Model));
        }
        else
        {
            var result = await provider.ResponsesAsync(options);
            Assert.Equal("resp-1", result.Id);
            Assert.Equal("verda/model-one", result.Model);
        }

        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("X-Request-Id"));
    }

    [Fact]
    public async Task Image_and_transcription_calls_are_not_supported()
    {
        var provider = CreateProvider(CreateClient(_ => throw new InvalidOperationException("No request expected.")));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new() { Model = "flux-dev", Prompt = "hi" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(new()
        {
            Model = "whisper",
            MediaType = "audio/wav",
            Audio = "data:audio/wav;base64,AQID"
        }));
    }

    private static VerdaProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new TestHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new TestHttpMessageHandler(responder));

    private static HttpResponseMessage ChatResponse()
        => JsonResponse(
            """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"model-one","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""");

    private static HttpResponseMessage ResponsesStreamResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                event: response.created
                data: {"type":"response.created","sequence_number":0,"response":{"id":"resp-1","object":"response","created_at":1,"status":"in_progress","model":"model-one","output":[]}}

                event: response.output_text.delta
                data: {"type":"response.output_text.delta","sequence_number":1,"item_id":"msg-1","output_index":0,"content_index":0,"delta":"hello"}

                event: response.completed
                data: {"type":"response.completed","sequence_number":2,"response":{"id":"resp-1","object":"response","created_at":1,"status":"completed","model":"model-one","output":[{"type":"message","id":"msg-1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"hello","annotations":[]}]}]}}

                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        };

    private static HttpResponseMessage ChatStreamResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"model-one","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

                data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"model-one","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

                data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"model-one","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertBearerAuth(HttpRequestMessage request)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
}
