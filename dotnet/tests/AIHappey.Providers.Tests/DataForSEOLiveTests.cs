using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DataForSEO;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class DataForSEOLiveTests
{
    private const string Result = """
        {"status_code":20000,"status_message":"Ok.","cost":0.2,"tasks":[{"id":"task-1","status_code":20000,"status_message":"Ok.",
        "result":[{"model_name":"actual-model","input_tokens":10,"output_tokens":5,"reasoning_tokens":2,"money_spent":0.1,
        "items":[{"type":"reasoning","sections":[{"type":"summary_text","text":"Thinking"}]},
        {"type":"message","sections":[{"type":"text","text":"Answer","annotations":[{"url":"https://example.com","title":"Example","start_index":0,"end_index":6}]}]}],
        "fan_out_queries":["query"],"future":{"large":9007199254740993}}]}]}
        """;

    [Theory]
    [InlineData("chat_gpt", "gpt-4.1")]
    [InlineData("claude", "claude-sonnet")]
    [InlineData("gemini", "gemini-pro")]
    [InlineData("perplexity", "sonar")]
    public async Task Maps_native_live_request_and_response(string endpoint, string model)
    {
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal($"/v3/ai_optimization/{endpoint}/llm_responses/live", http.RequestUri!.AbsolutePath);
            Assert.Equal("Basic", http.Headers.Authorization!.Scheme);
            Assert.Equal("bG9naW46cGFzcw==", http.Headers.Authorization.Parameter);
            using var doc = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var task = Assert.Single(doc.RootElement.EnumerateArray());
            Assert.Equal(model, task.GetProperty("model_name").GetString());
            Assert.Equal("Next", task.GetProperty("user_prompt").GetString());
            Assert.Equal("Be concise", task.GetProperty("system_message").GetString());
            Assert.Equal("user", task.GetProperty("message_chain")[0].GetProperty("role").GetString());
            Assert.Equal("ai", task.GetProperty("message_chain")[1].GetProperty("role").GetString());
            Assert.Equal("Earlier answer", task.GetProperty("message_chain")[1].GetProperty("message").GetString());
            Assert.Equal(512, task.GetProperty("max_output_tokens").GetInt32());
            Assert.True(task.GetProperty("web_search").GetBoolean());
            Assert.Equal(9007199254740993L, task.GetProperty("future").GetProperty("number").GetInt64());
            return Json(Result);
        });
        var provider = Provider(client);
        using var metadata = JsonDocument.Parse("""{"web_search":true,"future":{"number":9007199254740993,"null":null}}""");
        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "dataforseo", Model = $"dataforseo/{endpoint}/{model}", Instructions = "Be concise", MaxOutputTokens = 512,
            Metadata = new() { ["dataforseo"] = metadata.RootElement.Clone() },
            Input = new AIInput { Items = [Message("user", "Earlier"), Message("assistant", "Earlier answer"), Message("user", "Next")] }
        });
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal("dataforseo/actual-model", response.Model);
        Assert.Equal("Answer", Assert.Single(response.Output!.Items!.SelectMany(i => i.Content ?? []).OfType<AITextContentPart>()).Text);
        Assert.Equal("Thinking", Assert.Single(response.Output.Items.SelectMany(i => i.Content ?? []).OfType<AIReasoningContentPart>()).Text);
        var textPart = Assert.Single(response.Output.Items.SelectMany(i => i.Content ?? []).OfType<AITextContentPart>());
        Assert.Equal("https://example.com", Assert.IsType<JsonElement>(textPart.Metadata!["responses.annotations"])[0].GetProperty("url").GetString());
        var usage = Assert.IsType<AIUsage>(response.Usage);
        Assert.Equal(15, usage.TotalTokens);
        Assert.Equal(2, usage.ReasoningTokens);
        var raw = Assert.IsType<Dictionary<string, object?>>(response.Metadata!["dataforseo"]);
        Assert.Equal(9007199254740993L, Assert.IsType<JsonElement>(raw["raw"]).GetProperty("tasks")[0].GetProperty("result")[0].GetProperty("future").GetProperty("large").GetInt64());
    }

    [Fact]
    public async Task All_protocols_support_buffered_streaming_and_nonstreaming()
    {
        var count = 0;
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("/v3/ai_optimization/chat_gpt/llm_responses/live", http.RequestUri!.AbsolutePath);
            using var doc = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var payload = doc.RootElement[0];
            Assert.False(payload.TryGetProperty("system", out _));
            Assert.False(payload.TryGetProperty("store", out _));
            Assert.False(payload.TryGetProperty("stop_sequences", out _));
            Assert.False(payload.TryGetProperty("service_tier", out _));
            count++;
            return Json(Result);
        });
        var provider = Provider(client);
        const string model = "dataforseo/chat_gpt/gpt-4.1";
        var chat = new ChatCompletionOptions { Model = model, Messages = [new() { Role = "user", Content = "Hello" }] };
        var responses = new ResponseRequest { Model = model, Input = "Hello", Store = true };
        var messages = new MessagesRequest { Model = model, MaxTokens = 1024, System = new MessagesContent("Be concise"), Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }] };
        var ui = new ChatRequest { Model = model, Messages = [new UIMessage { Id = "u", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] }] };
        var completion = await provider.CompleteChatAsync(chat);
        Assert.Contains("Answer", JsonSerializer.Serialize(completion));
        var chatStream = await Collect(provider.CompleteChatStreamingAsync(chat));
        Assert.Contains("Answer", JsonSerializer.Serialize(chatStream));
        Assert.Contains("10", JsonSerializer.Serialize(chatStream));
        var response = await provider.ResponsesAsync(responses);
        var responseJson = JsonSerializer.Serialize(response);
        Assert.Contains("Answer", responseJson);
        Assert.Contains("example.com", responseJson);
        var responsesStream = await Collect(provider.ResponsesStreamingAsync(responses));
        Assert.Contains("Answer", JsonSerializer.Serialize(responsesStream));
        Assert.Contains("example.com", JsonSerializer.Serialize(responsesStream));
        var message = await provider.MessagesAsync(messages, []);
        Assert.Contains("Answer", JsonSerializer.Serialize(message));
        var messagesStream = await Collect(provider.MessagesStreamingAsync(messages, []));
        Assert.Contains("Answer", JsonSerializer.Serialize(messagesStream));
        var uiParts = await Collect(provider.StreamAsync(ui));
        Assert.Equal("Answer", Assert.Single(uiParts.OfType<TextDeltaUIMessageStreamPart>()).Delta);
        Assert.Single(uiParts.OfType<FinishUIPart>());
        Assert.Equal(7, count);
    }

    [Fact]
    public async Task Discovered_model_routes_to_its_actual_endpoint()
    {
        using var client = Client((http, _) =>
        {
            if (http.Method == HttpMethod.Get)
            {
                var match = http.RequestUri!.AbsolutePath.Contains("/gemini/");
                return Task.FromResult(Json(match
                    ? """{"tasks":[{"result":[{"model_name":"special-model"}]}]}"""
                    : """{"tasks":[{"result":[]}]}"""));
            }
            Assert.Equal("/v3/ai_optimization/gemini/llm_responses/live", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(Result));
        });
        await Provider(client).ExecuteUnifiedAsync(Request("dataforseo/special-model"));
    }

    [Theory]
    [InlineData("""{"status_code":40100,"status_message":"Bad credentials"}""", "40100")]
    [InlineData("""{"status_code":20000,"tasks":[{"status_code":40501,"status_message":"Bad task"}]}""", "40501")]
    [InlineData("""{"status_code":20000,"tasks":[{"status_code":20000,"result":[]}]}""", "result")]
    public async Task Native_errors_are_not_reported_as_success(string body, string expected)
    {
        using var client = Client((_, _) => Task.FromResult(Json(body)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task Unsupported_history_is_rejected_without_truncation()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected"));
        var request = new AIRequest { ProviderId = "dataforseo", Model = "chat_gpt/gpt", Input = new AIInput { Items = Enumerable.Range(0, 12).Select(i => Message("user", i.ToString())).ToList() } };
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(request));
    }

    [Fact]
    public async Task Protocol_native_fields_pass_through_without_forwarding_protocol_controls()
    {
        using var client = Client(async (http, ct) =>
        {
            using var doc = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var task = doc.RootElement[0];
            Assert.True(task.GetProperty("web_search").GetBoolean());
            Assert.Equal("business", task.GetProperty("system_message").GetString());
            Assert.False(task.TryGetProperty("system", out _));
            Assert.False(task.TryGetProperty("stop_sequences", out _));
            return Json(Result);
        });
        var request = new MessagesRequest
        {
            Model = "chat_gpt/gpt", MaxTokens = 1024, System = new MessagesContent("business"),
            Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }],
            StopSequences = ["stop"],
            AdditionalProperties = new() { ["web_search"] = JsonSerializer.SerializeToElement(true) }
        };
        await Provider(client).MessagesAsync(request, []);
    }

    [Fact]
    public async Task Native_prompt_and_history_can_be_passed_without_unified_messages()
    {
        using var client = Client(async (http, ct) =>
        {
            using var doc = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("native", doc.RootElement[0].GetProperty("user_prompt").GetString());
            Assert.Equal("ai", doc.RootElement[0].GetProperty("message_chain")[0].GetProperty("role").GetString());
            return Json(Result);
        });
        using var options = JsonDocument.Parse("""{"user_prompt":"native","message_chain":[{"role":"ai","message":"previous"}]}""");
        await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "dataforseo", Model = "chat_gpt/gpt",
            Metadata = new() { ["dataforseo"] = options.RootElement.Clone() }
        });
    }

    [Fact]
    public async Task Rejects_stored_responses_without_silently_discarding_history()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected"));
        var request = new ResponseRequest { Model = "chat_gpt/gpt", Input = "Next", PreviousResponseId = "previous" };
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ResponsesAsync(request));
    }

    [Fact]
    public async Task Http_and_discovery_errors_are_reported()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid credentials")
        }));
        var provider = Provider(client);
        var liveError = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(Request()));
        Assert.Equal(HttpStatusCode.Unauthorized, liveError.StatusCode);
        Assert.Contains("invalid credentials", liveError.Message);
        var discoveryError = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(Request("unknown")));
        Assert.Equal(HttpStatusCode.Unauthorized, discoveryError.StatusCode);
    }

    [Fact]
    public async Task Cancellation_reaches_live_http_request()
    {
        using var client = Client(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(Result);
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ExecuteUnifiedAsync(Request(), cancellation.Token));
    }

    private static AIRequest Request(string model = "chat_gpt/gpt") => new() { ProviderId = "dataforseo", Model = model, Input = new AIInput { Text = "Hello" } };
    private static AIInputItem Message(string role, string text) => new() { Role = role, Content = [new AITextContentPart { Type = "text", Text = text }] };
    private static DataForSEOProvider Provider(HttpClient client) => new(new Keys(), new AsyncCacheHelper(), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(new Handler(handler));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream) { var list = new List<T>(); await foreach (var item in stream) list.Add(item); return list; }
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "bG9naW46cGFzcw=="; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
