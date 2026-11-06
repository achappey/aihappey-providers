using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Ampersand;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class AmpersandProviderTests
{
    private const string Idle = """
        {"id":"sess_1","status":"idle","agent":{"model":"ampersand-v0.1"},"required_actions":[],
        "usage":{"input_tokens":12,"output_tokens":3,"total_tokens":15},"future":{"number":9007199254740993,"null":null}}
        """;
    private const string Waiting = """
        {"id":"sess_1","status":"requires_action","agent":{"model":"ampersand-v0.1"},
        "required_actions":[{"type":"function_call","turn_id":"turn_1","call_id":"call_1","name":"lookup","arguments":{"q":"test"}}],"usage":null}
        """;
    private const string Items = """
        {"object":"list","data":[{"id":"msg_1","type":"message","role":"assistant",
        "content":[{"type":"output_text","text":"Hello"}],"future":[null,{}]}],"has_more":false,"last_id":"msg_1"}
        """;
    private const string Turns = """
        {"object":"list","data":[{"id":"turn_1","status":"completed","error":null}],"has_more":false,"last_id":"turn_1"}
        """;
    private static readonly string Stream = Frame("agent.session.created", """{"id":"sess_1","status":"in_progress"}""")
        + """
        event: agent.session.turn.output_text.delta
        data: {"type":"agent.session.turn.output_text.delta","session_id":"sess_1","turn_id":"turn_1","item_id":"msg_1","content_index":0,"delta":"Provisional"}

        event: agent.session.turn.output_text.done
        data: {"type":"agent.session.turn.output_text.done","session_id":"sess_1","turn_id":"turn_1","item_id":"msg_1","content_index":0,"text":"Hello"}

        event: agent.session.turn.completed
        data: {"type":"agent.session.turn.completed","turn":{"id":"turn_1","status":"completed"}}

        event: future.event
        data: {"type":"future.event","number":9007199254740993,"null":null}


        """ + Frame("agent.session.idle", Idle);

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<AmpersandProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "ampersand"));
    }

    [Fact]
    public async Task Models_use_request_scoped_auth_without_beta_header()
    {
        using var client = Client((http, _) =>
        {
            Auth(http, models: true);
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("/v1/models", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"object":"list","data":[{"id":"ampersand-v0.1","owned_by":"humansand"},{"id":"ampersand-tutor-v0.1","owned_by":"humansand"}]}"""));
        });
        var models = (await Provider(client).ListModels()).ToList();
        Assert.Equal(2, models.Count);
        Assert.Equal("ampersand/ampersand-v0.1", models[0].Id);
        Assert.Equal("humansand", models[0].OwnedBy);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("OpenAI-Beta"));
    }

    [Fact]
    public async Task Compatibility_surfaces_use_native_sessions()
    {
        using var client = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Post ? Sse(Stream) : Read(http)));
        var provider = Provider(client);
        var chat = new ChatCompletionOptions
        {
            Model = "ampersand/ampersand-v0.1", Messages = [new() { Role = "user", Content = "Hello" }]
        };
        Assert.NotNull(await provider.CompleteChatAsync(chat));
        Assert.NotEmpty(await Collect(provider.CompleteChatStreamingAsync(chat)));
        var responses = new ResponseRequest { Model = "ampersand/ampersand-v0.1", Input = "Hello" };
        Assert.NotNull(await provider.ResponsesAsync(responses));
        Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(responses)));
        var messages = new MessagesRequest
        {
            Model = "ampersand/ampersand-v0.1", MaxTokens = 1024,
            Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }]
        };
        Assert.NotNull(await provider.MessagesAsync(messages, []));
        Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, [])));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    [InlineData("ui")]
    public async Task Compatibility_receipts_continue_the_original_session(string protocol)
    {
        var submitted = 0;
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                if (http.RequestUri!.AbsolutePath.EndsWith("/events"))
                {
                    var evt = Assert.Single(Element(await http.Content!.ReadAsStringAsync(ct)).GetProperty("events").EnumerateArray());
                    Assert.Equal("Next", Assert.Single(evt.GetProperty("input").EnumerateArray()).GetProperty("content")[0].GetProperty("text").GetString());
                    submitted++;
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                }
                return Sse(Stream);
            }
            return http.RequestUri!.AbsolutePath.EndsWith("/events") ? Sse(Stream) : Read(http);
        });
        var provider = Provider(client);
        const string model = "ampersand/ampersand-v0.1";
        if (protocol == "chat")
        {
            var first = await provider.CompleteChatAsync(new ChatCompletionOptions
            {
                Model = model, Messages = [new() { Role = "user", Content = "Hello" }]
            });
            var assistant = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(first.Choices.First().Message))!;
            await provider.CompleteChatAsync(new ChatCompletionOptions
            {
                Model = model, Messages = [assistant, new() { Role = "user", Content = "Next" }]
            });
        }
        else if (protocol == "responses")
        {
            var first = await provider.ResponsesAsync(new ResponseRequest { Model = model, Input = "Hello" });
            await provider.ResponsesAsync(new ResponseRequest
            {
                Model = model,
                Input = first.OutputItems.Append(new ResponseInputMessage { Role = ResponseRole.User, Content = "Next" }).ToList()
            });
        }
        else if (protocol == "messages")
        {
            var first = await provider.MessagesAsync(new MessagesRequest
            {
                Model = model, MaxTokens = 1024, Messages = [new() { Role = "user", Content = new MessagesContent("Hello") }]
            }, []);
            await provider.MessagesAsync(new MessagesRequest
            {
                Model = model, MaxTokens = 1024, Messages = [
                    new() { Role = "assistant", Content = new MessagesContent(first.Content) },
                    new() { Role = "user", Content = new MessagesContent("Next") }
                ]
            }, []);
        }
        else
        {
            var first = await provider.ExecuteUnifiedAsync(Request());
            var tool = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>());
            var chat = new ChatRequest
            {
                Model = model, Messages = [
                    new UIMessage { Id = "a", Role = Role.assistant, Parts = [
                        new ToolInvocationPart { Type = "tool-ampersand_session", ToolName = tool.ToolName, ToolCallId = tool.ToolCallId,
                            ProviderExecuted = true, State = "output-available", Input = tool.Input!, Output = tool.Output! }
                    ] },
                    new UIMessage { Id = "u", Role = Role.user, Parts = [new TextUIPart { Text = "Next" }] }
                ]
            };
            chat = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(chat, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
            var parts = await Collect(provider.StreamAsync(chat));
            Assert.Equal("Hello", Assert.Single(parts.OfType<TextDeltaUIMessageStreamPart>()).Delta);
            Assert.Single(parts.OfType<FinishUIPart>());
        }
        Assert.Equal(1, submitted);
    }

    [Fact]
    public async Task Missing_credentials_hide_models_and_reject_execution()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected."));
        var provider = Provider(client, null);
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(Request()));
    }

    [Fact]
    public async Task Creation_preserves_native_options_and_raw_response_without_mutation()
    {
        var native = Element("""
            {"agent_id":"agent_saved","agent":{"reasoning":{"effort":"max"},"multi_agent":{"enabled":true,"max_concurrent_subagents":3},
             "ampersand":{"allow_silent_completion":false,"compaction":"disabled"},"future":{"number":9007199254740993,"null":null}},
             "input":[{"role":"user","content":[{"type":"input_text","text":"native"}],"metadata":{"channel":"setup"}}],
             "metadata":{"purpose":"test"},"future":null}
            """);
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            if (http.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/agents/sessions", http.RequestUri!.AbsolutePath);
                Assert.False(http.Headers.Contains("Idempotency-Key"));
                Assert.Contains(http.Headers.Accept, accept => accept.MediaType == "text/event-stream");
                var body = Element(await http.Content!.ReadAsStringAsync(ct));
                foreach (var property in native.EnumerateObject())
                    if (property.Name != "agent") Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)));
                foreach (var property in native.GetProperty("agent").EnumerateObject())
                    Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty("agent").GetProperty(property.Name)));
                Assert.Equal("ampersand-v0.1", body.GetProperty("agent").GetProperty("model").GetString());
                Assert.Equal("none", body.GetProperty("environment").GetProperty("type").GetString());
                Assert.True(body.GetProperty("stream").GetBoolean());
                return Sse(Stream);
            }
            return Read(http);
        });
        var request = Request(native);
        var before = JsonSerializer.Serialize(request);
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal("completed", result.Status);
        Assert.Equal("Hello", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal(15, Assert.IsType<AIUsage>(result.Usage).TotalTokens);
        var receipt = Assert.IsType<JsonElement>(result.Metadata!["ampersand"]);
        Assert.True(JsonElement.DeepEquals(Element(Idle), receipt.GetProperty("session")));
        Assert.True(JsonElement.DeepEquals(Element(Items).GetProperty("data"), receipt.GetProperty("items")));
        Assert.Equal(6, receipt.GetProperty("events").GetArrayLength());
        Assert.Single(result.Output.Items[0].Content!.OfType<AIToolCallContentPart>(), tool => tool.ProviderExecuted == true);
    }

    [Fact]
    public async Task Maps_instructions_tools_and_structured_output()
    {
        using var client = Client(async (http, ct) =>
        {
            if (http.Method != HttpMethod.Post) return Read(http);
            var agent = Element(await http.Content!.ReadAsStringAsync(ct)).GetProperty("agent");
            Assert.Equal("Be helpful.\n\nSystem instructions.", agent.GetProperty("instructions").GetString());
            Assert.Equal("lookup", agent.GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal("object", agent.GetProperty("tools")[0].GetProperty("parameters").GetProperty("type").GetString());
            var format = agent.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.Equal("answer", format.GetProperty("name").GetString());
            Assert.False(format.TryGetProperty("json_schema", out _));
            return Sse(Stream);
        });
        await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "ampersand", Model = "ampersand-v0.1", Instructions = "Be helpful.",
            Tools = [new AIToolDefinition { Name = "lookup", Description = "Find a value", InputSchema = new { type = "object" } }],
            ResponseFormat = Element("""{"type":"json_schema","json_schema":{"name":"answer","schema":{"type":"object"},"strict":true}}"""),
            Input = new AIInput
            {
                Items = [
                    new AIInputItem { Role = "system", Content = [new AITextContentPart { Type = "text", Text = "System instructions." }] },
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }] }
                ]
            }
        });
    }

    [Fact]
    public async Task Streaming_waits_for_session_idle_and_uses_authoritative_text()
    {
        using var client = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Post ? Sse(Stream) : Read(http)));
        var events = await Collect(Provider(client).StreamUnifiedAsync(Request()));
        Assert.Equal(6, events.Count(evt => evt.Event.Type == "data-ampersand-event"));
        Assert.Equal("Hello", string.Concat(events.Select(evt => evt.Event.Data).OfType<AITextDeltaEventData>().Select(text => text.Delta)));
        var finish = Assert.IsType<AIFinishEventData>(events.Last().Event.Data);
        Assert.Equal("stop", finish.FinishReason);
        Assert.Equal(15, finish.TotalTokens);
        Assert.Equal(15, finish.MessageMetadata!.TotalTokens);
        Assert.True(events.FindIndex(evt => evt.Event.Type == "text-delta") <
            events.FindLastIndex(evt => evt.Event.Type == "data-ampersand-event"));
        Assert.Equal(2, events.Count(evt => evt.Event.Data is AIToolOutputAvailableEventData { ToolName: "ampersand_session" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receipt_continues_session_and_sends_only_new_input(bool roundtrip)
    {
        using var initial = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Post ? Sse(Stream) : Read(http)));
        var first = await Provider(initial).ExecuteUnifiedAsync(Request());
        var history = first.Output!.Items![0].Content!;
        if (roundtrip)
        {
            var tool = Assert.Single(history.OfType<AIToolCallContentPart>());
            history = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolName = tool.ToolName, ToolCallId = tool.ToolCallId, ProviderExecuted = true,
                Output = Element(tool.Output)
            }];
        }
        var submitted = false;
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            if (http.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/agents/sessions/sess_1/events", http.RequestUri!.AbsolutePath);
                Assert.True(http.Headers.Contains("Idempotency-Key"));
                var events = Element(await http.Content!.ReadAsStringAsync(ct)).GetProperty("events");
                var evt = Assert.Single(events.EnumerateArray());
                Assert.Equal("agent.session.input.message", evt.GetProperty("type").GetString());
                Assert.Equal("Next", Assert.Single(evt.GetProperty("input").EnumerateArray()).GetProperty("content")[0].GetProperty("text").GetString());
                submitted = true;
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            if (http.RequestUri!.AbsolutePath.EndsWith("/events"))
            {
                Assert.True(submitted);
                Assert.Equal("", http.RequestUri.Query);
                Assert.False(http.Headers.Contains("Last-Event-ID"));
                return Sse(Stream);
            }
            if (http.RequestUri.Query.Contains("order=asc")) Assert.Contains("after=msg_1", http.RequestUri.Query);
            return Read(http);
        });
        var request = new AIRequest
        {
            ProviderId = "ampersand", Model = "ampersand-v0.1",
            Input = new AIInput
            {
                Items = [
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Old" }] },
                    new AIInputItem { Role = "assistant", Content = history },
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Next" }] }
                ]
            }
        };
        await Provider(client).ExecuteUnifiedAsync(request);
        Assert.True(submitted);
    }

    [Fact]
    public async Task Tool_results_echo_original_turn_and_call_and_do_not_replay_user_input()
    {
        using var initial = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Post
            ? Sse(Frame("agent.session.requires_action", Waiting)) : Read(http, Waiting)));
        var first = await Provider(initial).ExecuteUnifiedAsync(Request());
        Assert.Equal("incomplete", first.Status);
        Assert.Null(first.Usage);
        var pending = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>(), tool => tool.ProviderExecuted != true);
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                var evt = Assert.Single(Element(await http.Content!.ReadAsStringAsync(ct)).GetProperty("events").EnumerateArray());
                Assert.Equal("agent.session.input.tool_result", evt.GetProperty("type").GetString());
                Assert.Equal("turn_1", evt.GetProperty("turn_id").GetString());
                Assert.Equal("call_1", evt.GetProperty("call_id").GetString());
                Assert.True(evt.GetProperty("success").GetBoolean());
                Assert.Equal("found", evt.GetProperty("output").GetString());
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            if (http.RequestUri!.AbsolutePath.EndsWith("/events")) return Sse(Stream);
            return Read(http, http.RequestUri.AbsolutePath.EndsWith("sess_1") ? Waiting : Idle);
        });
        await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "ampersand", Model = "ampersand-v0.1",
            Input = new AIInput
            {
                Items = [
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Old" }] },
                    new AIInputItem { Role = "assistant", Content = [
                        first.Output.Items[0].Content!.OfType<AIToolCallContentPart>().Single(tool => tool.ProviderExecuted == true),
                        new AIToolCallContentPart { Type = "tool-call", ToolCallId = pending.ToolCallId, ToolName = pending.ToolName, Output = "found" }
                    ] }
                ]
            }
        });
    }

    [Fact]
    public async Task Native_events_are_passed_through_with_the_supplied_retry_key()
    {
        var native = Element("""{"session_id":"sess_1","idempotency_key":"retry_1","events":[{"type":"agent.session.input.cancel"}]}""");
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                Assert.Equal("retry_1", Assert.Single(http.Headers.GetValues("Idempotency-Key")));
                Assert.True(JsonElement.DeepEquals(native.GetProperty("events"),
                    Element(await http.Content!.ReadAsStringAsync(ct)).GetProperty("events")));
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            return http.RequestUri!.AbsolutePath.EndsWith("/events") ? Sse(Stream) : Read(http);
        });
        await Provider(client).ExecuteUnifiedAsync(Request(native));
    }

    [Fact]
    public async Task Poll_only_recovers_without_submitting_input()
    {
        using var client = Client((http, _) =>
        {
            Assert.Equal(HttpMethod.Get, http.Method);
            return Task.FromResult(http.RequestUri!.AbsolutePath.EndsWith("/events") ? Sse(Stream) : Read(http));
        });
        var result = await Provider(client).ExecuteUnifiedAsync(Request(Element("""{"session_id":"sess_1","pollOnly":true}""")));
        Assert.Equal("completed", result.Status);
        Assert.Equal("Hello", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task Same_native_call_id_in_different_turns_has_distinct_compatible_ids()
    {
        var waiting = Waiting.Replace("\"required_actions\":[", """
            "required_actions":[{"type":"function_call","turn_id":"turn_2","call_id":"call_1","name":"lookup","arguments":{}},
            """);
        using var client = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Post
            ? Sse(Frame("agent.session.requires_action", waiting)) : Read(http, waiting)));
        var response = await Provider(client).ExecuteUnifiedAsync(Request());
        var calls = response.Output!.Items![0].Content!.OfType<AIToolCallContentPart>().Where(call => call.ProviderExecuted != true).ToList();
        Assert.Equal(2, calls.Count);
        Assert.NotEqual(calls[0].ToolCallId, calls[1].ToolCallId);
        Assert.All(calls, call => Assert.Matches("^[A-Za-z0-9_-]{1,64}$", call.ToolCallId));
    }

    [Fact]
    public async Task User_role_tool_result_is_not_sent_again_as_a_message()
    {
        using var initial = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Post
            ? Sse(Frame("agent.session.requires_action", Waiting)) : Read(http, Waiting)));
        var first = await Provider(initial).ExecuteUnifiedAsync(Request());
        var pending = Assert.Single(first.Output!.Items![0].Content!.OfType<AIToolCallContentPart>(), tool => tool.ProviderExecuted != true);
        var sessionReads = 0;
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                var evt = Assert.Single(Element(await http.Content!.ReadAsStringAsync(ct)).GetProperty("events").EnumerateArray());
                Assert.Equal("agent.session.input.tool_result", evt.GetProperty("type").GetString());
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }
            if (http.RequestUri!.AbsolutePath.EndsWith("/events")) return Sse(Stream);
            return Read(http, http.RequestUri.AbsolutePath.EndsWith("sess_1") && sessionReads++ == 0 ? Waiting : Idle);
        });
        var result = await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "ampersand", Model = "ampersand-v0.1",
            Input = new AIInput
            {
                Items = [
                    new AIInputItem { Role = "assistant", Content = first.Output.Items[0].Content },
                    new AIInputItem { Role = "user", Content = [
                        new AIToolCallContentPart { Type = "tool-call", ToolCallId = pending.ToolCallId, Output = "found" }
                    ] }
                ]
            }
        });
        Assert.Equal("completed", result.Status);
    }

    [Fact]
    public async Task Native_session_updates_are_not_normalized()
    {
        var native = Element("""{"session_id":"sess_1","agent":{"reasoning":{"effort":null}},"metadata":{"note":"test"},"input":"Next"}""");
        var updated = false;
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Post)
            {
                var body = Element(await http.Content!.ReadAsStringAsync(ct));
                if (http.RequestUri!.AbsolutePath.EndsWith("/events"))
                {
                    Assert.True(updated);
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                }
                Assert.Equal("/v1/agents/sessions/sess_1", http.RequestUri.AbsolutePath);
                Assert.Equal(JsonValueKind.Null, body.GetProperty("agent").GetProperty("reasoning").GetProperty("effort").ValueKind);
                Assert.Equal("test", body.GetProperty("metadata").GetProperty("note").GetString());
                Assert.False(body.TryGetProperty("input", out _));
                updated = true;
                return Json(Idle);
            }
            return http.RequestUri!.AbsolutePath.EndsWith("/events") ? Sse(Stream) : Read(http);
        });
        await Provider(client).ExecuteUnifiedAsync(Request(native));
        Assert.True(updated);
    }

    [Fact]
    public async Task Items_are_paginated_and_silent_completion_is_valid()
    {
        var pages = 0;
        using var client = Client((http, _) =>
        {
            if (http.Method == HttpMethod.Post) return Task.FromResult(Sse(Stream));
            if (http.RequestUri!.AbsolutePath.EndsWith("/items"))
            {
                if (pages++ == 0) return Task.FromResult(Json("""{"data":[{"id":"user_1","type":"message","role":"user","content":[]}],"has_more":true,"last_id":"user_1"}"""));
                Assert.Contains("after=user_1", http.RequestUri.Query);
                return Task.FromResult(Json("""{"data":[],"has_more":false,"last_id":null}"""));
            }
            return Task.FromResult(Read(http));
        });
        var result = await Provider(client).ExecuteUnifiedAsync(Request());
        Assert.Equal(2, pages);
        Assert.Equal("completed", result.Status);
        Assert.Empty(result.Output!.Items![0].Content!.OfType<AITextContentPart>());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Idle_session_does_not_hide_failed_or_cancelled_turns(string status)
    {
        using var client = Client((http, _) => Task.FromResult(http.Method == HttpMethod.Post ? Sse(Stream)
            : http.RequestUri!.AbsolutePath.EndsWith("/turns")
                ? Json($$$"""{"data":[{"id":"turn_1","status":"{{{status}}}","error":{"code":"failure","message":"broken"}}]}""")
                : Read(http)));
        var result = await Provider(client).ExecuteUnifiedAsync(Request());
        Assert.Equal("failed", result.Status);
        var receipt = Assert.IsType<JsonElement>(result.Metadata!["ampersand"]);
        Assert.Equal("failure", receipt.GetProperty("turns").GetProperty("data")[0].GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Truncated_stream_and_native_errors_fail_explicitly()
    {
        using var truncated = Client((_, _) => Task.FromResult(Sse(Frame("agent.session.created", """{"id":"sess_1","status":"in_progress"}"""))));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Provider(truncated).ExecuteUnifiedAsync(Request()));
        using var error = Client((_, _) => Task.FromResult(Sse("""data: {"type":"error","error":{"code":"native_error","message":"broken"}}""" + "\n\n")));
        Assert.Contains("native_error", (await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(error).ExecuteUnifiedAsync(Request()))).Message);
        using var denied = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("""{"error":{"code":"insufficient_credit","message":"Add credit"}}""")
        }));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(denied).ExecuteUnifiedAsync(Request()));
        Assert.Equal(HttpStatusCode.PaymentRequired, exception.StatusCode);
        Assert.Contains("insufficient_credit", exception.Message);
    }

    [Fact]
    public async Task Invalid_options_and_cancellation_make_no_requests()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("No HTTP expected."));
        var provider = Provider(client);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(Element("[]"))));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(Element("""{"events":[]}"""))));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(Element("""{"agent":{"model":"other"}}"""))));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "ampersand", Model = "ampersand-v0.1", Temperature = 0.4, Input = new AIInput { Text = "Hello" }
        }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request(), cancelled.Token));
    }

    private static HttpResponseMessage Read(HttpRequestMessage http, string session = Idle)
        => http.RequestUri!.AbsolutePath.EndsWith("/items") ? Json(Items)
            : http.RequestUri.AbsolutePath.EndsWith("/turns") ? Json(Turns) : Json(session);
    private static string Frame(string type, string session)
        => $"event: {type}\ndata: " + JsonSerializer.Serialize(new { type, event_id = "event_" + type, session = Element(session) }) + "\n\n";
    private static AIRequest Request(JsonElement? native = null) => new()
    {
        ProviderId = "ampersand", Model = "ampersand/ampersand-v0.1", Input = new AIInput { Text = "Hello" },
        Metadata = native is { } value ? new() { ["ampersand"] = value } : null
    };
    private static AmpersandProvider Provider(HttpClient client, string? key = "test-key") => new(new Keys(key), new Clients(client));
    private static void Auth(HttpRequestMessage http, bool models = false)
    {
        Assert.Equal("https", http.RequestUri!.Scheme);
        Assert.Equal("ampersand.humansand.ai", http.RequestUri.Host);
        Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
        if (models) Assert.False(http.Headers.Contains("OpenAI-Beta"));
        else Assert.Equal("agents=v1", Assert.Single(http.Headers.GetValues("OpenAI-Beta")));
    }
    private static JsonElement Element(object? value) => value is string text
        ? JsonSerializer.Deserialize<JsonElement>(text) : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "text/event-stream") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }
    private sealed class Keys(string? key = "test-key") : IApiKeyResolver { public string? Resolve(string provider) => key; }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
