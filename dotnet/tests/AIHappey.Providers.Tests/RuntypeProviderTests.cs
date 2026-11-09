using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Runtype;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Providers.Tests;

public sealed class RuntypeProviderTests
{
    [Fact]
    public async Task ListsAgentRunsThroughTheUnifiedRunsEndpointAndPreservesRawData()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/runs", request.RequestUri!.AbsolutePath);
            Assert.Contains("agentId=agent-1", request.RequestUri.Query);
            Assert.Contains("status=failed", request.RequestUri.Query);
            Assert.Contains("includeCount=true", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            return JsonResponse("""{"data":[{"executionId":"exec-1","agentId":"agent-1","status":"failed","finalOutput":"raw"}],"pagination":{"hasMore":false,"nextCursor":null}}""");
        }));

        var response = await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "runtype",
            Model = "runtype/agents/agent-1/runs",
            Metadata = new Dictionary<string, object?>
            {
                ["runtype"] = new { status = "failed", includeCount = true }
            }
        });

        var raw = Assert.IsType<JsonElement>(response.Metadata!["runtype.raw"]);
        Assert.Equal("raw", raw.GetProperty("data")[0].GetProperty("finalOutput").GetString());
        Assert.False(raw.GetProperty("pagination").GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task DeprecatedAgentExecutionsListIsNotAProviderRoute()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request expected.")));

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "runtype",
            Model = "runtype/agents/agent-1/executions"
        }));
    }

    [Fact]
    public async Task ListsInternalConversationEventsUsingTheDocumentedPath()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/agents/agent-1/conversations/conversation-1/events", request.RequestUri!.AbsolutePath);
            Assert.Contains("limit=10", request.RequestUri.Query);
            return JsonResponse("""{"data":[{"id":"event-1","eventKind":"agent_activity","payload":{"ok":true}}]}""");
        }));

        var response = await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "runtype",
            Model = "runtype/agents/agent-1/conversations/events",
            Metadata = new Dictionary<string, object?>
            {
                ["runtype"] = new { conversationId = "conversation-1", limit = 10 }
            }
        });

        var raw = Assert.IsType<JsonElement>(response.Metadata!["runtype.raw"]);
        Assert.Equal("event-1", raw.GetProperty("data")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ExecutionEventStreamingRemainsAvailableOnItsExplicitRoute()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/agents/agent-1/executions/exec-1/events", request.RequestUri!.AbsolutePath);
            Assert.Contains("after=opaque.cursor", request.RequestUri.Query);
            Assert.Contains(request.Headers.Accept, header => header.MediaType == "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "id: opaque.cursor.1\nevent: text_delta\ndata: {\"type\":\"text_delta\",\"id\":\"text-1\",\"executionId\":\"exec-1\",\"delta\":\"Hi\"}\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }));

        var events = new List<AIStreamEvent>();
        await foreach (var item in Provider(client).StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "runtype",
            Model = "runtype/agents/agent-1/execution/events",
            Metadata = new Dictionary<string, object?>
            {
                ["runtype"] = new { executionId = "exec-1", after = "opaque.cursor" }
            }
        }))
            events.Add(item);

        Assert.Contains(events, item => item.Event?.Type == "text-delta");
        Assert.Contains(events, item => item.Event?.Type == "data-runtype-event");
    }

    [Fact]
    public async Task CancelsDetachedExecutionWithConversationContext()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/agents/agent-1/executions/exec-1/cancel", request.RequestUri!.AbsolutePath);
            Assert.Contains("conversationId=conversation-1", request.RequestUri.Query);
            return JsonResponse("""{"executionId":"exec-1","status":"cancelled"}""", HttpStatusCode.Accepted);
        }));

        var response = await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "runtype",
            Model = "runtype/agents/agent-1/execution/cancel",
            Metadata = new Dictionary<string, object?>
            {
                ["runtype"] = new { executionId = "exec-1", conversationId = "conversation-1" }
            }
        });

        Assert.Equal("cancelled", response.Status);
    }

    private static RuntypeProvider Provider(HttpClient client)
        => new(new Keys(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new Factory(client));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "runtype" ? "test-key" : null;
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }
}
