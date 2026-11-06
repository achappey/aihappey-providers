using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Kiosapi;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class KiosapiProviderTests
{
    private const string Pending = """
        {"id":"run-1","agent_id":"agt-1","thread_id":"thr-1","status":"requires_action","cost_rupiah":12,
         "pending_tool_calls":[{"id":"call-1","name":"weather","arguments":"{\"city\":\"Jakarta\"}"}],"future":{"x":[1,null]}}
        """;
    private const string Done = """
        {"id":"run-1","agent_id":"agt-1","thread_id":"thr-1","status":"succeeded","output":"Sunny","cost_rupiah":21,"future":{"x":[1,null]}}
        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<KiosapiProvider>(root.GetRequiredService<ProviderRegistry>().GetModelProvider(scope.ServiceProvider, "kiosapi"));
    }

    [Fact]
    public async Task Discovery_uses_agents_and_request_scoped_authentication()
    {
        using var client = Client((request, _) =>
        {
            Assert.Equal("/v1/agents", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("kios_test", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""{"agents":[{"id":"agt-1","name":"Weather","model_id":"openai/gpt-4o"}]}"""));
        });
        var model = Assert.Single(await Provider(client).ListModels());
        Assert.Equal("kiosapi/agt-1", model.Id);
        Assert.Equal("Weather", model.Name);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Thread_run_and_client_tool_results_round_trip_without_replaying_user_input()
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("kios_test", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            switch (++calls)
            {
                case 1:
                    Assert.Equal("/v1/agents/agt-1/threads", path);
                    var threadBody = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                    Assert.Equal("Custom", threadBody.GetProperty("title").GetString());
                    Assert.Equal(42, threadBody.GetProperty("future").GetInt32());
                    return Json("""{"id":"thr-1","agent_id":"agt-1","future":true}""");
                case 2:
                    Assert.Equal("/v1/threads/thr-1/runs", path);
                    Assert.Equal("request-1", request.Headers.GetValues("Idempotency-Key").Single());
                    var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
                    Assert.Equal("Hello", body.GetProperty("input").GetString());
                    Assert.Equal(7, body.GetProperty("future_field").GetInt32());
                    return Json(Pending);
                case 3:
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal("/v1/runs/run-1", path);
                    return Json(Pending);
                case 4:
                    Assert.Equal("/v1/runs/run-1/tool_outputs", path);
                    var outputs = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct)).GetProperty("outputs");
                    Assert.Equal("call-1", outputs[0].GetProperty("tool_call_id").GetString());
                    Assert.Equal("32 degrees", outputs[0].GetProperty("output").GetString());
                    return Json(Done);
                default: throw new InvalidOperationException("Unexpected request.");
            }
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "kiosapi", Model = "kiosapi/agt-1", Input = new AIInput { Text = "Hello" },
            Metadata = new Dictionary<string, object?>
            {
                ["kiosapi"] = JsonSerializer.Deserialize<JsonElement>("""{"thread":{"title":"Custom","future":42},"run":{"future_field":7},"idempotency_key":"request-1"}""")
            }
        });
        Assert.Equal("requires_action", first.Status);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Pending), Assert.IsType<JsonElement>(first.Metadata!["kiosapi"])));
        var content = first.Output!.Items![0].Content!;
        var receipt = Assert.Single(content.OfType<AIToolCallContentPart>(), p => p.ProviderExecuted == true);
        var pending = Assert.Single(content.OfType<AIToolCallContentPart>(), p => p.ProviderExecuted == false);
        Assert.Equal("Jakarta", Assert.IsType<JsonElement>(pending.Input).GetProperty("city").GetString());
        var second = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "kiosapi", Model = "agt-1",
            Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "assistant",
                    Content = [receipt, new AIToolCallContentPart
                    {
                        Type = "tool-call", ToolCallId = pending.ToolCallId, ToolName = pending.ToolName,
                        Input = pending.Input, ProviderExecuted = false, State = "output-available", Output = "32 degrees"
                    }]
                }]
            }
        });
        Assert.Equal("succeeded", second.Status);
        Assert.Equal("Sunny", Assert.Single(second.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal(4, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Terminal_failure_is_not_reported_as_success()
    {
        using var client = Client((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/threads")
            ? """{"id":"thr-1","agent_id":"agt-1"}"""
            : """{"id":"run-1","agent_id":"agt-1","thread_id":"thr-1","status":"failed","error":"insufficient_balance"}""")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "kiosapi", Model = "agt-1", Input = new AIInput { Text = "Hello" }
        }));
        Assert.Contains("insufficient_balance", error.Message);
    }

    [Fact]
    public async Task New_user_turn_reuses_thread_after_completed_run()
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            calls++;
            if (calls == 1) return Json("""{"id":"thr-1","agent_id":"agt-1"}""");
            Assert.Equal("/v1/threads/thr-1/runs", request.RequestUri!.AbsolutePath);
            var input = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct)).GetProperty("input").GetString();
            Assert.Equal(calls == 2 ? "Hello" : "Next", input);
            return Json(Done);
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(new AIRequest { ProviderId = "kiosapi", Model = "agt-1", Input = new AIInput { Text = "Hello" } });
        var second = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "kiosapi", Model = "agt-1",
            Input = new AIInput { Items =
            [
                new AIInputItem { Role = "assistant", Content = first.Output!.Items![0].Content },
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Next" }] }
            ] }
        });
        Assert.Equal("succeeded", second.Status);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Polls_queued_run_and_preserves_terminal_response_in_stream()
    {
        var calls = 0;
        using var client = Client((request, _) =>
        {
            calls++;
            return Task.FromResult(Json(calls switch
            {
                1 => """{"id":"thr-1","agent_id":"agt-1"}""",
                2 => """{"id":"run-1","agent_id":"agt-1","thread_id":"thr-1","status":"queued"}""",
                _ => Done
            }));
        });
        var events = new List<AIStreamEvent>();
        await foreach (var evt in Provider(client).StreamUnifiedAsync(new AIRequest { ProviderId = "kiosapi", Model = "agt-1", Input = new AIInput { Text = "Hi" } }))
            events.Add(evt);
        Assert.Equal(3, calls);
        Assert.Contains(events, e => e.Event.Type == "text-delta" && ((AITextDeltaEventData)e.Event.Data!).Delta == "Sunny");
        Assert.Contains(events, e => e.Event.Type == "finish" && JsonElement.DeepEquals(
            JsonSerializer.Deserialize<JsonElement>(Done), Assert.IsType<JsonElement>(e.Metadata!["kiosapi"])));
    }

    [Fact]
    public async Task Partial_tool_outputs_are_not_submitted()
    {
        var calls = 0;
        using var client = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(Json("""
                {"id":"run-1","agent_id":"agt-1","thread_id":"thr-1","status":"requires_action",
                 "pending_tool_calls":[{"id":"call-1","name":"one","arguments":"{}"},{"id":"call-2","name":"two","arguments":"{}"}]}
                """));
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "kiosapi", Model = "agt-1",
            Metadata = new Dictionary<string, object?> { ["kiosapi"] = new { thread_id = "thr-1", run_id = "run-1" } },
            Input = new AIInput { Items = [new AIInputItem { Role = "tool", Content =
            [
                new AIToolCallContentPart { Type = "tool-call", ToolCallId = "call-1", Output = "result", State = "output-available" }
            ] }] }
        }));
        Assert.Contains("every pending tool call", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Chat_completions_can_resume_a_client_tool_call()
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            calls++;
            if (calls == 1) return Json("""{"id":"thr-1","agent_id":"agt-1"}""");
            if (calls is 2 or 3) return Json(Pending);
            Assert.Equal("/v1/runs/run-1/tool_outputs", request.RequestUri!.AbsolutePath);
            var outputs = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct)).GetProperty("outputs");
            Assert.Equal("32 degrees", outputs[0].GetProperty("output").GetString());
            return Json(Done);
        });
        var provider = Provider(client);
        var first = await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
            """{"model":"kiosapi/agt-1","messages":[{"role":"user","content":"Hello"}]}""")!);
        var native = JsonSerializer.SerializeToNode(first, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var choice = native["choices"]![0]!;
        Assert.Equal("tool_calls", choice["finish_reason"]!.GetValue<string>());
        var assistant = choice["message"]!.DeepClone();
        var next = new JsonObject
        {
            ["model"] = "kiosapi/agt-1",
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "Hello" },
                assistant,
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-1", ["content"] = "32 degrees" }
            }
        };
        var second = await provider.CompleteChatAsync(next.Deserialize<ChatCompletionOptions>()!);
        Assert.Contains("Sunny", JsonSerializer.Serialize(second));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Pending_run_stream_emits_client_tool_call_and_tool_finish_reason()
    {
        var calls = 0;
        using var client = Client((_, _) => Task.FromResult(Json(++calls == 1
            ? """{"id":"thr-1","agent_id":"agt-1"}""" : Pending)));
        var events = new List<AIStreamEvent>();
        await foreach (var evt in Provider(client).StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "kiosapi", Model = "agt-1", Input = new AIInput { Text = "Hello" }
        })) events.Add(evt);
        var tool = Assert.Single(events, e => e.Event.Type == "tool-input-available" && e.Event.Id == "call-1");
        Assert.False(((AIToolInputAvailableEventData)tool.Event.Data!).ProviderExecuted);
        Assert.DoesNotContain(events, e => e.Event.Type == "tool-output-available" && e.Event.Id == "call-1");
        var finish = Assert.Single(events, e => e.Event.Type == "finish");
        Assert.Equal("tool-calls", ((AIFinishEventData)finish.Event.Data!).FinishReason);
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Streaming_adapters_return_polled_text_and_native_metadata(string endpoint)
    {
        var calls = 0;
        using var client = Client((_, _) => Task.FromResult(Json(++calls == 1
            ? """{"id":"thr-1","agent_id":"agt-1"}""" : Done)));
        var provider = Provider(client);
        var chunks = new List<string>();
        switch (endpoint)
        {
            case "ui":
                await foreach (var chunk in provider.StreamAsync(new ChatRequest
                {
                    Model = "kiosapi/agt-1",
                    Messages = [new UIMessage { Id = "user-1", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] }]
                })) chunks.Add(JsonSerializer.Serialize(chunk));
                break;
            case "chat":
                await foreach (var chunk in provider.CompleteChatStreamingAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                    """{"model":"kiosapi/agt-1","messages":[{"role":"user","content":"Hello"}]}""")!))
                    chunks.Add(JsonSerializer.Serialize(chunk));
                break;
            case "responses":
                await foreach (var chunk in provider.ResponsesStreamingAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    """{"model":"kiosapi/agt-1","input":"Hello"}""")!))
                    chunks.Add(JsonSerializer.Serialize(chunk));
                break;
            case "messages":
                await foreach (var chunk in provider.MessagesStreamingAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    """{"model":"kiosapi/agt-1","max_tokens":100,"messages":[{"role":"user","content":"Hello"}]}""")!, []))
                    chunks.Add(JsonSerializer.Serialize(chunk));
                break;
        }
        Assert.Equal(2, calls);
        Assert.Contains(chunks, chunk => chunk.Contains("Sunny"));
        Assert.Contains(chunks, chunk => chunk.Contains("cost_rupiah"));
    }

    private static KiosapiProvider Provider(HttpClient client) => new(new Keys(), new AsyncCacheHelper(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => "kios_test";
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
