using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;

namespace AIHappey.Providers.Tests;

public sealed class AnthropicVersionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SelectedVersionIsRequestScopedForNativeAndUnifiedMessages(bool stream, bool unified)
    {
        string expected = "";
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(expected, Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.False(body.TryGetProperty("anthropic-version", out _));
            Assert.Equal(0.7, body.GetProperty("temperature").GetDouble()); // Header selection must not rewrite the body.
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(stream
                    ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"model\",\"content\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                    : """{"id":"msg","type":"message","role":"assistant","model":"model","content":[],"usage":{"input_tokens":1,"output_tokens":1}}""",
                    Encoding.UTF8, stream ? "text/event-stream" : "application/json")
            };
        }));
        var provider = new AnthropicProvider(new Keys(), new Factory(client));
        // Use the same provider/client throughout: an explicit choice must never leak into a later request.
        foreach (var version in new[] { "2026-10-21", "2023-06-01", "future-version", null })
        {
            expected = version ?? "2023-06-01";
            Dictionary<string, string> headers = version is null ? [] : new() { ["AnThRoPiC-VeRsIoN"] = version };
            if (unified)
            {
                var request = JsonSerializer.Deserialize<MessagesRequest>("""{"model":"model","messages":[],"max_tokens":10,"temperature":0.7}""")!;
                request.Headers = headers;
                var ai = request.ToUnifiedRequest("anthropic");
                if (stream) await Drain(provider.StreamUnifiedAsync(ai));
                else await provider.ExecuteUnifiedAsync(ai);
            }
            else
            {
                var request = JsonSerializer.Deserialize<MessagesRequest>("""{"model":"model","messages":[],"max_tokens":10,"temperature":0.7}""")!;
                if (stream) await Drain(provider.MessagesStreamingAsync(request, headers));
                else await provider.MessagesAsync(request, headers);
            }
            Assert.Equal("2023-06-01", Assert.Single(client.DefaultRequestHeaders.GetValues("anthropic-version")));
        }
    }

    private static async Task Drain<T>(IAsyncEnumerable<T> values) { await foreach (var _ in values) { } }
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
