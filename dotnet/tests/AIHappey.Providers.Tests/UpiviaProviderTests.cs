using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Upivia;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class UpiviaProviderTests
{
    private const string Session = """
        {"id":"session_1","agent_id":"agent_1","model_id":"openai/gpt-4o-mini","title":"Research",
         "future":{"number":9007199254740993,"array":[null,true,{},[]]}}
        """;
    private const string Done = """
        {"usage":{"input_tokens":12,"output_tokens":3},"future":{"number":9007199254740993,"array":[null,{}]}}
        """;
    private static readonly string Events = """
        : keepalive

        event: status
        data: {"status":"running"}

        event: thinking
        data: {"delta":"Thinking"}

        event: tool_planned
        data: {"tool_name":"search","request_id":"req_1","arguments":{"query":"test"}}

        event: tool_executing
        data: {"tool_name":"search","request_id":"req_1"}

        event: tool_done
        data: {"tool_name":"search","request_id":"req_1","result":{"future":9007199254740993}}

        event: message_delta
        data: {"delta":"Hel"}

        event: future
        data: {"unknown":[null,{},[]]}

        event: message_delta
        data: {"delta":"lo"}

        """ + "\n\nevent: done\ndata: " + Done + "\n\n";

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<UpiviaProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "upivia"));
    }

    [Fact]
    public async Task Discovery_uses_agent_models_and_request_scoped_auth()
    {
        using var client = Client((http, _) =>
        {
            Auth(http);
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("/api/v1/agents", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"agents":[{"id":"agent_1","name":"Research","description":"Find sources"}]}"""));
        });
        var model = Assert.Single(await Provider(client).ListModels());
        Assert.Equal("upivia/agent_1", model.Id);
        Assert.Equal("Research", model.Name);
        Assert.Contains("agent", model.Tags!);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Missing_credentials_hide_discovery_and_reject_chat()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected."));
        var provider = Provider(client, null);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(Request()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"agents":[{}]}""")]
    public async Task Malformed_discovery_is_explicit(string source)
    {
        using var client = Client((_, _) => Task.FromResult(Json(source)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    [Fact]
    public async Task Native_session_and_turn_options_are_preserved_without_mutation()
    {
        var native = Element("""
            {"session":{"title":"Native","model_id":"custom","future":[null,{}]},
             "message":"Native message","modelId":"custom","instructions":"Native instructions",
             "require_approval_all":true,"content":[{"type":"image_url","image_url":{"url":"https://example.com/image.png"}}],
             "local_tools":[{"name":"read_file","future":null}],
             "future":{"number":9007199254740993,"array":[null,true,{},[]]},"futureNull":null}
            """);
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            if (http.RequestUri!.AbsolutePath.EndsWith("/sessions"))
            {
                Assert.Equal("agent_1", body.GetProperty("agent_id").GetString());
                foreach (var property in native.GetProperty("session").EnumerateObject())
                    Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)));
                return Json(Session);
            }
            Assert.Equal("/api/v1/chat/turn", http.RequestUri.AbsolutePath);
            Assert.Equal("session_1", body.GetProperty("sessionId").GetString());
            Assert.False(body.TryGetProperty("session", out _));
            foreach (var property in native.EnumerateObject().Where(property => property.Name != "session"))
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)));
            return Sse(Events);
        });
        var request = Request(native);
        var before = JsonSerializer.Serialize(request);
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("completed", result.Status);
        Assert.Equal("Hello", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal("Thinking", Assert.Single(result.Output.Items[0].Content!.OfType<AIReasoningContentPart>()).Text);
        Assert.Equal(15, Assert.IsType<AIUsage>(result.Usage).TotalTokens);
        var metadata = Assert.IsType<JsonElement>(result.Metadata!["upivia"]);
        Assert.True(JsonElement.DeepEquals(Element(Session), metadata.GetProperty("session")));
        Assert.True(JsonElement.DeepEquals(Element(Done), metadata.GetProperty("done")));
        Assert.Equal(9, metadata.GetProperty("events").GetArrayLength());
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Streaming_preserves_every_event_and_maps_text_reasoning_tools_and_usage()
    {
        using var client = StandardClient();
        var events = await Collect(Provider(client).StreamUnifiedAsync(Request()));
        Assert.Equal("Hello", string.Concat(events.Select(evt => evt.Event.Data).OfType<AITextDeltaEventData>().Select(delta => delta.Delta)));
        Assert.Equal("Thinking", Assert.Single(events.Select(evt => evt.Event.Data).OfType<AIReasoningDeltaEventData>()).Delta);
        Assert.Equal(9, events.Count(evt => evt.Event.Type == "data-upivia-event"));
        var input = Assert.Single(events, evt => evt.Event.Data is AIToolInputAvailableEventData { ToolName: "search" });
        var output = Assert.Single(events, evt => evt.Event.Data is AIToolOutputAvailableEventData { ToolName: "search" });
        Assert.Equal(input.Event.Id, output.Event.Id);
        Assert.True(Assert.IsType<AIToolOutputAvailableEventData>(output.Event.Data).ProviderExecuted);
        var receipts = events.Select(evt => evt.Event.Data).OfType<AIToolOutputAvailableEventData>()
            .Where(data => data.ToolName == "upivia_session").ToList();
        Assert.Equal(2, receipts.Count);
        Assert.True(receipts[0].Preliminary);
        Assert.NotEqual(true, receipts[1].Preliminary);
        var finish = Assert.IsType<AIFinishEventData>(events.Last().Event.Data);
        Assert.Equal("stop", finish.FinishReason);
        Assert.Equal(15, finish.TotalTokens);
        Assert.True(JsonElement.DeepEquals(Element(Done), Assert.IsType<JsonElement>(finish.Response)));
    }

    [Theory]
    [InlineData("tool_pending")]
    [InlineData("local_tools_pending")]
    public async Task Native_interactions_remain_raw_and_do_not_report_completion(string type)
    {
        const string pending = """{"approval_id":"approval_1","request_id":"req_1","future":[null,{}]}""";
        using var client = StandardClient($"event: {type}\ndata: {pending}\n\nevent: done\ndata: {{}}\n\n");
        var result = await Provider(client).ExecuteUnifiedAsync(Request());
        Assert.Equal("incomplete", result.Status);
        var metadata = Assert.IsType<JsonElement>(result.Metadata!["upivia"]);
        Assert.True(JsonElement.DeepEquals(Element(pending), metadata.GetProperty("events")[0].GetProperty("data")));
    }

    [Fact]
    public async Task Pending_interactions_are_not_reported_as_end_turn_on_compatibility_surfaces()
    {
        const string pending = "event: tool_pending\ndata: {\"approval_id\":\"approval_1\"}\n\nevent: done\ndata: {}\n\n";
        using var client = StandardClient(pending);
        var provider = Provider(client);
        var chat = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "upivia/agent_1", Messages = [new() { Role = "user", Content = "Hello" }]
        });
        Assert.Equal("tool_calls", Assert.Single(chat.Choices).FinishReason);
        var messages = await provider.MessagesAsync(new MessagesRequest
        {
            Model = "upivia/agent_1", MaxTokens = 1024,
            Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }]
        }, []);
        Assert.Equal("pause_turn", messages.StopReason);
        var responses = await provider.ResponsesAsync(new ResponseRequest { Model = "upivia/agent_1", Input = "Hello" });
        Assert.Equal("incomplete", responses.Status);
        var streaming = await Collect(provider.StreamUnifiedAsync(Request()));
        Assert.Equal("tool-calls", Assert.IsType<AIFinishEventData>(streaming.Last().Event.Data).FinishReason);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("blocked")]
    public async Task Failed_service_tools_are_reported_as_errors_with_raw_metadata(string status)
    {
        var source = """
            event: tool_planned
            data: {"tool_name":"search","request_id":"req_1","arguments":{"query":"test"}}

            """ + $"\n\nevent: tool_done\ndata: {{\"tool_name\":\"search\",\"request_id\":\"req_1\",\"status\":\"{status}\",\"reason_code\":\"policy_violation\"}}\n\n"
            + "event: done\ndata: {}\n\n";
        using var client = StandardClient(source);
        var provider = Provider(client);
        var events = await Collect(provider.StreamUnifiedAsync(Request()));
        var failed = Assert.Single(events.Select(evt => evt.Event.Data).OfType<AIToolOutputErrorEventData>());
        Assert.Contains("policy_violation", failed.ErrorText);
        Assert.DoesNotContain(events, evt => evt.Event.Data is AIToolOutputAvailableEventData { ToolName: "search" });
        var result = await provider.ExecuteUnifiedAsync(Request());
        var tool = Assert.Single(result.Output!.Items![0].Content!.OfType<AIToolCallContentPart>(),
            part => part.ToolName == "search");
        Assert.Equal("output-error", tool.State);
    }

    [Fact]
    public async Task Native_resume_is_passed_through_without_replaying_a_message()
    {
        var native = Element("""{"sessionId":"session_1","continue_from":{"request_id":"req_1","action":"approve","future":null}}""");
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            if (http.Method == HttpMethod.Get) return Json(Session);
            Assert.Equal("/api/v1/chat/turn", http.RequestUri!.AbsolutePath);
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(JsonElement.DeepEquals(native, body));
            return Sse(Events);
        });
        await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "upivia", Model = "upivia/agent_1", Metadata = new() { ["upivia"] = native }
        });
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    [InlineData("ui")]
    public async Task Session_receipts_survive_protocol_roundtrips_and_only_send_latest_turn(string protocol)
    {
        var turns = 0;
        var creates = 0;
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get)
            {
                Assert.Equal("/api/v1/chat/sessions/session_1", http.RequestUri!.AbsolutePath);
                return Json(Session);
            }
            var body = Element(await http.Content!.ReadAsStringAsync(ct));
            if (http.RequestUri!.AbsolutePath.EndsWith("/sessions"))
            {
                creates++;
                return Json(Session);
            }
            Assert.Equal("session_1", body.GetProperty("sessionId").GetString());
            Assert.Equal(turns++ == 0 ? "Hello" : "Next", body.GetProperty("message").GetString());
            return Sse("event: message_delta\ndata: {\"delta\":\"Hello\"}\n\nevent: done\ndata: {}\n\n");
        });
        var provider = Provider(client);
        if (protocol == "chat")
        {
            var first = await provider.CompleteChatAsync(new ChatCompletionOptions
            {
                Model = "upivia/agent_1", Messages = [new() { Role = "user", Content = "Hello" }]
            });
            var assistant = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(first.Choices.First().Message))!;
            await provider.CompleteChatAsync(new ChatCompletionOptions
            {
                Model = "upivia/agent_1", Messages = [assistant, new() { Role = "user", Content = "Next" }]
            });
        }
        else if (protocol == "responses")
        {
            var first = await provider.ResponsesAsync(new ResponseRequest { Model = "upivia/agent_1", Input = "Hello" });
            await provider.ResponsesAsync(new ResponseRequest
            {
                Model = "upivia/agent_1",
                Input = first.OutputItems.Append(new ResponseInputMessage { Role = ResponseRole.User, Content = "Next" }).ToList()
            });
        }
        else if (protocol == "messages")
        {
            var first = await provider.MessagesAsync(new MessagesRequest
            {
                Model = "upivia/agent_1", MaxTokens = 1024,
                Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }]
            }, []);
            await provider.MessagesAsync(new MessagesRequest
            {
                Model = "upivia/agent_1", MaxTokens = 1024,
                Messages = [
                    new() { Role = "assistant", Content = new MessagesContent(first.Content) },
                    new() { Role = "user", Content = new MessagesContent("Next") }
                ]
            }, []);
        }
        else
        {
            var first = await provider.ExecuteUnifiedAsync(Request());
            var tool = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
            var chat = new ChatRequest
            {
                Model = "upivia/agent_1",
                Messages = [
                    new UIMessage { Id = "a", Role = Role.assistant, Parts = [
                        new ToolInvocationPart { Type = "tool-upivia_session", ToolName = tool.ToolName,
                            ToolCallId = tool.ToolCallId, ProviderExecuted = true, State = "output-available",
                            Input = tool.Input!, Output = tool.Output! }] },
                    new UIMessage { Id = "u", Role = Role.user, Parts = [new TextUIPart { Text = "Next" }] }
                ]
            };
            chat = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(chat, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
            Assert.NotEmpty(await Collect(provider.StreamAsync(chat)));
        }
        Assert.Equal(2, turns);
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task Compatibility_streams_use_the_native_chat_turn()
    {
        using var client = StandardClient();
        var provider = Provider(client);
        Assert.NotEmpty(await Collect(provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "upivia/agent_1", Messages = [new() { Role = "user", Content = "Hello" }]
        })));
        Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(new ResponseRequest { Model = "upivia/agent_1", Input = "Hello" })));
        Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(new MessagesRequest
        {
            Model = "upivia/agent_1", MaxTokens = 1024,
            Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }]
        }, [])));
    }

    [Theory]
    [InlineData("event: error\ndata: {\"message\":\"bad\"}\n\n", typeof(InvalidOperationException))]
    [InlineData("event: done\ndata: {\"status\":\"failed\"}\n\n", typeof(InvalidOperationException))]
    [InlineData("event: status\ndata: {}\n\n", typeof(EndOfStreamException))]
    [InlineData("event: done\ndata: not-json\n\n", typeof(JsonException))]
    public async Task Stream_errors_and_truncation_never_emit_success(string source, Type exception)
    {
        using var client = StandardClient(source);
        var emitted = new List<AIStreamEvent>();
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var evt in Provider(client).StreamUnifiedAsync(Request())) emitted.Add(evt);
        });
        Assert.IsAssignableFrom(exception, error);
        Assert.DoesNotContain(emitted, evt => evt.Event.Type == "finish");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"sessionId":42}""")]
    [InlineData("""{"session":[]}""")]
    [InlineData("""{"session":{"agent_id":"other"}}""")]
    [InlineData("""{"continue_from":{"request_id":"req_1"}}""")]
    public async Task Invalid_metadata_fails_before_HTTP(string native)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request(Element(native))));
    }

    [Fact]
    public async Task Session_agent_mismatch_is_rejected_before_chat()
    {
        using var client = Client((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            return Task.FromResult(Json(Session.Replace("agent_1", "other")));
        });
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(
            Request(Element("""{"sessionId":"session_1"}"""))));
    }

    [Fact]
    public async Task Session_history_without_a_new_user_message_is_not_replayed()
    {
        using var initial = StandardClient();
        var first = await Provider(initial).ExecuteUnifiedAsync(Request());
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "upivia", Model = "upivia/agent_1",
            Input = new AIInput
            {
                Items = [
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] },
                    new AIInputItem { Role = "assistant", Content = first.Output!.Items![0].Content }
                ]
            }
        }));
    }

    [Fact]
    public async Task HTTP_errors_cancellation_and_failed_POST_are_not_retried()
    {
        var posts = 0;
        using var client = Client((http, _) =>
        {
            if (http.RequestUri!.AbsolutePath.EndsWith("/sessions")) return Task.FromResult(Json(Session));
            posts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("""{"error":"upstream","request_id":"req_1"}""")
            });
        });
        var provider = Provider(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(Request()));
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Contains("req_1", error.Message);
        Assert.Equal(1, posts);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request(), cancelled.Token));
        Assert.Equal(1, posts);
    }

    private static AIRequest Request(JsonElement? native = null) => new()
    {
        ProviderId = "upivia", Model = "upivia/agent_1", Input = new AIInput { Text = "Hello" },
        Metadata = native is { } value ? new() { ["upivia"] = value } : null
    };

    private static UpiviaProvider Provider(HttpClient client, string? key = "upivia_pat_live_test")
        => new(new Keys(key), new AsyncCacheHelper(), new Clients(client));
    private static void Auth(HttpRequestMessage http)
    {
        Assert.Equal("https", http.RequestUri!.Scheme);
        Assert.Equal("www.upivia.com", http.RequestUri.Host);
        Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
        Assert.Equal("upivia_pat_live_test", http.Headers.Authorization?.Parameter);
    }
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };
    private static HttpClient StandardClient(string? events = null) => Client((http, _) =>
        Task.FromResult(http.RequestUri!.AbsolutePath.EndsWith("/turn") ? Sse(events ?? Events) : Json(Session)));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
    private sealed class Keys(string? key = "upivia_pat_live_test") : IApiKeyResolver { public string? Resolve(string provider) => key; }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
