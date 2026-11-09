using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.PaperInstruments;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class PaperInstrumentsProviderTests
{
    private const string Model = "ultramarine-1-flash";
    private const string CustomOptions = """
        {"future_object":{"nested":[true,null,{}]},"future_array":[],
         "future_null":null,"future_number":9007199254740993,
         "metadata":{"user_id":"user-1","paperinstruments":"application-data"}}
        """;

    [Fact]
    public async Task Registration_and_static_discovery_use_the_provider_name_without_HTTP()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(null));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registered = root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "paperinstruments");
        Assert.IsType<PaperInstrumentsProvider>(registered);

        using var client = Client(_ => throw new InvalidOperationException("Discovery must not call /models."));
        var provider = Provider(client, key: null);
        Assert.Equal("paperinstruments", provider.GetIdentifier());
        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("paperinstruments/" + Model, model.Id);
        Assert.Equal(Model, model.Name);
        Assert.Empty(client.DefaultRequestHeaders);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancelled.Token));
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Native_endpoints_preserve_raw_options_and_use_request_scoped_filtered_headers(
        string endpoint, bool streaming)
    {
        var calls = 0;
        using var client = Client(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api.paperinstruments.com", request.RequestUri!.Host);
            Assert.Equal(endpoint == "chat" ? "/v1/chat/completions" : "/v1/" + endpoint,
                request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("resolved-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("sticky-session", Assert.Single(request.Headers.GetValues("x-session-id")));
            Assert.Equal("trace-1", Assert.Single(request.Headers.GetValues("x-trace-id")));
            foreach (var name in CredentialNames)
                Assert.False(request.Headers.Contains(name), name);
            if (endpoint == "messages")
                Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.Contains(request.Headers.Accept,
                accept => accept.MediaType == (streaming ? "text/event-stream" : "application/json"));

            var payload = Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(Model, payload.GetProperty("model").GetString());
            Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
            AssertCustomOptions(payload);
            Assert.Equal(0.6, payload.GetProperty("temperature").GetDouble());
            Assert.Equal(0.8, payload.GetProperty("top_p").GetDouble());
            Assert.Equal("max", payload.GetProperty("reasoning_effort").GetString());
            if (endpoint == "responses")
            {
                Assert.False(payload.TryGetProperty("top_k", out _));
                Assert.Equal("max", payload.GetProperty("reasoning").GetProperty("effort").GetString());
            }
            else
                Assert.Equal(23, payload.GetProperty("top_k").GetInt32());

            return Reply(endpoint, streaming);
        });
        var provider = Provider(client);
        var body = NativeBody(endpoint, streaming, customized: true);
        await InvokeNative(provider, endpoint, streaming, body);
        Assert.Equal(1, calls);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Omitted_sampling_and_reasoning_options_keep_provider_defaults(string endpoint)
    {
        using var client = Client(async request =>
        {
            var payload = Parse(await request.Content!.ReadAsStringAsync());
            foreach (var field in new[] { "temperature", "top_p", "top_k", "reasoning_effort", "reasoning" })
                Assert.False(payload.TryGetProperty(field, out _), field);
            return Reply(endpoint, streaming: false);
        });
        await InvokeNative(Provider(client), endpoint, streaming: false,
            NativeBody(endpoint, streaming: false, customized: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_uses_native_responses_and_only_the_paperinstruments_metadata_scope(bool streaming)
    {
        var calls = 0;
        using var client = Client(async request =>
        {
            calls++;
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.Equal("resolved-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("unified-session", Assert.Single(request.Headers.GetValues("x-session-id")));
            foreach (var name in CredentialNames)
                Assert.False(request.Headers.Contains(name), name);
            var payload = Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
            AssertCustomOptions(payload);
            Assert.Equal("max", payload.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.False(payload.TryGetProperty("paperinstruments", out _));
            Assert.False(payload.TryGetProperty("foreign_secret", out _));
            Assert.False(payload.TryGetProperty("headers", out _));
            return Reply("responses", streaming);
        });
        var options = Parse(CustomOptions).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        options["reasoning"] = Parse("""{"effort":"max","future_setting":true}""");
        options["headers"] = new Dictionary<string, string>(Headers())
        {
            ["x-session-id"] = "unified-session"
        };
        var input = new AIRequest
        {
            ProviderId = "paperinstruments",
            Model = Model,
            Input = new AIInput { Text = "Hello" },
            Metadata = new()
            {
                ["paperinstruments"] = options,
                ["other"] = Parse("""{"foreign_secret":"do-not-forward"}""")
            }
        };
        if (streaming)
            await Drain(Provider(client).StreamUnifiedAsync(input));
        else
            await Provider(client).ExecuteUnifiedAsync(input);
        Assert.Equal(1, calls);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Reused_client_does_not_retain_credentials_or_sticky_routing_headers()
    {
        var calls = 0;
        using var client = Client(request =>
        {
            Assert.Equal(calls == 0 ? "first-key" : "second-key", request.Headers.Authorization?.Parameter);
            Assert.Equal(calls == 0, request.Headers.Contains("x-session-id"));
            calls++;
            return Task.FromResult(Reply("responses", streaming: false));
        });
        var keys = new Keys("first-key");
        var provider = new PaperInstrumentsProvider(keys, new Factory(client));
        await provider.ResponsesAsync(new ResponseRequest { Model = Model, Input = "Hello", Headers = Headers() });
        keys.Key = "second-key";
        await provider.ResponsesAsync(new ResponseRequest { Model = Model, Input = "Hello again" });
        Assert.Equal(2, calls);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Missing_credentials_fail_before_HTTP()
    {
        using var client = Client(_ => throw new InvalidOperationException("Unexpected HTTP request."));
        var provider = Provider(client, key: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CompleteChatAsync(new ChatCompletionOptions { Model = Model }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ResponsesAsync(new ResponseRequest { Model = Model, Input = "Hello" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.MessagesAsync(new MessagesRequest { Model = Model, MaxTokens = 16, Messages = [] }, Headers()));
    }

    private static async Task InvokeNative(PaperInstrumentsProvider provider, string endpoint, bool streaming, JsonElement body)
    {
        switch (endpoint)
        {
            case "chat":
                var chat = body.Deserialize<ChatCompletionOptions>()!;
                chat.Headers = Headers();
                if (streaming)
                {
                    var parts = await Drain(provider.CompleteChatStreamingAsync(chat));
                    var delta = JsonSerializer.SerializeToElement(Assert.Single(parts))
                        .GetProperty("choices")[0].GetProperty("delta");
                    Assert.Equal("Thinking", delta.GetProperty("reasoning_content").GetString());
                }
                else
                {
                    var response = await provider.CompleteChatAsync(chat);
                    var message = JsonSerializer.SerializeToElement(response)
                        .GetProperty("choices")[0].GetProperty("message");
                    Assert.Equal("Thinking", message.GetProperty("reasoning_content").GetString());
                }
                break;
            case "responses":
                var responses = body.Deserialize<ResponseRequest>(ResponseJson.Default)!;
                responses.Headers = Headers();
                if (streaming) Assert.Single(await Drain(provider.ResponsesStreamingAsync(responses)));
                else Assert.Equal("paperinstruments/" + Model, (await provider.ResponsesAsync(responses)).Model);
                Assert.Equal("caller-authorization", responses.Headers["Authorization"]);
                break;
            default:
                var messages = body.Deserialize<MessagesRequest>()!;
                if (streaming) Assert.Single(await Drain(provider.MessagesStreamingAsync(messages, Headers())));
                else Assert.Equal("paperinstruments/" + Model, (await provider.MessagesAsync(messages, Headers())).Model);
                break;
        }
    }

    private static JsonElement NativeBody(string endpoint, bool streaming, bool customized)
    {
        var fields = Parse(CustomOptions).EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        fields["model"] = JsonSerializer.SerializeToElement(Model);
        fields["stream"] = JsonSerializer.SerializeToElement(streaming);
        if (endpoint == "responses") fields["input"] = JsonSerializer.SerializeToElement("Hello");
        else
        {
            fields["messages"] = Parse("""[{"role":"user","content":"Hello"}]""");
            fields["max_tokens"] = JsonSerializer.SerializeToElement(32);
        }
        if (customized)
        {
            fields["temperature"] = JsonSerializer.SerializeToElement(0.6);
            fields["top_p"] = JsonSerializer.SerializeToElement(0.8);
            fields["reasoning_effort"] = JsonSerializer.SerializeToElement("max");
            if (endpoint == "responses") fields["reasoning"] = Parse("""{"effort":"max"}""");
            else fields["top_k"] = JsonSerializer.SerializeToElement(23);
        }
        return JsonSerializer.SerializeToElement(fields);
    }

    private static void AssertCustomOptions(JsonElement payload)
    {
        foreach (var property in Parse(CustomOptions).EnumerateObject())
        {
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
            Assert.Single(payload.EnumerateObject(), candidate => candidate.Name == property.Name);
        }
    }

    private static readonly string[] CredentialNames =
        ["X-PaperInstruments-Key", "X-Other-Key", "Proxy-Authorization", "x-api-key", "x-goog-api-key", "api-key"];

    private static Dictionary<string, string> Headers()
    {
        var headers = CredentialNames.ToDictionary(name => name, _ => "do-not-forward", StringComparer.OrdinalIgnoreCase);
        headers["Authorization"] = "caller-authorization";
        headers["x-session-id"] = "sticky-session";
        headers["x-trace-id"] = "trace-1";
        return headers;
    }

    private static HttpResponseMessage Reply(string endpoint, bool streaming)
    {
        var body = endpoint switch
        {
            "chat" when streaming => """data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"ultramarine-1-flash","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello","reasoning_content":"Thinking"},"finish_reason":null}]}""" + "\n\ndata: [DONE]\n\n",
            "chat" => """{"id":"chat-1","object":"chat.completion","created":1,"model":"ultramarine-1-flash","choices":[{"index":0,"message":{"role":"assistant","content":"Hello","reasoning_content":"Thinking"},"finish_reason":"stop"}]}""",
            "responses" when streaming => """data: {"type":"response.completed","sequence_number":1,"response":{"id":"resp-1","model":"ultramarine-1-flash","status":"completed","output":[]}}""" + "\n\ndata: [DONE]\n\n",
            "responses" => """{"id":"resp-1","model":"ultramarine-1-flash","status":"completed","output":[]}""",
            _ when streaming => "data: {\"type\":\"message_stop\"}\n\ndata: [DONE]\n\n",
            _ => """{"id":"msg-1","type":"message","role":"assistant","model":"ultramarine-1-flash","content":[],"usage":{"input_tokens":1,"output_tokens":1}}"""
        };
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
        };
    }

    private static JsonElement Parse(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static PaperInstrumentsProvider Provider(HttpClient client, string? key = "resolved-key")
        => new(new Keys(key), new Factory(client));
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
        public string? Resolve(string provider) => provider == "paperinstruments" ? Key : null;
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
