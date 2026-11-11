using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Providers.Tests;

public sealed class OpenAIContainerLifecycleTests
{
    private const string Model = "gpt-6.1-terra";
    private const string SavedContainer = "cntr_saved";
    private const string CreatedContainer = "cntr_created";

    public static IEnumerable<object[]> ContainerCases()
    {
        foreach (var streaming in new[] { false, true })
        foreach (var tools in new[] { "code_interpreter", "shell", "none", "empty", "function", "local_shell" })
        foreach (var container in new[]
        {
            "\"cntr_saved\"", "{\"id\":\"cntr_saved\"}", "{\"container_id\":\"cntr_saved\"}",
            "null", "{}", "\"\"", "17", "{\"id\":17}"
        })
            yield return [streaming, tools, container];
    }

    [Theory]
    [MemberData(nameof(ContainerCases))]
    public async Task Native_requests_consume_only_root_container_state_before_hosted_tool_checks(
        bool streaming, string tools, string container)
    {
        using var session = new ProviderSession();
        var request = NativeRequest(tools, container);
        var before = JsonSerializer.SerializeToElement(request, ResponseJson.Default);

        var result = await Execute(session.Provider, request, streaming);

        var sent = Assert.Single(session.ResponseBodies);
        Assert.False(sent.TryGetProperty("container", out _));
        Assert.False(request.AdditionalProperties!.ContainsKey("container"));
        foreach (var property in before.EnumerateObject().Where(property => property.Name is not ("container" or "tools" or "stream")))
            Assert.True(JsonElement.DeepEquals(property.Value, sent.GetProperty(property.Name)), property.Name);
        Assert.True(JsonElement.DeepEquals(before.GetProperty("metadata"), sent.GetProperty("metadata")));

        var hosted = tools is "code_interpreter" or "shell";
        var validId = container.Contains(SavedContainer, StringComparison.Ordinal);
        if (hosted && validId)
        {
            AssertToolContainer(sent, tools, SavedContainer);
            Assert.Equal([("GET", $"/v1/containers/{SavedContainer}")],
                session.ContainerCalls.Where(call => !call.Path.EndsWith("/files", StringComparison.Ordinal)));
            Assert.Equal(SavedContainer, ContainerMetadata(result));
        }
        else
        {
            Assert.Empty(session.ContainerCalls);
            Assert.Equal(before.TryGetProperty("tools", out var originalTools), sent.TryGetProperty("tools", out var sentTools));
            if (originalTools.ValueKind != JsonValueKind.Undefined)
                Assert.True(JsonElement.DeepEquals(originalTools, sentTools));
        }
    }

    [Theory]
    [InlineData(false, "code_interpreter")]
    [InlineData(true, "code_interpreter")]
    [InlineData(false, "shell")]
    [InlineData(true, "shell")]
    public async Task Attachments_still_upload_and_emit_lifecycle_metadata(bool streaming, string tool)
    {
        using var session = new ProviderSession();
        var request = NativeRequest(tool, "null", attachment: true);

        var result = await Execute(session.Provider, request, streaming);

        var sent = Assert.Single(session.ResponseBodies);
        Assert.False(sent.TryGetProperty("container", out _));
        AssertToolContainer(sent, tool, CreatedContainer);
        Assert.Contains(("POST", "/v1/containers"), session.ContainerCalls);
        Assert.Contains(("POST", $"/v1/containers/{CreatedContainer}/files"), session.ContainerCalls);
        Assert.Contains("/mnt/data/notes.txt", sent.GetProperty("input").GetRawText());
        Assert.DoesNotContain("base64", sent.GetProperty("input").GetRawText());
        Assert.Equal(CreatedContainer, ContainerMetadata(result));
        Assert.Contains("upload_files", result.GetRawText());
        Assert.Contains("source_identity", result.GetRawText());
    }

