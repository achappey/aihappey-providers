using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;

namespace AIHappey.Providers.Tests;

public class NativeRequestMetadataTransportTests
{
    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task NativeSerializationPreservesMetadataAndRootOptions(string endpoint, bool streaming)
    {
        var root = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"model","input":"hello","messages":[],"max_tokens":100,
             "metadata":{"user_id":"user","openai":"application","anthropic":"application"},
             "reasoning":{"effort":"high"},"service_tier":"priority",
             "provider":{"order":["example"],"allow_fallbacks":false},
             "future_null":null,"future_nested":{"values":[1,false,null,{}]},
             "future_empty":{},"future_array":[],"future_number":9007199254740993,
             "providerMetadata":{"literal":"unknown"}}
            """);
        JsonElement? sent = null;
        using var http = new HttpClient(new CaptureHandler(async request =>
        {
            sent = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streaming ? "data: [DONE]\n\n" : endpoint switch
                {
                    "chat" => """{"id":"chat","object":"chat.completion","created":1,"model":"model","choices":[]}""",
                    "messages" => """{"id":"msg","type":"message","role":"assistant","model":"model","content":[],"usage":{"input_tokens":1,"output_tokens":1}}""",
                    _ => """{"id":"resp","model":"model","status":"completed","output":[]}"""
                }, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        })) { BaseAddress = new Uri("https://provider.test/") };

        switch (endpoint)
        {
            case "chat":
                var chat = root.Deserialize<ChatCompletionOptions>()!;
                if (streaming) await Drain(http.GetChatCompletionUpdates(chat, "openai"));
                else await http.GetChatCompletion(chat, "openai");
                break;
            case "messages":
                var messages = root.Deserialize<MessagesRequest>()!;
                if (streaming) await Drain(http.PostMessagesStreaming(messages, "anthropic"));
                else await http.PostMessages(messages, "anthropic");
                break;
            default:
                var responses = root.Deserialize<ResponseRequest>(ResponseJson.Default)!;
                if (streaming) await Drain(http.GetResponsesUpdates(responses, "openai"));
                else await http.GetResponses(responses, "openai");
                break;
        }

        Assert.NotNull(sent);
        foreach (var property in root.EnumerateObject().Where(property => property.Name.StartsWith("future_")
            || property.Name is "metadata" or "reasoning" or "service_tier" or "provider" or "providerMetadata"))
        {
            Assert.True(JsonElement.DeepEquals(property.Value, sent.Value.GetProperty(property.Name)), property.Name);
            Assert.Single(sent.Value.EnumerateObject(), candidate => candidate.Name == property.Name);
        }
        Assert.False(sent.Value.TryGetProperty("openai", out _));
        Assert.False(sent.Value.TryGetProperty("anthropic", out _));
    }

    private static async Task Drain<T>(IAsyncEnumerable<T> stream)
    {
        await foreach (var _ in stream) { }
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
