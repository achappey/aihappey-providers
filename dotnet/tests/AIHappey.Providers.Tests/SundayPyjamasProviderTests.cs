using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SundayPyjamas;
using AIHappey.Unified.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class SundayPyjamasProviderTests
{
    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("sundaypyjamas"));
        Assert.IsType<SundayPyjamasProvider>(registry.GetModelProvider(scope.ServiceProvider, "sundaypyjamas"));
    }

    [Fact]
    public async Task Discovery_lists_agents_and_chat_model_and_caches_by_key()
    {
        var keys = new Keys();
        var calls = 0;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = Client((request, _) =>
        {
            calls++;
            Assert.Equal("/api/v1/managed-agents/agents", request.RequestUri!.AbsolutePath);
            Assert.Equal(keys.Key, request.Headers.Authorization!.Parameter);
            return Task.FromResult(Json("""{"agents":[{"id":"agent-1","name":"Research","description":"Market research","status":"active"}]}"""));
        });
        var provider = new SundayPyjamasProvider(keys, new AsyncCacheHelper(cache), new Factory(client));
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["sundaypyjamas/llama-3.3-70b-versatile", "sundaypyjamas/agent-1"], models.Select(model => model.Id));
        Assert.Equal("Market research", models[1].Description);
        Assert.Contains("agent", models[1].Tags!);
        await provider.ListModels();
        Assert.Equal(1, calls);
        keys.Key = "spj_ai_second";
        await provider.ListModels();
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Assist_preserves_native_options_events_timeline_and_session_receipt()
    {
        const string native = """{"session":{"end_user_id":"user-1","metadata":{"custom":null}},"model":"custom-model","future":{"n":9007199254740993,"list":[true,null,{},[]]}}""";
        var options = JsonSerializer.Deserialize<JsonElement>(native);
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal("suite.sundaypyjamas.com", request.RequestUri!.Host);
            Assert.Equal("spj_ai_test", request.Headers.Authorization!.Parameter);
            calls++;
            switch (calls)
            {
                case 1:
                    Assert.Equal("/api/v1/managed-agents/agents/agent-1/sessions", request.RequestUri.AbsolutePath);
                    var session = await Body(request, ct);
                    Assert.True(JsonElement.DeepEquals(options.GetProperty("session"), session));
                    return Json("""{"session":{"id":"sess-1","future":[true,null]}}""");
                case 2:
                    Assert.Equal("/api/v1/managed-agents/sessions/sess-1/messages", request.RequestUri.AbsolutePath);
                    var body = await Body(request, ct);
                    Assert.Equal("Hello", body.GetProperty("message").GetString());
                    Assert.Equal("custom-model", body.GetProperty("model").GetString());
                    Assert.True(JsonElement.DeepEquals(options.GetProperty("future"), body.GetProperty("future")));
                    Assert.False(body.TryGetProperty("session", out _));
                    Assert.False(body.TryGetProperty("secret", out _));
                    return Sse(": heartbeat\n\nevent: future\nid: evt-1\ndata: {\"number\":9007199254740993,\ndata: \"nested\":[true,null]}\n\ndata: [DONE]");
                default:
                    Assert.Equal("/api/v1/managed-agents/sessions/sess-1/events", request.RequestUri.AbsolutePath);
                    return Json("""{"events":[{"id":"msg-1","type":"message","role":"assistant","content":"Reply","metadata":{"custom":null}}],"future":{}}""");
            }
        });
        var request = Request(new() { ["sundaypyjamas"] = options, ["other"] = new { secret = true } });
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(native, options.GetRawText());
        var content = result.Output!.Items![0].Content!;
        Assert.Equal("Reply", Assert.Single(content.OfType<AITextContentPart>()).Text);
        var tool = Assert.Single(content.OfType<AIToolCallContentPart>());
        Assert.Equal("sundaypyjamas_session", tool.ToolName);
        var raw = JsonSerializer.SerializeToElement(result.Metadata!["sundaypyjamas"], JsonSerializerOptions.Web);
        Assert.Equal("sess-1", raw.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.Null, raw.GetProperty("timeline").GetProperty("events")[0]
            .GetProperty("metadata").GetProperty("custom").ValueKind);
        Assert.Equal("{\"number\":9007199254740993,\n\"nested\":[true,null]}", raw.GetProperty("events")[0].GetProperty("data").GetString());
        Assert.Equal("[DONE]", raw.GetProperty("events")[1].GetProperty("data").GetString());
        Assert.Null(result.Usage);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Receipt_reuses_session_and_does_not_replay_old_assistant_messages()
    {
        var calls = 0;
        using var client = Client((request, _) =>
        {
            calls++;
            return Task.FromResult(calls switch
            {
                1 => Json("""{"session":{"id":"sess-1"}}"""),
                2 or 5 => Sse("data: {}\n\n"),
                3 or 4 => Json("""{"events":[{"id":"old","type":"message","role":"assistant","content":"Old"}]}"""),
                _ => Json("""{"events":[{"id":"old","type":"message","role":"assistant","content":"Old"},{"id":"new","type":"message","role":"assistant","content":"New"}]}""")
            });
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request());
        var next = new AIRequest
        {
            ProviderId = "sundaypyjamas", Model = "sundaypyjamas/agent-1",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem { Role = "assistant", Content = first.Output!.Items![0].Content },
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Next" }] }
                ]
            }
        };
        var second = await provider.ExecuteUnifiedAsync(next);
        Assert.Equal("New", Assert.Single(second.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task Autonomous_run_preserves_raw_artifacts_and_native_metadata()
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.EndsWith("/agents/agent-1/runs", request.RequestUri!.AbsolutePath);
                var body = await Body(request, ct);
                Assert.Equal("Hello", body.GetProperty("task").GetString());
                Assert.Equal("sess-existing", body.GetProperty("sessionId").GetString());
                Assert.Equal("message-1", body.GetProperty("metadata").GetProperty("messageId").GetString());
                Assert.False(body.TryGetProperty("mode", out _));
                return Json("""{"runId":"run-1","sessionId":"sess-existing","created_at":"2026-07-08T10:05:00Z"}""");
            }
            Assert.EndsWith("/agents/agent-1/runs/run-1", request.RequestUri!.AbsolutePath);
            return Json("""{"run":{"id":"run-1","status":"completed"},"events":[{"payload":{"custom":null}}],"artifacts":[{"filename":"report.pdf","size_bytes":9007199254740993}]}""");
        });
        var result = await Provider(client).ExecuteUnifiedAsync(Request(new()
        {
            ["sundaypyjamas"] = new { mode = "autonomous", sessionId = "sess-existing", metadata = new { messageId = "message-1" } }
        }));
        var raw = JsonSerializer.SerializeToElement(result.Metadata!["sundaypyjamas"]);
        Assert.Equal(9007199254740993L, raw.GetProperty("run").GetProperty("artifacts")[0].GetProperty("size_bytes").GetInt64());
        Assert.Single(result.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Chat_stream_is_raw_text_and_preserves_conversation_history()
    {
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal("/api/v1/chat", request.RequestUri!.AbsolutePath);
            var body = await Body(request, ct);
            Assert.Equal("Hello", body.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("llama-3.3-70b-versatile", body.GetProperty("model").GetString());
            return Sse("Hello\nworld \u2603");
        });
        var request = new AIRequest
        {
            ProviderId = "sundaypyjamas", Model = "sundaypyjamas/llama-3.3-70b-versatile",
            Input = new AIInput { Text = "Hello" }
        };
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal("Hello\nworld \u2603", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
    }

    [Theory]
    [InlineData("assist")]
    [InlineData("autonomous")]
    public async Task Native_http_errors_retain_status_and_body(string mode)
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"workspace limit"}""")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(Request(new()
        {
            ["sundaypyjamas"] = new { mode, sessionId = "sess-1" }
        })));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("workspace limit", error.Message);
    }

    [Fact]
    public async Task Failed_run_is_not_success()
    {
        var calls = 0;
        using var client = Client((_, _) => Task.FromResult(Json(++calls == 1
            ? """{"runId":"run-1","sessionId":"sess-1"}"""
            : """{"run":{"status":"failed"},"events":[{"payload":{"error":"tool failed"}}],"artifacts":[]}""")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(Request(new()
        {
            ["sundaypyjamas"] = new { mode = "autonomous" }
        })));
        Assert.Contains("tool failed", error.Message);
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Public_streaming_surfaces_preserve_reply_receipt_and_native_metadata(string surface)
    {
        var calls = 0;
        using var client = Client((_, _) => Task.FromResult(++calls switch
        {
            1 => Json("""{"session":{"id":"sess-1"}}"""),
            2 => Sse("event: custom\ndata: {\"future\":true}\n\n"),
            _ => Json("""{"events":[{"id":"reply-1","type":"message","role":"assistant","content":"Hello world","metadata":{"raw_number":9007199254740993}}]}""")
        }));
        var provider = Provider(client);
        var chunks = new List<string>();
        switch (surface)
        {
            case "ui":
                await foreach (var part in provider.StreamAsync(new ChatRequest
                {
                    Model = "sundaypyjamas/agent-1",
                    Messages = [new UIMessage { Id = "user-1", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] }]
                })) chunks.Add(JsonSerializer.Serialize(part));
                break;
            case "chat":
                await foreach (var part in provider.CompleteChatStreamingAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                    """{"model":"sundaypyjamas/agent-1","messages":[{"role":"user","content":"Hello"}]}""")!))
                    chunks.Add(JsonSerializer.Serialize(part));
                break;
            case "responses":
                await foreach (var part in provider.ResponsesStreamingAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    """{"model":"sundaypyjamas/agent-1","input":"Hello"}""")!))
                    chunks.Add(JsonSerializer.Serialize(part));
                break;
            case "messages":
                await foreach (var part in provider.MessagesStreamingAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    """{"model":"sundaypyjamas/agent-1","max_tokens":100,"messages":[{"role":"user","content":"Hello"}]}""")!, []))
                    chunks.Add(JsonSerializer.Serialize(part));
                break;
        }
        Assert.Contains(chunks, chunk => chunk.Contains("Hello world"));
        Assert.Contains(chunks, chunk => chunk.Contains("sess-1"));
        Assert.Contains(chunks, chunk => chunk.Contains("9007199254740993"));
    }

    [Fact]
    public async Task Invalid_catalog_and_cancelled_requests_fail_explicitly()
    {
        using var client = Client((_, _) => Task.FromResult(Json("{}")));
        var provider = Provider(client);
        await Assert.ThrowsAsync<JsonException>(() => provider.ListModels());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ExecuteUnifiedAsync(Request(), cancelled.Token));
    }

    private static AIRequest Request(Dictionary<string, object?>? metadata = null) => new()
    {
        ProviderId = "sundaypyjamas", Model = "sundaypyjamas/agent-1",
        Input = new AIInput { Text = "Hello" }, Metadata = metadata
    };
    private static async Task<JsonElement> Body(HttpRequestMessage request, CancellationToken ct)
        => JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
    private static SundayPyjamasProvider Provider(HttpClient client)
        => new(new Keys(), new AsyncCacheHelper(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));
    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private sealed class Keys : IApiKeyResolver
    {
        public string? Key { get; set; } = "spj_ai_test";
        public string? Resolve(string provider) => provider == "sundaypyjamas" ? Key : null;
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
