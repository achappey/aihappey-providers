using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.WrenAI;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class WrenAIProviderTests
{
    private const string Sse = """
        event: init
        data: {"threadId":5,"threadResponseId":7,"answerId":"a-1","version":1}

        event: thinking
        data: {"block_id":1,"content":"Grouping by state."}

        event: thinking_done
        data: {}

        event: tool_call
        data: {"block_id":2,"id":"t1","name":"execute_sql","sql":"SELECT 1"}

        event: tool_result
        data: {"block_id":3,"id":"t1","name":"execute_sql","columns":["x"],"rows":[[1]],"rowCount":1}

        event: answer
        data: {"block_id":4,"content":"SP leads."}

        event: unknown_future_event
        data: {"a":1}

        event: done
        data: {"session_id":"s-1","trace_id":null,"version":1,"usage":{"input_tokens":3,"output_tokens":4}}

        """;

    [Fact]
    public async Task Stream_maps_agent_events_and_passes_raw_frames_through()
    {
        var handler = new Handler(Sse);
        var provider = Create(handler, "42:secret");
        var request = new AIRequest
        {
            ProviderId = "wrenai",
            Model = "wrenai/projects/1",
            Input = new AIInput { Text = "Top states?" },
            Metadata = new() { ["wrenai"] = new { threadId = 5, agentModel = "gpt-5.5", sessionProperties = new { region = "US" } } }
        };

        var events = new List<AIStreamEvent>();
        await foreach (var evt in provider.StreamUnifiedAsync(request)) events.Add(evt);

        var types = events.Select(evt => evt.Event.Type).ToArray();
        Assert.Contains("reasoning-delta", types);
        Assert.Contains("tool-input-available", types);
        Assert.Contains("tool-output-available", types);
        Assert.Equal("SP leads.", events.Select(evt => evt.Event.Data).OfType<AITextDeltaEventData>().Single().Delta);
        // Unknown event types are still surfaced raw and never break the stream.
        Assert.Contains(events, evt => evt.Event.Type == "data-wrenai-event" && evt.Event.Id == "unknown_future_event");
        var finish = events.Last().Event.Data as AIFinishEventData;
        Assert.Equal(("stop", 3, 4, 7), (finish!.FinishReason, finish.InputTokens, finish.OutputTokens, finish.TotalTokens));

        Assert.Equal("Bearer", handler.Authorization!.Scheme);
        Assert.Equal("secret", handler.Authorization.Parameter);
        Assert.Equal("/api/v2/stream/agent_ask", handler.Path);
        Assert.Equal("region=US", handler.SessionProperties);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1, body.RootElement.GetProperty("projectId").GetInt64());
        Assert.Equal("Top states?", body.RootElement.GetProperty("question").GetString());
        Assert.Equal(5, body.RootElement.GetProperty("threadId").GetInt32());
        Assert.Equal("gpt-5.5", body.RootElement.GetProperty("agentModel").GetString());
    }

    [Fact]
    public async Task Execute_returns_answer_with_raw_events_and_ids()
    {
        var provider = Create(new Handler(Sse), "secret");
        var response = await provider.ExecuteUnifiedAsync(new AIRequest { ProviderId = "wrenai", Model = "wrenai/projects/1", Input = new AIInput { Text = "Hi" } });

        Assert.Equal("completed", response.Status);
        var text = response.Output!.Items!.Single().Content!.OfType<AITextContentPart>().Single();
        Assert.Equal("SP leads.", text.Text);
        var details = (Dictionary<string, object?>)response.Metadata!["wrenai"]!;
        Assert.Equal("7", details["threadResponseId"]!.ToString());
    }

    [Fact]
    public async Task ListModels_uses_org_id_and_keeps_agentic_projects()
    {
        var handler = new Handler("""[{"id":1,"name":"Sales","projectType":"AGENTIC"},{"id":2,"name":"Old","projectType":"CLASSIC"}]""");
        var models = (await Create(handler, "42:secret").ListModels()).ToArray();

        var model = Assert.Single(models);
        Assert.Equal("wrenai/projects/1", model.Id);
        Assert.Equal("/api/v2/projects", handler.Path);
        Assert.Equal("?orgId=42", handler.Query);
        Assert.Empty(await Create(handler, "project-key").ListModels());
    }

    private static WrenAIProvider Create(Handler handler, string key)
        => new(new Keys(key), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new Factory(new HttpClient(handler)));

    private sealed class Keys(string key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(string content) : HttpMessageHandler
    {
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Path { get; private set; }
        public string? Query { get; private set; }
        public string? Body { get; private set; }
        public string? SessionProperties { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            Path = request.RequestUri!.AbsolutePath;
            Query = request.RequestUri.Query;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            SessionProperties = request.Headers.TryGetValues("X-Wren-Session-Properties", out var values) ? values.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }
}
