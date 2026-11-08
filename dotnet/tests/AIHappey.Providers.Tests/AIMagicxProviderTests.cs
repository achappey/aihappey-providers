using System.Net;
using System.Text;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AIMagicx;

namespace AIHappey.Tests;

public sealed class AIMagicxProviderTests
{
    [Fact]
    public async Task Model_discovery_uses_the_documented_endpoint_and_request_scoped_auth()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("https://www.aimagicx.com/api/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal(string.Concat("Bearer ", "test-key"), request.Headers.Authorization?.ToString());
            return Json("""{"object":"list","data":[{"id":"openai/gpt-4o-mini","owned_by":"openai"}]}""");
        });
        var provider = Provider(client);

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("aimagicx/openai/gpt-4o-mini", model.Id);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Chat_and_stream_use_documented_endpoint_and_request_scoped_auth()
    {
        var calls = 0;
        using var client = CreateClient(request =>
        {
            Assert.Equal("https://www.aimagicx.com/api/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal(string.Concat("Bearer ", "test-key"), request.Headers.Authorization?.ToString());
            return calls++ == 0
                ? Json("""{"id":"chatcmpl-1","object":"chat.completion","created":1700000000,"model":"openai/gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}]}""")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"id\":\"chatcmpl-2\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"openai/gpt-4o-mini\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n",
                        Encoding.UTF8, "text/event-stream")
                };
        });
        var provider = Provider(client);
        var options = new ChatCompletionOptions { Model = "openai/gpt-4o-mini" };

        var response = await provider.CompleteChatAsync(options);
        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(options))
            updates.Add(update);

        Assert.Equal("hello", response.Choices.First().Message.Content);
        Assert.NotEmpty(updates);
        Assert.Equal(2, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static AIMagicxProvider Provider(HttpClient client)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), new TestHttpClientFactory(client));

    private static HttpResponseMessage Json(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new TestHandler(send));

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
