using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Spawnlabs;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Role = AIHappey.Vercel.Models.Role;

namespace AIHappey.Tests;

public sealed class SpawnlabsProviderTests
{
    private const string Created = """{"session_id":"session_1","status":"running","future":[9007199254740993,null,{}]}""";
    private const string Completed = """
        {"status":"completed","running":false,"last_assistant_message":"Done","artifacts":[{"url":"https://example.com/report","future":true}],
         "cost_usd":0.0342,"total_turns":1,"future":{"number":9007199254740993,"array":[true,null,{},[]]}}
        """;
    private const string Waiting = """
        {"status":"waiting_for_answer","running":true,"last_assistant_message":"Which database?",
         "waiting_for_answer":{"tool_use_id":"tu_1","question":{"question":"Which database?","options":["PostgreSQL","SQLite"]}}}
        """;
    private const string Native = """
        {"model":"native-model","system_prompt":"native system","skill_ids":["skill_1"],"prompt":"native prompt",
         "future":{"number":9007199254740993,"array":[true,null,{},[]]},"futureNull":null}
        """;

    [Fact]
    public async Task Provider_is_registered_and_discovers_all_definitions_without_network()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<SpawnlabsProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "spawnlabs"));
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network."));
        Assert.Equal(["spawnlabs/base", "spawnlabs/pitch_deck", "spawnlabs/presentation", "spawnlabs/researcher", "spawnlabs/outreach"],
            (await Provider(client).ListModels()).Select(model => model.Id));
    }

    [Fact]
    public async Task Create_preserves_native_options_raw_responses_and_does_not_mutate_input_or_headers()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("test-key", http.Headers.GetValues("X-API-Key").Single());
            if (calls++ == 0)
            {
                Assert.Equal(HttpMethod.Post, http.Method);
                Assert.Equal("https://spawnapi.spawnlabs.ai/api/v1/sessions", http.RequestUri!.AbsoluteUri);
                var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                foreach (var property in JsonSerializer.Deserialize<JsonElement>(Native).EnumerateObject())
                    Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
                Assert.Equal("researcher", body.GetProperty("definition").GetString());
                Assert.False(body.TryGetProperty("other", out _));
                return Json(Created);
            }
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("https://spawnapi.spawnlabs.ai/api/v1/sessions/session_1/poll", http.RequestUri!.AbsoluteUri);
            Assert.Null(http.Content);
            return Json(Completed);
        });
        var request = Request(Native);
        var before = JsonSerializer.Serialize(request);
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal(2, calls);
        Assert.Equal("completed", result.Status);
        Assert.Null(result.Usage);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("X-API-Key"));
        var metadata = Assert.IsType<JsonElement>(result.Metadata!["spawnlabs"]);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Created), metadata.GetProperty("create")));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Completed), metadata.GetProperty("poll")));
        Assert.Equal("session_1", metadata.GetProperty("session_id").GetString());
        Assert.Equal("Done", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        var receipt = Assert.Single(result.Output.Items[0].Content!.OfType<AIToolCallContentPart>());
        Assert.True(receipt.ProviderExecuted);
        Assert.Equal(metadata.GetRawText(), Assert.IsType<CallToolResult>(receipt.Output).StructuredContent!.Value.GetRawText());
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("vercel", true)]
    public async Task Adapters_preserve_native_options_and_poll_until_completed(string endpoint, bool streaming)
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            if (calls++ == 0)
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("native-model", body.GetProperty("model").GetString());
                Assert.Equal("native system", body.GetProperty("system_prompt").GetString());
                Assert.Equal("native prompt", body.GetProperty("prompt").GetString());
                Assert.Equal("skill_1", body.GetProperty("skill_ids")[0].GetString());
                Assert.Equal(9007199254740993L, body.GetProperty("future").GetProperty("number").GetInt64());
                Assert.False(body.TryGetProperty("pollIntervalMs", out _));
                return Json(Created);
            }
            return Json(calls == 2 ? """{"status":"running","future":null}""" : Completed);
        });
        var provider = Provider(client);
        var extra = "\"body\":" + Native + ",\"pollIntervalMs\":1";
        switch (endpoint)
        {
            case "chat":
                var chat = JsonSerializer.Deserialize<ChatCompletionOptions>(
                    $$$"""{"model":"spawnlabs/researcher","messages":[{"role":"user","content":"Hello"}],{{{extra}}} }""")!;
                if (streaming) Assert.Contains("9007199254740993", await Drain(provider.CompleteChatStreamingAsync(chat)));
                else Assert.Contains("9007199254740993", JsonSerializer.Serialize(await provider.CompleteChatAsync(chat)));
                break;
            case "responses":
                var responses = JsonSerializer.Deserialize<ResponseRequest>(
                    $$$"""{"model":"spawnlabs/researcher","input":"Hello",{{{extra}}} }""")!;
                if (streaming) Assert.Contains("9007199254740993", await Drain(provider.ResponsesStreamingAsync(responses)));
                else Assert.Contains("9007199254740993", JsonSerializer.Serialize(await provider.ResponsesAsync(responses)));
                break;
            case "messages":
                var messages = JsonSerializer.Deserialize<MessagesRequest>(
                    $$$"""{"model":"spawnlabs/researcher","max_tokens":100,"messages":[{"role":"user","content":"Hello"}],{{{extra}}} }""")!;
                if (streaming) Assert.Contains("9007199254740993", await Drain(provider.MessagesStreamingAsync(messages, [])));
                else Assert.Contains("9007199254740993", JsonSerializer.Serialize(await provider.MessagesAsync(messages, [])));
                break;
            default:
                var vercel = JsonSerializer.Deserialize<AIHappey.Vercel.Models.ChatRequest>(
                    $$$"""{"model":"spawnlabs/researcher","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hello"}]}],"providerMetadata":{"spawnlabs":{ {{{extra}}} } } }""")!;
                Assert.Contains("9007199254740993", await Drain(provider.StreamAsync(vercel)));
                break;
        }
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task History_receipt_resumes_session_and_sends_only_follow_up_prompt()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            var call = calls++;
            if (call == 0) return Json(Created);
            if (call == 3)
            {
                Assert.EndsWith("/session_1/message", http.RequestUri!.AbsoluteUri);
                var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("latest", body.GetProperty("prompt").GetString());
                Assert.Single(body.EnumerateObject());
                return Json("""{"session_id":"session_1","status":"running"}""");
            }
            Assert.EndsWith("/session_1/poll", http.RequestUri!.AbsoluteUri);
            return Json(Completed);
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request());
        var next = Request("""{"model":"not-forwarded","skill_ids":["not-forwarded"]}""");
        next.Input!.Items!.Insert(0, new AIInputItem
        {
            Role = "assistant", Content = first.Output!.Items![0].Content
        });
        await provider.ExecuteUnifiedAsync(next);
        Assert.Equal(5, calls);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    [InlineData("vercel")]
    public async Task Adapters_round_trip_session_receipts_for_follow_up_turns(string endpoint)
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            var call = calls++;
            if (call == 0) return Json(Created);
            if (call == 3)
            {
                Assert.EndsWith("/session_1/message", http.RequestUri!.AbsoluteUri);
                var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("follow up", body.GetProperty("prompt").GetString());
                return Json("""{"status":"running","session_id":"session_1"}""");
            }
            Assert.EndsWith("/session_1/poll", http.RequestUri!.AbsoluteUri);
            return Json(Completed);
        });
        var provider = Provider(client);
        switch (endpoint)
        {
            case "chat":
                var chat = await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                    """{"model":"researcher","messages":[{"role":"user","content":"Hello"}]}""")!);
                var message = JsonSerializer.SerializeToElement(chat).GetProperty("choices")[0].GetProperty("message");
                await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                    $$"""{"model":"researcher","messages":[{{message.GetRawText()}},{"role":"user","content":"follow up"}]}""")!);
                break;
            case "responses":
                var response = await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    """{"model":"researcher","input":"Hello"}""")!);
                var output = JsonSerializer.SerializeToElement(response).GetProperty("output").GetRawText();
                await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    $$"""{"model":"researcher","input":[{{output[1..^1]}},{"role":"user","content":"follow up"}]}""")!);
                break;
            case "messages":
                var messages = await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    """{"model":"researcher","max_tokens":100,"messages":[{"role":"user","content":"Hello"}]}""")!, []);
                var content = JsonSerializer.SerializeToElement(messages).GetProperty("content").GetRawText();
                await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    $$"""{"model":"researcher","max_tokens":100,"messages":[{"role":"assistant","content":{{content}}},{"role":"user","content":"follow up"}]}""")!, []);
                break;
            default:
                var first = await provider.ExecuteUnifiedAsync(Request());
                var receipt = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
                await Drain(provider.StreamAsync(new ChatRequest
                {
                    Model = "researcher",
                    Messages =
                    [
                        new UIMessage
                        {
                            Id = "assistant-1", Role = Role.assistant,
                            Parts = [new ToolInvocationPart
                            {
                                Type = "tool-spawnlabs_session", ToolName = receipt.ToolName, ToolCallId = receipt.ToolCallId,
                                ProviderExecuted = true, State = "output-available", Input = receipt.Input!, Output = receipt.Output!
                            }]
                        },
                        new UIMessage { Id = "user-2", Role = Role.user, Parts = [new TextUIPart { Text = "follow up" }] }
                    ]
                }));
                break;
        }
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task Create_maps_latest_user_prompt_and_system_instructions()
    {
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get) return Json(Completed);
            var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("latest", body.GetProperty("prompt").GetString());
            Assert.Equal("instructions\n\nsystem", body.GetProperty("system_prompt").GetString());
            return Json(Created);
        });
        var request = Request(instructions: "instructions");
        request.Input!.Items!.Insert(0, new AIInputItem
        { Role = "system", Content = [new AITextContentPart { Type = "text", Text = "system" }] });
        await Provider(client).ExecuteUnifiedAsync(request);
    }

    [Fact]
    public async Task Waiting_emits_elicitation_and_native_answer_resumes_the_pending_question()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            var call = calls++;
            if (call == 0) return Json(Created);
            if (call == 1 || call == 2) return Json(Waiting);
            if (call == 3)
            {
                Assert.EndsWith("/session_1/answer", http.RequestUri!.AbsoluteUri);
                var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("tu_1", body.GetProperty("tool_use_id").GetString());
                Assert.Equal("PostgreSQL", body.GetProperty("answers").GetProperty("database").GetString());
                Assert.Equal(2, body.EnumerateObject().Count());
                return Json("""{"status":"answered","session_id":"session_1","tool_use_id":"tu_1"}""");
            }
            return Json(Completed);
        });
        var provider = Provider(client);
        var waiting = await provider.ExecuteUnifiedAsync(Request());
        Assert.Equal("incomplete", waiting.Status);
        var form = waiting.Output!.Items![0].Content!.OfType<AIToolCallContentPart>()
            .Single(tool => tool.ToolName == "ai_input_required");
        Assert.False(form.ProviderExecuted);
        Assert.Contains("Which database?", JsonSerializer.Serialize(form.Input));
        Assert.Contains("SQLite", JsonSerializer.Serialize(waiting.Metadata));
        var answer = await provider.ExecuteUnifiedAsync(Request(
            """{"session_id":"session_1","tool_use_id":"tu_1","answers":{"database":"PostgreSQL"}}"""));
        Assert.Equal("completed", answer.Status);
        Assert.Equal(5, calls);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("user")]
    public async Task Elicitation_result_resumes_the_session(string role)
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            if (calls++ == 0) return Json(Waiting);
            if (http.Method == HttpMethod.Post)
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("tu_1", body.GetProperty("tool_use_id").GetString());
                Assert.Equal("SQLite", body.GetProperty("answers").GetProperty("database").GetString());
                return Json("""{"status":"answered"}""");
            }
            return Json(Completed);
        });
        var request = Request("""{"session_id":"session_1"}""");
        request.Input!.Items!.Add(new AIInputItem
        {
            Role = role,
            Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolName = "ai_input_required", ToolCallId = "spawnlabs-answer-session_1-tu_1",
                Output = JsonSerializer.Deserialize<JsonElement>(
                    """{"action":"accept","content":{"answers_json":"{\"database\":\"SQLite\"}"}}""")
            }]
        });
        Assert.Equal("completed", (await Provider(client).ExecuteUnifiedAsync(request)).Status);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("running", "incomplete", "other")]
    [InlineData("stopped", "incomplete", "other")]
    [InlineData("error", "failed", "error")]
    public async Task Poll_only_and_poll_exhaustion_preserve_truthful_status(string status, string expected, string finishReason)
    {
        using var client = Client((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            return Task.FromResult(Json($$"""{"status":"{{status}}","future":null}"""));
        });
        var provider = Provider(client);
        var request = Request("""{"session_id":"session_1","pollOnly":true,"maxPolls":1}""");
        Assert.Equal(expected, (await provider.ExecuteUnifiedAsync(request)).Status);
        var events = new List<AIStreamEvent>();
        await foreach (var evt in provider.StreamUnifiedAsync(request)) events.Add(evt);
        Assert.Equal(finishReason, Assert.IsType<AIFinishEventData>(events.Last().Event.Data).FinishReason);
        Assert.Equal(status == "error", events.Any(evt => evt.Event.Type == "error"));
    }

    [Fact]
    public async Task Streaming_persists_receipt_before_polling_and_cancellation_does_not_stop_the_remote_session()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        using var client = Client((http, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, http.Method);
            return Task.FromResult(Json(Created));
        });
        var events = new List<AIStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in Provider(client).StreamUnifiedAsync(Request(), cts.Token))
            {
                events.Add(evt);
                if (evt.Event.Type == "tool-output-available") cts.Cancel();
            }
        });
        Assert.Contains("session_1", JsonSerializer.Serialize(events));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("""{"pollOnly":true}""")]
    [InlineData("""{"session_id":null}""")]
    [InlineData("""{"pollIntervalMs":0}""")]
    [InlineData("""{"maxPolls":"one"}""")]
    [InlineData("""{"prompt":false}""")]
    [InlineData("""{"definition":"base"}""")]
    [InlineData("""{"definition":null}""")]
    [InlineData("""{"body":null}""")]
    [InlineData("""{"tool_use_id":"tu_1"}""")]
    [InlineData("""{"answers":[]}""")]
    [InlineData("""{"session_id":"s","pollOnly":true,"tool_use_id":"t","answers":{}}""")]
    [InlineData("[]")]
    public async Task Invalid_options_fail_before_network(string options)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request(options)));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("stopped")]
    [InlineData("error")]
    public async Task Follow_up_does_not_send_messages_to_unready_sessions(string status)
    {
        var calls = 0;
        using var client = Client((http, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, http.Method);
            return Task.FromResult(Json($$"""{"status":"{{status}}"}"""));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(
            Request("""{"session_id":"session_1"}""")));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Stale_native_answers_are_rejected_without_mutation()
    {
        using var client = Client((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            return Task.FromResult(Json(Waiting));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(
            Request("""{"session_id":"session_1","tool_use_id":"stale","answers":{}}""")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"status":"unexpected"}""")]
    public async Task Malformed_poll_responses_fail_explicitly(string response)
    {
        using var client = Client((_, _) => Task.FromResult(Json(response)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ExecuteUnifiedAsync(
            Request("""{"session_id":"session_1","pollOnly":true}""")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"session_id":""}""")]
    public async Task Malformed_create_responses_fail_explicitly(string response)
    {
        using var client = Client((_, _) => Task.FromResult(Json(response)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
    }

    [Fact]
    public async Task Unsupported_media_input_is_not_silently_discarded()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network."));
        var request = Request();
        request.Input!.Items!.Add(new AIInputItem
        { Role = "user", Content = [new AIFileContentPart { Type = "file", MediaType = "image/png", Data = "https://example.com/image.png" }] });
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ExecuteUnifiedAsync(request));
    }

    [Fact]
    public async Task Conflicting_session_receipts_fail_before_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network."));
        var request = Request("""{"session_id":"session_2"}""");
        request.Input!.Items!.Insert(0, new AIInputItem
        {
            Role = "assistant",
            Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolName = "spawnlabs_session", ToolCallId = "receipt", ProviderExecuted = true,
                Output = JsonSerializer.Deserialize<JsonElement>("""{"session_id":"session_1","definition":"researcher"}""")
            }]
        });
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(request));
    }

    [Fact]
    public async Task Upstream_errors_keep_http_status_and_body()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        { Content = new StringContent("""{"detail":{"error":"insufficient_credits","message":"Top up"}}""") }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
        Assert.Equal(HttpStatusCode.PaymentRequired, error.StatusCode);
        Assert.Contains("insufficient_credits", error.Message);
    }

    private static AIRequest Request(string options = "{}", string? instructions = null) => new()
    {
        ProviderId = "spawnlabs", Model = "spawnlabs/researcher",
        Instructions = instructions,
        Metadata = new() { ["spawnlabs"] = JsonSerializer.Deserialize<JsonElement>(options), ["other"] = new { secret = "not forwarded" } },
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

    private static SpawnlabsProvider Provider(HttpClient client) => new(new Keys(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static async Task<string> Drain<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        Assert.NotEmpty(items);
        return JsonSerializer.Serialize(items);
    }
    private sealed class Keys : IApiKeyResolver { public string Resolve(string provider) => "test-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return send(request, cancellationToken);
        }
    }
}
