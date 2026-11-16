using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ByteIT;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class ByteITProviderTests
{
    [Fact]
    public async Task Registration_and_catalog_discovery_do_not_require_credentials()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(null));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var provider = Assert.IsType<ByteITProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "byteit"));
        Assert.Equal(new[] { "byteit/parse", "byteit/extract", "byteit/classification", "byteit/custom" },
            (await provider.ListModels()).Select(m => m.Id));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancelled.Token));
    }

    [Theory]
    [InlineData("data:application/pdf;base64,JVBERg==", null)]
    [InlineData("JVBERg==", "application/pdf")]
    [InlineData("https://documents.example/invoice.pdf", null)]
    public async Task Parse_uploads_files_preserves_native_options_and_returns_markdown(string data, string? mediaType)
    {
        var posts = 0;
        var downloads = 0;
        using var client = Client(async (message, _) =>
        {
            if (message.RequestUri!.Host == "documents.example")
            {
                downloads++;
                Assert.False(message.Headers.Contains("X-API-Key"));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF"))
                };
            }
            AssertAuthentication(message);
            if (message.Method == HttpMethod.Post)
            {
                posts++;
                Assert.Equal("/v1/jobs/parse-jobs/", message.RequestUri.AbsolutePath);
                var form = await ReadForm(message);
                Assert.Equal("%PDF", form["file"]);
                Assert.Equal("localfile", form["input_connector"]);
                Assert.Equal("md", form["output_format"]);
                Assert.Equal("""{"languages":["de","en"],"parse_type":"complex","page_range":"1-4","future":null}""", form["processing_options"]);
                Assert.Equal("""{"nested":[true,null,{}],"number":9007199254740993}""", form["future_option"]);
                Assert.DoesNotContain("other", form.Keys);
                Assert.DoesNotContain("foreign_secret", form.Keys);
                var upload = Assert.Single(Assert.IsType<MultipartFormDataContent>(message.Content),
                    p => p.Headers.ContentDisposition!.Name?.Trim('"') == "file");
                Assert.EndsWith(".pdf", upload.Headers.ContentDisposition!.FileName!.Trim('"'));
                return Json("""{"parse_job":{"job_id":"parse-1"}}""");
            }
            return JobResponse(message, "# Invoice");
        });
        var request = Request(data: data, mediaType: mediaType, filename: null, options: Parse("""
                {"processing_options":{"languages":["de","en"],"parse_type":"complex","page_range":"1-4","future":null},
                 "future_option":{"nested":[true,null,{}],"number":9007199254740993}}
                """));
        request.Metadata!["other"] = Parse("""{"foreign_secret":true}""");
        var response = await Provider(client).ExecuteUnifiedAsync(request);
        var item = Assert.Single(response.Output!.Items!);
        Assert.Equal("# Invoice", Assert.IsType<AITextContentPart>(Assert.Single(item.Content!)).Text);
        Assert.Equal("parse-1", item.Metadata!["byteit.job_id"]);
        Assert.Equal(2, Assert.IsAssignableFrom<JsonNode>(item.Metadata["byteit.job"])["page_count"]!.GetValue<int>());
        Assert.Equal(1, posts);
        Assert.Equal(data.StartsWith("https:") ? 1 : 0, downloads);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_output_runs_parse_then_extract_and_preserves_nested_schema(bool nativeSchema)
    {
        var phases = new List<string>();
        var schema = Parse("""{"type":"object","properties":{"company":{"type":"object","properties":{"names":{"type":"array","items":{"type":"string"}}}}}}""");
        using var client = Client(async (message, _) =>
        {
            AssertAuthentication(message);
            if (message.Method == HttpMethod.Post)
            {
                var path = message.RequestUri!.AbsolutePath;
                if (path == "/v1/jobs/parse-jobs/")
                {
                    phases.Add("parse");
                    var form = await ReadForm(message);
                    Assert.DoesNotContain("schema", form.Keys);
                    Assert.DoesNotContain("extraction_complexity", form.Keys);
                    return Json("""{"parse_job":{"job_id":"parse-1"}}""");
                }
                Assert.Equal("/v1/jobs/extract-jobs/", path);
                phases.Add("extract");
                var body = Parse(await message.Content!.ReadAsStringAsync());
                Assert.Equal("parse-1", body.GetProperty("parse_job_id").GetString());
                Assert.Equal(schema.GetRawText(), body.GetProperty("schema").GetRawText());
                Assert.Equal("high", body.GetProperty("extraction_complexity").GetString());
                Assert.False(body.TryGetProperty("processing_options", out var ignored));
                return Json("""{"extract_job":{"job_id":"extract-1"}}""");
            }
            return JobResponse(message, """{"company":{"names":["ByteIT"]}}""");
        });
        var options = new Dictionary<string, object?>
        {
            ["processing_options"] = new { languages = new[] { "de" } },
            ["extraction_complexity"] = "high"
        };
        if (nativeSchema)
            options["schema"] = schema;
        var request = Request(options: options, responseFormat: nativeSchema
            ? null : new { type = "json_schema", json_schema = new { name = "invoice", schema } });
        var response = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal(new[] { "parse", "extract" }, phases);
        var item = Assert.Single(response.Output!.Items!);
        Assert.Equal("extract-1", item.Metadata!["byteit.job_id"]);
        Assert.Equal("ByteIT", Assert.IsAssignableFrom<JsonNode>(item.Metadata["byteit.structured_output"])["company"]!["names"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Existing_parse_job_allows_extraction_without_uploading_again()
    {
        using var client = Client(async (message, _) =>
        {
            AssertAuthentication(message);
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/jobs/extract-jobs/", message.RequestUri!.AbsolutePath);
                var body = Parse(await message.Content!.ReadAsStringAsync());
                Assert.Equal("existing/id", body.GetProperty("parse_job_id").GetString());
                Assert.True(body.GetProperty("future_extract_option").GetBoolean());
                return Json("""{"extract_job":{"job_id":"extract-1"}}""");
            }
            return JobResponse(message, """{"total":10}""");
        });
        var request = Request("extract", noFiles: true, options: Parse("""
            {"parse_job_id":"existing/id","schema":{"type":"object"},"future_extract_option":true}
            """));
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Equal("""{"total":10}""", Assert.IsType<AITextContentPart>(Assert.Single(Assert.Single(result.Output!.Items!).Content!)).Text);
    }

    [Fact]
    public async Task S3_connector_metadata_is_forwarded_without_a_local_upload()
    {
        using var client = Client(async (message, _) =>
        {
            if (message.Method == HttpMethod.Post)
            {
                var form = await ReadForm(message);
                Assert.Equal("s3", form["input_connector"]);
                Assert.Equal("""{"source_bucket":"documents","source_path_inside_bucket":"invoice.pdf"}""", form["input_connection_data"]);
                Assert.DoesNotContain("file", form.Keys);
                return Json("""{"parse_job":{"job_id":"parse-1"}}""");
            }
            return JobResponse(message, "Invoice");
        });
        var request = Request(noFiles: true, options: Parse("""
            {"input_connector":"s3","input_connection_data":{"source_bucket":"documents","source_path_inside_bucket":"invoice.pdf"}}
            """));
        await Provider(client).ExecuteUnifiedAsync(request);
    }

    [Fact]
    public async Task Classification_passes_classes_and_returns_raw_result()
    {
        using var client = Client(async (message, _) =>
        {
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/jobs/classification-jobs/", message.RequestUri!.AbsolutePath);
                var form = await ReadForm(message);
                Assert.Equal("""[{"label":"invoice","description":"An invoice"}]""", form["classes"]);
                return Json("""{"classification_job":{"job_id":"classify-1"}}""");
            }
            return JobResponse(message, """{"document_class":"invoice","pages":[1,2]}""");
        });
        var request = Request("classification", options: Parse("""{"classes":[{"label":"invoice","description":"An invoice"}]}"""));
        var response = await Provider(client).ExecuteUnifiedAsync(request);
        Assert.Contains("\"document_class\":\"invoice\"", Assert.IsType<AITextContentPart>(
            Assert.Single(Assert.Single(response.Output!.Items!).Content!)).Text);
    }

    [Fact]
    public async Task Image_url_chat_input_uses_the_concrete_data_url_media_type()
    {
        using var client = Client((message, _) =>
        {
            if (message.Method == HttpMethod.Post)
            {
                var upload = Assert.Single(Assert.IsType<MultipartFormDataContent>(message.Content),
                    p => p.Headers.ContentDisposition!.Name?.Trim('"') == "file");
                Assert.Equal("image/png", upload.Headers.ContentType!.MediaType);
                Assert.Equal("document-1.png", upload.Headers.ContentDisposition!.FileName!.Trim('"'));
                return Task.FromResult(Json("""{"parse_job":{"job_id":"parse-1"}}"""));
            }
            return Task.FromResult(JobResponse(message, "Scanned image"));
        });
        var request = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"byteit/parse","messages":[{"role":"user","content":[
              {"type":"image_url","image_url":{"url":"data:image/png;base64,aW1hZ2U="}}
            ]}]}
            """)!;
        await Provider(client).CompleteChatAsync(request);
    }

    [Fact]
    public async Task Custom_groups_documents_and_supports_prompt_schema_and_extra_zip_files()
    {
        using var client = Client(async (message, _) =>
        {
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/jobs/custom-jobs/", message.RequestUri!.AbsolutePath);
                var form = Assert.IsType<MultipartFormDataContent>(message.Content).ToList();
                Assert.Equal(2, form.Count(p => p.Headers.ContentDisposition!.Name?.Trim('"') == "files"));
                var prompt = Assert.Single(form, p => p.Headers.ContentDisposition!.Name?.Trim('"') == "user_prompt");
                Assert.Equal("Extract every line item", await prompt.ReadAsStringAsync());
                Assert.DoesNotContain(form, p => p.Headers.ContentDisposition!.Name?.Trim('"') == "include_extras");
                return Json("""{"custom_job":{"job_id":"custom-1"}}""");
            }
            if (message.RequestUri!.AbsolutePath.EndsWith("/result/"))
            {
                Assert.Equal("?include_extras=true", message.RequestUri.Query);
                return Reply("ZIP", "application/zip");
            }
            return JobResponse(message);
        });
        var request = Request("custom");
        request.Input!.Items![0].Content!.Add(File(request));
        request.Input.Items[0].Content!.Add(new AITextContentPart { Type = "text", Text = "Extract every line item" });
        request.Metadata!["byteit"] = Parse("""{"schema":{"type":"object"},"include_extras":true}""");
        var events = new List<AIStreamEvent>();
        await foreach (var part in Provider(client).StreamUnifiedAsync(request))
            events.Add(part);
        Assert.Equal(new[] { "file", "finish" }, events.Select(e => e.Event.Type));
        Assert.Equal("byteit/custom", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).Model);
        Assert.Contains("data:application/zip;base64,WklQ", JsonSerializer.Serialize(events[0].Event.Data));
    }

    [Theory]
    [InlineData("md", "text/markdown")]
    [InlineData("txt", "text/plain")]
    [InlineData("html", "text/html")]
    [InlineData("json", "application/json")]
    [InlineData("zip", "application/zip")]
    public async Task All_parse_formats_work_with_octet_stream_downloads(string format, string mediaType)
    {
        using var client = Client((message, _) => Task.FromResult(message.Method == HttpMethod.Post
            ? Json("""{"parse_job":{"job_id":"parse-1"}}""")
            : JobResponse(message, format == "json" ? """{"pages":[]}""" : "result")));
        var request = Request(options: new { output_format = format });
        var result = await Provider(client).ExecuteUnifiedAsync(request);
        var content = Assert.Single(Assert.Single(result.Output!.Items!).Content!);
        if (format == "zip")
        {
            var file = Assert.IsType<AIFileContentPart>(content);
            Assert.Equal(mediaType, file.MediaType);
            Assert.Equal("data:application/zip;base64,cmVzdWx0", file.Data);
        }
        else
            Assert.IsType<AITextContentPart>(content);
    }

    [Fact]
    public async Task Multiple_files_use_only_the_latest_user_message_and_stream_text_in_order()
    {
        var uploads = 0;
        var polls = 0;
        using var client = Client((message, _) =>
        {
            if (message.Method == HttpMethod.Post)
            {
                uploads++;
                return Task.FromResult(Json("""{"parse_job":{"job_id":"parse-1"}}"""));
            }
            if (message.RequestUri!.AbsolutePath.EndsWith("/status/") && polls++ == 0)
                return Task.FromResult(Json("""{"processing_status":"queued","progress":0}"""));
            return Task.FromResult(JobResponse(message, "result"));
        });
        var request = Request();
        request.Input!.Items!.Insert(0, new AIInputItem { Role = "user", Content = [new AIFileContentPart { Type = "file", Data = "invalid" }] });
        request.Input.Items[^1].Content!.Add(new AIFileContentPart { Type = "file", Data = "data:image/png;base64,aW1hZ2U=" });
        var events = new List<AIStreamEvent>();
        await foreach (var part in Provider(client).StreamUnifiedAsync(request))
            events.Add(part);
        Assert.Equal(2, uploads);
        Assert.Equal(new[] { "text-start", "text-delta", "text-end", "text-start", "text-delta", "text-end", "finish" },
            events.Select(e => e.Event.Type));
    }

    [Theory]
    [InlineData("failed", "PDF_PASSWORD_PROTECTED")]
    [InlineData("unknown", "invalid status")]
    public async Task Job_failures_and_unrecognized_statuses_are_not_returned_as_success(string status, string error)
    {
        using var client = Client((message, _) => Task.FromResult(message.Method == HttpMethod.Post
            ? Json("""{"parse_job":{"job_id":"parse-1"}}""")
            : Json($$"""{"processing_status":"{{status}}","message":"PDF_PASSWORD_PROTECTED"}""")));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(Request()));
        Assert.Contains(error, exception.Message);
    }

    [Fact]
    public async Task Polling_cancellation_and_http_errors_propagate()
    {
        using var cancelled = new CancellationTokenSource();
        using var client = Client((message, _) =>
        {
            if (message.Method == HttpMethod.Post)
                return Task.FromResult(Json("""{"parse_job":{"job_id":"parse-1"}}"""));
            cancelled.Cancel();
            return Task.FromResult(Json("""{"processing_status":"processing"}"""));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ExecuteUnifiedAsync(Request(), cancelled.Token));

        using var failing = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"code":"RATE_LIMITED"}""")
        }));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(failing).ExecuteUnifiedAsync(Request()));
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("RATE_LIMITED", exception.Message);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("files")]
    [InlineData("schema")]
    [InlineData("base64")]
    [InlineData("batch")]
    [InlineData("empty")]
    [InlineData("oversize")]
    [InlineData("too-many")]
    [InlineData("metadata")]
    public async Task Invalid_input_is_rejected_before_provider_http(string kind)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected HTTP."));
        var request = kind switch
        {
            "model" => Request("nonexistent"),
            "files" => Request(noFiles: true),
            "schema" => Request("extract"),
            "base64" => Request(data: "data:application/pdf,not-base64"),
            "empty" => Request(data: ""),
            "oversize" => Request(data: new byte[50 * 1024 * 1024 + 1]),
            "batch" => Request(options: new { queue_for_batch = true }),
            "too-many" => Request("custom", fileCount: 26),
            "metadata" => Request(options: new[] { "invalid metadata" }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (kind == "batch")
            await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ExecuteUnifiedAsync(request));
        else
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(request));
    }

    [Fact]
    public async Task Missing_credentials_fail_explicitly()
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected HTTP."));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client, null).ExecuteUnifiedAsync(Request()));
        Assert.Contains("No ByteIT API key", exception.Message);
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("vercel", true)]
    public async Task Public_adapters_route_document_requests_to_the_native_parse_workflow(string adapter, bool streaming)
    {
        using var client = Client((message, _) =>
        {
            AssertAuthentication(message);
            return Task.FromResult(message.Method == HttpMethod.Post
                ? Json("""{"parse_job":{"job_id":"parse-1"}}""")
                : JobResponse(message, "Invoice text"));
        });
        var provider = Provider(client);
        const string input = """[{"role":"user","content":[{"type":"file","file":{"filename":"invoice.pdf","file_data":"data:application/pdf;base64,JVBERg=="}}]}]""";
        const string responsesInput = """[{"role":"user","content":[{"type":"input_file","filename":"invoice.pdf","file_data":"data:application/pdf;base64,JVBERg=="}]}]""";
        const string messagesInput = """[{"role":"user","content":[{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"JVBERg=="}}]}]""";
        const string vercelInput = """[{"id":"user-1","role":"user","parts":[{"type":"file","mediaType":"application/pdf","filename":"invoice.pdf","url":"data:application/pdf;base64,JVBERg=="}]}]""";
        switch (adapter)
        {
            case "chat":
                var chat = JsonSerializer.Deserialize<ChatCompletionOptions>($$"""{"model":"byteit/parse","messages":{{input}}}""")!;
                if (streaming) await Drain(provider.CompleteChatStreamingAsync(chat));
                else Assert.NotNull(await provider.CompleteChatAsync(chat));
                break;
            case "responses":
                var responses = JsonSerializer.Deserialize<ResponseRequest>($$"""{"model":"byteit/parse","input":{{responsesInput}}}""")!;
                if (streaming) await Drain(provider.ResponsesStreamingAsync(responses));
                else Assert.NotNull(await provider.ResponsesAsync(responses));
                break;
            case "messages":
                var messages = JsonSerializer.Deserialize<MessagesRequest>($$"""{"model":"byteit/parse","max_tokens":100,"messages":{{messagesInput}}}""")!;
                if (streaming) await Drain(provider.MessagesStreamingAsync(messages, []));
                else Assert.NotNull(await provider.MessagesAsync(messages, []));
                break;
            case "vercel":
                var vercel = JsonSerializer.Deserialize<ChatRequest>($$"""{"model":"byteit/parse","messages":{{vercelInput}}}""")!;
                await Drain(provider.StreamAsync(vercel));
                break;
        }
    }

    private static AIRequest Request(string model = "parse", object? options = null, object? responseFormat = null,
        object? data = null, string? mediaType = "application/pdf", string? filename = "invoice.pdf", bool noFiles = false, int fileCount = 1) => new()
    {
        ProviderId = "byteit",
        Model = $"byteit/{model}",
        Metadata = options is null ? [] : new() { ["byteit"] = options },
        ResponseFormat = responseFormat,
        Input = new AIInput
        {
            Items = [new AIInputItem
            {
                Role = "user", Content = Enumerable.Range(0, noFiles ? 0 : fileCount).Select(_ => (AIContentPart)new AIFileContentPart
                {
                    Type = "file", MediaType = mediaType, Filename = filename, Data = data ?? "JVBERg=="
                }).ToList()
            }]
        }
    };

    private static AIFileContentPart File(AIRequest request)
        => request.Input!.Items!.Last().Content!.OfType<AIFileContentPart>().First();

    private static void AssertAuthentication(HttpRequestMessage message)
    {
        Assert.Equal("byteit.ai", message.RequestUri!.Host);
        Assert.Equal("resolved-key", Assert.Single(message.Headers.GetValues("X-API-Key")));
        Assert.Null(message.Headers.Authorization);
    }

    private static async Task<Dictionary<string, string>> ReadForm(HttpRequestMessage message)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in Assert.IsType<MultipartFormDataContent>(message.Content))
            result[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync();
        return result;
    }

    private static HttpResponseMessage JobResponse(HttpRequestMessage message, string result = "{}")
    {
        var path = message.RequestUri!.AbsolutePath;
        if (path.EndsWith("/status/"))
            return Json("""{"processing_status":"completed","progress":100}""");
        if (path.EndsWith("/result/"))
            return Reply(result, "application/octet-stream");
        return Json("""{"job_id":"job","page_count":2,"credits_cost":3,"processing_status":"completed"}""");
    }

    private static HttpResponseMessage Reply(string body, string mediaType)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    private static HttpResponseMessage Json(string body) => Reply(body, "application/json");
    private static JsonElement Parse(string body) => JsonSerializer.Deserialize<JsonElement>(body);
    private static ByteITProvider Provider(HttpClient client, string? key = "resolved-key") => new(new Keys(key), new Factory(client));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static async Task Drain<T>(IAsyncEnumerable<T> stream)
    {
        var count = 0;
        await foreach (var _ in stream) count++;
        Assert.True(count > 0);
    }
    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
