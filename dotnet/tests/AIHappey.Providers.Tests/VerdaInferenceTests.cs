using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Verda;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses;
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
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
            };
        });

        await foreach (var _ in CreateProvider(client).ResponsesStreamingAsync(
                           new ResponseRequest { Model = "model-one", Input = "Say hello" }))
        {
        }

        Assert.Null(client.DefaultRequestHeaders.Authorization);
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
