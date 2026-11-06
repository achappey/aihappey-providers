using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Nolgia;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Role = AIHappey.Vercel.Models.Role;

namespace AIHappey.Tests;

public sealed class NolgiaProviderTests
{
    private const string Accepted = """
        {"user_message_id":"user_1","agent_message_id":"reply_1","session_id":"session_1",
         "session":{"id":"session_1"},"future":[9007199254740993,null,{}]}
        """;
    private const string Complete = """
        {"id":"reply_1","session_id":"session_1","status":"complete","content":"Which length?",
         "question":{"text":"Which length?","options":[{"label":"5 seconds","description":"One beat"}]},
         "feedback":{"rating":"up"},"future":{"number":9007199254740993,"array":[true,null,{},[]]}}
        """;
    private const string Assets = """
        {"assets":[{"id":"asset_1","url":"https://example.com/output.mp4","future":null}],"future":[{},[],null]}
        """;
    private const string Native = """
        {"content":"Native prompt","new_session":{"name":"Product launch","project_id":"project_1"},
         "future":{"number":9007199254740993,"array":[true,null,{},[]]},"futureNull":null}
        """;

    [Fact]
    public async Task Provider_is_registered_and_lists_account_configured_agent_without_network()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<NolgiaProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "nolgia"));
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network."));
        Assert.Equal("nolgia/agent", Assert.Single(await Provider(client).ListModels()).Id);
    }

    [Fact]
    public async Task Create_passes_native_fields_and_preserves_raw_receipts_questions_feedback_and_assets()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("Bearer", http.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization.Parameter);
            switch (calls++)
            {
                case 0:
                    Assert.Equal(HttpMethod.Post, http.Method);
                    Assert.Equal("https://api.nolgia.ai/v1/agent/messages", http.RequestUri!.AbsoluteUri);
                    var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                    foreach (var property in JsonSerializer.Deserialize<JsonElement>(Native).EnumerateObject())
                        Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
                    Assert.False(body.TryGetProperty("other", out _));
                    return Json(Accepted);
                case 1:
                    Assert.Equal(HttpMethod.Get, http.Method);
                    Assert.Equal("https://api.nolgia.ai/v1/agent/messages/reply_1", http.RequestUri!.AbsoluteUri);
                    Assert.Null(http.Content);
                    return Json(Complete);
                default:
                    Assert.Equal("https://api.nolgia.ai/v1/agent/sessions/session_1/assets", http.RequestUri!.AbsoluteUri);
                    return Json(Assets);
            }
        });
        var request = Request(Native);
        var before = JsonSerializer.Serialize(request);
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal(3, calls);
        Assert.Equal("completed", result.Status);
        Assert.Null(result.Usage);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        var metadata = Assert.IsType<JsonElement>(result.Metadata!["nolgia"]);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Accepted), metadata.GetProperty("submit")));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Complete), metadata.GetProperty("message")));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Assets), metadata.GetProperty("assets")));
        Assert.Equal("session_1", metadata.GetProperty("session_id").GetString());
        Assert.Equal("Which length?", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
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
    public async Task Adapters_preserve_native_metadata_and_poll_the_accepted_reply(string endpoint, bool streaming)
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            if (calls++ == 0)
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                foreach (var property in JsonSerializer.Deserialize<JsonElement>(Native).EnumerateObject())
                    Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)));
                Assert.False(body.TryGetProperty("pollIntervalMs", out _));
                Assert.False(body.TryGetProperty("timeoutMs", out _));
                return Json(Accepted);
            }
            if (http.RequestUri!.AbsolutePath.EndsWith("/assets")) return Json(Assets);
            Assert.EndsWith("/agent/messages/reply_1", http.RequestUri.AbsolutePath);
            return Json(calls == 2 ? """{"status":"pending","queued":true}""" : Complete);
        });
        var provider = Provider(client);
        var extra = "\"body\":" + Native + ",\"pollIntervalMs\":1,\"timeoutMs\":10000";
        string output;
        switch (endpoint)
        {
            case "chat":
                var chat = JsonSerializer.Deserialize<ChatCompletionOptions>(
                    $$$"""{"model":"nolgia/agent","messages":[{"role":"user","content":"Hello"}],{{{extra}}} }""")!;
                output = streaming ? await Drain(provider.CompleteChatStreamingAsync(chat))
                    : JsonSerializer.Serialize(await provider.CompleteChatAsync(chat));
                break;
            case "responses":
                var responses = JsonSerializer.Deserialize<ResponseRequest>(
                    $$$"""{"model":"nolgia/agent","input":"Hello",{{{extra}}} }""")!;
                output = streaming ? await Drain(provider.ResponsesStreamingAsync(responses))
                    : JsonSerializer.Serialize(await provider.ResponsesAsync(responses));
                break;
            case "messages":
                var messages = JsonSerializer.Deserialize<MessagesRequest>(
                    $$$"""{"model":"nolgia/agent","max_tokens":100,"messages":[{"role":"user","content":"Hello"}],{{{extra}}} }""")!;
                output = streaming ? await Drain(provider.MessagesStreamingAsync(messages, []))
                    : JsonSerializer.Serialize(await provider.MessagesAsync(messages, []));
                break;
            default:
                var vercel = JsonSerializer.Deserialize<ChatRequest>(
                    $$$"""{"model":"nolgia/agent","messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Hello"}]}],"providerMetadata":{"nolgia":{ {{{extra}}} } } }""")!;
                output = await Drain(provider.StreamAsync(vercel));
                break;
        }
        Assert.Contains("9007199254740993", output);
        Assert.Contains("asset_1", output);
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    [InlineData("vercel")]
    public async Task Session_receipts_round_trip_to_send_questions_answers_as_normal_messages(string endpoint)
    {
        var submits = 0;
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
                if (submits++ == 0) Assert.Equal(JsonValueKind.Object, body.GetProperty("new_session").ValueKind);
                else
                {
                    Assert.Equal("session_1", body.GetProperty("session_id").GetString());
                    Assert.Equal("5 seconds", body.GetProperty("content").GetString());
                    Assert.False(body.TryGetProperty("new_session", out _));
                }
                return Json(Accepted);
            }
            return Json(http.RequestUri!.AbsolutePath.EndsWith("/assets") ? Assets : Complete);
        });
        var provider = Provider(client);
        switch (endpoint)
        {
            case "chat":
                var chat = await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                    """{"model":"agent","messages":[{"role":"user","content":"Hello"}]}""")!);
                var message = JsonSerializer.SerializeToElement(chat).GetProperty("choices")[0].GetProperty("message");
                await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                    $$"""{"model":"agent","messages":[{{message.GetRawText()}},{"role":"user","content":"5 seconds"}]}""")!);
                break;
            case "responses":
                var response = await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    """{"model":"agent","input":"Hello"}""")!);
                var output = JsonSerializer.SerializeToElement(response).GetProperty("output").GetRawText();
                await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    $$"""{"model":"agent","input":[{{output[1..^1]}},{"role":"user","content":"5 seconds"}]}""")!);
                break;
            case "messages":
                var messages = await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    """{"model":"agent","max_tokens":100,"messages":[{"role":"user","content":"Hello"}]}""")!, []);
                var content = JsonSerializer.SerializeToElement(messages).GetProperty("content").GetRawText();
                await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    $$"""{"model":"agent","max_tokens":100,"messages":[{"role":"assistant","content":{{content}}},{"role":"user","content":"5 seconds"}]}""")!, []);
                break;
            default:
                var first = await provider.ExecuteUnifiedAsync(Request());
                var receipt = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
                await Drain(provider.StreamAsync(new ChatRequest
                {
                    Model = "agent",
                    Messages =
                    [
                        new UIMessage
                        {
                            Id = "assistant-1", Role = Role.assistant,
                            Parts = [new ToolInvocationPart
                            {
                                Type = "tool-nolgia_session", ToolName = receipt.ToolName, ToolCallId = receipt.ToolCallId,
                                ProviderExecuted = true, State = "output-available", Input = receipt.Input!, Output = receipt.Output!
                            }]
                        },
                        new UIMessage { Id = "user-2", Role = Role.user, Parts = [new TextUIPart { Text = "5 seconds" }] }
                    ]
                }));
                break;
        }
        Assert.Equal(2, submits);
    }

    [Fact]
    public async Task Steering_without_reply_id_returns_an_incomplete_receipt_without_polling()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            calls++;
            var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(body.GetProperty("steering").GetBoolean());
            Assert.Equal("session_1", body.GetProperty("session_id").GetString());
            return Json("""{"user_message_id":"steer_1"}""");
        });
        var response = await Provider(client).ExecuteUnifiedAsync(Request("""{"session_id":"session_1","steering":true}"""));
        Assert.Equal("incomplete", response.Status);
        Assert.Equal(1, calls);
        Assert.Single(response.Output!.Items![0].Content!);
    }

    [Theory]
    [InlineData("failed", "failed")]
    [InlineData("interrupted", "incomplete")]
    public async Task Terminal_errors_keep_the_served_error_code_and_assets(string status, string expected)
    {
        using var client = Client((http, _) => Task.FromResult(Json(http.Method == HttpMethod.Post ? Accepted
            : http.RequestUri!.AbsolutePath.EndsWith("/assets") ? Assets
            : $$"""{"status":"{{status}}","error":"Turn stopped","error_code":"backend_error"}""")));
        var response = await Provider(client).ExecuteUnifiedAsync(Request());
        Assert.Equal(expected, response.Status);
        Assert.Equal("Turn stopped", Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Contains("backend_error", JsonSerializer.Serialize(response));
        Assert.Contains("asset_1", JsonSerializer.Serialize(response));
        var events = new List<AIStreamEvent>();
        await foreach (var evt in Provider(client).StreamUnifiedAsync(Request())) events.Add(evt);
        Assert.Contains(events, evt => evt.Event.Type == "error");
    }

    [Theory]
    [InlineData(409, """{"code":"session_busy","pending_agent_message_id":"pending_1"}""")]
    [InlineData(429, """{"code":"session_busy"}""")]
    [InlineData(402, """{"status":402,"title":"Credits","detail":"Top up","code":"insufficient_credits"}""")]
    [InlineData(401, "Unauthorized")]
    public async Task Http_errors_are_not_retried_and_preserve_status_and_raw_body(int status, string body)
    {
        var calls = 0;
        using var client = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains(body, error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Timeout_and_cancellation_stop_polling_without_interrupting_the_agent()
    {
        var posts = 0;
        using var client = Client((http, _) =>
        {
            if (http.Method == HttpMethod.Post) posts++;
            Assert.DoesNotContain("interrupt", http.RequestUri!.AbsolutePath);
            Assert.DoesNotContain("assets", http.RequestUri.AbsolutePath);
            return Task.FromResult(Json(http.Method == HttpMethod.Post ? Accepted : """{"status":"pending"}"""));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ExecuteUnifiedAsync(
            Request("""{"timeoutMs":100,"pollIntervalMs":1000}""")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ExecuteUnifiedAsync(Request(), cancellation.Token));
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"body":null}""")]
    [InlineData("""{"session_id":""}""")]
    [InlineData("""{"new_session":null}""")]
    [InlineData("""{"session_id":"s","new_session":{}}""")]
    [InlineData("""{"content":false}""")]
    [InlineData("""{"timeoutMs":0}""")]
    [InlineData("""{"pollIntervalMs":0}""")]
    [InlineData("""{"maxPollIntervalMs":1}""")]
    public async Task Invalid_native_options_fail_before_network(string options)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request(options)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"session_id":"session_1"}""")]
    [InlineData("""{"session_id":"session_1","user_message_id":"user_1"}""")]
    public async Task Malformed_acceptance_fails_explicitly(string accepted)
    {
        using var client = Client((_, _) => Task.FromResult(Json(accepted)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"status":"unknown"}""")]
    public async Task Malformed_reply_fails_explicitly(string reply)
    {
        using var client = Client((http, _) => Task.FromResult(Json(http.Method == HttpMethod.Post ? Accepted : reply)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
    }

    [Fact]
    public async Task Conflicting_receipts_and_unbound_history_fail_before_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network."));
        var request = Request();
        request.Input!.Items!.Insert(0, new AIInputItem
        { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "Earlier reply" }] });
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(request));
        request = Request("""{"session_id":"session_2"}""");
        request.Input!.Items!.Insert(0, new AIInputItem
        {
            Role = "assistant",
            Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolName = "nolgia_session", ToolCallId = "receipt", ProviderExecuted = true,
                Output = JsonSerializer.Deserialize<JsonElement>("""{"session_id":"session_1"}""")
            }]
        });
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(request));
    }

    [Fact]
    public async Task Unsupported_media_and_instructions_are_not_silently_discarded()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network."));
        var request = Request();
        request.Input!.Items![0].Content!.Add(new AIFileContentPart { Type = "file", MediaType = "image/png", Data = "https://example.com/image.png" });
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ExecuteUnifiedAsync(request));
        request = Request(instructions: "System prompt");
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ExecuteUnifiedAsync(request));
    }

    private static AIRequest Request(string options = "{}", string? instructions = null) => new()
    {
        ProviderId = "nolgia", Model = "nolgia/agent", Instructions = instructions,
        Metadata = new() { ["nolgia"] = JsonSerializer.Deserialize<JsonElement>(options), ["other"] = new { secret = "not forwarded" } },
        Input = new AIInput { Items = [new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] }] }
    };
    private static NolgiaProvider Provider(HttpClient client) => new(new Keys(), new Factory(client));
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
