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
    public async Task Discovery_exposes_only_documented_operations_without_network()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call."));
        Assert.Equal(["buildprint/agents/start", "buildprint/agents/list"],
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
    public async Task List_forwards_filters_and_returns_the_raw_page_without_draining_pagination()
    {
        const string page = """{"items":[{"id":"agent_1","title":"Agent","status":"running"}],"cursor":"next","isDone":false,"future":null}""";
        var calls = 0;
        using var client = Client((http, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("?appId=app%20%2F%26&archived=false&limit=100&cursor=next%2F%2B%3D&status=running&future=9007199254740993",
                http.RequestUri!.Query);
            Assert.Null(http.Content);
            return Task.FromResult(Json(page));
        });
        var result = await Provider(client).ExecuteUnifiedAsync(Request("agents/list",
            JsonSerializer.Deserialize<JsonElement>("""{"appId":"app /&","archived":false,"limit":100,"cursor":"next/+=","status":"running","future":9007199254740993}""")));
        Assert.Equal(1, calls);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(page),
            Assert.IsType<JsonElement>(result.Metadata!["buildprint"])));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Adapters_preserve_native_metadata(string endpoint)
    {
        using var client = Client(async (http, ct) =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await http.Content!.ReadAsStringAsync(ct));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Native).GetProperty("future"),
                body.GetProperty("future")));
            return Json(Receipt);
        });
        var provider = Provider(client);
        var extra = Native[1..^1];
        switch (endpoint)
        {
            case "chat":
                await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(
                    $$$"""{"model":"agents/start","messages":[{"role":"user","content":"Hello"}],"buildprint":{ {{{extra}}} }}""")!);
                break;
            case "responses":
                await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
                    $$$"""{"model":"agents/start","input":"Hello","buildprint":{ {{{extra}}} }}""")!);
                break;
            default:
                await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(
                    $$$"""{"model":"agents/start","max_tokens":100,"messages":[{"role":"user","content":"Hello"}],"buildprint":{ {{{extra}}} }}""")!, []);
                break;
        }

    }

    [Fact]
    public async Task Null_cursor_is_omitted_for_the_first_page()
    {
        using var client = Client((http, _) =>
        {
            Assert.Equal("", http.RequestUri!.Query);
            return Task.FromResult(Json("""{"items":[],"cursor":null,"isDone":true}"""));
        });
        await Provider(client).ExecuteUnifiedAsync(Request("agents/list",
            JsonSerializer.Deserialize<JsonElement>("""{"cursor":null}""")));
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
    [InlineData("agents/list", """{"filter":{}}""")]
    [InlineData("unknown", "{}")]
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
    private sealed class Keys : IApiKeyResolver { public string Resolve(string provider) => "test-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
