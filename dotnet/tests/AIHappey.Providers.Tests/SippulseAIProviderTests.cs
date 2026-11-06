using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SippulseAI;
using AIHappey.Unified.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class SippulseAIProviderTests
{
    [Fact]
    public async Task Runs_agent_in_thread_and_continues_using_session_receipt()
    {
        var calls = 0;
        using var client = Client(async (http, ct) =>
        {
            Assert.Equal("test-key", http.Headers.GetValues("api-key").Single());
            if (calls++ == 0)
            {
                Assert.Equal(HttpMethod.Post, http.Method);
                Assert.Equal("/threads", http.RequestUri!.AbsolutePath);
                var create = Element(await http.Content!.ReadAsStringAsync(ct));
                Assert.Equal("agent_1", create.GetProperty("agent_id").GetString());
                Assert.Equal("uid_1", create.GetProperty("uid").GetString());
                Assert.True(create.GetProperty("future").GetProperty("enabled").GetBoolean());
                return Json("""{"id":"thread_1","status":"active","future":null}""");
            }

            Assert.Equal("/threads/thread_1/run", http.RequestUri!.AbsolutePath);
            var run = Element(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("user", run.GetProperty("role").GetString());
            Assert.Equal(calls == 2 ? "Hello" : "Next", run.GetProperty("content").GetString());
            return Json("""{"choices":[{"message":{"role":"assistant","content":"Hi"}}],"usage":{"total_tokens":4},"future":null}""");
        });

        var provider = Provider(client);
        var first = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "sippulseai",
            Model = "sippulseai/agent_1",
            Input = new AIInput { Text = "Hello" },
            Metadata = new Dictionary<string, object?>
            {
                ["sippulseai"] = Element("""{"uid":"uid_1","vars":{"name":"Ana"},"future":{"enabled":true}}""")
            }
        });
        Assert.Equal("Hi", Assert.Single(first.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        var metadata = Assert.IsType<JsonElement>(first.Metadata!["sippulseai"]);
        Assert.Equal("thread_1", metadata.GetProperty("thread_id").GetString());
        Assert.Equal("thread_1", metadata.GetProperty("thread").GetProperty("id").GetString());
        Assert.True(metadata.GetProperty("raw").TryGetProperty("future", out _));

        var second = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "sippulseai",
            Model = "sippulseai/agent_1",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem { Role = "assistant", Content = first.Output.Items![0].Content },
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Next" }] }
                ]
            }
        });
        Assert.Equal("Hi", Assert.Single(second.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Streaming_preserves_native_events_and_final_response()
    {
        var calls = 0;
        using var client = Client((http, _) =>
        {
            if (calls++ == 0) return Task.FromResult(Json("""{"id":"thread_1"}"""));
            Assert.Equal("/threads/thread_1/stream", http.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"id\":\"m1\",\"event\":\"message_chunk\",\"content\":\"Hi\"}\n\n" +
                    "data: {\"id\":\"m1\",\"event\":\"message_complete\",\"content\":\"Hi\"}\n\n" +
                    "data: {\"event\":\"message_report\",\"usage\":{\"total_tokens\":4}}\n\n" +
                    "data: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream")
            });
        });

        var events = await Collect(Provider(client).StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "sippulseai",
            Model = "sippulseai/agent_1",
            Input = new AIInput { Text = "Hello" }
        }));
        Assert.Equal("Hi", string.Concat(events.Select(item => item.Event.Data)
            .OfType<AITextDeltaEventData>().Select(item => item.Delta)));
        Assert.Contains(events, item => item.Event.Type == "data-sippulseai-event");
        Assert.Contains(events, item => item.Event.Data is AIToolOutputAvailableEventData { ToolName: "sippulseai_session" });
        Assert.Equal(4, Assert.IsType<AIFinishEventData>(events.Last().Event.Data).TotalTokens);
    }

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<SippulseAIProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "sippulseai"));
    }

    private static SippulseAIProvider Provider(HttpClient client)
        => new(new Keys(), new Clients(client));

    private static JsonElement Element(string value)
        => JsonSerializer.Deserialize<JsonElement>(value);

    private static HttpResponseMessage Json(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class Clients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
