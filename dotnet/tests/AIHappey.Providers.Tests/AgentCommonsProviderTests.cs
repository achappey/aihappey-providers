using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AgentCommons;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class AgentCommonsProviderTests
{
    private const string Completed = """
        {"data":{"sessionId":"session_1","content":"Hello","usage":{"inputTokens":12,"outputTokens":3},
        "future":{"number":9007199254740993,"array":[true,null,{},[]]}},"requestId":"request_1"}
        """;
    private const string Final = """
        {"type":"final","sessionId":"session_1","runId":"run_1","seq":8,"content":"Hello","usage":{"inputTokens":12,"outputTokens":3},"future":{"number":9007199254740993,"null":null}}
        """;
    private static readonly string Events = """
        : comment

        data: {"type":"run_started","runId":"run_1","seq":0}

        data: {"type":"token","content":"Thinking","phase":"commentary","seq":1}

        data: {"type":"toolStart","toolName":"webSearch","args":{"query":"test"},"seq":2}

        data: {"type":"toolProgress","progress":0.5,"seq":3}

        data: {"type":"toolEnd","toolName":"webSearch","output":{"future":9007199254740993},"seq":4}

        data: {"type":"token","content":"Hel","phase":"final_answer","seq":5}

        data: {"type":"future_event","future":[null,{}],"seq":6}

        data: {"type":"token","content":"lo","seq":7}

        """ + "\n\ndata: " + Final + "\n\n";

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<AgentCommonsProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "agentcommons"));
    }

    [Fact]
    public async Task Discovery_uses_agents_and_request_scoped_authentication()
    {
        using var client = Client((http, _) =>
        {
            Auth(http);
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("/v1/agents", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"data":[{"id":"agent_1","name":"Research","description":"Find sources"}]}"""));
        });
        var model = Assert.Single(await Provider(client).ListModels());
        Assert.Equal("agentcommons/agent_1", model.Id);
        Assert.Equal("Research", model.Name);
        Assert.Contains("agent", model.Tags!);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Missing_credentials_hide_discovery_and_reject_runs()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected."));
        var provider = Provider(client, null);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(Request()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":[{}]}""")]
    public async Task Malformed_discovery_fails_explicitly(string body)
    {
        using var client = Client((_, _) => Task.FromResult(Json(body)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    [Fact]
    public async Task Run_preserves_native_options_unknown_values_and_entire_response()
    {
        var native = Element("""
            {"sessionId":"session_existing","messages":[{"role":"user","content":"native"}],
             "attachments":[{"fileId":"file_1"}],"provenance":{"mode":"full","onchain":false},
             "computerRequest":{"enabled":true},"cliContext":"native context",
             "future":{"number":9007199254740993,"array":[null,{}]},"futureNull":null}
            """);
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            Assert.Equal("/v1/agents/run", http.RequestUri!.AbsolutePath);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("agent_1", body.GetProperty("agentId").GetString());
            foreach (var property in native.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)));
            return Json(Completed);
        });
        var request = Request(native);
        var before = JsonSerializer.Serialize(request);
        var response = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("completed", response.Status);
        Assert.Equal("Hello", Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal(15, Assert.IsType<AIUsage>(response.Usage).TotalTokens);
        var receipt = Assert.IsType<JsonElement>(response.Metadata!["agentcommons"]);
        Assert.True(JsonElement.DeepEquals(Element(Completed), receipt.GetProperty("raw")));
        Assert.Equal("session_1", receipt.GetProperty("sessionId").GetString());
        Assert.True(Assert.Single(response.Output.Items[0].Content!.OfType<AIToolCallContentPart>()).ProviderExecuted);
    }

    [Fact]
    public async Task History_receipt_continues_session_and_sends_only_latest_user_turn()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            if (calls++ == 0) Assert.False(body.TryGetProperty("sessionId", out _));
            else
            {
                Assert.Equal("session_1", body.GetProperty("sessionId").GetString());
                var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
                Assert.Equal("Next", message.GetProperty("content").GetString());
            }
            return Json(Completed);
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request());
        var input = new AIInput
        {
            Items = [
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] },
                new AIInputItem { Role = "assistant", Content = first.Output!.Items![0].Content },
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Next" }] }
            ]
        };
        await provider.ExecuteUnifiedAsync(new AIRequest { ProviderId = "agentcommons", Model = "agent_1", Input = input });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(
            new AIRequest { ProviderId = "agentcommons", Model = "agent_other", Input = input }));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Streaming_maps_tokens_tools_usage_and_keeps_all_native_events()
    {
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            Assert.Equal("/v1/agents/run/stream", http.RequestUri!.AbsolutePath);
            Assert.Contains(http.Headers.Accept, accept => accept.MediaType == "text/event-stream");
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("Hello", body.GetProperty("messages")[0].GetProperty("content").GetString());
            return Sse(Events);
        });
        var events = await Collect(Provider(client).StreamUnifiedAsync(Request()));
        Assert.Equal("Hello", string.Concat(events.Select(evt => evt.Event.Data).OfType<AITextDeltaEventData>().Select(delta => delta.Delta)));
        Assert.Equal("Thinking", Assert.Single(events.Select(evt => evt.Event.Data).OfType<AIReasoningDeltaEventData>()).Delta);
        Assert.Single(events, evt => evt.Event.Type == "reasoning-end");
        var toolInput = Assert.Single(events, evt => evt.Event.Data is AIToolInputAvailableEventData { ToolName: "webSearch" });
        var toolOutput = Assert.Single(events, evt => evt.Event.Data is AIToolOutputAvailableEventData { ToolName: "webSearch" });
        Assert.Equal(toolInput.Event.Id, toolOutput.Event.Id);
        Assert.True(Assert.IsType<AIToolOutputAvailableEventData>(toolOutput.Event.Data).ProviderExecuted);
        Assert.Equal(9, events.Count(evt => evt.Event.Type == "data-agentcommons-event"));
        var finish = Assert.IsType<AIFinishEventData>(events.Last().Event.Data);
        Assert.Equal(15, finish.TotalTokens);
        Assert.Equal(15, finish.MessageMetadata!.TotalTokens);
        Assert.True(JsonElement.DeepEquals(Element(Final), Assert.IsType<JsonElement>(finish.Response)));
        Assert.Single(events, evt => evt.Event.Data is AIToolOutputAvailableEventData { ToolName: "agentcommons_session" });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UI_session_receipt_survives_roundtrip(bool jsonRoundtrip)
    {
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("session_1", body.GetProperty("sessionId").GetString());
            Assert.Equal("Next", body.GetProperty("messages")[0].GetProperty("content").GetString());
            return Sse("data: " + Final + "\n\n");
        });
        using var initial = Client((_, _) => Task.FromResult(Json(Completed)));
        var first = await Provider(initial).ExecuteUnifiedAsync(Request());
        var tool = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
        var chat = new ChatRequest
        {
            Model = "agentcommons/agent_1",
            Messages = [
                new UIMessage { Id = "a", Role = Role.assistant, Parts = [
                    new ToolInvocationPart { Type = "tool-agentcommons_session", ToolName = tool.ToolName,
                        ToolCallId = tool.ToolCallId, Title = tool.Title, ProviderExecuted = true, State = "output-available",
                        Input = tool.Input!, Output = tool.Output! }] },
                new UIMessage { Id = "u", Role = Role.user, Parts = [new TextUIPart { Text = "Next" }] }
            ]
        };
        if (jsonRoundtrip)
            chat = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(chat, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        var parts = await Collect(Provider(client).StreamAsync(chat));
        Assert.Equal("Hello", Assert.Single(parts.OfType<TextDeltaUIMessageStreamPart>()).Delta);
        Assert.Single(parts.OfType<FinishUIPart>());
    }

    [Fact]
    public async Task Messages_session_receipt_survives_roundtrip()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            if (calls++ > 0)
            {
                Assert.Equal("session_1", body.GetProperty("sessionId").GetString());
                Assert.Equal("Next", Assert.Single(body.GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());
            }
            return Json(Completed);
        });
        var provider = Provider(client);
        var first = await provider.MessagesAsync(new MessagesRequest
        {
            Model = "agentcommons/agent_1", MaxTokens = 1024,
            Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }]
        }, []);
        await provider.MessagesAsync(new MessagesRequest
        {
            Model = "agentcommons/agent_1", MaxTokens = 1024,
            Messages = [
                new() { Role = "assistant", Content = new MessagesContent(first.Content) },
                new() { Role = "user", Content = new MessagesContent("Next") }
            ]
        }, []);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    public async Task OpenAI_session_receipt_survives_roundtrip(string protocol)
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            if (calls++ > 0)
            {
                Assert.Equal("session_1", body.GetProperty("sessionId").GetString());
                Assert.Equal("Next", Assert.Single(body.GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());
            }
            return Json(Completed);
        });
        var provider = Provider(client);
        if (protocol == "chat")
        {
            var first = await provider.CompleteChatAsync(new ChatCompletionOptions
            {
                Model = "agentcommons/agent_1", Messages = [new() { Role = "user", Content = "Hello" }]
            });
            var assistant = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(first.Choices.First().Message))!;
            await provider.CompleteChatAsync(new ChatCompletionOptions
            {
                Model = "agentcommons/agent_1",
                Messages = [assistant, new() { Role = "user", Content = "Next" }]
            });
        }
        else
        {
            var first = await provider.ResponsesAsync(new ResponseRequest { Model = "agentcommons/agent_1", Input = "Hello" });
            await provider.ResponsesAsync(new ResponseRequest
            {
                Model = "agentcommons/agent_1",
                Input = first.OutputItems.Append(new ResponseInputMessage { Role = ResponseRole.User, Content = "Next" }).ToList()
            });
        }
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Compatibility_surfaces_use_native_runs()
    {
        using var client = Client((http, _) => Task.FromResult(
            http.RequestUri!.AbsolutePath.EndsWith("/stream") ? Sse(Events) : Json(Completed)));
        var provider = Provider(client);
        var chat = new ChatCompletionOptions
        {
            Model = "agentcommons/agent_1",
            Messages = [new() { Role = "user", Content = "Hello" }]
        };
        Assert.NotNull(await provider.CompleteChatAsync(chat));
        Assert.NotEmpty(await Collect(provider.CompleteChatStreamingAsync(chat)));
        var responses = new ResponseRequest { Model = "agentcommons/agent_1", Input = "Hello" };
        Assert.NotNull(await provider.ResponsesAsync(responses));
        Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(responses)));
        var messages = new MessagesRequest
        {
            Model = "agentcommons/agent_1", MaxTokens = 1024,
            Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }]
        };
        Assert.NotNull(await provider.MessagesAsync(messages, []));
        Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, [])));
    }

    [Theory]
    [InlineData("data: {\"type\":\"error\",\"message\":\"bad\",\"requestId\":\"req_1\"}\n\n")]
    [InlineData("event: error\ndata: {\"message\":\"bad\"}\n\n")]
    [InlineData("data: {\"type\":\"failed\",\"message\":\"bad\"}\n\n")]
    [InlineData("data: {\"type\":\"cancelled\"}\n\n")]
    public async Task Native_stream_errors_do_not_emit_success(string source)
    {
        using var client = Client((_, _) => Task.FromResult(Sse(source)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(Provider(client).StreamUnifiedAsync(Request())));
    }

    [Fact]
    public async Task HTTP_errors_cancellation_and_truncation_are_explicit()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":{"code":"FORBIDDEN"},"requestId":"req_1"}""")
        }));
        var provider = Provider(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(Request()));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("req_1", error.Message);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request(), cancelled.Token));
        using var truncated = Client((_, _) => Task.FromResult(Sse("data: {\"type\":\"keepalive\"}\n\n")));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Collect(Provider(truncated).StreamUnifiedAsync(Request())));
        using var malformed = Client((_, _) => Task.FromResult(Sse("data: not-json\n\n")));
        await Assert.ThrowsAnyAsync<JsonException>(() => Collect(Provider(malformed).StreamUnifiedAsync(Request())));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"sessionId":42}""")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"agentId":"other"}""")]
    public async Task Invalid_native_options_fail_before_sending(string native)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request(Element(native))));
    }

    private static AIRequest Request(JsonElement? native = null) => new()
    {
        ProviderId = "agentcommons", Model = "agentcommons/agent_1", Input = new AIInput { Text = "Hello" },
        Metadata = native is { } value ? new() { ["agentcommons"] = value } : null
    };

    private static AgentCommonsProvider Provider(HttpClient client, string? key = "test-key")
        => new(new Keys(key), new AsyncCacheHelper(), new Clients(client));
    private static void Auth(HttpRequestMessage http)
    {
        Assert.Equal("https", http.RequestUri!.Scheme);
        Assert.Equal("api.agentcommons.io", http.RequestUri.Host);
        Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
    }
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
    private sealed class Keys(string? key = "test-key") : IApiKeyResolver { public string? Resolve(string provider) => key; }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