    [Theory]
    [InlineData(false, "code_interpreter")]
    [InlineData(true, "code_interpreter")]
    [InlineData(false, "shell")]
    [InlineData(true, "shell")]
    public async Task Explicit_tool_container_still_wins_over_saved_state(bool streaming, string tool)
    {
        using var session = new ProviderSession();
        var request = NativeRequest(tool, "{\"id\":\"cntr_saved\"}");
        request.Tools = JsonSerializer.Deserialize<List<ResponseToolDefinition>>(
            tool == "code_interpreter"
                ? """[{"type":"code_interpreter","container":"cntr_explicit"}]"""
                : """[{"type":"shell","environment":{"type":"container_reference","container_id":"cntr_explicit"}}]""");

        await Execute(session.Provider, request, streaming);

        var sent = Assert.Single(session.ResponseBodies);
        Assert.False(sent.TryGetProperty("container", out _));
        AssertToolContainer(sent, tool, "cntr_explicit");
        Assert.Equal([("GET", "/v1/containers/cntr_explicit")],
            session.ContainerCalls.Where(call => !call.Path.EndsWith("/files", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false, "code_interpreter")]
    [InlineData(true, "code_interpreter")]
    [InlineData(false, "shell")]
    [InlineData(true, "shell")]
    public async Task Expired_saved_container_is_replaced_and_attachments_are_uploaded(bool streaming, string tool)
    {
        using var session = new ProviderSession { SavedContainerExpired = true };
        var request = NativeRequest(tool, "{\"id\":\"cntr_saved\"}", attachment: true);

        await Execute(session.Provider, request, streaming);

        var sent = Assert.Single(session.ResponseBodies);
        Assert.False(sent.TryGetProperty("container", out _));
        AssertToolContainer(sent, tool, CreatedContainer);
        Assert.Equal(new[]
        {
            ("GET", $"/v1/containers/{SavedContainer}"), ("POST", "/v1/containers"),
            ("POST", $"/v1/containers/{CreatedContainer}/files")
        }, session.ContainerCalls.Where(call => !call.Path.EndsWith("/files", StringComparison.Ordinal) || call.Method != "GET"));
    }

    [Theory]
    [InlineData(false, "code_interpreter")]
    [InlineData(true, "code_interpreter")]
    [InlineData(false, "shell")]
    [InlineData(true, "shell")]
    public async Task Chat_continuation_after_upload_and_approved_client_tool_reuses_container(
        bool streaming, string tool)
    {
        using var session = new ProviderSession { FirstResponseCallsClientTool = true };
        var chat = ChatRequest(tool);
        var firstResponse = await session.Provider.ResponsesAsync(chat.ToUnifiedRequest("openai").ToResponseRequest("openai"));
        var first = firstResponse.ToUnifiedResponse("openai");
        var firstMetadata = JsonSerializer.SerializeToElement(firstResponse.Metadata, JsonSerializerOptions.Web)
            .GetProperty("providerMetadata");
        Assert.Equal(CreatedContainer, firstMetadata.GetProperty("openai").GetProperty("container").GetProperty("id").GetString());
        var parts = first.Output!.Items!.SelectMany(item => item.ToUIMessage().Parts).ToList();
        var clientCall = Assert.Single(first.Output.Items!.SelectMany(item => item.Content ?? [])
            .OfType<AIToolCallContentPart>(), part => part.ToolName == "read_resource");
        // Model the UI message posted after the client approves and executes this call.
        parts.Add(new ToolInvocationPart
        {
            Type = "tool-read_resource", ToolCallId = "fc_client", Input = clientCall.Input!,
            State = "approval-responded", ProviderExecuted = false,
            Output = JsonSerializer.Deserialize<JsonElement>("""{"content":[],"structuredContent":{"files":["notes.txt"]},"isError":false}"""),
            Approval = new ToolApproval { Id = "fc_client", Approved = true, IsAutomatic = true, Reason = "read_resource", Signature = "test-signature" },
            CallProviderMetadata = new()
            {
                ["openai"] = new() { ["type"] = "function_call", ["id"] = "fc_client", ["call_id"] = "call_client" }
            }
        });
        chat.Messages!.Add(new UIMessage
        {
            Id = "assistant-turn", Role = Role.assistant, Parts = parts,
            Metadata = new() { ["providerMetadata"] = firstMetadata }
        });
        var before = JsonSerializer.Serialize(chat, JsonSerializerOptions.Web);
        var restored = chat.ToUnifiedRequest("openai").ToResponseRequest("openai");
        Assert.Equal(CreatedContainer, restored.AdditionalProperties!["container"].GetProperty("id").GetString());
        var expectedCall = Assert.Single(restored.Input!.Items!.OfType<ResponseFunctionCallItem>());

        if (streaming)
            await Drain(session.Provider.StreamAsync(chat));
        else
            await session.Provider.ExecuteUnifiedAsync(chat.ToUnifiedRequest("openai"));

        Assert.Equal(before, JsonSerializer.Serialize(chat, JsonSerializerOptions.Web));
        Assert.Equal(2, session.ResponseBodies.Count);
        var continuation = session.ResponseBodies[1];
        Assert.False(continuation.TryGetProperty("container", out _));
        AssertToolContainer(continuation, tool, CreatedContainer);
        var input = continuation.GetProperty("input").EnumerateArray().ToList();
        var call = Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call");
        var output = Assert.Single(input, item => item.GetProperty("type").GetString() == "function_call_output");
        Assert.Equal(expectedCall.CallId, call.GetProperty("call_id").GetString());
        Assert.Equal(expectedCall.CallId, output.GetProperty("call_id").GetString());
        Assert.Equal("read_resource", call.GetProperty("name").GetString());
        Assert.Contains("notes.txt", output.GetProperty("output").ToString());
        Assert.Single(session.ContainerCalls, call => call == ("POST", "/v1/containers"));
        Assert.Contains(("GET", $"/v1/containers/{CreatedContainer}"), session.ContainerCalls);
        Assert.Equal("auto", continuation.GetProperty("service_tier").GetString());
        Assert.True(continuation.GetProperty("future_option").GetProperty("preserved").GetBoolean());
    }

    [Theory]
    [InlineData(false, "code_interpreter")]
    [InlineData(true, "code_interpreter")]
    [InlineData(false, "shell")]
    [InlineData(true, "shell")]
    public async Task Chat_can_disable_and_reenable_hosted_tools_without_losing_saved_history(
        bool streaming, string tool)
    {
        using var session = new ProviderSession();
        var chat = ChatRequest(tool);
        chat.Messages =
        [
            new UIMessage
            {
                Id = "previous", Role = Role.assistant, Parts = [new TextUIPart { Text = "Previous result" }],
                Metadata = new() { ["providerMetadata"] = new { openai = new { container = new { id = SavedContainer } } } }
            },
            new UIMessage { Id = "next", Role = Role.user, Parts = [new TextUIPart { Text = "Continue" }] }
        ];
        var originalOptions = chat.ProviderMetadata!["openai"].Clone();
        var historyBefore = JsonSerializer.Serialize(chat.Messages, JsonSerializerOptions.Web);
        chat.ProviderMetadata["openai"] = JsonSerializer.SerializeToElement(new { service_tier = "auto" });

        await ExecuteChat(session.Provider, chat, streaming);

        Assert.Empty(session.ContainerCalls);
        Assert.False(Assert.Single(session.ResponseBodies).TryGetProperty("container", out _));
        Assert.Equal(historyBefore, JsonSerializer.Serialize(chat.Messages, JsonSerializerOptions.Web));

        chat.ProviderMetadata["openai"] = originalOptions;
        await ExecuteChat(session.Provider, chat, streaming);

        AssertToolContainer(session.ResponseBodies[1], tool, SavedContainer);
        Assert.False(session.ResponseBodies[1].TryGetProperty("container", out _));
        Assert.Contains(("GET", $"/v1/containers/{SavedContainer}"), session.ContainerCalls);
        Assert.DoesNotContain(session.ContainerCalls, call => call.Method is "POST" or "DELETE");
        Assert.Equal(historyBefore, JsonSerializer.Serialize(chat.Messages, JsonSerializerOptions.Web));
    }

    private static ResponseRequest NativeRequest(string tools, string container, bool attachment = false)
        => JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"gpt-6.1-terra","store":false,"input":[{"type":"message","role":"user","content":CONTENT}],
             "tools":TOOLS,"container":CONTAINER,"service_tier":"auto","include":["reasoning.encrypted_content"],
             "metadata":{"container":{"id":"application-data"},"custom":"keep"},
             "future_option":{"preserved":true,"values":[1,null,false]},"future_null":null}
            """.Replace("CONTENT", attachment
                ? """[{"type":"input_file","filename":"notes.txt","file_data":"data:text/plain;base64,aGVsbG8="},{"type":"input_text","text":"Read the attachment"}]"""
                : """[{"type":"input_text","text":"Continue"}]""")
                .Replace("TOOLS", ToolsJson(tools)).Replace("CONTAINER", container), ResponseJson.Default)!;

    private static string ToolsJson(string tools) => tools switch
    {
        "code_interpreter" => """[{"type":"code_interpreter","container":{"type":"auto"}}]""",
        "shell" => """[{"type":"shell","environment":{"type":"container_auto","memory_limit":"4g"}}]""",
        "local_shell" => """[{"type":"shell","environment":{"type":"local"}}]""",
        "function" => """[{"type":"function","name":"read_resource","parameters":{"type":"object","properties":{}}}]""",
        "empty" => "[]",
        _ => "null"
    };

    private static ChatRequest ChatRequest(string tool)
        => JsonSerializer.Deserialize<ChatRequest>("""
            {"model":"gpt-6.1-terra","tools":[{"name":"read_resource","inputSchema":{"type":"object","properties":{}}}],
             "providerMetadata":{"openai":{"service_tier":"auto","future_option":{"preserved":true},"tools":TOOLS}},
             "messages":[{"id":"user","role":"user","parts":[
               {"type":"file","filename":"notes.txt","mediaType":"text/plain","url":"data:text/plain;base64,aGVsbG8="},
               {"type":"text","text":"Read the attachment and use the client tool"}]}]}
            """.Replace("TOOLS", ToolsJson(tool)), JsonSerializerOptions.Web)!;

    private static void AssertToolContainer(JsonElement sent, string tool, string expected)
    {
        var hosted = Assert.Single(sent.GetProperty("tools").EnumerateArray(), item => item.GetProperty("type").GetString() == tool);
        if (tool == "code_interpreter")
            Assert.Equal(expected, hosted.GetProperty("container").GetString());
        else
        {
            var environment = hosted.GetProperty("environment");
            Assert.Equal("container_reference", environment.GetProperty("type").GetString());
            Assert.Equal(expected, environment.GetProperty("container_id").GetString());
            Assert.False(environment.TryGetProperty("memory_limit", out _));
        }
    }

    private static string? ContainerMetadata(JsonElement result)
        => result.GetProperty("metadata").GetProperty("providerMetadata").GetProperty("openai")
            .GetProperty("container").GetProperty("id").GetString();

    private static async Task<JsonElement> Execute(OpenAIProvider provider, ResponseRequest request, bool streaming)
    {
        request.Stream = streaming;
        if (!streaming)
            return JsonSerializer.SerializeToElement(await provider.ResponsesAsync(request), ResponseJson.Default);

        var parts = new List<JsonElement>();
        await foreach (var part in provider.ResponsesStreamingAsync(request))
            parts.Add(JsonSerializer.SerializeToElement(part, ResponseJson.Default));
        var completed = Assert.Single(parts, part => part.GetProperty("type").GetString() == "response.completed");
        var result = completed.GetProperty("response");
        // Include synthetic upload events as well as completion metadata in assertions.
        var root = JsonNode.Parse(result.GetRawText())!.AsObject();
        root["test_events"] = JsonSerializer.SerializeToNode(parts);
        return JsonSerializer.SerializeToElement(root);
    }

    private static async Task ExecuteChat(OpenAIProvider provider, ChatRequest chat, bool streaming)
    {
        if (streaming) await Drain(provider.StreamAsync(chat));
        else await provider.ExecuteUnifiedAsync(chat.ToUnifiedRequest("openai"));
    }

    private static async Task Drain<T>(IAsyncEnumerable<T> stream)
    {
        await foreach (var _ in stream) { }
    }

    private sealed class ProviderSession : IDisposable
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly HttpClient _client;
        public OpenAIProvider Provider { get; }
        public List<JsonElement> ResponseBodies { get; } = [];
        public List<(string Method, string Path)> ContainerCalls { get; } = [];
        public bool SavedContainerExpired { get; init; }
        public bool FirstResponseCallsClientTool { get; init; }

        public ProviderSession()
        {
            _client = new HttpClient(new CaptureHandler(Send));
            Provider = new(new KeyResolver(), new ClientFactory(_client), new AsyncCacheHelper(_cache), new EndUserIdResolver());
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method.Method;
            if (path == "/v1/models")
                return Json("""{"data":[{"id":"gpt-6.1-terra","owned_by":"openai"}]}""");
            if (path == "/v1/agents")
                return Json("""{"data":[],"has_more":false}""");
            if (path.StartsWith("/v1/containers", StringComparison.Ordinal))
            {
                ContainerCalls.Add((method, path));
                if (path.EndsWith("/files", StringComparison.Ordinal))
                {
                    if (method == "GET") return Json("""{"data":[],"has_more":false}""");
                    var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                    Assert.Equal("hello", await Assert.Single(multipart).ReadAsStringAsync(cancellationToken));
                    return Json(JsonSerializer.Serialize(new
                    {
                        id = "cfile_notes", container_id = path.Split('/')[3], path = "/mnt/data/notes.txt", source = "user"
                    }));
                }
                if (method == "POST")
                    return Json("""{"id":"cntr_created","status":"running"}""");
                return Json(JsonSerializer.Serialize(new
                {
                    id = path.Split('/')[3], status = SavedContainerExpired ? "expired" : "running"
                }));
            }
            if (path != "/v1/responses")
                throw new InvalidOperationException($"Unexpected test HTTP request: {method} {path}");

            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            ResponseBodies.Add(body);
            var response = JsonSerializer.Serialize(new
            {
                id = "resp_test", @object = "response", model = Model, status = "completed", tools = Array.Empty<object>(),
                output = FirstResponseCallsClientTool && ResponseBodies.Count == 1
                    ? new object[] { new { type = "function_call", id = "fc_client", call_id = "call_client", name = "read_resource", arguments = "{}", status = "completed" } }
                    : [],
                usage = new { input_tokens = 10, output_tokens = 2, total_tokens = 12 }
            });
            if (!body.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
                return Json(response);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_test\",\"model\":\"gpt-6.1-terra\",\"status\":\"in_progress\",\"output\":[]}}\n\n"
                    + "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":1,\"response\":" + response + "}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public void Dispose()
        {
            _client.Dispose();
            _cache.Dispose();
        }
    }

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class EndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
