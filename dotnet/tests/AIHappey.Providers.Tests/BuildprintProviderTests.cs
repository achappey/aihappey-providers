using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Buildprint;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class BuildprintProviderTests
{
    private const string Native = """
        {"appId":"app_1","model":"native-model","reasoningEffort":"high","branches":["main"],
         "allowEdits":false,"future":{"number":9007199254740993,"array":[true,null,{},[]]},"futureNull":null}
        """;
    private const string NativeMessage = """{"prompt":"native conversation prompt","future":"ignored by the message endpoint"}""";
    private const string Receipt = """{"agentId":"agent_1","future":{"number":9007199254740993,"array":[null,{}]}}""";

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<BuildprintProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "buildprint"));
    }

    [Fact]
    public async Task Discovery_lists_agents_and_the_start_operation()
    {
        using var client = Client((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("https://api.buildprint.ai/api/v1/agents?limit=100", http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
            return Task.FromResult(Json("""{"items":[{"id":"agent_1","title":"Agent One"}],"cursor":null,"isDone":true}"""));
        });
        Assert.Equal(["buildprint/agents/start", "buildprint/agent_1"],
            (await Provider(client).ListModels()).Select(model => model.Id));
    }

    [Fact]
    public async Task Start_preserves_native_options_and_receipt_without_mutating_input()
    {
        var native = JsonSerializer.Deserialize<JsonElement>(Native);
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://api.buildprint.ai/api/v1/agents", http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
            var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
            foreach (var property in native.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.Equal("latest", body.GetProperty("prompt").GetString());
            Assert.False(body.TryGetProperty("other", out _));
            return Json(Receipt);
        });
        var request = Request("agents/start", native);
        var before = JsonSerializer.Serialize(request);
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Receipt),
            Assert.IsType<JsonElement>(result.Metadata!["buildprint"])));
        Assert.Null(result.Usage);
        Assert.True(Assert.Single(result.Output!.Items![0].Content!.OfType<AIToolCallContentPart>()).ProviderExecuted);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Native_prompt_is_not_overwritten()
    {
        using var client = Client(async (http, ct) =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("native prompt", body.GetProperty("prompt").GetString());
            return Json(Receipt);
        });
        await Provider(client).ExecuteUnifiedAsync(Request("agents/start",
            JsonSerializer.Deserialize<JsonElement>("""{"appId":"app_1","prompt":"native prompt"}""")));
    }

    [Fact]
    public async Task List_models_follows_native_pagination_and_maps_agent_ids()
    {
        const string firstPage = """{"items":[{"id":"agent_1","title":"Agent One"}],"cursor":"next /+=","isDone":false}""";
        const string lastPage = """{"items":[{"id":"agent_2","title":"Agent Two"}],"cursor":null,"isDone":true}""";
        var calls = 0;
        using var client = Client((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
            Assert.Null(http.Content);
            Assert.Equal(calls++ == 0 ? "?limit=100" : "?limit=100&cursor=next%20%2F%2B%3D", http.RequestUri!.Query);
            return Task.FromResult(Json(calls == 1 ? firstPage : lastPage));
        });
        var models = (await Provider(client).ListModels()).ToArray();
        Assert.Equal(2, calls);
        Assert.Equal(["buildprint/agent_1", "buildprint/agent_2"], models.Skip(1).Select(model => model.Id));
        Assert.Equal(["Agent One", "Agent Two"], models.Skip(1).Select(model => model.Name));
    }

    [Fact]
    public async Task Conversation_sends_a_message_to_the_selected_agent()
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://api.buildprint.ai/api/v1/agents/agent_1/messages", http.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
            var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("latest", body.GetProperty("prompt").GetString());
            Assert.Equal(1, body.EnumerateObject().Count());
            return Json("""{"agentId":"agent_1","queued":true}""");
        });

        var result = await Provider(client).ExecuteUnifiedAsync(Request("buildprint/agent_1",
            JsonSerializer.Deserialize<JsonElement>("{}")));

        Assert.Equal("buildprint/agent_1", result.Model);
        Assert.Equal("buildprint_send_agent_message",
            Assert.Single(result.Output!.Items![0].Content!.OfType<AIToolCallContentPart>()).ToolName);
    }

    [Fact]
    public async Task Conversation_preserves_native_prompt_without_mutating_input()
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("https://api.buildprint.ai/api/v1/agents/agent_1/messages", http.RequestUri!.AbsoluteUri);
            var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("native conversation prompt", body.GetProperty("prompt").GetString());
            Assert.Single(body.EnumerateObject());
            return Json("""{"agentId":"agent_1","queued":true}""");
        });
        var request = Request("agent_1", JsonSerializer.Deserialize<JsonElement>(NativeMessage));
        var before = JsonSerializer.Serialize(request);

        await Provider(client).ExecuteUnifiedAsync(request);

        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData("chat", false, true)]
    [InlineData("chat", true, true)]
    [InlineData("responses", false, true)]
    [InlineData("responses", true, true)]
    [InlineData("messages", false, true)]
    [InlineData("messages", true, true)]
    [InlineData("chat", false, false)]
    [InlineData("chat", true, false)]
    [InlineData("responses", false, false)]
    [InlineData("responses", true, false)]
    [InlineData("messages", false, false)]
    [InlineData("messages", true, false)]
    public async Task Adapters_preserve_native_options_and_fall_back_to_user_text(string endpoint, bool streaming, bool nativePrompt)
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, http.Method);
            Assert.Equal("https://api.buildprint.ai/api/v1/agents/agent_1/messages", http.RequestUri!.AbsoluteUri);
            var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal(nativePrompt ? "native conversation prompt" : "Hello", body.GetProperty("prompt").GetString());
            Assert.Single(body.EnumerateObject());
            return Json("""{"agentId":"agent_1","queued":true}""");
        });
        var provider = Provider(client);
        // Native endpoint options belong at the request root; provider-keyed options are for unified/Vercel requests.
        var extra = nativePrompt ? NativeMessage[1..^1] : "\"future\":\"ignored by the message endpoint\"";
        switch (endpoint)
        {
            case "chat":
                var chat = JsonSerializer.Deserialize<ChatCompletionOptions>(
                    $$$"""{"model":"agent_1","messages":[{"role":"user","content":"Hello"}],{{{extra}}} }""")!;
                if (streaming) await Drain(provider.CompleteChatStreamingAsync(chat));
                else await provider.CompleteChatAsync(chat);
                break;
            case "responses":
                var responses = JsonSerializer.Deserialize<ResponseRequest>(
                    $$$"""{"model":"agent_1","input":"Hello",{{{extra}}} }""")!;
                if (streaming) await Drain(provider.ResponsesStreamingAsync(responses));
                else await provider.ResponsesAsync(responses);
                break;
            default:
                var messages = JsonSerializer.Deserialize<MessagesRequest>(
                    $$$"""{"model":"agent_1","max_tokens":100,"messages":[{"role":"user","content":"Hello"}],{{{extra}}} }""")!;
                if (streaming) await Drain(provider.MessagesStreamingAsync(messages, []));
                else await provider.MessagesAsync(messages, []);
                break;
        }

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public async Task Invalid_native_conversation_prompts_fail_before_sending(string prompt)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call."));
        var request = Request("agent_1", JsonSerializer.Deserialize<JsonElement>($$"""{"prompt":{{prompt}}}"""));

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(request));
    }

    [Fact]
    public async Task Null_cursor_is_omitted_for_the_first_page()
    {
        using var client = Client((http, _) =>
        {
            Assert.Equal("?limit=100", http.RequestUri!.Query);
            return Task.FromResult(Json("""{"items":[],"cursor":null,"isDone":true}"""));
        });
        await Provider(client).ListModels();
    }

    [Fact]
    public async Task Streaming_emits_one_operation_receipt_and_preserves_metadata()
    {
        using var client = Client((_, _) => Task.FromResult(Json(Receipt)));
        var events = new List<AIStreamEvent>();
        await foreach (var evt in Provider(client).StreamUnifiedAsync(Request("agents/start",
            JsonSerializer.Deserialize<JsonElement>(Native)))) events.Add(evt);
        Assert.Equal(["tool-input-available", "tool-output-available", "text-start", "text-delta", "text-end", "finish"],
            events.Select(evt => evt.Event.Type));
        Assert.All(events, evt => Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Receipt),
            Assert.IsType<JsonElement>(evt.Metadata!["buildprint"]))));
    }

    [Theory]
    [InlineData("agents/start", "{}")]
    [InlineData("agents/start", "[]")]
    [InlineData("agents/list", "{}")]
    [InlineData("", "{}")]
    public async Task Invalid_requests_fail_before_sending(string model, string options)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call."));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(
            Request(model, JsonSerializer.Deserialize<JsonElement>(options))));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"agentId":null}""")]
    public async Task Malformed_receipts_fail_explicitly(string reply)
    {
        using var client = Client((_, _) => Task.FromResult(Json(reply)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ExecuteUnifiedAsync(
            Request("agents/start", JsonSerializer.Deserialize<JsonElement>(Native))));
    }

    [Fact]
    public async Task Upstream_errors_keep_status_and_body()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("""{"code":"forbidden"}""") }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(
            Request("agents/start", JsonSerializer.Deserialize<JsonElement>(Native))));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("forbidden", error.Message);
    }

    private static AIRequest Request(string model, JsonElement options) => new()
    {
        ProviderId = "buildprint", Model = model,
        Metadata = new() { ["buildprint"] = options, ["other"] = new { secret = "not forwarded" } },
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

    private static BuildprintProvider Provider(HttpClient client) => new(new Keys(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static async Task Drain<T>(IAsyncEnumerable<T> stream)
    {
        var count = 0;
        await foreach (var _ in stream) count++;
        Assert.True(count > 0, "The streaming adapter must emit at least one event.");
    }
    private sealed class Keys : IApiKeyResolver { public string Resolve(string provider) => "test-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
