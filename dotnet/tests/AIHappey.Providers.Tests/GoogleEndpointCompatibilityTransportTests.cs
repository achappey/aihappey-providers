using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Providers.Tests;

public class GoogleEndpointCompatibilityTransportTests
{
    private const string Model = "gemini-3.8-flash";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    public async Task CompatibilityEndpointsSendValidGoogleInstructionsOptionsAndBuiltins(string endpoint, bool streaming)
    {
        JsonElement sent = default;
        using var http = new HttpClient(new CaptureHandler(async request =>
        {
            Assert.Equal("/v1beta/interactions", request.RequestUri!.AbsolutePath);
            sent = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            return Reply(streaming, toolCall: false);
        }));
        var provider = Provider(http);
        var fields = """
            "max_tokens":1024,"stream":true,"store":false,"service_tier":"standard","temperature":1,
            "response_modalities":["text"],"generation_config":{"thinking_level":"low","thinking_summaries":"auto"},
            "tools":[{"type":"url_context"},{"type":"google_search","search_types":["web_search"]}]
            """;
        var messages = endpoint == "messages"
            ? "\"system\":\"Be honest\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],"
            : "\"messages\":[{\"role\":\"system\",\"content\":\"Be honest\"},{\"role\":\"developer\",\"content\":\"Be concise\"},{\"role\":\"user\",\"content\":\"hello\"}],";
        var payload = "{\"model\":\"" + Model + "\"," + messages + fields + "}";
        var output = await Invoke(provider, endpoint, streaming, payload);

        Assert.Contains("Hello", string.Join("", output.Select(item => item.GetRawText())));
        Assert.Equal(endpoint == "messages" ? "Be honest" : "Be honest\n\nBe concise", sent.GetProperty("system_instruction").GetString());
        Assert.Single(sent.GetProperty("input").EnumerateArray());
        Assert.Equal(streaming, sent.GetProperty("stream").GetBoolean());
        Assert.False(sent.GetProperty("store").GetBoolean());
        Assert.False(sent.TryGetProperty("system", out _));
        Assert.False(sent.TryGetProperty("max_tokens", out _));
        Assert.False(sent.TryGetProperty("metadata", out _));
        Assert.Equal("standard", sent.GetProperty("service_tier").GetString());
        Assert.Equal("text", sent.GetProperty("response_modalities")[0].GetString());
        var config = sent.GetProperty("generation_config");
        Assert.Equal(1024, config.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(1, config.GetProperty("temperature").GetDouble());
        Assert.Equal("low", config.GetProperty("thinking_level").GetString());
        Assert.Equal("auto", config.GetProperty("thinking_summaries").GetString());
        Assert.Collection(sent.GetProperty("tools").EnumerateArray(),
            tool => Assert.Equal("url_context", tool.GetProperty("type").GetString()),
            tool =>
            {
                Assert.Equal("google_search", tool.GetProperty("type").GetString());
                Assert.Equal("web_search", tool.GetProperty("search_types")[0].GetString());
            });
    }

    [Theory]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    public async Task ClientFunctionResponsesAndContinuationWorkInBothModes(string endpoint, bool streaming)
    {
        var sent = new List<JsonElement>();
        using var http = new HttpClient(new CaptureHandler(async request =>
        {
            sent.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
            return Reply(streaming, toolCall: sent.Count == 1);
        }));
        var provider = Provider(http);
        var tools = endpoint == "messages"
            ? "\"tools\":[{\"name\":\"lookup\",\"input_schema\":{\"type\":\"object\"}}],\"tool_choice\":{\"type\":\"tool\",\"name\":\"lookup\"}"
            : "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"parameters\":{\"type\":\"object\"}}}],\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"lookup\"}}";
        var root = "\"model\":\"" + Model + "\",\"max_tokens\":100," + tools + ",";
        var initial = "{" + root + "\"messages\":[{\"role\":\"user\",\"content\":\"look up id 1\"}]}";
        var output = await Invoke(provider, endpoint, streaming, initial);

        AssertFunctionOutput(endpoint, streaming, output);
        Assert.Equal("lookup", sent[0].GetProperty("tools")[0].GetProperty("name").GetString());
        var choice = sent[0].GetProperty("generation_config").GetProperty("tool_choice").GetProperty("allowed_tools");
        Assert.Equal("any", choice.GetProperty("mode").GetString());
        Assert.Equal("lookup", choice.GetProperty("tools")[0].GetString());
        Assert.False(sent[0].TryGetProperty("tool_choice", out _));

        var continuation = endpoint == "messages" ? """
            "messages":[{"role":"user","content":"look up id 1"},
            {"role":"assistant","content":[{"type":"tool_use","id":"call-1","name":"lookup","input":{"id":1}}]},
            {"role":"user","content":[{"type":"tool_result","tool_use_id":"call-1","content":"found"}]}]
            """ : """
            "messages":[{"role":"user","content":"look up id 1"},
            {"role":"assistant","content":null,"tool_calls":[{"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"id\":1}"}}]},
            {"role":"tool","tool_call_id":"call-1","content":"found"}]
            """;
        output = await Invoke(provider, endpoint, streaming, "{" + root + continuation + "}");
        Assert.Contains("Hello", string.Join("", output.Select(item => item.GetRawText())));
        AssertCompletedOutput(endpoint, streaming, output);
        var input = sent[1].GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(3, input.Count);
        Assert.Equal("function_call", input[1].GetProperty("type").GetString());
        Assert.Equal("call-1", input[1].GetProperty("id").GetString());
        Assert.Equal(1, input[1].GetProperty("arguments").GetProperty("id").GetInt32());
        Assert.Equal("function_result", input[2].GetProperty("type").GetString());
        Assert.Equal("call-1", input[2].GetProperty("call_id").GetString());
        Assert.Equal("lookup", input[2].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    public async Task ParallelClientFunctionsRemainInOneAssistantResponse(string endpoint, bool streaming)
    {
        using var http = new HttpClient(new CaptureHandler(_ => Task.FromResult(Reply(streaming, true, callCount: 2))));
        var tools = endpoint == "messages"
            ? "\"tools\":[{\"name\":\"lookup\",\"input_schema\":{\"type\":\"object\"}}]"
            : "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"parameters\":{\"type\":\"object\"}}}]";
        var payload = "{\"model\":\"" + Model + "\",\"max_tokens\":100," + tools
            + ",\"messages\":[{\"role\":\"user\",\"content\":\"look up ids 1 and 2\"}]}";

        AssertFunctionOutput(endpoint, streaming, await Invoke(Provider(http), endpoint, streaming, payload), callCount: 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesStillPreservesInstructionsNativeOptionsAndClientFunctions(bool streaming)
    {
        JsonElement sent = default;
        using var http = new HttpClient(new CaptureHandler(async request =>
        {
            sent = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            return Reply(streaming, false);
        }));
        var provider = Provider(http);
        var source = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"gemini-3.8-flash","instructions":"Be honest","input":"hello",
            "tools":[{"type":"function","name":"lookup","parameters":{"type":"object"}}],
            "generation_config":{"thinking_level":"low"},"response_modalities":["text"]}
            """, ResponseJson.Default)!;
        var output = streaming
            ? await Collect(provider.ResponsesStreamingAsync(source))
            : [JsonSerializer.SerializeToElement(await provider.ResponsesAsync(source), Json)];
        Assert.Contains("Hello", string.Join("", output.Select(item => item.GetRawText())));
        Assert.Equal("Be honest", sent.GetProperty("system_instruction").GetString());
        Assert.Equal("low", sent.GetProperty("generation_config").GetProperty("thinking_level").GetString());
        Assert.Equal("text", sent.GetProperty("response_modalities")[0].GetString());
        Assert.Equal("lookup", sent.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ApiChatStillPreservesProviderMetadataBuiltinsAndInstructions()
    {
        JsonElement sent = default;
        using var http = new HttpClient(new CaptureHandler(async request =>
        {
            sent = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync());
            return Reply(true, false);
        }));
        var source = JsonSerializer.Deserialize<ChatRequest>("""
            {"model":"gemini-3.8-flash","messages":[
            {"id":"s","role":"system","parts":[{"type":"text","text":"Be honest"}]},
            {"id":"u","role":"user","parts":[{"type":"text","text":"hello"}]}],
            "providerMetadata":{"google":{"generation_config":{"thinking_level":"low"},
            "response_modalities":["text"],"tools":[{"type":"url_context"},{"type":"google_search","search_types":["web_search"]}]}}}
            """, Json)!;
        var output = await Collect(Provider(http).StreamAsync(source));
        Assert.Contains("Hello", string.Join("", output.Select(item => item.GetRawText())));
        Assert.Equal("Be honest", sent.GetProperty("system_instruction").GetString());
        Assert.Equal("low", sent.GetProperty("generation_config").GetProperty("thinking_level").GetString());
        Assert.Equal(2, sent.GetProperty("tools").GetArrayLength());
        Assert.Equal("web_search", sent.GetProperty("tools")[1].GetProperty("search_types")[0].GetString());
    }

    private static void AssertFunctionOutput(string endpoint, bool streaming, List<JsonElement> output, int callCount = 1)
    {
        if (!streaming)
        {
            var root = Assert.Single(output);
            if (endpoint == "messages")
            {
                var calls = root.GetProperty("content").EnumerateArray()
                    .Where(item => item.GetProperty("type").GetString() == "tool_use").ToList();
                Assert.Equal(callCount, calls.Count);
                for (var i = 0; i < callCount; i++)
                {
                    Assert.Equal($"call-{i + 1}", calls[i].GetProperty("id").GetString());
                    Assert.Equal("lookup", calls[i].GetProperty("name").GetString());
                    Assert.Equal(i + 1, calls[i].GetProperty("input").GetProperty("id").GetInt32());
                }
                Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
            }
            else
            {
                var choice = Assert.Single(root.GetProperty("choices").EnumerateArray());
                var message = choice.GetProperty("message");
                Assert.Equal("assistant", message.GetProperty("role").GetString());
                Assert.Equal(JsonValueKind.Null, message.GetProperty("content").ValueKind);
                var calls = message.GetProperty("tool_calls").EnumerateArray().ToList();
                Assert.Equal(callCount, calls.Count);
                for (var i = 0; i < callCount; i++)
                {
                    Assert.Equal($"call-{i + 1}", calls[i].GetProperty("id").GetString());
                    Assert.Equal("lookup", calls[i].GetProperty("function").GetProperty("name").GetString());
                    Assert.Equal(i + 1, JsonSerializer.Deserialize<JsonElement>(calls[i].GetProperty("function")
                        .GetProperty("arguments").GetString()!).GetProperty("id").GetInt32());
                }
                Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
            }
            return;
        }

        var arguments = new Dictionary<int, StringBuilder>();
        var ids = new Dictionary<int, string>();
        void Append(int index, string? json)
        {
            if (!arguments.TryGetValue(index, out var builder))
                arguments[index] = builder = new StringBuilder();
            builder.Append(json);
        }
        if (endpoint == "messages")
        {
            foreach (var item in output)
            {
                if (item.TryGetProperty("content_block", out var block)
                    && block.TryGetProperty("name", out var name))
                {
                    Assert.Equal("lookup", name.GetString());
                    ids[item.GetProperty("index").GetInt32()] = block.GetProperty("id").GetString()!;
                }
                if (item.TryGetProperty("delta", out var delta) && delta.TryGetProperty("partial_json", out var json))
                    Append(item.GetProperty("index").GetInt32(), json.GetString());
            }
            Assert.Contains(output, item => item.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("stop_reason", out var reason) && reason.GetString() == "tool_use");
        }
        else
        {
            foreach (var item in output)
                foreach (var choice in item.GetProperty("choices").EnumerateArray())
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("tool_calls", out var calls))
                        foreach (var call in calls.EnumerateArray())
                        {
                            var index = call.GetProperty("index").GetInt32();
                            if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                                ids[index] = id.GetString()!;
                            if (call.TryGetProperty("function", out var function) && function.TryGetProperty("arguments", out var json))
                                Append(index, json.GetString());
                        }
            Assert.Contains(output.SelectMany(item => item.GetProperty("choices").EnumerateArray()),
                choice => choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "tool_calls");
        }
        Assert.Equal(callCount, ids.Count);
        Assert.Equal(callCount, arguments.Count);
        foreach (var (index, json) in arguments)
        {
            var argument = JsonSerializer.Deserialize<JsonElement>(json.ToString()).GetProperty("id").GetInt32();
            Assert.Equal($"call-{argument}", ids[index]);
        }
        Assert.Equal(Enumerable.Range(1, callCount).Select(i => $"call-{i}").Order(), ids.Values.Order());
    }

    private static void AssertCompletedOutput(string endpoint, bool streaming, List<JsonElement> output)
    {
        if (!streaming)
        {
            var root = Assert.Single(output);
            Assert.Equal(endpoint == "messages" ? "end_turn" : "stop", endpoint == "messages"
                ? root.GetProperty("stop_reason").GetString()
                : Assert.Single(root.GetProperty("choices").EnumerateArray()).GetProperty("finish_reason").GetString());
        }
        else if (endpoint == "messages")
            Assert.Contains(output, item => item.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("stop_reason", out var reason) && reason.GetString() == "end_turn");
        else
            Assert.Contains(output.SelectMany(item => item.GetProperty("choices").EnumerateArray()),
                choice => choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "stop");
    }

    private static async Task<List<JsonElement>> Invoke(GoogleAIProvider provider, string endpoint, bool streaming, string payload)
    {
        if (endpoint == "messages")
        {
            var source = JsonSerializer.Deserialize<MessagesRequest>(payload, MessagesJson.Default)!;
            return streaming ? await Collect(provider.MessagesStreamingAsync(source, []))
                : [JsonSerializer.SerializeToElement(await provider.MessagesAsync(source, []), MessagesJson.Default)];
        }
        var chat = JsonSerializer.Deserialize<ChatCompletionOptions>(payload, Json)!;
        return streaming ? await Collect(provider.CompleteChatStreamingAsync(chat, default))
            : [JsonSerializer.SerializeToElement(await provider.CompleteChatAsync(chat), Json)];
    }

    private static async Task<List<JsonElement>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var output = new List<JsonElement>();
        await foreach (var item in stream) output.Add(JsonSerializer.SerializeToElement(item, Json));
        return output;
    }

    private static HttpResponseMessage Reply(bool streaming, bool toolCall, int callCount = 1)
    {
        List<InteractionStep> steps = toolCall
            ? Enumerable.Range(1, callCount).Select(i => (InteractionStep)new InteractionFunctionCallStep
            {
                Id = $"call-{i}", Name = "lookup", Arguments = JsonSerializer.SerializeToElement(new { id = i })
            }).ToList()
            : [new InteractionModelOutputStep { Content = [new InteractionTextContent { Text = "Hello" }] }];
        var interaction = new Interaction
        {
            Id = "interaction-test", Model = Model, Status = toolCall ? "requires_action" : "completed",
            Steps = steps, Usage = new InteractionUsage { TotalInputTokens = 5, TotalOutputTokens = 2, TotalTokens = 7 }
        };
        if (!streaming)
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(interaction, InteractionJson.Default), Encoding.UTF8, "application/json")
            };

        var events = new List<InteractionStreamEventPart>
        {
            new InteractionCreatedEvent { Interaction = new Interaction { Id = interaction.Id, Model = Model, Status = "in_progress" } }
        };
        for (var i = 0; i < steps.Count; i++)
        {
            events.Add(new InteractionStepStartEvent { Index = i, Step = toolCall ? steps[i] : new InteractionModelOutputStep { Content = [] } });
            events.Add(new InteractionStepDeltaEvent
            {
                Index = i, Delta = toolCall ? new InteractionArgumentsDeltaData { Arguments = JsonSerializer.Serialize(new { id = i + 1 }) }
                    : new InteractionTextDeltaData { Text = "Hello" }
            });
            events.Add(new InteractionStepStopEvent { Index = i });
        }
        events.Add(new InteractionCompletedEvent { Interaction = interaction });
        var sse = string.Concat(events.Select(item => $"event: {item.EventType}\ndata: {JsonSerializer.Serialize(item, InteractionJson.Default)}\n\n"));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    }

    private static GoogleAIProvider Provider(HttpClient http)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), NullLogger<GoogleAIProvider>.Instance, new TestClientFactory(http));

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestClientFactory(HttpClient http) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => http;
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
