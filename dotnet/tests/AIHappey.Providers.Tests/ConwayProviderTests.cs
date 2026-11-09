using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Conway;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class ConwayProviderTests
{
    private const string Native = """
        {"temperature":0.25,"max_tokens":123,"top_p":0.9,"stop":["END"],
         "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}],
         "tool_choice":"auto","reasoning_effort":"future-effort",
         "future":{"large":9007199254740993,"array":[true,null,{},[]]},"future_null":null,
         "future_CASE":"UPPER","future_case":"lower"}
        """;
    private const string Reply = """
        {"id":"chat_1","object":"chat.completion","created":1,"model":"gpt-5.2",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15},
         "future":{"large":9007199254740993}}
        """;
    private const string Events = """
        : keepalive

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"gpt-5.2","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}],"future":{"large":9007199254740993}}

        data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"gpt-5.2","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}

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
        Assert.IsType<ConwayProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "conway"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_preserves_native_options_responses_and_request_state(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://inference.conway.tech/v1/chat/completions", http.RequestUri!.AbsoluteUri);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal("gpt-5.2", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.Equal(42, body.GetProperty("unknown_top_level").GetInt32());
            Assert.True(http.Headers.Contains("X-Trace"));
            Assert.False(http.Headers.Contains("X-Other-Key"));
            return streaming ? Response(Events, true) : Response(Reply);
        });
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"conway/gpt-5.2","messages":[{"role":"user","content":"Hi"}],
             "temperature":1,"stream":
            """ + (!streaming).ToString().ToLowerInvariant() + ""","unknown_top_level":42,"conway":""" + Native + "}")!;
        options.Headers = new() { ["authorization"] = "untrusted", ["X-Trace"] = "trace", ["X-Other-Key"] = "do-not-forward" };
        var before = JsonSerializer.Serialize(options);
        var provider = Provider(client);
        if (streaming)
        {
            var updates = await Collect(provider.CompleteChatStreamingAsync(options));
            Assert.Equal(2, updates.Count);
            Assert.Equal("conway/gpt-5.2", updates[0].Model);
            Assert.Equal(9007199254740993L, updates[0].AdditionalProperties!["future"].GetProperty("large").GetInt64());
            Assert.Equal(15, updates[1].Usage!.TotalTokens);
        }
        else
        {
            var result = await provider.CompleteChatAsync(options);
            Assert.Equal("conway/gpt-5.2", result.Model);
            Assert.Equal(9007199254740993L, result.AdditionalProperties!["future"].GetProperty("large").GetInt64());
        }
        Assert.Equal(before, JsonSerializer.Serialize(options));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Null(client.BaseAddress);
    }

    [Theory]
    [InlineData("unified")]
    [InlineData("unified-stream")]
    [InlineData("responses")]
    [InlineData("responses-stream")]
    [InlineData("messages")]
    [InlineData("messages-stream")]
    [InlineData("ui")]
    public async Task Compatibility_surfaces_use_chat_and_preserve_raw_metadata(string surface)
    {
        var streaming = surface.EndsWith("stream") || surface == "ui";
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("/v1/chat/completions", http.RequestUri!.AbsolutePath);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            AssertNative(body);
            Assert.Equal("gpt-5.2", body.GetProperty("model").GetString());
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            return streaming ? Response(Events, true) : Response(Reply);
        });
        var provider = Provider(client);
        var unified = new AIRequest
        {
            ProviderId = "conway", Model = "conway/gpt-5.2", Input = new() { Text = "Hi" },
            Metadata = new() { ["conway"] = Element(Native), ["other"] = new { private_option = "no" } }
        };
        var before = JsonSerializer.Serialize(unified);
        var responses = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"conway/gpt-5.2","input":"Hi","conway":
            """ + Native + "}")!;
        var messages = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"conway/gpt-5.2","max_tokens":20,"messages":[{"role":"user","content":"Hi"}],"conway":
            """ + Native + "}")!;
        switch (surface)
        {
            case "unified": Assert.NotNull((await provider.ExecuteUnifiedAsync(unified)).Output); break;
            case "unified-stream": Assert.NotEmpty(await Collect(provider.StreamUnifiedAsync(unified))); break;
            case "responses": Assert.NotNull(await provider.ResponsesAsync(responses)); break;
            case "responses-stream": Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(responses))); break;
            case "messages": Assert.NotEmpty((await provider.MessagesAsync(messages, [])).Content); break;
            case "messages-stream": Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, []))); break;
            default:
                Assert.NotEmpty(await Collect(provider.StreamAsync(JsonSerializer.Deserialize<ChatRequest>("""
                    {"model":"conway/gpt-5.2","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}],
                     "providerMetadata":{"conway":
                    """ + Native + "}}")!)));
                break;
        }
        Assert.Equal(before, JsonSerializer.Serialize(unified));
    }

    [Theory]
    [InlineData("auto", false)]
    [InlineData("auto", true)]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("any", false)]
    [InlineData("any", true)]
    [InlineData("tool", false)]
    [InlineData("tool", true)]
    public async Task Messages_maps_tool_choices_to_OpenAI_format(string choice, bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            var expected = choice == "tool"
                ? Element("""{"type":"function","function":{"name":"lookup"}}""")
                : JsonSerializer.SerializeToElement(choice == "any" ? "required" : choice);
            Assert.True(JsonElement.DeepEquals(expected, body.GetProperty("tool_choice")));
            return streaming ? Response(Events, true) : Response(Reply);
        });
        var request = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"conway/gpt-5.2","max_tokens":20,"messages":[{"role":"user","content":"Hi"}],
             "tools":[{"name":"lookup","input_schema":{"type":"object"}}],"tool_choice":{"type":
            """ + JsonSerializer.Serialize(choice) + (choice == "tool" ? ""","name":"lookup" """ : "") + "}}")!;
        var provider = Provider(client);
        if (streaming)
            Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(request, [])));
        else
            Assert.NotEmpty((await provider.MessagesAsync(request, [])).Content);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("models")]
    public async Task Models_map_available_entries_capabilities_and_per_token_pricing(string envelope)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var calls = 0;
        using var client = Client((http, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("https://inference.conway.tech/v1/models", http.RequestUri!.AbsoluteUri);
            return Task.FromResult(Response("{\"" + envelope + "\":" + """
                [{"id":"gpt-5.2","provider":"openai","display_name":"GPT 5.2","context_window":400000,"max_tokens":128000,
                  "supports_tools":true,"supports_vision":true,"pricing":{"input_per_million":1.75,"output_per_million":14}},
                 {"id":"hidden","available":false},{"id":""},{"id":"claude-sonnet-4.5","owned_by":"anthropic",
                  "pricing":{"input_per_1m_tokens_usd":3,"output_per_1m_tokens_usd":15}},
                 {"id":"kimi-k2.5","pricing":{"input_per_1k":0,"output_per_1k":0.002}}]}
                """));
        });
        var provider = Provider(client, cache: new AsyncCacheHelper(cache));
        var models = (await provider.ListModels()).ToList();
        Assert.Equal(3, models.Count);
        Assert.Equal("conway/gpt-5.2", models[0].Id);
        Assert.Equal("GPT 5.2", models[0].Name);
        Assert.Equal("openai", models[0].OwnedBy);
        Assert.Equal(400000, models[0].ContextWindow);
        Assert.Equal(128000, models[0].MaxTokens);
        Assert.Contains("tools", models[0].Tags!);
        Assert.Contains("vision", models[0].Tags!);
        Assert.Equal(0.00000175m, models[0].Pricing!.Input);
        Assert.Equal(0.000014m, models[0].Pricing!.Output);
        Assert.Equal(0.000003m, models[1].Pricing!.Input);
        Assert.Equal(0m, models[2].Pricing!.Input);
        Assert.Equal(0.000002m, models[2].Pricing!.Output);
        await provider.ListModels();
        Assert.Equal(1, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Credentials_are_resolved_per_request_and_partition_model_cache()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var key = "first-key";
        var calls = 0;
        using var client = new HttpClient(new Handler((http, _) =>
        {
            calls++;
            Assert.Equal(key, http.Headers.Authorization?.Parameter);
            return Task.FromResult(Response("""{"data":[{"id":"gpt-5.2"}]}"""));
        }));
        var provider = Provider(client, () => key, new AsyncCacheHelper(cache));
        await provider.ListModels();
        key = "second-key";
        await provider.ListModels();
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Missing_credentials_hide_models_and_reject_chat_without_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
        var provider = Provider(client, () => null);
        Assert.Empty(await provider.ListModels());
        var request = new ChatCompletionOptions { Model = "gpt-5.2", Messages = [] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(provider.CompleteChatStreamingAsync(request)));
    }

    [Fact]
    public async Task Http_errors_are_not_reported_as_success()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        { Content = new StringContent("""{"error":{"message":"Insufficient credits"}}""") }));
        var provider = Provider(client);
        var request = new ChatCompletionOptions { Model = "gpt-5.2", Messages = [] };
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(request));
        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(provider.CompleteChatStreamingAsync(request)));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
    }

    [Fact]
    public async Task Malformed_catalog_fails_instead_of_returning_no_models()
    {
        using var client = Client((_, _) => Task.FromResult(Response("""{"unexpected":[]}""")));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    [Fact]
    public async Task Requests_honor_cancellation()
    {
        using var source = new CancellationTokenSource();
        using var client = Client((_, ct) =>
        {
            source.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not forwarded.");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).CompleteChatAsync(
            new ChatCompletionOptions { Model = "gpt-5.2", Messages = [] }, source.Token));
    }

    [Fact]
    public void Undocumented_endpoints_fail_without_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected HTTP"));
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
        Assert.False(body.TryGetProperty("conway", out _));
        Assert.False(body.TryGetProperty("other", out _));
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }

    private static ConwayProvider Provider(HttpClient client, Func<string?>? key = null, AsyncCacheHelper? cache = null)
        => new(new Keys(key ?? (() => "test-key")), cache ?? new AsyncCacheHelper(), new Factory(client));
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Response(string value, bool stream = false) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler((http, ct) =>
        {
            Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
            return send(http, ct);
        }));
    private sealed class Keys(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string provider)
        {
            Assert.Equal("conway", provider);
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
