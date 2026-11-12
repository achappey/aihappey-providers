using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepL;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class DeepLUnifiedTests
{
    public static IEnumerable<object[]> AdapterCases()
    {
        foreach (var api in new[] { "chat", "responses", "messages", "ui" })
            foreach (var streaming in new[] { false, true })
                if (api != "ui" || streaming)
                    foreach (var operation in new[] { "translate-to/DE", "rephrase", "correct" })
                        yield return [api, streaming, operation];
    }

    [Theory]
    [MemberData(nameof(AdapterCases))]
    public async Task All_adapters_use_the_same_text_routes_latest_user_and_raw_options(string api, bool streaming, string model)
    {
        var calls = 0;
        var options = model.StartsWith("translate")
            ? """{"source_lang":"EN","context":"context","glossary_ids":["g1","g2"],"custom_instructions":["Keep it short"],"future":{"nested":[true,null,1.25]}}"""
            : model == "rephrase" ? """{"target_lang":"en-US","tone":"friendly"}""" : """{"target_lang":"en-US"}""";
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            Assert.Equal(model.StartsWith("translate") ? "/v2/translate" : $"/v2/write/{model}", request.RequestUri!.AbsolutePath);
            var payload = Element(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("latest", Assert.Single(payload.GetProperty("text").EnumerateArray()).GetString());
            foreach (var property in Element(options).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)));
            Assert.False(payload.TryGetProperty("metadata", out _));
            Assert.False(payload.TryGetProperty("deepl", out _));
            Assert.False(payload.TryGetProperty("system", out _));
            return Json(model.StartsWith("translate") ? """{"translations":[{"text":"result","detected_source_language":"EN"}]}"""
                : """{"improvements":[{"text":"result","target_language":"en-US"}]}""");
        }));
        var result = await InvokeAdapter(Provider(client), api, streaming, "deepl/" + model, options);
        Assert.Contains("result", result);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("ui", true)]
    public async Task Documents_survive_every_adapter_as_inline_content(string api, bool streaming)
    {
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            if (path == "/v2/document")
            {
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                Assert.Equal("source.txt", form.Single(p => Field(p) == "file").Headers.ContentDisposition!.FileName!.Trim('"'));
                Assert.Equal("source", await form.Single(p => Field(p) == "file").ReadAsStringAsync(ct));
                Assert.Equal("DE", await form.Single(p => Field(p) == "target_lang").ReadAsStringAsync(ct));
                return Json("""{"document_id":"doc","document_key":"document-secret"}""");
            }
            Assert.Equal("document-secret", Element(await request.Content!.ReadAsStringAsync(ct)).GetProperty("document_key").GetString());
            return path.EndsWith("/result") ? Binary("translated", "text/plain") : Json("""{"document_id":"doc","status":"done","billed_characters":10}""");
        }));
        var result = await InvokeAdapter(Provider(client), api, streaming, "deepl/translate-to/DE", "{}", file: true);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("translated")), result);
        Assert.DoesNotContain("document-secret", result);
        Assert.Equal(new[] { "/v2/document", "/v2/document/doc", "/v2/document/doc/result" }, calls);
    }

    [Fact]
    public async Task Mixed_input_preserves_order_and_routes_all_supported_options_to_the_correct_request()
    {
        var calls = new List<string>();
        var polls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("DeepL-Auth-Key pro-key", request.Headers.Authorization!.ToString());
            Assert.Equal("team", Assert.Single(request.Headers.GetValues("X-DeepL-Reporting-Tag")));
            if (request.RequestUri.AbsolutePath == "/v2/translate")
            {
                var payload = Element(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal(new[] { "one", "two" }, payload.GetProperty("text").EnumerateArray().Select(t => t.GetString()));
                Assert.Equal("context", payload.GetProperty("context").GetString());
                Assert.False(payload.TryGetProperty("output_format", out _));
                Assert.False(payload.TryGetProperty("enable_watermark", out _));
                Assert.Equal(2, payload.GetProperty("glossary_ids").GetArrayLength());
                return Json("""{"translations":[{"text":"eins"},{"text":"zwei"}]}""");
            }
            if (request.RequestUri.AbsolutePath == "/v2/document")
            {
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                Assert.DoesNotContain(form, p => Field(p) == "context");
                Assert.Equal("true", await form.Single(p => Field(p) == "enable_watermark").ReadAsStringAsync(ct));
                Assert.Equal("docx", await form.Single(p => Field(p) == "output_format").ReadAsStringAsync(ct));
                Assert.Equal(2, form.Count(p => Field(p) == "glossary_ids"));
                Assert.Equal("[\"Keep it short\"]", await form.Single(p => Field(p) == "custom_instructions").ReadAsStringAsync(ct));
                Assert.Equal("{\"nested\":[true,null]}", await form.Single(p => Field(p) == "future").ReadAsStringAsync(ct));
                return Json("""{"document_id":"doc","document_key":"document-secret","quality_evaluation_job_id":"quality"}""");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/result")) return Binary("file result", "application/octet-stream");
            polls++;
            return Json(polls == 1 ? """{"status":"queued"}""" : polls == 2 ? """{"status":"translating","seconds_remaining":0}"""
                : """{"status":"done","billed_characters":42}""");
        }));
        var response = await Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE", [Text("one"), File(), Text("two")],
            """{"source_lang":"EN","context":"context","output_format":"docx","enable_watermark":true,"glossary_ids":["g1","g2"],"custom_instructions":["Keep it short"],"future":{"nested":[true,null]}}""",
            new() { ["Authorization"] = "wrong", ["X-DeepL-Reporting-Tag"] = "team" }));
        var parts = response.Output!.Items!.Single().Content!;
        Assert.Equal("eins", Assert.IsType<AITextContentPart>(parts[0]).Text);
        var file = Assert.IsType<AIFileContentPart>(parts[1]);
        Assert.Equal("source.docx", file.Filename);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", file.MediaType);
        Assert.Equal("zwei", Assert.IsType<AITextContentPart>(parts[2]).Text);
        var serialized = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("document-secret", serialized);
        Assert.Contains("quality", serialized);
        Assert.Equal(6, calls.Count);
    }

    [Theory]
    [InlineData("translate-to/DE", "{\"target_lang\":\"FR\"}", false)]
    [InlineData("correct", "{\"tone\":\"friendly\"}", false)]
    [InlineData("correct", "{\"writing_style\":\"business\"}", false)]
    [InlineData("translate-to/DE", "{\"file\":\"override\"}", false)]
    [InlineData("translate-to/DE", "{\"output_format\":\"docx\"}", false)]
    [InlineData("translate-to/DE", "{\"context\":\"text-only\"}", true)]
    [InlineData("translate-to/DE", "{\"output_format\":\"../docx\"}", true)]
    [InlineData("translate-to/", "{}", false)]
    [InlineData("other/correct", "{}", false)]
    public async Task Invalid_options_and_models_are_rejected_before_network_calls(string model, string options, bool file)
    {
        using var client = NoRequests();
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request(model, file ? [File()] : [Text("text")], options)));
    }

    [Theory]
    [InlineData("rephrase")]
    [InlineData("correct")]
    public async Task Writing_models_reject_files_instead_of_discarding_them(string model)
    {
        using var client = NoRequests();
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ExecuteUnifiedAsync(Request(model, [Text("text"), File()])));
    }

    [Fact]
    public async Task All_files_are_validated_before_any_billable_text_or_document_call()
    {
        using var client = NoRequests();
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE",
            [Text("text"), File(), new AIFileContentPart { Type = "file", Filename = "bad.txt", Data = "not base64!" }])));
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE",
            [new AIFileContentPart { Type = "file", Filename = "remote.txt", Data = "https://example.com/private.txt" }])));
    }

    [Theory]
    [InlineData("error", "Cannot translate document")]
    [InlineData("unknown", "unknown document translation status")]
    public async Task Terminal_or_invalid_document_states_do_not_download(string state, string message)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, ct) =>
        {
            calls++;
            return Task.FromResult(Json(calls == 1 ? """{"document_id":"doc","document_key":"document-secret"}"""
                : JsonSerializer.Serialize(new { status = state, error_message = "Cannot translate document document-secret pro-key" })));
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE", [File()])));
        Assert.Contains(message, error.Message);
        Assert.DoesNotContain("document-secret", error.Message);
        Assert.DoesNotContain("pro-key", error.Message);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(456)]
    public async Task Permanent_errors_are_not_retried_and_include_trace_and_code(int status)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, ct) =>
        {
            calls++;
            var response = Json("""{"error":{"message":"bad pro-key","code":"invalid_request"}}""", (HttpStatusCode)status);
            response.Headers.Add("X-Trace-ID", "trace-123");
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(Request("correct", [Text("text")])));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("invalid_request", error.Message);
        Assert.Contains("trace-123", error.Message);
        Assert.DoesNotContain("pro-key", error.Message);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Text_requests_retry_transient_statuses_with_fresh_content(int status)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Contains("text", await request.Content!.ReadAsStringAsync(ct));
            return ++calls == 1 ? Json("""{"message":"retry"}""", (HttpStatusCode)status) : Json("""{"improvements":[{"text":"fixed"}]}""");
        }));
        await Provider(client).ExecuteUnifiedAsync(Request("correct", [Text("text")]));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Retry_budget_is_bounded()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, ct) =>
        {
            calls++;
            return Task.FromResult(Json("""{"message":"busy"}""", HttpStatusCode.TooManyRequests));
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(Request("correct", [Text("text")])));
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_and_result_download_errors_are_not_blindly_replayed(bool download)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, ct) =>
        {
            calls++;
            return Task.FromResult(!download || calls == 3 ? Json("""{"message":"ambiguous"}""", HttpStatusCode.InternalServerError)
                : calls == 1 ? Json("""{"document_id":"doc","document_key":"document-secret"}""") : Json("""{"status":"done"}"""));
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE", [File()])));
        Assert.Equal(download ? 3 : 1, calls);
    }

    [Fact]
    public async Task Explicitly_throttled_document_upload_can_be_retried_without_replaying_a_job()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, ct) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? Json("""{"message":"throttled"}""", HttpStatusCode.TooManyRequests)
                : calls == 2 ? Json("""{"document_id":"doc","document_key":"secret"}""")
                : calls == 3 ? Json("""{"status":"done"}""") : Binary("translated", "text/plain"));
        }));
        await Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE", [File()]));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Cancellation_interrupts_polling_and_does_not_download()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler((request, ct) =>
        {
            calls++;
            if (calls == 2) cancellation.Cancel();
            return Task.FromResult(Json(calls == 1 ? """{"document_id":"doc","document_key":"secret"}""" : """{"status":"queued"}"""));
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE", [File()]), cancellation.Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Missing_text_results_are_errors_not_silent_empty_translations()
    {
        using var client = new HttpClient(new Handler((request, ct) => Task.FromResult(Json("""{"translations":[]}"""))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE", [Text("text")])));
    }

    [Fact]
    public async Task Document_deadline_is_bounded_and_distinct_from_caller_cancellation()
    {
        var clock = new DeadlineClock();
        var calls = 0;
        using var client = new HttpClient(new Handler((request, ct) =>
        {
            calls++;
            if (calls == 2) clock.Expire();
            return Task.FromResult(Json(calls == 1 ? """{"document_id":"doc","document_key":"secret"}""" : """{"status":"queued"}"""));
        }));
        var provider = new DeepLProvider(new KeyResolver(), new AsyncCacheHelper(), new Factory(client), clock);
        await Assert.ThrowsAsync<TimeoutException>(() => provider.ExecuteUnifiedAsync(Request("translate-to/DE", [File()])));
        Assert.Equal(TimeSpan.FromMinutes(10), clock.DueTime);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("source.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("source.pdf", "application/pdf")]
    [InlineData("source.md", "text/markdown")]
    [InlineData("source.yaml", "application/yaml")]
    public async Task Binary_inputs_keep_the_original_filename_and_infer_result_media_type(string filename, string mediaType)
    {
        var original = new byte[] { 0, 1, 2, 127, 255 };
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v2/document")
            {
                var part = Assert.IsType<MultipartFormDataContent>(request.Content).Single(p => Field(p) == "file");
                Assert.Equal(filename, part.Headers.ContentDisposition!.FileName!.Trim('"'));
                Assert.Equal(mediaType, part.Headers.ContentType!.MediaType);
                Assert.Equal(original, await part.ReadAsByteArrayAsync(ct));
                return Json("""{"document_id":"doc","document_key":"secret"}""");
            }
            return path.EndsWith("/result") ? Binary("result", "application/octet-stream") : Json("""{"status":"done"}""");
        }));
        var response = await Provider(client).ExecuteUnifiedAsync(Request("translate-to/DE",
            [new AIFileContentPart { Type = "file", Filename = filename, Data = original }]));
        var file = Assert.IsType<AIFileContentPart>(Assert.Single(response.Output!.Items!.Single().Content!));
        Assert.Equal(filename, file.Filename);
        Assert.Equal(mediaType, file.MediaType);
        Assert.StartsWith($"data:{mediaType};base64,", file.Data!.ToString());
    }

    [Fact]
    public async Task Direct_text_works_but_an_empty_latest_user_does_not_retranslate_history()
    {
        using var client = new HttpClient(new Handler((request, ct) => Task.FromResult(Json("""{"improvements":[{"text":"fixed"}]}"""))));
        var provider = Provider(client);
        var response = await provider.ExecuteUnifiedAsync(new AIRequest { ProviderId = "deepl", Model = "correct", Input = new AIInput { Text = "direct" } });
        Assert.Equal("fixed", Assert.IsType<AITextContentPart>(Assert.Single(response.Output!.Items!.Single().Content!)).Text);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "deepl", Model = "correct", Input = new AIInput { Items =
                [new AIInputItem { Role = "user", Content = [Text("history")] }, new AIInputItem { Role = "user", Content = [] }] }
        }));
    }

    [Fact]
    public async Task Discovery_respects_resource_targets_deduplicates_and_caches_v3_results()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((request, ct) =>
        {
            calls++;
            var resource = request.RequestUri!.Query;
            return Task.FromResult(Json(resource.EndsWith("translate_text")
                ? """[{"lang":"de","name":"German","usable_as_target":true,"status":"stable","features":{"formality":{"status":"stable"}}},{"lang":"en","name":"English","usable_as_target":false,"features":{}}]"""
                : resource.EndsWith("translate_document")
                    ? """[{"lang":"de","name":"German","usable_as_target":true,"features":{}},{"lang":"fr","name":"French","usable_as_target":true,"features":{}}]"""
                    : resource.EndsWith("voice")
                        ? """[{"lang":"de","name":"German","usable_as_target":true,"features":{}},{"lang":"en-US","name":"American English","usable_as_target":true,"features":{}},{"lang":"de","name":"German","usable_as_target":true,"features":{}},{"lang":"en","name":"English","usable_as_target":false,"features":{}}]"""
                        : """[{"lang":"en-US","name":"American English","usable_as_target":true,"features":{}}]"""));
        }));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new DeepLProvider(new KeyResolver(), new AsyncCacheHelper(cache), new Factory(client));
        var models = (await provider.ListModels(default)).ToList();
        await provider.ListModels(default);
        Assert.Equal(4, calls);
        Assert.Equal(7, models.Count);
        Assert.Contains(models, m => m.Id == "deepl/voice-realtime" && m.Type == "transcription" && m.Tags!.Contains("real-time"));
        Assert.Contains(models, m => m.Id == "deepl/voice-realtime/translate-to/en-US" && m.Type == "transcription");
        Assert.DoesNotContain(models, m => m.Id == "deepl/voice-realtime/translate-to/en");
        var german = Assert.Single(models, m => m.Id == "deepl/translate-to/DE");
        Assert.Contains("translate_document", german.Tags!);
        Assert.Contains("deepl:translate_text:de:formality:stable", german.Tags!);
        Assert.Contains(models, m => m.Id == "deepl/translate-to/FR");
        Assert.DoesNotContain(models, m => m.Id == "deepl/translate-to/EN");
        Assert.Contains(models, m => m.Id == "deepl/correct" && m.Description!.Contains("en-US"));
    }

    private static async Task<string> InvokeAdapter(DeepLProvider provider, string api, bool streaming, string model, string options, bool file = false)
    {
        var textPart = """{"type":"text","text":"latest"}""";
        var filePart = """{"type":"file","file":{"filename":"source.txt","file_data":"data:text/plain;base64,c291cmNl"}}""";
        var latest = file ? filePart : textPart;
        var root = Element(options).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        root["model"] = model;
        object Messages(string part) => new[]
        {
            new { role = "user", content = (object)"old" },
            new { role = "assistant", content = (object)"history" },
            new { role = "user", content = (object)new[] { Element(part) } }
        };
        if (api == "chat")
        {
            root["messages"] = Messages(latest);
            var request = JsonSerializer.Deserialize<ChatCompletionOptions>(JsonSerializer.Serialize(root), JsonSerializerOptions.Web)!;
            return streaming ? await SerializeStream(provider.CompleteChatStreamingAsync(request)) : JsonSerializer.Serialize(await provider.CompleteChatAsync(request));
        }
        if (api == "responses")
        {
            latest = file ? """{"type":"input_file","filename":"source.txt","file_data":"data:text/plain;base64,c291cmNl"}""" : """{"type":"input_text","text":"latest"}""";
            root["input"] = Messages(latest);
            var request = JsonSerializer.Deserialize<ResponseRequest>(JsonSerializer.Serialize(root), JsonSerializerOptions.Web)!;
            return streaming ? await SerializeStream(provider.ResponsesStreamingAsync(request)) : JsonSerializer.Serialize(await provider.ResponsesAsync(request));
        }
        if (api == "messages")
        {
            latest = file ? """{"type":"document","title":"source.txt","source":{"type":"base64","media_type":"text/plain","data":"c291cmNl"}}""" : textPart;
            root["max_tokens"] = 1024;
            root["system"] = "not a translation option";
            root["messages"] = Messages(latest);
            var request = JsonSerializer.Deserialize<MessagesRequest>(JsonSerializer.Serialize(root), JsonSerializerOptions.Web)!;
            return streaming ? await SerializeStream(provider.MessagesStreamingAsync(request, [])) : JsonSerializer.Serialize(await provider.MessagesAsync(request, []));
        }
        latest = file ? """{"type":"file","filename":"source.txt","mediaType":"text/plain","url":"data:text/plain;base64,c291cmNl"}""" : textPart;
        var chat = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(new
        {
            model,
            messages = new[]
            {
                new { id = "old", role = "user", parts = new[] { Element("""{"type":"text","text":"old"}""") } },
                new { id = "new", role = "user", parts = new[] { Element(latest) } }
            },
            providerMetadata = new { deepl = Element(options) }
        }), JsonSerializerOptions.Web)!;
        return await SerializeStream(provider.StreamAsync(chat));
    }

    private static async Task<string> SerializeStream<T>(IAsyncEnumerable<T> stream)
    {
        var parts = new List<object?>();
        await foreach (var part in stream) parts.Add(part);
        return JsonSerializer.Serialize(parts, JsonSerializerOptions.Web);
    }

    private static AIRequest Request(string model, List<AIContentPart> parts, string options = "{}", Dictionary<string, string>? headers = null)
        => new() { ProviderId = "deepl", Model = model, Input = new AIInput { Items = [new AIInputItem { Role = "user", Content = parts }] }, Metadata = new() { ["deepl"] = Element(options) }, Headers = headers };
    private static AITextContentPart Text(string text) => new() { Type = "text", Text = text };
    private static AIFileContentPart File() => new() { Type = "file", Filename = "source.txt", MediaType = "text/plain", Data = "data:text/plain;base64,c291cmNl" };
    private static string? Field(HttpContent content) => content.Headers.ContentDisposition?.Name?.Trim('"');
    private static DeepLProvider Provider(HttpClient client) => new(new KeyResolver(), new AsyncCacheHelper(), new Factory(client));
    private static HttpClient NoRequests() => new(new Handler((request, ct) => throw new InvalidOperationException("Unexpected network request.")));
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Binary(string content, string type)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)) };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return result;
    }
    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "pro-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private Action? _expire;
        public TimeSpan DueTime { get; private set; }
        public void Expire() => _expire!();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            _expire = () => callback(state);
            return new DeadlineTimer();
        }
        private sealed class DeadlineTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
