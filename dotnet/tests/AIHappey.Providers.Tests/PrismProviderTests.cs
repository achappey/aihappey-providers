using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Prism;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;

namespace AIHappey.Tests;

public sealed class PrismProviderTests
{
    private const string Model = "deepseek-v4.1-flash";
    private const string ResponseBody = """
        {"id":"resp-native","model":"deepseek-v4.1-flash","status":"completed","output":[
          {"type":"message","id":"msg-native","role":"assistant","status":"completed",
           "content":[{"type":"output_text","text":"Hello","annotations":[]}]},
          {"type":"function_call","id":"fc-native","call_id":"call-native","name":"lookup",
           "arguments":"{\"query\":\"hello\"}","status":"completed"}],
         "usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}
        """;
    private const string MessageBody = """
        {"id":"msg-native","type":"message","role":"assistant","model":"deepseek-v4.1-flash",
         "content":[{"type":"thinking","thinking":"Consider this","signature":"native-signature"},
          {"type":"text","text":"Hello"},
          {"type":"tool_use","id":"tool-native","name":"lookup","input":{"query":"hello"}}],
         "stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":5}}
        """;

    [Theory]
    [InlineData("responses", false, false)]
    [InlineData("responses", true, false)]
    [InlineData("responses", false, true)]
    [InlineData("responses", true, true)]
    [InlineData("messages", false, false)]
    [InlineData("messages", true, false)]
    public async Task Native_requests_preserve_payloads_headers_and_events(string endpoint, bool streaming, bool explicitMessageType)
    {
        var body = endpoint == "responses"
            ? """
              {"model":"deepseek-v4.1-flash","instructions":"Be precise",
               "input":[{"role":"user","content":[{"type":"input_text","text":"Hello"},
                 {"type":"input_image","image_url":"data:image/png;base64,AQID"}]}],
               "tools":[{"type":"function","name":"lookup","parameters":{"type":"object"}}],
               "reasoning":{"effort":"high"},"store":false,
               "metadata":{"prism":"application-data"},"future_option":{"nested":[true,null,{}]}}
              """
            : """
              {"model":"deepseek-v4.1-flash","max_tokens":1024,
               "system":[{"type":"text","text":"Be precise"}],
               "messages":[{"role":"user","content":[{"type":"text","text":"Hello"},
                 {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AQID"}}]}],
               "tools":[{"name":"lookup","input_schema":{"type":"object"}}],
               "thinking":{"type":"enabled","budget_tokens":512},
               "metadata":{"user_id":"user-1"},"future_option":{"nested":[true,null,{}]}}
              """;
        var original = JsonSerializer.Deserialize<JsonElement>(body);
        if (explicitMessageType)
        {
            var document = JsonNode.Parse(body)!;
            document["input"]![0]!["type"] = "message";
            original = JsonSerializer.SerializeToElement(document);
        }
        // Compare the typed contract's wire representation: omitted message types default to "message".
        var expected = endpoint == "responses"
            ? JsonSerializer.SerializeToElement(original.Deserialize<ResponseRequest>(ResponseJson.Default), ResponseJson.Default)
            : original;
        var calls = 0;
        using var client = Client(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.prisminference.com/v1/" + endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("resolved-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("trace-native", Assert.Single(request.Headers.GetValues("x-trace-id")));
            foreach (var name in new[] { "X-Prism-Key", "X-Other-Key", "x-api-key", "Proxy-Authorization" })
                Assert.False(request.Headers.Contains(name), name);
            if (endpoint == "messages")
                Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.Contains(request.Headers.Accept,
                value => value.MediaType == (streaming ? "text/event-stream" : "application/json"));

            var payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
            foreach (var property in original.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(expected.GetProperty(property.Name), payload.GetProperty(property.Name)),
                    $"{property.Name}: expected {expected.GetProperty(property.Name)}; actual {payload.GetProperty(property.Name)}");
            if (endpoint == "responses")
            {
                Assert.Equal("message", payload.GetProperty("input")[0].GetProperty("type").GetString());
                Assert.True(JsonElement.DeepEquals(original.GetProperty("input")[0].GetProperty("content"),
                    payload.GetProperty("input")[0].GetProperty("content")));
            }
            return Reply(endpoint, streaming);
        });
        var provider = Provider(client);
        var headers = Headers();
        if (endpoint == "responses")
        {
            var request = original.Deserialize<ResponseRequest>(ResponseJson.Default)!;
            request.Headers = headers;
            request.Stream = !streaming;
            if (streaming)
            {
                var part = Assert.Single(await Drain(provider.ResponsesStreamingAsync(request)));
                var json = JsonSerializer.SerializeToElement(part, ResponseJson.Default);
                Assert.Equal("response.output_text.delta", json.GetProperty("type").GetString());
                Assert.Equal("native-delta", json.GetProperty("delta").GetString());
                Assert.Equal(7, json.GetProperty("sequence_number").GetInt32());
            }
            else
            {
                var response = await provider.ResponsesAsync(request);
                Assert.Equal("resp-native", response.Id);
                var json = JsonSerializer.SerializeToElement(response, ResponseJson.Default);
                Assert.Equal("function_call", json.GetProperty("output")[1].GetProperty("type").GetString());
                Assert.Equal("call-native", json.GetProperty("output")[1].GetProperty("call_id").GetString());
            }
            Assert.Equal(!streaming, request.Stream);
            Assert.Same(headers, request.Headers);
        }
        else
        {
            var request = original.Deserialize<MessagesRequest>()!;
            request.Stream = !streaming;
            if (streaming)
            {
                var part = Assert.Single(await Drain(provider.MessagesStreamingAsync(request, headers)));
                var json = JsonSerializer.SerializeToElement(part);
                Assert.Equal("content_block_delta", json.GetProperty("type").GetString());
                Assert.Equal("thinking_delta", json.GetProperty("delta").GetProperty("type").GetString());
                Assert.Equal("native-thinking", json.GetProperty("delta").GetProperty("thinking").GetString());
            }
            else
            {
                var response = await provider.MessagesAsync(request, headers);
                Assert.Equal("msg-native", response.Id);
                var json = JsonSerializer.SerializeToElement(response);
                Assert.Equal("native-signature", json.GetProperty("content")[0].GetProperty("signature").GetString());
                Assert.Equal("tool-native", json.GetProperty("content")[2].GetProperty("id").GetString());
            }
            Assert.Equal(!streaming, request.Stream);
        }
        Assert.Equal("caller-authorization", headers["authorization"]);
        Assert.False(headers.ContainsKey("anthropic-version"));
        Assert.Equal(1, calls);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_requests_use_native_responses_without_recursion(bool streaming)
    {
        var calls = 0;
        using var client = Client(async request =>
        {
            calls++;
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            var payload = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            Assert.Equal(Model, payload.GetProperty("model").GetString());
            Assert.Equal(streaming, payload.GetProperty("stream").GetBoolean());
            Assert.False(payload.GetProperty("store").GetBoolean());
            return Reply("responses", streaming);
        });
        var provider = Provider(client);
        var input = new AIRequest
        {
            ProviderId = "prism",
            Model = Model,
            Input = new AIInput { Text = "Hello" }
        };
        if (streaming)
            await Drain(provider.StreamUnifiedAsync(input));
        else
            await provider.ExecuteUnifiedAsync(input);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Native_errors_retain_the_HTTP_status(string endpoint, bool streaming)
    {
        using var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"Prism rate limit"}}""")
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Invoke(Provider(client), endpoint, streaming));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("Prism rate limit", error.Message);
    }

    [Theory]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Missing_credentials_fail_before_HTTP(string endpoint, bool streaming)
    {
        using var client = Client(_ => throw new InvalidOperationException("Unexpected HTTP request"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(Provider(client, key: null), endpoint, streaming));
    }

    private static async Task Invoke(PrismProvider provider, string endpoint, bool streaming)
    {
        if (endpoint == "responses")
        {
            var request = new ResponseRequest { Model = Model, Input = "Hello" };
            if (streaming) await Drain(provider.ResponsesStreamingAsync(request));
            else await provider.ResponsesAsync(request);
        }
        else
        {
            var request = new MessagesRequest { Model = Model, MaxTokens = 1024, Messages = [] };
            if (streaming) await Drain(provider.MessagesStreamingAsync(request, []));
            else await provider.MessagesAsync(request, []);
        }
    }

    private static HttpResponseMessage Reply(string endpoint, bool streaming)
    {
        var body = endpoint switch
        {
            "responses" when streaming => """
                data: {"type":"response.output_text.delta","sequence_number":7,"item_id":"msg-native","output_index":0,"content_index":0,"delta":"native-delta"}
                """ + "\n\ndata: [DONE]\n\n",
            "responses" => ResponseBody,
            _ when streaming => """
                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"native-thinking"}}
                """ + "\n\ndata: [DONE]\n\n",
            _ => MessageBody
        };
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
        };
    }

    private static Dictionary<string, string> Headers() => new()
    {
        ["authorization"] = "caller-authorization",
        ["X-Prism-Key"] = "do-not-forward",
        ["X-Other-Key"] = "do-not-forward",
        ["x-api-key"] = "do-not-forward",
        ["Proxy-Authorization"] = "do-not-forward",
        ["x-trace-id"] = "trace-native"
    };

    private static PrismProvider Provider(HttpClient client, string? key = "resolved-key")
        => new(new Keys(key), new AsyncCacheHelper(), new Factory(client));

    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        => new(new Handler(send));

    private static async Task<List<T>> Drain<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }

    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "prism" ? key : null;
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
