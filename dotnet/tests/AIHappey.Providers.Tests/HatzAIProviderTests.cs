using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.HatzAI;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class HatzAIProviderTests
{
    private const string RawOptions = """
        {"metadata":{"hatzai":"application-value","user_id":"user"},
         "future_null":null,"future_empty":{},"future_array":[],
         "future_number":9007199254740993,"future_nested":{"values":[true,null,{}]},
         "harness":{"enabled":true,"tools":[{"type":"future_tool","config":{}}]},
         "tools_to_use":["daytona_code_execution"],"file_uuids":["file-1"],
         "agent_id":"agent-1","conversation_id":"conversation-1"}
        """;

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Compatible_routes_streaming_auth_and_raw_options_are_preserved(string endpoint, bool streaming)
    {
        var calls = 0;
        using var client = Client(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://ai.hatz.ai/v1/" + Route(endpoint), request.RequestUri!.AbsoluteUri);
            AssertAuth(request, "resolved-key");
            Assert.Equal("trace-1", Assert.Single(request.Headers.GetValues("x-trace-id")));
            if (endpoint == "messages")
            {
                Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
                Assert.Equal("body-header", Assert.Single(request.Headers.GetValues("x-body-header")));
            }
            var payload = Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
            Assert.Equal("vendor/test-model", payload.GetProperty("model").GetString());
            if (endpoint != "chat")
                AssertOptions(payload);
            else
            {
                AssertOptions(payload, includeMetadata: false);
                Assert.True(payload.TryGetProperty("input", out _));
            }
            Assert.False(payload.TryGetProperty("headers", out _));
            return endpoint == "chat" ? ChatCompatibilityReply(streaming) : Reply(endpoint, streaming);
        });
        await Invoke(Provider(client), endpoint, streaming, Body("hatzai/vendor/test-model"));
        Assert.Equal(1, calls);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_uses_responses_and_unwraps_only_hatzai_metadata(bool streaming)
    {
        using var client = Client(async request =>
        {
            Assert.Equal("/v1/openai/responses", request.RequestUri!.AbsolutePath);
            AssertAuth(request, "resolved-key");
            var payload = Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("vendor/test-model", payload.GetProperty("model").GetString());
            AssertOptions(payload);
            Assert.False(payload.TryGetProperty("hatzai", out _));
            Assert.False(payload.TryGetProperty("foreign_setting", out _));
            Assert.False(payload.TryGetProperty("headers", out _));
            return Reply("responses", streaming);
        });
        var metadata = Parse(RawOptions).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        metadata["headers"] = Headers();
        var input = new AIRequest
        {
            ProviderId = "hatzai",
            Model = "hatzai/vendor/test-model",
            Input = new AIInput { Text = "Hello" },
            Metadata = new() { ["hatzai"] = metadata, ["other"] = new { foreign_setting = true } }
        };
        if (streaming) Assert.NotEmpty(await Drain(Provider(client).StreamUnifiedAsync(input)));
        else await Provider(client).ExecuteUnifiedAsync(input);
    }

    [Fact]
    public async Task UI_stream_uses_responses_and_preserves_provider_metadata()
    {
        var calls = 0;
        using var client = Client(async request =>
        {
            calls++;
            Assert.Equal("/v1/openai/responses", request.RequestUri!.AbsolutePath);
            AssertAuth(request, "resolved-key");
            var payload = Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("test-model", payload.GetProperty("model").GetString());
            AssertOptions(payload);
            return Reply("responses", true);
        });
        await Drain(Provider(client).StreamAsync(new ChatRequest
        {
            Model = "hatzai/test-model",
            Messages = [new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] }],
            ProviderMetadata = new() { ["hatzai"] = Parse(RawOptions) }
        }));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("responses", "hatzai/vendor/model", "vendor/model")]
    [InlineData("responses", "hatzai/hatzai/model", "hatzai/model")]
    [InlineData("responses", "vendor/hatzai/model", "vendor/hatzai/model")]
    [InlineData("responses", "vendor/model", "vendor/model")]
    [InlineData("messages", "hatzai/vendor/model", "vendor/model")]
    [InlineData("messages", "hatzai/hatzai/model", "hatzai/model")]
    [InlineData("messages", "vendor/hatzai/model", "vendor/hatzai/model")]
    [InlineData("messages", "vendor/model", "vendor/model")]
    public async Task Native_models_remove_only_one_leading_provider_prefix(string endpoint, string model, string expected)
    {
        using var client = Client(async request =>
        {
            var payload = Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(expected, payload.GetProperty("model").GetString());
            return Reply(endpoint, false);
        });
        await Invoke(Provider(client), endpoint, false, Body(model));
    }

    [Theory]
    [InlineData(false, """{"type":"function","function":{"name":"lookup"}}""", """{"type":"function","name":"lookup"}""")]
    [InlineData(true, """{"type":"function","function":{"name":"lookup"}}""", """{"type":"function","name":"lookup"}""")]
    [InlineData(false, "\"auto\"", "\"auto\"")]
    [InlineData(true, "\"auto\"", "\"auto\"")]
    [InlineData(false, "\"none\"", "\"none\"")]
    [InlineData(true, "\"none\"", "\"none\"")]
    [InlineData(false, "\"required\"", "\"required\"")]
    [InlineData(true, "\"required\"", "\"required\"")]
    [InlineData(false, """{"type":"function","name":"lookup"}""", """{"type":"function","name":"lookup"}""")]
    [InlineData(true, """{"type":"function","name":"lookup"}""", """{"type":"function","name":"lookup"}""")]
    [InlineData(false, """{"type":"function","function":{"name":"lookup"},"future_option":{"value":9007199254740993,"empty":{},"null":null}}""",
        """{"type":"function","name":"lookup","future_option":{"value":9007199254740993,"empty":{},"null":null}}""")]
    [InlineData(true, """{"type":"function","function":{"name":"lookup"},"future_option":{"value":9007199254740993,"empty":{},"null":null}}""",
        """{"type":"function","name":"lookup","future_option":{"value":9007199254740993,"empty":{},"null":null}}""")]
    [InlineData(false, """{"type":"function","name":"lookup","future_option":{"value":9007199254740993,"empty":{},"null":null}}""",
        """{"type":"function","name":"lookup","future_option":{"value":9007199254740993,"empty":{},"null":null}}""")]
    [InlineData(true, """{"type":"function","name":"lookup","future_option":{"value":9007199254740993,"empty":{},"null":null}}""",
        """{"type":"function","name":"lookup","future_option":{"value":9007199254740993,"empty":{},"null":null}}""")]
    [InlineData(false, """{"type":"function","function":{"name":"lookup","future_option":{"enabled":true}}}""",
        """{"type":"function","name":"lookup","function":{"future_option":{"enabled":true}}}""")]
    [InlineData(true, """{"type":"function","function":{"name":"lookup","future_option":{"enabled":true}}}""",
        """{"type":"function","name":"lookup","function":{"future_option":{"enabled":true}}}""")]
    [InlineData(false, """{"type":"future_choice","options":[true,null,{}]}""", """{"type":"future_choice","options":[true,null,{}]}""")]
    [InlineData(true, """{"type":"future_choice","options":[true,null,{}]}""", """{"type":"future_choice","options":[true,null,{}]}""")]
    public async Task Chat_named_function_tool_choice_is_flattened_without_changing_other_choices(
        bool streaming, string toolChoice, string expected)
    {
        using var client = Client(async request =>
        {
            Assert.Equal("/v1/openai/responses", request.RequestUri!.AbsolutePath);
            var payload = Parse(await request.Content!.ReadAsStringAsync());
            var actual = payload.GetProperty("tool_choice");
            Assert.True(JsonElement.DeepEquals(Parse(expected), actual), $"Expected {expected}; actual {actual.GetRawText()}");
            return ChatCompatibilityReply(streaming);
        });
        var fields = Body().EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        fields["tool_choice"] = Parse(toolChoice);
        fields["tools"] = Parse("""
            [{"type":"function","function":{"name":"lookup","description":"Look up a value","parameters":{"type":"object","properties":{}}}}]
            """);
        await Invoke(Provider(client), "chat", streaming, JsonSerializer.SerializeToElement(fields));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_extensions_cannot_replace_mapped_responses_fields_or_forward_chat_only_fields(bool streaming)
    {
        using var client = Client(async request =>
        {
            var payload = Parse(await request.Content!.ReadAsStringAsync());
            AssertOptions(payload, includeMetadata: false);
            Assert.DoesNotContain("extension-must-not-replace-input", payload.GetProperty("input").GetRawText());
            Assert.Contains("Hello", payload.GetProperty("input").GetRawText());
            foreach (var name in new[] { "messages", "max_tokens", "functions", "function_call", "stream_options" })
                Assert.False(payload.TryGetProperty(name, out _), name);
            return ChatCompatibilityReply(streaming);
        });
        var chat = Body().Deserialize<ChatCompletionOptions>()!;
        Assert.NotNull(chat.AdditionalProperties);
        chat.AdditionalProperties["input"] = Parse("\"extension-must-not-replace-input\"");
        chat.AdditionalProperties["functions"] = Parse("""[{"name":"old_chat_function"}]""");
        chat.AdditionalProperties["function_call"] = Parse("\"auto\"");
        if (streaming) await Drain(Provider(client).CompleteChatStreamingAsync(chat));
        else await Provider(client).CompleteChatAsync(chat);
    }

    [Theory]
    [InlineData("""[{"id":"model-a","display_name":"Model A"},{"name":"model-b"},{"id":"model-a"},{}]""")]
    [InlineData("""{"models":[{"id":"model-a","display_name":"Model A"},{"name":"model-b"},{"id":"model-a"},{}]}""")]
    [InlineData("""{"data":[{"id":"model-a","display_name":"Model A"},{"name":"model-b"},{"id":"model-a"},{}]}""")]
    public async Task Discovery_supports_common_shapes_and_caches_per_key(string body)
    {
        var keys = new Keys("first-key");
        var calls = 0;
        using var client = Client(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://ai.hatz.ai/v1/chat/models", request.RequestUri!.AbsoluteUri);
            AssertAuth(request, keys.Key!);
            calls++;
            return Task.FromResult(JsonReply(body));
        });
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var provider = new HatzAIProvider(keys, new Factory(client), new AsyncCacheHelper(memory));
        var first = (await provider.ListModels()).ToArray();
        Assert.Equal(["hatzai/model-a", "hatzai/model-b"], first.Select(model => model.Id));
        Assert.Equal("Model A", first[0].Name);
        await provider.ListModels();
        Assert.Equal(1, calls);
        keys.Key = "second-key";
        await provider.ListModels();
        Assert.Equal(2, calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancelled.Token));
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Discovery_maps_the_documented_native_name_display_name_and_developer()
    {
        using var client = Client(_ => Task.FromResult(JsonReply("""
            {"data":[{"id":"internal-id","name":"gpt-4","developer":"OpenAI","display_name":"GPT 4","max_tokens":8192,"vision":false}]}
            """)));
        var model = Assert.Single(await Provider(client).ListModels());
        Assert.Equal("hatzai/gpt-4", model.Id);
        Assert.Equal("GPT 4", model.Name);
        Assert.Equal("OpenAI", model.OwnedBy);
    }

    [Fact]
    public async Task Reused_client_does_not_leak_auth_or_request_headers()
    {
        var calls = 0;
        using var client = Client(request =>
        {
            AssertAuth(request, calls == 0 ? "first-key" : "second-key");
            Assert.Equal(calls == 0, request.Headers.Contains("x-trace-id"));
            calls++;
            return Task.FromResult(Reply("responses", false));
        });
        var keys = new Keys("first-key");
        var provider = new HatzAIProvider(keys, new Factory(client), new AsyncCacheHelper());
        await provider.ResponsesAsync(new ResponseRequest { Model = "test-model", Input = "Hello", Headers = Headers() });
        keys.Key = "second-key";
        await provider.ResponsesAsync(new ResponseRequest { Model = "test-model", Input = "Hello" });
        Assert.Equal(2, calls);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Missing_credentials_fail_before_HTTP_and_discovery_is_empty()
    {
        using var client = Client(_ => throw new InvalidOperationException("Unexpected HTTP."));
        var provider = Provider(client, null);
        Assert.Empty(await provider.ListModels());
        foreach (var endpoint in new[] { "chat", "responses", "messages" })
            foreach (var streaming in new[] { false, true })
                await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, endpoint, streaming, Body()));
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Errors_and_cancellation_propagate(string endpoint, bool streaming)
    {
        var calls = 0;
        using var client = Client(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"invalid key"}}""")
            });
        });
        var provider = Provider(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Invoke(provider, endpoint, streaming, Body()));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(1, calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Invoke(provider, endpoint, streaming, Body(), cancelled.Token));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.OK, true)]
    [InlineData("""{"models":{}}""", HttpStatusCode.OK, true)]
    [InlineData("""{"error":"unavailable"}""", HttpStatusCode.ServiceUnavailable, false)]
    public async Task Discovery_rejects_bad_shapes_and_propagates_errors(string body, HttpStatusCode status, bool malformed)
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
        if (malformed) await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
        else
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ListModels());
            Assert.Equal(status, error.StatusCode);
        }
    }

    [Fact]
    public async Task Registration_resolves_hatzai_and_unsupported_capabilities_throw()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(null));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var provider = Assert.IsType<HatzAIProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "hatzai"));
        Assert.Equal("hatzai", provider.GetIdentifier());
        await Assert.ThrowsAsync<NotSupportedException>(
       () => provider.ImageRequest(null!));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.EmbeddingRequestAsync(null!));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.SpeechRequest(null!));
    }

    private static async Task Invoke(HatzAIProvider provider, string endpoint, bool streaming, JsonElement body,
        CancellationToken ct = default)
    {
        switch (endpoint)
        {
            case "chat":
                var chat = body.Deserialize<ChatCompletionOptions>()!;
                chat.Headers = Headers();
                if (streaming)
                {
                    var parts = await Drain(provider.CompleteChatStreamingAsync(chat, ct));
                    Assert.Contains(parts, part => JsonSerializer.SerializeToElement(part).GetProperty("choices")
                        .EnumerateArray().Any(choice => choice.TryGetProperty("delta", out var delta)
                            && delta.TryGetProperty("content", out var content) && content.GetString() == "Hello"));
                }
                else
                {
                    var response = await provider.CompleteChatAsync(chat, ct);
                    Assert.Equal("Hello", JsonSerializer.SerializeToElement(response).GetProperty("choices")[0]
                        .GetProperty("message").GetProperty("content").GetString());
                }
                Assert.Equal("untrusted", chat.Headers["aUtHoRiZaTiOn"]);
                Assert.Equal(body.GetProperty("model").GetString(), chat.Model);
                break;
            case "responses":
                var responses = body.Deserialize<ResponseRequest>(ResponseJson.Default)!;
                responses.Headers = Headers();
                if (streaming) Assert.Single(await Drain(provider.ResponsesStreamingAsync(responses, ct)));
                else Assert.NotNull(await provider.ResponsesAsync(responses, ct));
                Assert.Equal("untrusted", responses.Headers["aUtHoRiZaTiOn"]);
                Assert.Equal(body.GetProperty("model").GetString(), responses.Model);
                break;
            default:
                var messages = body.Deserialize<MessagesRequest>()!;
                messages.Headers = new() { ["x-body-header"] = "body-header", ["X-API-Key"] = "body-key" };
                if (streaming) Assert.Single(await Drain(provider.MessagesStreamingAsync(messages, Headers(), ct)));
                else Assert.NotNull(await provider.MessagesAsync(messages, Headers(), ct));
                Assert.Equal("body-key", messages.Headers["X-API-Key"]);
                Assert.Equal(body.GetProperty("model").GetString(), messages.Model);
                break;
        }
    }

    private static void AssertOptions(JsonElement payload, bool includeMetadata = true)
    {
        foreach (var property in Parse(RawOptions).EnumerateObject()
                     .Where(property => includeMetadata || property.Name != "metadata"))
        {
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
            Assert.Single(payload.EnumerateObject(), candidate => candidate.Name == property.Name);
        }
    }

    private static JsonElement Body(string model = "test-model")
    {
        var fields = Parse(RawOptions).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        fields["model"] = JsonSerializer.SerializeToElement(model);
        fields["input"] = JsonSerializer.SerializeToElement("Hello");
        fields["messages"] = Parse("""[{"role":"user","content":"Hello"}]""");
        fields["max_tokens"] = JsonSerializer.SerializeToElement(32);
        return JsonSerializer.SerializeToElement(fields);
    }

    private static Dictionary<string, string> Headers() => new()
    {
        ["aUtHoRiZaTiOn"] = "untrusted",
        ["X-aPi-KeY"] = "untrusted",
        ["aPi-KeY"] = "untrusted",
        ["pRoXy-AuThOrIzAtIoN"] = "untrusted",
        ["x-GoOg-ApI-kEy"] = "untrusted",
        ["X-HatzAI-Key"] = "untrusted",
        ["X-Other-Key"] = "untrusted",
        ["x-trace-id"] = "trace-1"
    };

    private static void AssertAuth(HttpRequestMessage request, string key)
    {
        Assert.Equal(key, Assert.Single(request.Headers.GetValues("X-API-Key")));
        foreach (var name in Headers().Keys.Where(name => !name.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase)
            && name != "x-trace-id"))
            Assert.False(request.Headers.Contains(name), name);
    }

    private static string Route(string endpoint) => endpoint switch
    {
        "chat" or "responses" => "openai/responses",
        _ => "anthropic/messages"
    };

    private static HttpResponseMessage Reply(string endpoint, bool streaming)
    {
        var body = endpoint switch
        {
            "responses" when streaming => """data: {"type":"response.completed","sequence_number":1,"response":{"id":"resp","model":"test-model","status":"completed","output":[]}}""" + "\n\ndata: [DONE]\n\n",
            "responses" => """{"id":"resp","model":"test-model","status":"completed","output":[]}""",
            _ when streaming => "data: {\"type\":\"message_stop\"}\n\ndata: [DONE]\n\n",
            _ => """{"id":"msg","type":"message","role":"assistant","model":"test-model","content":[],"usage":{"input_tokens":1,"output_tokens":1}}"""
        };
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
        };
    }

    private static HttpResponseMessage ChatCompatibilityReply(bool streaming)
    {
        const string response = """
            {"id":"resp","model":"test-model","status":"completed","output":[
                {"id":"msg","type":"message","role":"assistant","status":"completed",
                 "content":[{"type":"output_text","text":"Hello","annotations":[]}]}],
             "usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}
            """;
        var frames = new[]
        {
            """{"type":"response.created","sequence_number":0,"response":{"id":"resp","model":"test-model","status":"in_progress","output":[]}}""",
            """{"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"msg","type":"message","role":"assistant","status":"in_progress","content":[]}}""",
            """{"type":"response.content_part.added","sequence_number":2,"item_id":"msg","output_index":0,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""",
            """{"type":"response.output_text.delta","sequence_number":3,"item_id":"msg","output_index":0,"content_index":0,"delta":"Hello"}""",
            """{"type":"response.completed","sequence_number":4,"response":""" + JsonSerializer.Serialize(Parse(response)) + "}"
        };
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(streaming ? string.Concat(frames.Select(frame => "data: " + frame + "\n\n"))
                + "data: [DONE]\n\n" : response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
        };
    }

    private static HttpResponseMessage JsonReply(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    private static JsonElement Parse(string body) => JsonSerializer.Deserialize<JsonElement>(body);
    private static HatzAIProvider Provider(HttpClient client, string? key = "resolved-key")
        => new(new Keys(key), new Factory(client), new AsyncCacheHelper());
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Key { get; set; } = key;
        public string? Resolve(string provider) => provider == "hatzai" ? Key : null;
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return send(request);
        }
    }
}
