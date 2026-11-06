using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AgencyOSX;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class AgencyOSXProviderTests
{
    private const string Reply = """
        {"response":"Here are your prospects.","agent":{"id":"kai","name":"Kai"},"chatId":"chat-1",
         "usage":{"estimated_cost_usd":0.0854},"wallet":{"balanceUsd":12.34},
         "future":{"number":9007199254740993,"array":[true,null,{},[]]}}
        """;
    private const string NativeOptions = """
        {"chatId":"existing-chat","future_object":{"nested":[true,null,{},[]]},
         "future_number":9007199254740993,"future_null":null}
        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(() => "aosx_test"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("agencyosx"));
        Assert.IsType<AgencyOSXProvider>(registry.GetModelProvider(scope.ServiceProvider, "agencyosx"));
    }

    [Fact]
    public async Task Discovery_pages_agents_and_partitions_cache_by_credential()
    {
        var key = "aosx_first";
        var calls = 0;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var client = Client((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"Bearer {key}", request.Headers.Authorization?.ToString());
            Assert.Equal("app.agencyosx.ai", request.RequestUri!.Host);
            var first = calls % 2 == 1;
            Assert.Equal($"/api/v1/agents?limit=100&offset={(first ? 0 : 1)}", request.RequestUri.PathAndQuery);
            return Task.FromResult(Json(first
                ? """{"agents":[{"id":"kai","name":"Kai","title":"Lead Generator","description":"Find prospects","expertiseTopics":["Leads"]}],"pagination":{"total":2,"limit":1,"offset":0,"hasMore":true}}"""
                : """{"agents":[{"id":"mia","name":"Mia","title":"Ad specialist"}],"pagination":{"total":2,"limit":1,"offset":1,"hasMore":false}}"""));
        });
        var provider = new AgencyOSXProvider(new Keys(() => key), new AsyncCacheHelper(memory), new Factory(client));
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["agencyosx/kai", "agencyosx/mia"], models.Select(model => model.Id));
        Assert.Equal("Kai", models[0].Name);
        Assert.Equal("Find prospects", models[0].Description);
        Assert.Equal("Ad specialist", models[1].Description);
        Assert.All(models, model =>
        {
            Assert.Equal("AgencyOSX", model.OwnedBy);
            Assert.Equal("chat", model.Type);
            Assert.Contains("agent", model.Tags!);
        });
        await provider.ListModels();
        Assert.Equal(2, calls);
        key = "aosx_second";
        await provider.ListModels();
        Assert.Equal(4, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"agents":[{}],"pagination":{"hasMore":false}}""")]
    [InlineData("""{"agents":[],"pagination":{"hasMore":true,"offset":0,"limit":100}}""")]
    [InlineData("""{"agents":[{"id":"kai"}],"pagination":{"hasMore":true,"offset":0,"limit":0}}""")]
    [InlineData("""{"agents":[{"id":"kai"}],"pagination":{"hasMore":true,"offset":0,"limit":"100"}}""")]
    [InlineData("""{"agents":[{"id":"kai"}],"pagination":{"hasMore":true,"offset":100,"limit":100}}""")]
    [InlineData("""{"agents":[{"id":"kai"}],"pagination":{"hasMore":"true"}}""")]
    public async Task Malformed_discovery_fails_explicitly(string body)
    {
        using var client = Client((_, _) => Task.FromResult(Json(body)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    [Fact]
    public async Task Unified_preserves_native_request_and_full_response_without_inventing_tokens()
    {
        var native = JsonSerializer.Deserialize<JsonElement>(NativeOptions);
        using var client = Client(async (request, ct) =>
        {
            var body = await AssertChat(request, ct);
            foreach (var property in native.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.False(body.TryGetProperty("other", out _));
            return Json(Reply);
        });
        var metadata = new Dictionary<string, object?>
        {
            ["agencyosx"] = native, ["other"] = new { secret = "not-forwarded" }
        };
        var before = JsonSerializer.Serialize(metadata);
        var result = await Provider(client).ExecuteUnifiedAsync(Request(metadata));
        Assert.Equal(before, JsonSerializer.Serialize(metadata));
        Assert.Equal("agencyosx/kai", result.Model);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Reply),
            Assert.IsType<JsonElement>(result.Metadata!["agencyosx"])));
        var usage = Assert.IsType<JsonElement>(result.Usage);
        Assert.Equal(0.0854m, usage.GetProperty("estimated_cost_usd").GetDecimal());
        Assert.False(usage.TryGetProperty("total_tokens", out _));
        var content = Assert.Single(result.Output!.Items!).Content!;
        Assert.Equal("Here are your prospects.", Assert.Single(content.OfType<AITextContentPart>()).Text);
        Assert.True(Assert.Single(content.OfType<AIToolCallContentPart>()).ProviderExecuted);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Receipt_continues_the_same_conversation_without_resending_history()
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            var body = await AssertChat(request, ct);
            if (++calls == 1) Assert.False(body.TryGetProperty("chatId", out _));
            else Assert.Equal("chat-1", body.GetProperty("chatId").GetString());
            Assert.False(body.TryGetProperty("messages", out _));
            return Json(Reply);
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request());
        var second = new AIRequest
        {
            ProviderId = "agencyosx", Model = "kai",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem { Role = "assistant", Content = first.Output!.Items![0].Content },
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] }
                ]
            }
        };
        await provider.ExecuteUnifiedAsync(second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UI_receipt_continues_chat_and_preserves_exact_response_metadata()
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            var body = await AssertChat(request, ct);
            if (++calls == 2) Assert.Equal("chat-1", body.GetProperty("chatId").GetString());
            return Json(Reply);
        });
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request());
        var receipt = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
        var uiRequest = new ChatRequest
        {
            Model = "kai",
            Messages =
            [
                new UIMessage
                {
                    Id = "assistant-1", Role = Role.assistant,
                    Parts = [new ToolInvocationPart
                    {
                        Type = "tool-agencyosx_chat", ToolName = receipt.ToolName, ToolCallId = receipt.ToolCallId,
                        ProviderExecuted = true, State = "output-available", Input = receipt.Input!, Output = receipt.Output!
                    }]
                },
                new UIMessage { Id = "user-2", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] }
            ]
        };
        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(uiRequest)) parts.Add(part);
        var text = Assert.Single(parts.OfType<TextDeltaUIMessageStreamPart>());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Reply),
            Assert.IsType<JsonElement>(text.ProviderMetadata!["agencyosx"])));
        Assert.Single(parts.OfType<FinishUIPart>());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Receipt_cannot_silently_switch_agents()
    {
        using var client = Client((_, _) => Task.FromResult(Json(Reply)));
        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(Request());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "agencyosx", Model = "mia",
            Input = new AIInput
            {
                Text = "Hello",
                Items = [new AIInputItem { Role = "assistant", Content = first.Output!.Items![0].Content }]
            }
        }));
        Assert.Contains("different agent", error.Message);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_endpoint_adapters_preserve_root_options(string endpoint)
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            calls++;
            var body = await AssertChat(request, ct);
            foreach (var property in JsonSerializer.Deserialize<JsonElement>(NativeOptions).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.False(body.TryGetProperty("metadata", out _));
            return Json(Reply);
        });
        var provider = Provider(client);
        var extra = NativeOptions[1..^1];
        object? result = null;
        switch (endpoint)
        {
            case "chat":
                result = await provider.CompleteChatAsync(JsonSerializer.Deserialize<ChatCompletionOptions>($$"""
                    {"model":"agencyosx/kai","messages":[{"role":"user","content":"Hello"}],"metadata":{"project":"test"},{{extra}}}
                    """)!);
                break;
            case "responses":
                result = await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>($$"""
                    {"model":"agencyosx/kai","input":"Hello","metadata":{"project":"test"},{{extra}}}
                    """)!);
                break;
            case "messages":
                result = await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>($$"""
                    {"model":"agencyosx/kai","max_tokens":100,"messages":[{"role":"user","content":"Hello"}],{{extra}}}
                    """)!, []);
                break;
        }
        Assert.Equal(1, calls);
        Assert.NotNull(result);
        Assert.True(ContainsRawReply(JsonSerializer.SerializeToElement(result)));
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Streaming_adapters_make_one_native_call_and_keep_metadata(string endpoint)
    {
        var calls = 0;
        using var client = Client(async (request, ct) =>
        {
            calls++;
            var body = await AssertChat(request, ct);
            Assert.Equal("existing-chat", body.GetProperty("chatId").GetString());
            return Json(Reply);
        });
        var provider = Provider(client);
        var chunks = new List<string>();
        var extra = NativeOptions[1..^1];
        switch (endpoint)
        {
            case "ui":
                var uiRequest = new ChatRequest
                {
                    Model = "agencyosx/kai",
                    Messages = [new UIMessage { Id = "user-1", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] }],
                    ProviderMetadata = new()
                    {
                        ["agencyosx"] = JsonSerializer.Deserialize<JsonElement>(NativeOptions),
                        ["other"] = JsonSerializer.Deserialize<JsonElement>("""{"secret":"not-forwarded"}""")
                    }
                };
                var before = JsonSerializer.Serialize(uiRequest);
                await foreach (var chunk in provider.StreamAsync(uiRequest)) chunks.Add(JsonSerializer.Serialize(chunk));
                Assert.Equal(before, JsonSerializer.Serialize(uiRequest));
                break;
            case "chat":
                await foreach (var chunk in provider.CompleteChatStreamingAsync(JsonSerializer.Deserialize<ChatCompletionOptions>($$"""
                    {"model":"agencyosx/kai","messages":[{"role":"user","content":"Hello"}],{{extra}}}
                    """)!)) chunks.Add(JsonSerializer.Serialize(chunk));
                break;
            case "responses":
                await foreach (var chunk in provider.ResponsesStreamingAsync(JsonSerializer.Deserialize<ResponseRequest>($$"""
                    {"model":"agencyosx/kai","input":"Hello",{{extra}}}
                    """)!)) chunks.Add(JsonSerializer.Serialize(chunk));
                break;
            case "messages":
                await foreach (var chunk in provider.MessagesStreamingAsync(JsonSerializer.Deserialize<MessagesRequest>($$"""
                    {"model":"agencyosx/kai","max_tokens":100,"messages":[{"role":"user","content":"Hello"}],{{extra}}}
                    """)!, [])) chunks.Add(JsonSerializer.Serialize(chunk));
                break;
        }
        Assert.Equal(1, calls);
        Assert.Contains(chunks, chunk => chunk.Contains("Here are your prospects."));
        Assert.Contains(chunks, chunk => ContainsRawReply(JsonSerializer.Deserialize<JsonElement>(chunk)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"response":"Hello"}""")]
    [InlineData("""{"response":null,"chatId":"chat-1"}""")]
    public async Task Malformed_chat_fails_explicitly(string body)
    {
        using var client = Client((_, _) => Task.FromResult(Json(body)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
    }

    [Fact]
    public async Task Native_errors_retain_status_and_body()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("""{"error":"Insufficient wallet balance"}""")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
        Assert.Equal(HttpStatusCode.PaymentRequired, error.StatusCode);
        Assert.Contains("Insufficient wallet balance", error.Message);
    }

    [Fact]
    public async Task Missing_credentials_and_cancellation_do_not_send_requests()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No request expected."));
        var provider = new AgencyOSXProvider(new Keys(() => null), new AsyncCacheHelper(), new Factory(client));
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(Request()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ExecuteUnifiedAsync(Request(), cancellation.Token));
    }

    private static AIRequest Request(Dictionary<string, object?>? metadata = null) => new()
    {
        ProviderId = "agencyosx", Model = "agencyosx/kai", Input = new AIInput { Text = "Hello" }, Metadata = metadata
    };

    private static async Task<JsonElement> AssertChat(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://app.agencyosx.ai/api/v1/chat", request.RequestUri!.ToString());
        Assert.Equal("Bearer aosx_test", request.Headers.Authorization?.ToString());
        var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
        Assert.Equal("kai", body.GetProperty("agentId").GetString());
        Assert.Equal("Hello", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("secret", out _));
        return body;
    }

    private static AgencyOSXProvider Provider(HttpClient client)
        => new(new Keys(() => "aosx_test"), new AsyncCacheHelper(), new Factory(client));

    private static bool ContainsRawReply(JsonElement value)
    {
        if (JsonElement.DeepEquals(value, JsonSerializer.Deserialize<JsonElement>(Reply))) return true;
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().Any(property => ContainsRawReply(property.Value));
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Any(ContainsRawReply);
        return false;
    }

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));
    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Keys(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string provider)
        {
            Assert.Equal("agencyosx", provider);
            return key();
        }
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
