using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Athena;
using AIHappey.Unified.Models;

namespace AIHappey.Providers.Tests;

public sealed class AthenaProviderTests
{
    [Fact]
    public async Task InvokePassesProviderOptionsAndPreservesRawResponse()
    {
        string? requestBody = null;
        using var client = new HttpClient(new Handler(async message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.athenaintel.com/api/v0/agents/general/invoke", message.RequestUri!.ToString());
            Assert.Equal("test-key", message.Headers.GetValues("X-API-KEY").Single());
            requestBody = await message.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"messages":[{"type":"ai","content":"Athena answer"}],"structured_output":{"ok":true}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var provider = new AthenaProvider(new Keys(), new Factory(client));

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            Model = "athena/general",
            Instructions = "Be concise.",
            Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "user",
                    Content = [new AITextContentPart { Type = "text", Text = "Hello" }]
                }]
            },
            Metadata = new Dictionary<string, object?>
            {
                ["athena"] = new
                {
                    config = new { enabled_tools = new[] { "search" } },
                    channel = "sdk",
                    thread_id = "thread-1",
                    custom_option = new { preserved = true }
                }
            }
        });

        using var body = JsonDocument.Parse(requestBody!);
        Assert.Equal("search", body.RootElement.GetProperty("config").GetProperty("enabled_tools")[0].GetString());
        Assert.Equal("sdk", body.RootElement.GetProperty("channel").GetString());
        Assert.Equal("thread-1", body.RootElement.GetProperty("thread_id").GetString());
        Assert.True(body.RootElement.GetProperty("custom_option").GetProperty("preserved").GetBoolean());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Be concise.", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Hello", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("Athena answer", Assert.IsType<AITextContentPart>(
            Assert.Single(response.Output!.Items![0].Content!)).Text);

        var raw = Assert.IsType<JsonElement>(response.Metadata!["athena.raw"]);
        Assert.True(raw.GetProperty("structured_output").GetProperty("ok").GetBoolean());
        Assert.True(Assert.IsType<JsonElement>(response.Metadata["athena.structured_output"])
            .GetProperty("ok").GetBoolean());
    }

    private sealed class Keys : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "athena" ? "test-key" : null;
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
