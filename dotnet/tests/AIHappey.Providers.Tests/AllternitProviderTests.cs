using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Allternit;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class AllternitProviderTests
{
    private const string Reply = """
        {"id":"cmsg_1","object":"conversation.message","conversation_id":"conv_1","role":"assistant",
         "content":"Hello there","status":"completed","error":null,"tokens_in":12,"tokens_out":3,
         "future":{"number":9007199254740993,"array":[true,null,{},[]]}}
        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<AllternitProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "allternit"));
    }

    [Fact]
    public async Task Discovery_follows_after_pagination()
    {
        var calls = 0;
        using var client = Client((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("https://api.allternit.com/v1/agents?limit=100"
                + (calls == 0 ? "" : "&after=agent_1"), http.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(calls++ == 0
                ? """{"data":[{"id":"agent_1","name":"First"}],"has_more":true}"""
                : """{"data":[{"id":"agent_2","name":"Second"}],"has_more":false}"""));
        });
        var models = (await Provider(client).ListModels()).ToArray();
        Assert.Equal(["allternit/agent_1", "allternit/agent_2"], models.Select(model => model.Id));
        Assert.Equal(["First", "Second"], models.Select(model => model.Name));
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Discovery_rejects_nonadvancing_pagination()
    {
        using var client = Client((_, _) => Task.FromResult(Json(
            """{"data":[{"id":"agent_1"}],"has_more":true}""")));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    [Fact]
    public async Task New_conversation_preserves_native_metadata_and_sends_only_current_turn()
    {
        const string native = """
            {"metadata":{"large":9007199254740993,"array":[true,null,{},[]]},
             "future":{"value":9007199254740993},"nullable":null,"stream":true}
            """;
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            if (calls++ == 0)
            {
                Assert.Equal("/v1/agents/agent_1/conversations", http.RequestUri!.AbsolutePath);
                Assert.True(JsonElement.DeepEquals(Element(native).GetProperty("metadata"), body.GetProperty("metadata")));
                Assert.Single(body.EnumerateObject());
                return Json("""{"id":"conv_1","agent_id":"agent_1"}""");
            }
            Assert.Equal("/v1/conversations/conv_1/messages", http.RequestUri!.AbsolutePath);
            Assert.Equal("latest", body.GetProperty("content").GetString());
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.True(JsonElement.DeepEquals(Element(native).GetProperty("future"), body.GetProperty("future")));
            Assert.Equal(JsonValueKind.Null, body.GetProperty("nullable").ValueKind);
            Assert.False(body.TryGetProperty("other", out _));
            Assert.False(body.TryGetProperty("metadata", out _));
            return Json(Reply);
        });
        var request = Request(native);
        var before = JsonSerializer.Serialize(request);
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("Hello there", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        var metadata = JsonSerializer.SerializeToElement(result.Metadata!["allternit"]);
        Assert.Equal("conv_1", metadata.GetProperty("conversation_id").GetString());
        Assert.True(JsonElement.DeepEquals(Element(Reply), metadata.GetProperty("raw")));
        var usage = Assert.IsType<AIUsage>(result.Usage);
        Assert.Equal(12, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
        Assert.Equal(15, usage.TotalTokens);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Existing_conversation_is_checked_and_native_content_wins()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            if (calls++ == 0)
            {
                Assert.Equal(HttpMethod.Get, http.Method);
                Assert.Equal("/v1/conversations/conv_1", http.RequestUri!.AbsolutePath);
                return Json("""{"id":"conv_1","agent_id":"agent_1"}""");
            }
            Assert.Equal("/v1/conversations/conv_1/messages", http.RequestUri!.AbsolutePath);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("native", body.GetProperty("content").GetString());
            Assert.False(body.TryGetProperty("conversation_id", out _));
            return Json(Reply);
        });
        await Provider(client).ExecuteUnifiedAsync(Request("""{"conversation_id":"conv_1","content":"native"}"""));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Wrong_agent_fails_before_sending_a_message()
    {
        using var client = Client((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            return Task.FromResult(Json("""{"id":"conv_1","agent_id":"agent_other"}"""));
        });
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(
            Request("""{"conversation_id":"conv_1"}""")));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_adapters_preserve_provider_options_and_reply_metadata(string endpoint)
    {
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get) return Json("""{"id":"conv_1","agent_id":"agent_1"}""");
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("native", body.GetProperty("content").GetString());
            Assert.Equal(9007199254740993L, body.GetProperty("future").GetInt64());
            Assert.False(body.TryGetProperty("other", out _));
            return Json(Reply);
        });
        var provider = Provider(client);
        const string fields = """
            "model":"allternit/agent_1","allternit":{"conversation_id":"conv_1","content":"native","future":9007199254740993},
            "other":{"secret":"not forwarded"}
            """;
        object response = endpoint switch
        {
            "chat" => await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                "{" + fields + ""","messages":[{"role":"user","content":"Hello"}]}""")!),
            "responses" => await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
                "{" + fields + ""","input":"Hello"}""")!),
            _ => await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(
                "{" + fields + ""","max_tokens":100,"messages":[{"role":"user","content":"Hello"}]}""")!, [])
        };
        var serialized = JsonSerializer.Serialize(response);
        Assert.Contains("conv_1", serialized);
        Assert.Contains("9007199254740993", serialized);
    }

    [Fact]
    public async Task Streaming_maps_text_tools_usage_and_raw_metadata()
    {
        var sse = """
            : heartbeat

            event: future.event
            data: not even JSON

            event: message.delta
            data: {"delta":"Hello"}

            event: tool.started
            data: {"tool_call_id":"tool_1","name":"web_search","input":{"query":"test"},"future":9007199254740993}

            event: tool.completed
            data: {"tool_call_id":"tool_1","name":"web_search","output":[null,{},[]]}

            event: tool.failed
            data: {"tool_call_id":"tool_2","name":"email","error":"not available"}

            event: message.completed
            data:
            """ + Reply.Replace("\n", "\ndata: ") + "\n\n";
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get) return Json("""{"id":"conv_1","agent_id":"agent_1"}""");
            Assert.True(Element(await http.Content!.ReadAsStringAsync(ct)).GetProperty("stream").GetBoolean());
            Assert.Contains(http.Headers.Accept, value => value.MediaType == "text/event-stream");
            return Events(sse);
        });
        var events = await Collect(Provider(client), Request("""{"conversation_id":"conv_1","stream":false}"""));
        Assert.Equal("Hello there", string.Concat(events.Select(evt => evt.Event.Data).OfType<AITextDeltaEventData>()
            .Select(delta => delta.Delta)));
        Assert.Equal(2, events.Count(evt => evt.Event.Type == "tool-input-available"));
        var tool = Assert.Single(events.Select(evt => evt.Event.Data).OfType<AIToolOutputAvailableEventData>());
        Assert.True(tool.ProviderExecuted);
        Assert.True(JsonElement.DeepEquals(Element("[null,{},[]]"), Assert.IsType<JsonElement>(tool.Output)));
        Assert.Equal("not available", Assert.Single(events.Select(evt => evt.Event.Data)
            .OfType<AIToolOutputErrorEventData>()).ErrorText);
        var finish = Assert.IsType<AIFinishEventData>(events.Last().Event.Data);
        Assert.Equal("stop", finish.FinishReason);
        Assert.Equal(15, finish.TotalTokens);
        var metadata = JsonSerializer.SerializeToElement(events.Last().Metadata!["allternit"]);
        Assert.True(JsonElement.DeepEquals(Element(Reply), metadata.GetProperty("raw")));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    [InlineData("vercel")]
    public async Task Streaming_adapters_preserve_native_options_and_reply_metadata(string endpoint)
    {
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get) return Json("""{"id":"conv_1","agent_id":"agent_1"}""");
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("native", body.GetProperty("content").GetString());
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.Equal(9007199254740993L, body.GetProperty("future").GetInt64());
            return Events("event: message.completed\ndata: " + Reply.Replace("\n", "\ndata: ") + "\n\n");
        });
        var provider = Provider(client);
        const string fields = """
            "model":"allternit/agent_1","allternit":{"conversation_id":"conv_1","content":"native","future":9007199254740993}
            """;
        var serialized = new List<string>();
        switch (endpoint)
        {
            case "chat":
                var updates = new List<ChatCompletionUpdate>();
                await foreach (var update in provider.CompleteChatStreamingAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                    "{" + fields + ""","messages":[{"role":"user","content":"Hello"}]}""")!))
                {
                    updates.Add(update);
                    serialized.Add(JsonSerializer.Serialize(update));
                }
                Assert.Single(updates.Select(update => update.Id).Distinct());
                Assert.All(updates, update => Assert.Equal("allternit/agent_1", update.Model));
                Assert.Equal(15, updates.Last().Usage!.TotalTokens);
                break;
            case "responses":
                await foreach (var part in provider.ResponsesStreamingAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    "{" + fields + ""","input":"Hello"}""")!))
                    serialized.Add(JsonSerializer.Serialize(part));
                break;
            case "messages":
                await foreach (var part in provider.MessagesStreamingAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    "{" + fields + ""","max_tokens":100,"messages":[{"role":"user","content":"Hello"}]}""")!, []))
                    serialized.Add(JsonSerializer.Serialize(part));
                break;
            default:
                await foreach (var part in provider.StreamAsync(JsonSerializer.Deserialize<ChatRequest>("""
                    {"model":"allternit/agent_1","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hello"}]}],
                     "providerMetadata":{"allternit":{"conversation_id":"conv_1","content":"native","future":9007199254740993}}}
                    """)!))
                    serialized.Add(JsonSerializer.Serialize(part));
                break;
        }
        Assert.Contains(serialized, part => part.Contains("Hello there"));
        Assert.Contains(serialized, part => part.Contains("conv_1") && part.Contains("9007199254740993"));
    }

    [Theory]
    [InlineData("event: message.delta\ndata: {\"delta\":\"partial\"}\n\n", typeof(IOException))]
    [InlineData("event: message.completed\ndata: {\"id\":\"cmsg_1\"", typeof(IOException))]
    [InlineData("event: error\ndata: {\"error\":{\"code\":\"turn_failed\",\"message\":\"failed\"}}\n\n", typeof(InvalidOperationException))]
    public async Task Streaming_does_not_report_success_for_errors_or_truncation(string sse, Type error)
    {
        using var client = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Get
            ? Json("""{"id":"conv_1","agent_id":"agent_1"}""") : Events(sse)));
        await Assert.ThrowsAsync(error, () => Collect(Provider(client), Request("""{"conversation_id":"conv_1"}""")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16001)]
    public async Task Invalid_content_length_fails_before_network(int length)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call."));
        var options = JsonSerializer.Serialize(new { content = new string('x', length) });
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request(options)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16000)]
    public async Task Content_length_boundaries_are_accepted(int length)
    {
        using var client = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Get
            ? Json("""{"id":"conv_1","agent_id":"agent_1"}""") : Json(Reply)));
        await Provider(client).ExecuteUnifiedAsync(Request(JsonSerializer.Serialize(
            new { conversation_id = "conv_1", content = new string('x', length) })));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"conversation_id":false}""")]
    [InlineData("""{"conversation_id":"../bad"}""")]
    [InlineData("""{"metadata":[]}""")]
    public async Task Invalid_options_fail_before_network(string options)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request(options)));
    }

    [Fact]
    public async Task Failed_reply_is_not_returned_as_success()
    {
        using var client = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Get
            ? Json("""{"id":"conv_1","agent_id":"agent_1"}""")
            : Json("""{"id":"cmsg_1","conversation_id":"conv_1","status":"failed","error":"session failed"}""")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(
            Request("""{"conversation_id":"conv_1"}""")));
        Assert.Contains("session failed", error.Message);
    }

    [Theory]
    [InlineData(503, "runtime_starting")]
    [InlineData(409, "conversation_busy")]
    [InlineData(402, "spend_cap_reached")]
    public async Task Upstream_errors_keep_status_and_body_without_retries(int status, string code)
    {
        var calls = 0;
        using var client = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(code) });
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains(code, error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call."));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ExecuteUnifiedAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task Unsupported_input_fails_without_creating_a_conversation()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call."));
        var request = new AIRequest
        {
            ProviderId = "allternit", Model = "agent_1",
            Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "user", Content = [new AIFileContentPart { Type = "file", MediaType = "image/png", Data = "https://example.com/image.png" }]
                }]
            }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ExecuteUnifiedAsync(request));
    }

    private static AIRequest Request(string native = "{}") => new()
    {
        ProviderId = "allternit", Model = "allternit/agent_1",
        Metadata = new() { ["allternit"] = Element(native), ["other"] = new { secret = "not forwarded" } },
        Input = new AIInput
        {
            Items =
            [
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "old" }] },
                new AIInputItem { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "answer" }] },
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "latest" }] }
            ]
        }
    };

    private static async Task<List<AIStreamEvent>> Collect(AllternitProvider provider, AIRequest request)
    {
        var events = new List<AIStreamEvent>();
        await foreach (var evt in provider.StreamUnifiedAsync(request)) events.Add(evt);
        return events;
    }

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static AllternitProvider Provider(HttpClient client) => new(new Keys(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Events(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };
    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return send(request, cancellationToken);
        }
    }
}
