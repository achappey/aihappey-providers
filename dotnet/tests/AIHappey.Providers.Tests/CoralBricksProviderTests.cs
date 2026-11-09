using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.CoralBricks;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class CoralBricksProviderTests
{
    private const string Native = """
        {"reasoning_effort":"future-effort","metadata":{"array":[true,null,{},[]],"large":9007199254740993},
         "future_null":null,"future_empty":{},"future_CASE":"UPPER","future_case":"lower",
         "tools":[{"type":"future_tool","config":{"enabled":false}}]}
        """;
    private const string ChatReply = """
        {"id":"chat_1","object":"chat.completion","created":1,"model":"org/model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15},
         "future":{"large":9007199254740993,"array":[true,null,{},[]]}}
        """;
    private const string ResponseReply = """
        {"id":"resp_1","object":"response","created_at":1,"model":"org/model","status":"queued",
         "background":true,"store":true,"previous_response_id":"resp_previous","output":[],
         "future":{"large":9007199254740993,"array":[true,null,{},[]]}}
        """;
    private const string ChatEvents = """
        : keepalive

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"org/model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}],"future":{"large":9007199254740993}}

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"org/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}

        data: [DONE]

        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(() => "test-key"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<CoralBricksProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "coralbricks"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_chat_preserves_options_and_raw_reply_without_mutating_request(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("https://inference.coralbricks.ai/v1/chat/completions", http.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, http.Method);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal("org/model", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.True(body.GetProperty("store").GetBoolean());
            Assert.Equal(42, body.GetProperty("top_level_unknown").GetInt32());
            Assert.True(http.Headers.Contains("X-Trace"));
            Assert.False(http.Headers.Contains("X-Other-Key"));
            return streaming ? Events(ChatEvents) : Json(ChatReply);
        });
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"coralbricks/org/model","messages":[{"role":"user","content":"Hi"}],"store":true,
             "top_level_unknown":42,"coralbricks":
            """ + Native + "}")!;
        options.Headers = new() { ["authorization"] = "******", ["X-Trace"] = "trace", ["X-Other-Key"] = "do-not-forward" };
        var before = JsonSerializer.Serialize(options);
        var provider = Provider(client);
        if (streaming)
        {
            var updates = await Collect(provider.CompleteChatStreamingAsync(options));
            Assert.Equal(2, updates.Count);
            Assert.Equal("coralbricks/org/model", updates[0].Model);
            Assert.Equal(9007199254740993L, updates[0].AdditionalProperties!["future"].GetProperty("large").GetInt64());
            Assert.Equal(15, updates[1].Usage!.TotalTokens);
        }
        else
        {
            var result = await provider.CompleteChatAsync(options);
            Assert.Equal("coralbricks/org/model", result.Model);
            Assert.True(JsonElement.DeepEquals(Element(ChatReply).GetProperty("future"), result.AdditionalProperties!["future"]));
        }
        Assert.Equal(before, JsonSerializer.Serialize(options));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Null(client.BaseAddress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_responses_preserves_background_store_previous_id_and_raw_options(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("https://inference.coralbricks.ai/v1/responses", http.RequestUri!.AbsoluteUri);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.True(body.GetProperty("background").GetBoolean());
            Assert.True(body.GetProperty("store").GetBoolean());
            Assert.Equal("resp_previous", body.GetProperty("previous_response_id").GetString());
            Assert.Equal("org/model", body.GetProperty("model").GetString());
            return streaming ? Events(
                "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":"
                + ResponseReply.Replace("\n", "\ndata: ") + "}\n\n"
                + "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hello\",\"future\":{\"large\":9007199254740993}}\n\n"
                + "data: [DONE]\n\n") : Json(ResponseReply);
        });
        var options = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"coralbricks/org/model","input":"Hi","background":true,"store":true,
             "previous_response_id":"resp_previous","coralbricks":
            """ + Native + "}")!;
        options.Headers = new() { ["Authorization"] = "******" };
        var before = JsonSerializer.Serialize(options);
        var provider = Provider(client);
        if (streaming)
        {
            var updates = await Collect(provider.ResponsesStreamingAsync(options));
            var created = Assert.IsType<ResponseCreated>(updates[0]);
            Assert.Equal("coralbricks/org/model", created.Response.Model);
            Assert.True(JsonElement.DeepEquals(Element(ResponseReply).GetProperty("future"), created.Response.AdditionalProperties!["future"]));
            var delta = Assert.IsType<ResponseOutputTextDelta>(updates[1]);
            Assert.Equal("Hello", delta.Delta);
            Assert.Equal(9007199254740993L, delta.AdditionalProperties!["future"].GetProperty("large").GetInt64());
        }
        else
        {
            var result = await provider.ResponsesAsync(options);
            Assert.Equal("resp_1", result.Id);
            Assert.Equal("queued", JsonSerializer.SerializeToElement(result).GetProperty("status").GetString());
            Assert.Equal("coralbricks/org/model", result.Model);
            Assert.True(JsonElement.DeepEquals(Element(ResponseReply).GetProperty("future"), result.AdditionalProperties!["future"]));
        }
        Assert.Equal(before, JsonSerializer.Serialize(options));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Null(client.BaseAddress);
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("""{"store":false,"background":false,"previous_response_id":null}""", false)]
    public async Task Responses_does_not_invent_storage_or_background_defaults(string fields, bool? store)
    {
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            if (store is null)
            {
                Assert.False(body.TryGetProperty("store", out _));
                Assert.False(body.TryGetProperty("background", out _));
            }
            else
            {
                Assert.False(body.GetProperty("store").GetBoolean());
                Assert.False(body.GetProperty("background").GetBoolean());
            }
            return Json(ResponseReply);
        });
        await Provider(client).ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
            """{"model":"org/model","input":"Hi","coralbricks":""" + fields + "}")!);
    }

    [Fact]
    public async Task Responses_scoped_options_override_typed_values_without_cleaning_native_input()
    {
        const string native = """
            {"background":true,"store":true,"previous_response_id":"native_previous",
             "input":[{"type":"reasoning","id":"r_1","summary":[{"type":"summary_text","text":"Keep me"}]}],
             "reasoning":{"effort":"future-effort","future":null}}
            """;
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            foreach (var property in Element(native).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            return Json(ResponseReply);
        });
        var options = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"org/model","input":"mapped","background":false,"store":false,"previous_response_id":"mapped",
             "coralbricks":
            """ + native + "}")!;
        await Provider(client).ResponsesAsync(options);
    }

    [Fact]
    public async Task Native_extension_data_collisions_are_collapsed_without_losing_values()
    {
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(body.GetProperty("store").GetBoolean());
            Assert.Single(body.EnumerateObject(), property => property.Name == "store");
            return Json(ResponseReply);
        });
        await Provider(client).ResponsesAsync(new ResponseRequest
        {
            Model = "model", Store = false,
            AdditionalProperties = new() { ["store"] = Element("true") }
        });
    }

    [Theory]
    [InlineData("unified")]
    [InlineData("messages")]
    [InlineData("unified-stream")]
    [InlineData("messages-stream")]
    [InlineData("ui")]
    public async Task Adapters_preserve_native_json_and_use_chat_completions(string adapter)
    {
        var streaming = adapter.EndsWith("stream") || adapter == "ui";
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("/v1/chat/completions", http.RequestUri!.AbsolutePath);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.Equal("org/model", body.GetProperty("model").GetString());
            Assert.False(body.TryGetProperty("max_tokens", out var max) && max.ValueKind == JsonValueKind.Null);
            return streaming ? Events(ChatEvents) : Json(ChatReply);
        });
        var provider = Provider(client);
        var request = new AIRequest
        {
            ProviderId = "coralbricks", Model = "coralbricks/org/model", Input = new() { Text = "Hi" },
            Metadata = new() { ["coralbricks"] = Element(Native), ["other"] = new { secret = "no" } }
        };
        var messages = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"coralbricks/org/model","max_tokens":20,"messages":[{"role":"user","content":"Hi"}],"coralbricks":
            """ + Native + "}")!;
        var before = JsonSerializer.Serialize(request);
        switch (adapter)
        {
            case "unified":
                Assert.NotNull((await provider.ExecuteUnifiedAsync(request)).Output);
                break;
            case "messages":
                Assert.NotEmpty((await provider.MessagesAsync(messages, [])).Content);
                break;
            case "unified-stream":
                Assert.NotEmpty(await Collect(provider.StreamUnifiedAsync(request)));
                break;
            case "messages-stream":
                Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, [])));
                break;
            default:
                Assert.NotEmpty(await Collect(provider.StreamAsync(JsonSerializer.Deserialize<ChatRequest>("""
                    {"model":"coralbricks/org/model","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}],
                     "providerMetadata":{"coralbricks":
                    """ + Native + "}}")!)));
                break;
        }
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Discovery_maps_available_metadata_and_is_cached_per_credential()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var key = "test-key";
        var calls = 0;
        using var client = new HttpClient(new Handler((http, _) =>
        {
            Assert.Equal(key, http.Headers.Authorization?.Parameter);
            Assert.Equal("https://inference.coralbricks.ai/v1/models", http.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, http.Method);
            calls++;
            return Task.FromResult(Json("""
                {"data":[{"id":"org/model","name":"Model","owned_by":"org","created":42,"description":"Description",
                  "context_length":131072,"max_output_tokens":8192,"tags":["fast"],
                  "capabilities":{"tools":true,"vision":false},"supported_parameters":["temperature"]},
                  {"id":"second","context_window":4096,"capabilities":["reasoning"]},{"id":null},{}]}
                """));
        }));
        var provider = new CoralBricksProvider(new Keys(() => key), new AsyncCacheHelper(memory), new Factory(client));
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["coralbricks/org/model", "coralbricks/second"], models.Select(model => model.Id));
        Assert.Equal("Model", models[0].Name);
        Assert.Equal("org", models[0].OwnedBy);
        Assert.Equal(42, models[0].Created);
        Assert.Equal(131072, models[0].ContextWindow);
        Assert.Equal(8192, models[0].MaxTokens);
        Assert.Equal("Description", models[0].Description);
        Assert.Equal(["fast", "tools", "temperature"], models[0].Tags);
        Assert.Equal(["reasoning"], models[1].Tags);
        Assert.Null(models[0].Pricing);
        models[0].Name = "mutated";
        Assert.Equal("Model", (await provider.ListModels()).First().Name);
        Assert.Equal(1, calls);
        key = "second-key";
        await provider.ListModels();
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Missing_key_skips_discovery_and_rejects_native_requests_before_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call"));
        var provider = Provider(client, () => null);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new() { Model = "model" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResponsesAsync(new() { Model = "model" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(provider.CompleteChatStreamingAsync(new() { Model = "model" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(provider.ResponsesStreamingAsync(new() { Model = "model" })));
    }

    [Fact]
    public async Task Shared_client_defaults_and_auth_are_never_mutated()
    {
        var key = "first";
        using var client = new HttpClient(new Handler((http, _) =>
        {
            Assert.Equal(key, http.Headers.Authorization?.Parameter);
            Assert.Equal("inference.coralbricks.ai", http.RequestUri!.Host);
            return Task.FromResult(Json(ChatReply));
        }));
        client.BaseAddress = new Uri("https://unrelated.example/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "unrelated");
        var provider = Provider(client, () => key);
        await provider.CompleteChatAsync(new() { Model = "model" });
        key = "second";
        await provider.CompleteChatAsync(new() { Model = "model" });
        Assert.Equal("unrelated", client.DefaultRequestHeaders.Authorization.Parameter);
        Assert.Equal("https://unrelated.example/", client.BaseAddress.AbsoluteUri);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("chat-stream")]
    [InlineData("responses-stream")]
    public async Task Http_failures_propagate_status_and_provider_error(string endpoint)
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = new StringContent("""{"error":{"message":"quota exceeded"}}""") }));
        var provider = Provider(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            switch (endpoint)
            {
                case "models": await provider.ListModels(); break;
                case "chat": await provider.CompleteChatAsync(new() { Model = "model" }); break;
                case "responses": await provider.ResponsesAsync(new() { Model = "model" }); break;
                case "chat-stream": await Collect(provider.CompleteChatStreamingAsync(new() { Model = "model" })); break;
                default: await Collect(provider.ResponsesStreamingAsync(new() { Model = "model" })); break;
            }
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("quota exceeded", error.Message);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("chat-stream")]
    [InlineData("responses-stream")]
    public async Task Cancellation_reaches_native_transport(string endpoint)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = Client(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Cancellation was ignored");
        });
        var provider = Provider(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (endpoint)
            {
                case "models": await provider.ListModels(cancellation.Token); break;
                case "chat": await provider.CompleteChatAsync(new() { Model = "model" }, cancellation.Token); break;
                case "responses": await provider.ResponsesAsync(new() { Model = "model" }, cancellation.Token); break;
                case "chat-stream": await Collect(provider.CompleteChatStreamingAsync(new() { Model = "model" }, cancellation.Token)); break;
                default: await Collect(provider.ResponsesStreamingAsync(new() { Model = "model" }, cancellation.Token)); break;
            }
        });
    }

    [Fact]
    public async Task Malformed_catalog_fails_instead_of_caching_an_empty_list()
    {
        using var client = Client((_, _) => Task.FromResult(Json("""{"unexpected":[]}""")));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    [Fact]
    public void Unsupported_endpoints_fail_without_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call"));
        var supported = new HashSet<string>
        {
            "GetIdentifier", "ListModels", "CompleteChatAsync", "CompleteChatStreamingAsync", "ResponsesAsync",
            "ResponsesStreamingAsync", "MessagesAsync", "MessagesStreamingAsync", "StreamAsync",
            "ExecuteUnifiedAsync", "StreamUnifiedAsync"
        };
        var provider = Provider(client);
        foreach (var method in typeof(IModelProvider).GetMethods().Where(method => !supported.Contains(method.Name)))
        {
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(provider,
                method.GetParameters().Select(parameter => parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray()));
            Assert.IsType<NotSupportedException>(error.InnerException);
        }
    }

    private static void AssertNative(JsonElement body)
    {
        foreach (var property in Element(Native).EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
        Assert.False(body.TryGetProperty("coralbricks", out _));
        Assert.False(body.TryGetProperty("other", out _));
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }

    private static CoralBricksProvider Provider(HttpClient client, Func<string?>? key = null)
        => new(new Keys(key ?? (() => "test-key")), new AsyncCacheHelper(), new Factory(client));
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Events(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler((request, ct) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return send(request, ct);
        }));
    private sealed class Keys(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string provider)
        {
            Assert.Equal("coralbricks", provider);
            return key();
        }
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
