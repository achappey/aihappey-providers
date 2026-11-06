using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Viktor;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Unified.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class ViktorProviderTests
{
    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<ViktorProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "viktor"));
    }

    [Theory]
    [InlineData("chat/completions", false, null)]
    [InlineData("chat/completions", true, null)]
    [InlineData("responses", false, null)]
    [InlineData("responses", true, null)]
    [InlineData("responses", false, false)]
    [InlineData("responses", false, true)]
    [InlineData("responses", true, false)]
    [InlineData("responses", true, true)]
    [InlineData("messages", false, null)]
    [InlineData("messages", true, null)]
    public async Task Native_endpoints_preserve_raw_options_and_use_request_scoped_auth(string endpoint, bool streaming, bool? initialStream)
    {
        var input = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"viktor/reasoning","messages":[],"input":"Hello","max_tokens":100,
             "previous_response_id":"resp_previous","metadata":{"team":"finance"},
             "future":{"values":[1,false,null,{}],"number":9007199254740993},"future_null":null}
            """);
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://api.viktor.com/api/compat/v1/{endpoint}", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("viktor_test", request.Headers.Authorization?.Parameter);
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            foreach (var name in new[] { "model", "previous_response_id", "metadata", "future", "future_null" })
                Assert.True(JsonElement.DeepEquals(input.GetProperty(name), body.GetProperty(name)), name);
            if (endpoint == "responses")
                Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            else if (streaming)
                Assert.True(body.GetProperty("stream").GetBoolean());
            return Json(streaming ? "data: [DONE]\n\n" : endpoint switch
            {
                "chat/completions" => """{"id":"chat","object":"chat.completion","created":1,"model":"viktor/reasoning","choices":[]}""",
                "messages" => """{"id":"msg","type":"message","role":"assistant","model":"viktor/reasoning","content":[],"usage":{"input_tokens":1,"output_tokens":1}}""",
                _ => """{"id":"resp","model":"viktor/reasoning","status":"completed","output":[]}"""
            }, streaming);
        }));
        var provider = Provider(client);
        Assert.True(client.Timeout > TimeSpan.FromMinutes(10));

        switch (endpoint)
        {
            case "chat/completions":
                var chat = input.Deserialize<ChatCompletionOptions>()!;
                if (streaming) await Drain(provider.CompleteChatStreamingAsync(chat));
                else await provider.CompleteChatAsync(chat);
                break;
            case "messages":
                var messages = input.Deserialize<MessagesRequest>()!;
                if (streaming) await Drain(provider.MessagesStreamingAsync(messages, []));
                else await provider.MessagesAsync(messages, []);
                break;
            default:
                var responses = input.Deserialize<ResponseRequest>(ResponseJson.Default)!;
                responses.Stream = initialStream;
                if (streaming) await Drain(provider.ResponsesStreamingAsync(responses));
                else await provider.ResponsesAsync(responses);
                break;
        }
        Assert.Equal(1, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Unified_metadata_is_passed_to_the_native_responses_endpoint()
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("/api/compat/v1/responses", request.RequestUri!.AbsolutePath);
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("resp_previous", body.GetProperty("previous_response_id").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("future").GetProperty("value").ValueKind);
            return Json("""{"id":"resp","model":"viktor","status":"completed","output":[]}""");
        }));
        await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "viktor", Model = "viktor", Input = new AIInput { Text = "Hello" },
            Metadata = new Dictionary<string, object?>
            {
                ["viktor"] = JsonSerializer.Deserialize<JsonElement>("""{"previous_response_id":"resp_previous","future":{"value":null}}""")
            }
        });
    }

    [Fact]
    public async Task Discovery_uses_the_authenticated_workspace_model_list()
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/compat/v1/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("viktor_test", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""{"data":[{"id":"viktor","owned_by":"viktor"},{"id":"viktor/reasoning","owned_by":"viktor"}]}"""));
        }));
        var models = (await Provider(client).ListModels()).ToArray();
        Assert.Equal(2, models.Length);
        Assert.Contains(models, model => model.Name == "viktor/reasoning");
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Missing_credentials_fail_before_sending()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected HTTP request.")));
        var provider = new ViktorProvider(new Keys(null), new AsyncCacheHelper(), new Factory(client));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(new ChatCompletionOptions { Model = "viktor" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upstream_errors_are_not_reported_as_success(bool streaming)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"Workspace rate limit exceeded"}}""")
        })));
        var provider = Provider(client);
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            var request = new ChatCompletionOptions { Model = "viktor" };
            if (streaming) await Drain(provider.CompleteChatStreamingAsync(request));
            else await provider.CompleteChatAsync(request);
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("Workspace rate limit exceeded", error.Message);
    }

    private static ViktorProvider Provider(HttpClient client) => new(new Keys(), new AsyncCacheHelper(), new Factory(client));
    private static HttpResponseMessage Json(string body, bool streaming = false) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
    };

    private static async Task Drain<T>(IAsyncEnumerable<T> stream)
    {
        await foreach (var _ in stream) { }
    }

    private sealed class Keys(string? value = "viktor_test") : IApiKeyResolver
    {
        public string? Resolve(string provider) => value;
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
