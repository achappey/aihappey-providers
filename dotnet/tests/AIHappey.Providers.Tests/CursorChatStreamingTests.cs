using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cursor;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class CursorChatStreamingTests
{
    [Fact]
    public async Task Chat_stream_preserves_tool_indexes_across_interleaved_calls()
    {
        const string events = """
            event: tool_call
            data: {"callId":"a","name":"lookup","status":"started","args":{}}

            event: tool_call
            data: {"callId":"b","name":"lookup","status":"started","args":{}}

            event: tool_call
            data: {"callId":"a","name":"lookup","status":"completed","result":"first"}

            event: tool_call
            data: {"callId":"b","name":"lookup","status":"completed","result":"second"}

            event: result
            data: {"status":"FINISHED"}

            event: done
            data: {}

            """;
        using var client = new HttpClient(new Handler(events));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new CursorProvider(new Keys(), new AsyncCacheHelper(cache), new Factory(client));
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>(
            """{"model":"cursor/default","messages":[{"role":"user","content":"Hello"}]}""")!;

        var calls = new List<ChatCompletionToolCallChunk>();
        await foreach (var update in provider.CompleteChatStreamingAsync(options))
            calls.AddRange(update.Choices.SelectMany(choice => choice.Delta?.ToolCalls ?? []));

        var relevantCalls = calls.Where(call => call.Id is "a" or "b").ToArray();
        Assert.Equal(["a", "b", "a", "b"], relevantCalls.Select(call => call.Id));
        Assert.Equal([1, 2, 1, 2], relevantCalls.Select(call => call.Index));
    }

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(string events) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = request.RequestUri!.AbsolutePath switch
            {
                "/v1/agents" => """{"agent":{"id":"agent-1"},"run":{"id":"run-1"}}""",
                "/v1/agents/agent-1/runs/run-1/stream" => events,
                "/v1/agents/agent-1/usage" => "{}",
                "/v1/agents/agent-1/artifacts" => """{"artifacts":[]}""",
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
