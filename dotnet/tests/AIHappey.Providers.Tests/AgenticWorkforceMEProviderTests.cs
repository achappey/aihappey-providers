using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AgenticWorkforceME;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests;

public sealed class AgenticWorkforceMEProviderTests
{
    private const string Native = """
        {"content":"native prompt","autonomy":"require_approval","evidence":true,"tools":["orders.get"],
         "attachment_ids":["attachment-1"],"deliverable":"html",
         "future":{"number":9007199254740993,"array":[true,null,{},[]]},"futureNull":null}
        """;
    private const string Completed = """
        {"run":{"id":"run-1","agent_id":"agent-1","status":"succeeded","output":{"text":"Hello","json":{"future":true}},
        "tokens_in":12,"tokens_out":3,"cost_usd":0.001,"future":{"number":9007199254740993}},
        "steps":[{"kind":"tool","output":{"future":[null,{}]}}]}
        """;
    private const string Events = """
        event: run.started
        data: {"type":"run.started","run_id":"run-1","agent_id":"agent-1","status":"running"}

        event: llm.thinking
        data: {"type":"llm.thinking","idx":0,"text":"Thinking"}

        event: llm.delta
        data: {"type":"llm.delta","idx":0,"text":"Hel"}

        event: tool.called
        data: {"type":"tool.called","idx":1,"tool":"orders.get","args_preview":{"order_id":"1"}}

        event: tool.result
        data: {"type":"tool.result","idx":1,"ok":true,"output_preview":{"future":9007199254740993}}

        event: llm.delta
        data: {"type":"llm.delta","idx":2,"text":"lo"}

        event: run.completed
        data: {"type":"run.completed","status":"succeeded","output":{"text":"Hello"},"future":null}


        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<AgenticWorkforceMEProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "agenticworkforceme"));
    }

    [Fact]
    public async Task Discovery_uses_tenant_and_follows_native_pagination()
    {
        var calls = 0;
        using var client = Client((http, _) =>
        {
            Auth(http);
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal(calls++ == 0 ? "?limit=100" : "?limit=100&cursor=next%20%2F%2B%3D", http.RequestUri!.Query);
            return Task.FromResult(Json(calls == 1
                ? """{"items":[{"id":"agent-1","name":"One"}],"next_cursor":"next /+="}"""
                : """{"items":[{"id":"agent-2","name":"Two"}],"next_cursor":null}"""));
        });
        var models = (await Provider(client).ListModels()).ToArray();
        Assert.Equal(["agenticworkforceme/agent-1", "agenticworkforceme/agent-1/run",
            "agenticworkforceme/agent-2", "agenticworkforceme/agent-2/run"], models.Select(model => model.Id));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("X-Tenant-Id"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"items":[{}]}""")]
    [InlineData("""{"items":[],"next_cursor":42}""")]
    [InlineData("""{"items":[],"next_cursor":"repeated"}""")]
    public async Task Invalid_discovery_and_repeated_cursors_fail_explicitly(string page)
    {
        using var client = Client((_, _) => Task.FromResult(Json(page)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    [Fact]
    public async Task Thread_turn_preserves_native_message_thread_options_and_raw_result()
    {
        var native = Element(Native);
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            switch (calls++)
            {
                case 0:
                    Assert.EndsWith("/v1/threads", http.RequestUri!.AbsoluteUri);
                    var thread = Element(await http.Content!.ReadAsStringAsync(ct));
                    Assert.Equal("agent-1", thread.GetProperty("agent_id").GetString());
                    Assert.Equal("Case 1", thread.GetProperty("title").GetString());
                    Assert.True(thread.GetProperty("future").GetBoolean());
                    return Json("""{"id":"thread-1","future":[null,{}]}""");
                case 1:
                    Assert.EndsWith("/threads/thread-1/messages", http.RequestUri!.AbsoluteUri);
                    Assert.Equal("attempt-1", http.Headers.GetValues("Idempotency-Key").Single());
                    var body = Element(await http.Content!.ReadAsStringAsync(ct));
                    Assert.True(JsonElement.DeepEquals(native, body));
                    return Json("""{"run_id":"run-1","message_id":"message-1","future":9007199254740993}""");
                default:
                    return Json(Completed);
            }
        });
        var request = Request("agent-1", Element(Native[..^1] +
            ""","tenant_id":"tenant-1","idempotency_key":"attempt-1","thread":{"title":"Case 1","future":true}}"""));
        var before = JsonSerializer.Serialize(request);
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("completed", result.Status);
        Assert.Equal("Hello", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        var receipt = Assert.IsType<JsonElement>(result.Metadata!["agenticworkforceme"]);
        Assert.True(JsonElement.DeepEquals(Element(Completed), receipt.GetProperty("result")));
        Assert.Equal("thread-1", receipt.GetProperty("thread_id").GetString());
        Assert.Equal(15, Assert.IsType<AIUsage>(result.Usage).TotalTokens);
        Assert.True(Assert.Single(result.Output.Items[0].Content!.OfType<AIToolCallContentPart>()).ProviderExecuted);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Existing_thread_and_receipt_history_do_not_create_a_new_thread()
    {
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            if (http.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/threads/thread-1/messages", http.RequestUri!.AbsoluteUri);
                var body = Element(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("latest", body.GetProperty("content").GetString());
                return Json("""{"run_id":"run-1","message_id":"message-1"}""");
            }
            return Json(Completed);
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request("agent-1", Element("""{"thread_id":"thread-1"}""")));
        var next = new AIRequest
        {
            ProviderId = "agenticworkforceme", Model = "agent-1",
            Input = new AIInput { Items = [
                new AIInputItem { Role = "assistant", Content = first.Output!.Items![0].Content },
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "latest" }] }
            ] }
        };
        var second = await provider.ExecuteUnifiedAsync(next);
        Assert.Equal("completed", second.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "agenticworkforceme", Model = "other-agent", Input = next.Input
        }));
    }

    [Fact]
    public async Task UI_receipt_continues_the_thread_and_keeps_raw_metadata()
    {
        using var client = Client((http, _) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/threads/thread-1/messages", http.RequestUri!.AbsoluteUri);
                return Task.FromResult(Json("""{"run_id":"run-1"}"""));
            }
            return Task.FromResult(Json(Completed));
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request("agent-1", Element("""{"thread_id":"thread-1"}""")));
        var receipt = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
        var parts = await Collect(provider.StreamAsync(new ChatRequest
        {
            Model = "agent-1",
            Messages = [
                new UIMessage
                {
                    Id = "assistant-1", Role = Role.assistant,
                    Parts = [new ToolInvocationPart
                    {
                        Type = "tool-agenticworkforceme_run", ToolName = receipt.ToolName, ToolCallId = receipt.ToolCallId,
                        ProviderExecuted = true, State = "output-available", Input = receipt.Input!, Output = receipt.Output!
                    }]
                },
                new UIMessage { Id = "user-2", Role = Role.user, Parts = [new TextUIPart { Text = "Next" }] }
            ]
        }));
        var text = Assert.Single(parts.OfType<TextDeltaUIMessageStreamPart>());
        var raw = Assert.IsType<JsonElement>(text.ProviderMetadata!["agenticworkforceme"]);
        Assert.True(JsonElement.DeepEquals(Element(Completed), raw.GetProperty("result")));
        Assert.Single(parts.OfType<FinishUIPart>());
    }

    [Fact]
    public async Task Following_a_run_recovers_its_thread_for_the_next_turn()
    {
        var posts = 0;
        using var client = Client((http, _) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                posts++;
                Assert.EndsWith("/threads/thread-1/messages", http.RequestUri!.AbsoluteUri);
                return Task.FromResult(Json("""{"run_id":"run-1"}"""));
            }
            return Task.FromResult(Json(Completed.Replace(
                "\"agent_id\":\"agent-1\"", "\"agent_id\":\"agent-1\",\"thread_id\":\"thread-1\"")));
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request("agent-1", Element("""{"run_id":"run-1"}""")));
        Assert.Equal(0, posts);
        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "agenticworkforceme", Model = "agent-1",
            Input = new AIInput
            {
                Text = "Next",
                Items = [new AIInputItem { Role = "assistant", Content = first.Output!.Items![0].Content }]
            }
        });
        Assert.Equal(1, posts);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request("other-agent", Element("""{"run_id":"run-1"}"""))));
    }

    [Fact]
    public async Task One_off_preserves_whole_input_and_unknown_fields_without_creating_a_thread()
    {
        const string native = """{"input":{"text":"Job","order":{"id":"1"},"future":9007199254740993},"future":[null,{}]}""";
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            if (http.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/agents/agent-1/runs", http.RequestUri!.AbsoluteUri);
                Assert.True(JsonElement.DeepEquals(Element(native), Element(await http.Content!.ReadAsStringAsync(ct))));
                return Json("""{"run_id":"run-1"}""");
            }
            return Json(Completed);
        });
        var result = await Provider(client).ExecuteUnifiedAsync(Request("agenticworkforceme/agent-1/run", Element(native)));
        Assert.Equal("agenticworkforceme/agent-1/run", result.Model);
    }

    [Fact]
    public async Task SSE_streams_text_reasoning_and_raw_events_then_preserves_the_full_run()
    {
        var reads = 0;
        using var client = Client((http, _) =>
        {
            Auth(http);
            if (http.RequestUri!.AbsolutePath.EndsWith("/events"))
            {
                Assert.Contains(http.Headers.Accept, accept => accept.MediaType == "text/event-stream");
                Assert.Equal("?since=-1", http.RequestUri.Query);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Events.Replace("\n", "\r\n"), Encoding.UTF8, "text/event-stream")
                });
            }
            return Task.FromResult(Json(reads++ == 0 ? """{"run":{"status":"running"},"steps":[]}""" : Completed));
        });
        var events = await Collect(Provider(client).StreamUnifiedAsync(Request("agent-1", Element("""{"run_id":"run-1"}"""))));
        Assert.Equal("Hello", string.Concat(events.Select(evt => evt.Event.Data).OfType<AITextDeltaEventData>().Select(data => data.Delta)));
        Assert.Equal("Thinking", Assert.Single(events.Select(evt => evt.Event.Data).OfType<AIReasoningDeltaEventData>()).Delta);
        Assert.Equal(7, events.Count(evt => evt.Event.Type == "data-agenticworkforceme-event"));
        var finish = Assert.IsType<AIFinishEventData>(events.Last().Event.Data);
        Assert.Equal("stop", finish.FinishReason);
        Assert.Equal(15, finish.TotalTokens);
        var receipt = Assert.IsType<JsonElement>(events.Last().Metadata!["agenticworkforceme"]);
        Assert.True(JsonElement.DeepEquals(Element(Completed), receipt.GetProperty("result")));
        Assert.Equal(7, receipt.GetProperty("events").GetArrayLength());
    }

    [Theory]
    [InlineData("waiting_approval", "incomplete", "other")]
    [InlineData("failed", "failed", "error")]
    [InlineData("cancelled", "failed", "error")]
    [InlineData("expired", "failed", "error")]
    public async Task Paused_and_failed_runs_are_not_reported_as_success(string status, string expected, string reason)
    {
        using var client = Client((_, _) => Task.FromResult(Json($$"""{"run":{"status":"{{status}}"},"steps":[]}""")));
        var provider = Provider(client);
        var request = Request("agent-1", Element("""{"run_id":"run-1"}"""));
        Assert.Equal(expected, (await provider.ExecuteUnifiedAsync(request)).Status);
        var events = await Collect(provider.StreamUnifiedAsync(request));
        Assert.Equal(reason, Assert.IsType<AIFinishEventData>(events.Last().Event.Data).FinishReason);
        Assert.Equal(expected == "failed", events.Any(evt => evt.Event.Type == "error"));
    }

    [Theory]
    [InlineData("run.waiting", "waiting_approval", "other")]
    [InlineData("run.failed", "failed", "error")]
    public async Task SSE_stops_on_approval_or_failure_and_preserves_native_details(string type, string status, string reason)
    {
        var reads = 0;
        var data = $$"""{"type":"{{type}}","status":"{{status}}","approval_id":"approval-1","future":9007199254740993}""";
        using var client = Client((http, _) => Task.FromResult(http.RequestUri!.AbsolutePath.EndsWith("/events")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"event: {type}\ndata: {data}\n\n") }
            : Json(reads++ == 0 ? """{"run":{"status":"running"}}""" : $$"""{"run":{"status":"{{status}}"},"steps":[]}""")));
        var events = await Collect(Provider(client).StreamUnifiedAsync(Request("agent-1", Element("""{"run_id":"run-1"}"""))));
        Assert.Equal(reason, Assert.IsType<AIFinishEventData>(events.Last().Event.Data).FinishReason);
        var raw = Assert.IsType<JsonElement>(events.Single(evt => evt.Event.Type == "data-agenticworkforceme-event").Event.Data);
        Assert.True(JsonElement.DeepEquals(Element(data), raw));
    }

    [Fact]
    public async Task Reasoning_replacements_and_final_text_are_authoritative_in_the_response()
    {
        const string events = """
            event: llm.thinking
            data: {"type":"llm.thinking","text":"old"}

            event: llm.thinking
            data: {"type":"llm.thinking","text":"replacement","replace":true}

            event: llm.delta
            data: {"type":"llm.delta","text":"Intermediate"}

            event: run.completed
            data: {"type":"run.completed","status":"succeeded"}


            """;
        var reads = 0;
        using var client = Client((http, _) => Task.FromResult(http.RequestUri!.AbsolutePath.EndsWith("/events")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events) }
            : Json(reads++ == 0 ? """{"run":{"status":"running"}}""" : Completed)));
        var response = await Provider(client).ExecuteUnifiedAsync(Request("agent-1", Element("""{"run_id":"run-1"}""")));
        Assert.Equal("Hello", Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal("replacement", Assert.Single(response.Output.Items[0].Content!.OfType<AIReasoningContentPart>()).Text);
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("vercel", true)]
    public async Task Adapters_pass_native_metadata_to_the_message_endpoint(string endpoint, bool streaming)
    {
        var posts = 0;
        var reads = 0;
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            if (http.Method == HttpMethod.Post)
            {
                posts++;
                Assert.EndsWith("/threads/thread-1/messages", http.RequestUri!.AbsoluteUri);
                var body = Element(await http.Content!.ReadAsStringAsync(ct));
                Assert.True(JsonElement.DeepEquals(Element(Native), body));
                return Json("""{"run_id":"run-1","message_id":"message-1"}""");
            }
            if (http.RequestUri!.AbsolutePath.EndsWith("/events"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Events, Encoding.UTF8, "text/event-stream") };
            return Json(reads++ == 0 ? """{"run":{"status":"queued"}}""" : Completed);
        });
        var provider = Provider(client);
        var fields = "\"body\":" + Native + ""","thread_id":"thread-1" """;
        object result;
        switch (endpoint)
        {
            case "chat":
                var chat = JsonSerializer.Deserialize<ChatCompletionOptions>(
                    $$"""{"model":"agent-1","messages":[{"role":"user","content":"Hello"}],{{fields}}}""")!;
                result = streaming ? await Collect(provider.CompleteChatStreamingAsync(chat)) : await provider.CompleteChatAsync(chat);
                break;
            case "responses":
                var responses = JsonSerializer.Deserialize<ResponseRequest>($$"""{"model":"agent-1","input":"Hello",{{fields}}}""")!;
                result = streaming ? await Collect(provider.ResponsesStreamingAsync(responses)) : await provider.ResponsesAsync(responses);
                break;
            case "messages":
                var messages = JsonSerializer.Deserialize<MessagesRequest>(
                    $$"""{"model":"agent-1","max_tokens":100,"messages":[{"role":"user","content":"Hello"}],{{fields}}}""")!;
                result = streaming ? await Collect(provider.MessagesStreamingAsync(messages, [])) : await provider.MessagesAsync(messages, []);
                break;
            default:
                result = await Collect(provider.StreamAsync(JsonSerializer.Deserialize<ChatRequest>(
                    $$$"""{"model":"agent-1","messages":[{"id":"1","role":"user","parts":[{"type":"text","text":"Hello"}]}],"providerMetadata":{"agenticworkforceme":{ {{{fields}}} } } }""")!));
                break;
        }
        Assert.Equal(1, posts);
        var serialized = JsonSerializer.Serialize(result);
        Assert.Contains("9007199254740993", serialized);
        Assert.Contains("Hello", serialized);
        Assert.Contains("agenticworkforceme", serialized);
    }

    [Fact]
    public async Task Invalid_metadata_and_missing_tenant_fail_before_HTTP()
    {
        using var client = Client((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP request."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request("agent-1", Element("[]"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client, tenant: null).ExecuteUnifiedAsync(Request("agent-1")));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request("agent-1/run", Element("""{"thread_id":"thread-1"}"""))));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request("agent-1", Element("""{"run_id":42}"""))));
    }

    [Fact]
    public async Task HTTP_errors_cancellation_and_truncated_streams_are_not_silently_completed()
    {
        using var errors = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"code":"TENANT_REQUIRED"}""")
        }));
        var request = Request("agent-1", Element("""{"run_id":"run-1"}"""));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(errors).ExecuteUnifiedAsync(request));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("TENANT_REQUIRED", error.Message);
        using var truncated = Client((http, _) => Task.FromResult(http.RequestUri!.AbsolutePath.EndsWith("/events")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("event: heartbeat\ndata: {}\n\n") }
            : Json("""{"run":{"status":"running"}}""")));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Provider(truncated).ExecuteUnifiedAsync(request));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(errors).ExecuteUnifiedAsync(request, cancelled.Token));
    }

    private static AIRequest Request(string model, JsonElement? native = null) => new()
    {
        ProviderId = "agenticworkforceme", Model = model, Input = new AIInput { Text = "latest" },
        Metadata = native is { } value ? new() { ["agenticworkforceme"] = value } : null
    };

    private static AgenticWorkforceMEProvider Provider(HttpClient client, string? tenant = "tenant-1")
        => new(new Keys(), new Clients(client), Options.Create(new AgenticWorkforceMEProviderOptions { TenantId = tenant }));

    private static void Auth(HttpRequestMessage http)
    {
        Assert.Equal("https", http.RequestUri!.Scheme);
        Assert.Equal("console.agenticworkforce.me", http.RequestUri.Host);
        Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
        Assert.Equal("tenant-1", http.Headers.GetValues("X-Tenant-Id").Single());
    }

    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
