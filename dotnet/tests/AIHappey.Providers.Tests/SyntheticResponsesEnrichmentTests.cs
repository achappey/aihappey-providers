using System.Reflection;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Output;
using AIHappey.Responses.Streaming;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests;

public sealed class SyntheticResponsesEnrichmentTests
{
    private static readonly Type ProviderType = typeof(OpenAIProvider);
    private static object State() => Activator.CreateInstance(ProviderType.GetNestedType("OpenAiResponseStreamEnrichmentState", BindingFlags.NonPublic)!)!;
    private static object? Call(string name, object? instance, params object?[] args)
        => ProviderType.GetMethod(name, BindingFlags.NonPublic | (instance is null ? BindingFlags.Static : BindingFlags.Instance))!.Invoke(instance, args);

    [Theory]
    [InlineData("download_file")]
    [InlineData("upload_files")]
    [InlineData("upload_generated_image")]
    public async Task Synthetic_artifacts_use_standard_events_and_precede_the_terminal_snapshot(string name)
    {
        var state = State();
        var parts = new List<ResponseStreamPart>();
        var result = new CallToolResult
        {
            Content = [new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(
                new byte[] { 0, 1, 2, 255 }, "file:///report.docx", "application/octet-stream") }],
            StructuredContent = JsonSerializer.SerializeToElement(new { filename = "report.docx", data_url = "data:application/octet-stream;base64,AAEC/w==" })
        };
        Call("AddSyntheticCustomToolCallParts", null, parts, state, "call_1", name,
            JsonSerializer.SerializeToElement(new { file_id = "file_1" }), result,
            new Dictionary<string, Dictionary<string, object>> { ["openai"] = new() { ["container_id"] = "cntr_1" } }, null);
        Assert.Equal(new[] { "response.output_item.added", "response.mcp_call_arguments.done", "response.output_item.done" }, parts.Select(p => p.Type));
        var done = Assert.IsType<ResponseOutputItemDone>(parts[^1]);
        Assert.Equal("mcp_call", done.Item.Type);
        var payload = SyntheticResponsesTool.ParseResult(done.Item.AdditionalProperties!["output"].GetString()!);
        Assert.Equal("AAEC/w==", payload.GetProperty("content")[0].GetProperty("resource").GetProperty("blob").GetString());
        var provider = new OpenAIProvider(null!, new Factory(), new AsyncCacheHelper(), null!);
        var terminal = new ResponseCompleted { Response = new ResponseResult { Id = "resp_1", Status = "completed", Model = "test", Output = [] } };
        var finalParts = await (Task<List<ResponseStreamPart>>)Call("EnrichResponseStreamPartWithContainerFilesAsync", provider, terminal, state, CancellationToken.None)!;
        Assert.Same(terminal, finalParts[^1]);
        var snapshot = Assert.IsType<ResponseOutputMcpCall>(Assert.Single(terminal.Response.Output));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(snapshot, ResponseJson.Default),
            JsonSerializer.SerializeToElement(done.Item, ResponseJson.Default)));
    }

    [Fact]
    public void Native_and_synthetic_output_indexes_share_a_contiguous_identity_space()
    {
        var state = State();
        var first = (ResponseOutputItemAdded)Call("RemapNativeResponseOutputIndex", null,
            new ResponseOutputItemAdded { OutputIndex = 0, Item = new() { Id = "msg_1", Type = "message" } }, state)!;
        var parts = new List<ResponseStreamPart>();
        Call("AddSyntheticCustomToolCallParts", null, parts, state, "call_1", "download_file",
            JsonSerializer.SerializeToElement(new { }), new CallToolResult { Content = [] },
            new Dictionary<string, Dictionary<string, object>>(), null);
        var second = (ResponseOutputItemAdded)Call("RemapNativeResponseOutputIndex", null,
            new ResponseOutputItemAdded { OutputIndex = 1, Item = new() { Id = "msg_2", Type = "message" } }, state)!;
        Assert.Equal(0, first.OutputIndex);
        Assert.Equal(1, Assert.IsType<ResponseOutputItemAdded>(parts[0]).OutputIndex);
        Assert.Equal(2, second.OutputIndex);
        var delta = (ResponseOutputTextDelta)Call("RemapNativeResponseOutputIndex", null,
            new ResponseOutputTextDelta { ItemId = "msg_2", Outputindex = 1, Delta = "text" }, state)!;
        Assert.Equal(2, delta.Outputindex);
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoNetwork());
    }

    [Theory]
    [InlineData("application/octet-stream", "resource", "blob")]
    [InlineData("image/png", "image", "data")]
    [InlineData("text/plain", "resource", "text")]
    public void Download_result_builder_emits_valid_MCP_binary_image_and_text_content(string mimeType, string contentType, string field)
    {
        var downloadType = ProviderType.GetNestedType("OpenAiContainerCitationDownload", BindingFlags.NonPublic)!;
        var download = Activator.CreateInstance(downloadType)!;
        var bytes = mimeType == "text/plain" ? System.Text.Encoding.UTF8.GetBytes("héllo 🌳") : new byte[] { 0, 1, 2, 255 };
        void Set(string name, object value) => downloadType.GetProperty(name)!.SetValue(download, value);
        Set("Bytes", bytes); Set("MediaType", mimeType); Set("CanonicalUrl", "file:///report");
        Set("Filename", "report"); Set("FileId", "file_1"); Set("DataUrl", $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}");
        var result = (CallToolResult)Call("CreateDownloadToolCallResult", null, download)!;
        var json = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
        var content = json.GetProperty("content")[0];
        Assert.Equal(contentType, content.GetProperty("type").GetString());
        var payload = contentType == "resource" ? content.GetProperty("resource") : content;
        Assert.Equal(field == "text" ? System.Text.Encoding.UTF8.GetString(bytes) : Convert.ToBase64String(bytes), payload.GetProperty(field).GetString());
        Assert.Equal($"data:{mimeType};base64,{Convert.ToBase64String(bytes)}", json.GetProperty("structuredContent").GetProperty("data_url").GetString());
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("No network is needed for synthetic tool projection.");
    }
}
