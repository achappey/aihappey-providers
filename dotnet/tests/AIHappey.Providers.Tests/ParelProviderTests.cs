using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Parel;
using AIHappey.Unified.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class ParelProviderTests
{
    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<ParelProvider>(root.GetRequiredService<ProviderRegistry>().GetModelProvider(scope.ServiceProvider, "parel"));
    }

    [Fact]
    public async Task Discovery_uses_agent_names_and_request_scoped_auth()
    {
        using var client = Client((http, _) =>
        {
            Auth(http);
            Assert.Equal("/agents", http.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""[{"id":"agt_1","name":"support","model":"model-1"}]"""));
        });
        var model = Assert.Single(await Provider(client).ListModels());
        Assert.Equal("parel/support", model.Id);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Turn_preserves_native_payloads_and_returns_session_receipt()
    {
        var call = 0;
        using var client = Client(async (http, ct) =>
        {
            Auth(http);
            switch (call++)
            {
                case 0:
                    Assert.Equal("/sessions", http.RequestUri!.AbsolutePath);
                    var session = Parse(await http.Content!.ReadAsStringAsync(ct));
                    Assert.Equal("support", session.GetProperty("agent").GetString());
                    Assert.Equal("acme", session.GetProperty("instance").GetString());
                    Assert.Equal(9007199254740993L, session.GetProperty("future").GetInt64());
                    return Json("""{"id":"ssn_1","agentId":"agt_1","status":"ready","future":true}""");
                case 1:
                    Assert.Equal("/sessions/ssn_1/messages", http.RequestUri!.AbsolutePath);
                    return Json("[]");
                case 2:
                    Assert.Equal("msg-1", http.Headers.GetValues("Idempotency-Key").Single());
                    var body = Parse(await http.Content!.ReadAsStringAsync(ct));
                    Assert.Equal("native", body.GetProperty("content").GetString());
                    Assert.True(body.GetProperty("context").GetProperty("deep").GetBoolean());
                    Assert.Equal(JsonValueKind.Null, body.GetProperty("future").ValueKind);
                    return Json("""{"status":"accepted","inputId":"inp_1","turnId":"trn_1","warnings":[{"future":1}]}""");
                case 3:
                    Assert.Equal("/turns/trn_1", http.RequestUri!.AbsolutePath);
                    return Json("""{"id":"trn_1","status":"completed","terminal":true,"future":7}""");
                case 4:
                    return Json("""[{"id":"inp_1","role":"user","content":"native"},{"role":"assistant","parts":[{"type":"text","text":"Hello"}]}]""");
                default: throw new InvalidOperationException();
            }
        });
        var request = Request(Parse("""
            {"idempotency_key":"msg-1","session":{"instance":"acme","future":9007199254740993},
             "message":{"content":"native","context":{"deep":true},"future":null}}
            """));
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(5, call);
        Assert.Equal("completed", result.Status);
        Assert.Equal("Hello", Assert.Single(result.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        var receipt = Assert.IsType<JsonElement>(result.Metadata!["parel"]);
        Assert.Equal("ssn_1", receipt.GetProperty("session_id").GetString());
        Assert.Equal(7, receipt.GetProperty("turn").GetProperty("future").GetInt32());
        Assert.Single(result.Output.Items[0].Content!.OfType<AIToolCallContentPart>());
    }

    [Fact]
    public async Task Streaming_emits_receipt_and_validates_existing_agent()
    {
        var call = 0;
        using var client = Client((http, _) =>
        {
            Auth(http);
            return Task.FromResult(call++ switch
            {
                0 => Json("""{"id":"ssn_1","agentId":"agt_1"}"""),
                1 => Json("""{"id":"agt_1","name":"support"}"""),
                2 => Json("""[{"id":"old","role":"user","content":"old"},{"role":"assistant","content":"old reply"}]"""),
                3 => Json("""{"status":"accepted","inputId":"inp_2","turnId":"trn_2"}"""),
                4 => Json("""{"id":"trn_2","status":"completed","terminal":true}"""),
                5 => Json("""[{"id":"old","role":"user","content":"old"},{"role":"assistant","content":"old reply"},{"id":"inp_2","role":"user","content":"latest"},{"role":"assistant","content":"new reply"}]"""),
                _ => throw new InvalidOperationException()
            });
        });
        var events = new List<AIStreamEvent>();
        await foreach (var evt in Provider(client).StreamUnifiedAsync(Request(Parse("""{"session_id":"ssn_1"}""")))) events.Add(evt);
        Assert.Contains(events, e => e.Event.Type == "tool-input-available");
        Assert.Contains(events, e => e.Event.Type == "tool-output-available");
        Assert.Equal("new reply", Assert.IsType<AITextDeltaEventData>(Assert.Single(events, e => e.Event.Type == "text-delta").Event.Data).Delta);
        Assert.Equal("finish", events.Last().Event.Type);
    }

    [Fact]
    public async Task Queued_turn_uses_turn_id_to_exclude_other_replies()
    {
        var call = 0;
        using var client = Client((http, _) => Task.FromResult(call++ switch
        {
            0 => Json("""{"id":"ssn_1"}"""),
            1 => Json("[]"),
            2 => Json("""{"status":"queued","inputId":"inp_1"}"""),
            3 => Json("""{"inputId":"inp_1","turnId":"trn_2","status":"running"}"""),
            4 => Json("""{"id":"trn_2","status":"completed","terminal":true}"""),
            5 => Json("""[{"role":"user","id":"inp_1","parts":[]},{"role":"assistant","turnId":"trn_1","parts":[{"type":"text","text":"wrong"}]},{"role":"assistant","turnId":"trn_2","parts":[{"type":"text","text":"right"}]}]"""),
            _ => throw new InvalidOperationException()
        }));
        var response = await Provider(client).ExecuteUnifiedAsync(Request());
        Assert.Equal("right", Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task Prior_tool_receipt_continues_the_session_and_body_idempotency_key()
    {
        var call = 0;
        using var client = Client((http, _) =>
        {
            if (call == 3) Assert.Equal("native-key", http.Headers.GetValues("Idempotency-Key").Single());
            return Task.FromResult(call++ switch
            {
                0 => Json("""{"id":"ssn_1","agentId":"agt_1"}"""),
                1 => Json("""{"id":"agt_1"}"""),
                2 => Json("[]"),
                3 => Json("""{"status":"accepted","turnId":"trn_1"}"""),
                4 => Json("""{"status":"completed"}"""),
                5 => Json("""[{"role":"assistant","turnId":"trn_1","parts":[{"type":"text","text":"continued"}]}]"""),
                _ => throw new InvalidOperationException()
            });
        });
        var request = new AIRequest
        {
            ProviderId = "parel", Model = "parel/support",
            Metadata = new() { ["parel"] = Parse("""{"message":{"idempotencyKey":"native-key"}}""") },
            Input = new AIInput { Items =
            [
                new AIInputItem { Role = "assistant", Content =
                [
                    new AIToolCallContentPart { Type = "tool-call", ToolName = "parel_session", ProviderExecuted = true,
                        Input = Parse("""{"agent":"support","session_id":"ssn_1"}""") }
                ] },
                new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "next" }] }
            ] }
        };
        var response = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal("continued", Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task Failed_turn_and_wrong_session_do_not_return_success()
    {
        var call = 0;
        using var wrong = Client((_, _) => Task.FromResult(call++ == 0
            ? Json("""{"agentId":"other"}""") : Json("""{"id":"agt_1"}""")));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(wrong).ExecuteUnifiedAsync(Request(Parse("""{"session_id":"ssn_1"}"""))));
        call = 0;
        using var failed = Client((_, _) => Task.FromResult(call++ switch
        {
            0 => Json("""{"id":"ssn_1"}"""),
            1 => Json("[]"),
            2 => Json("""{"status":"accepted","turnId":"trn_1"}"""),
            _ => Json("""{"status":"error","errorCode":"rate_limited","errorMessage":"try later"}""")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(failed).ExecuteUnifiedAsync(Request()));
        Assert.Contains("try later", error.Message);
    }

    [Fact]
    public async Task Unsupported_options_are_rejected_before_network_requests()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected HTTP request"));
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ExecuteUnifiedAsync(Request(Parse("""{"injectInFlight":true}"""))));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request(Parse("[]"))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ExecuteUnifiedAsync(Request(), cancellation.Token));
    }

    private static AIRequest Request(JsonElement? metadata = null) => new()
    {
        ProviderId = "parel", Model = "parel/support", Input = new AIInput { Text = "latest" },
        Metadata = metadata is { } value ? new() { ["parel"] = value } : null
    };
    private static ParelProvider Provider(HttpClient client) => new(new Keys(), new Clients(client));
    private static void Auth(HttpRequestMessage http)
    {
        Assert.Equal("https://api.parel.sh", http.RequestUri!.GetLeftPart(UriPartial.Authority));
        Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", http.Headers.Authorization?.Parameter);
    }
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
