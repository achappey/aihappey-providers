using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Notte;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class NotteChatStreamingTests
{
    [Fact]
    public async Task Chat_stream_preserves_distinct_tool_indexes_across_inputs_and_outputs()
    {
        using var client = new HttpClient(new Handler());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new NotteProvider(new Keys(), new AsyncCacheHelper(cache), new Factory(client));
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>(
            """{"model":"notte/agent","messages":[{"role":"user","content":"Find a page"}]}""")!;

        var calls = new List<ChatCompletionToolCallChunk>();
        await foreach (var update in provider.CompleteChatStreamingAsync(options))
            calls.AddRange(update.Choices.SelectMany(choice => choice.Delta?.ToolCalls ?? []));

        Assert.Equal(
            ["notte-session-session-1", "notte-session-session-1",
             "notte-agent-agent-1-activity-0-tool", "notte-agent-agent-1-activity-0-tool"],
            calls.Select(call => call.Id));
        Assert.Equal([0, 0, 1, 1], calls.Select(call => call.Index));
    }

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = (request.Method.Method, request.RequestUri!.AbsolutePath) switch
            {
                ("POST", "/sessions/start") => """{"session_id":"session-1","status":"active"}""",
                ("POST", "/agents/start") => """
                    {"agent_id":"agent-1","steps":[{"type":"agent_completion","value":{"action":{"type":"navigate","url":"https://example.com"}}}]}
                    """,
                ("GET", "/agents/agent-1") => """
                    {"status":"closed","success":true,"answer":"Done","steps":[
                      {"type":"agent_completion","value":{"action":{"type":"navigate","url":"https://example.com"}}},
                      {"type":"execution_result","value":{"action":{"type":"navigate","url":"https://example.com"},"success":true}}
                    ]}
                    """,
                ("GET", "/sessions/session-1/files") => """{"files":[]}""",
                _ => throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
