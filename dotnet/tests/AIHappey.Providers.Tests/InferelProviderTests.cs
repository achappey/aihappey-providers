using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Inferel;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Providers.Tests;

public sealed class InferelProviderTests
{
    private const string Native = """
        {"temperature":null,"prompt_cache_key":"prefix","future_number":9007199254740993,
         "future_null":null,"future_empty":{},"future_array":[],"future_nested":{"values":[false,null,{}]}}
        """;
    private const string ChatReply = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"maker/model","choices":[
          {"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3},"native_future":{"ok":true}}
        """;
    private const string Models = """
        {"data":[{"id":"maker/model","name":"Model","owned_by":"maker","pricing":{
          "prompt":"0.0000021","completion":"0.0000105","input_cache_read":"0.0000003",
          "input_cache_write":0.00000375,"currency":"USD","unit":"per_token",
          "list":{"prompt":"0.000003","completion":"0.000015"}}}]}
        """;
    private const string ImageSchema = """
        {"model_type":"image","parameters":{"properties":{"text":{},"width":{},"height":{},
          "n":{},"seed":{},"aspect_ratio":{},"image_urls":{},"mask_url":{}}}}
        """;
    private const string VideoSchema = """
        {"model_type":"video","parameters":[{"name":"prompt"},{"name":"duration"},{"name":"resolution"},
          {"name":"aspect_ratio"},{"name":"fps"},{"name":"seed"},{"name":"generate_audio"},
          {"name":"first_frame_url"},{"name":"last_frame_url"},{"name":"image_urls"}]}
        """;
    private const string ImageSuccess = """
        {"request_id":"job-image","status":"success","outcome":{"media_urls":[
          {"id":"0","type":"image","url":"https://storage.example/image.png"}]},"price":35000}
        """;
    private const string VideoSuccess = """
        {"request_id":"job-video","status":"success","outcome":{"media_urls":[
          {"id":"0","type":"video","url":"https://storage.example/video.mp4"}]}}
        """;

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys(() => "inferel-test"));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("inferel"));
        Assert.IsType<InferelProvider>(registry.GetModelProvider(scope.ServiceProvider, "inferel"));
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Native_endpoints_preserve_raw_options_and_control_model_stream_and_auth(string endpoint, bool stream)
    {
        var native = Element(Native);
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("inference.inferel.ai", request.RequestUri!.Host);
            Assert.Equal(endpoint switch
            {
                "chat" => "/v1/chat/completions", "responses" => "/v1/responses", _ => "/v1/messages"
            }, request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer inferel-test", request.Headers.Authorization?.ToString());
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal("test-group", Assert.Single(request.Headers.GetValues("x-resource-group")));
            var body = await Body(request, ct);
            Assert.Equal("maker/model:pool", body.GetProperty("model").GetString());
            Assert.Equal(stream, body.GetProperty("stream").GetBoolean());
            Assert.False(body.TryGetProperty("inferel", out _));
            Assert.Equal("application", body.GetProperty("metadata").GetProperty("project").GetString());
            foreach (var property in native.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            if (stream && endpoint == "chat")
                Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            return Reply(stream ? "data: [DONE]\n\n" : endpoint switch
            {
                "chat" => ChatReply,
                "responses" => """{"id":"resp","model":"maker/model","status":"completed","output":[]}""",
                _ => """{"id":"msg","type":"message","role":"assistant","model":"maker/model","content":[],"usage":{"input_tokens":2,"output_tokens":1}}"""
            }, stream ? "text/event-stream" : "application/json");
        });
        var provider = Provider(client);
        var input = $$"""
            {"model":"inferel/maker/model:pool","input":"Hello","max_tokens":100,
             "messages":[{"role":"user","content":"Hello"}],"metadata":{"project":"application"},
             "stream":{{(!stream).ToString().ToLowerInvariant()}},"inferel":{{Native}}}
            """;
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer wrong", ["x-api-key"] = "wrong", ["x-resource-group"] = "test-group"
        };
        switch (endpoint)
        {
            case "chat":
                var chat = JsonSerializer.Deserialize<ChatCompletionOptions>(input)!;
                chat.Headers = headers;
                var before = JsonSerializer.Serialize(chat);
                if (stream) await Drain(provider.CompleteChatStreamingAsync(chat));
                else Assert.Equal("inferel/maker/model", (await provider.CompleteChatAsync(chat)).Model);
                Assert.Equal(before, JsonSerializer.Serialize(chat));
                break;
            case "responses":
                var responses = JsonSerializer.Deserialize<ResponseRequest>(input)!;
                responses.Headers = headers;
                before = JsonSerializer.Serialize(responses);
                if (stream) await Drain(provider.ResponsesStreamingAsync(responses));
                else Assert.Equal("inferel/maker/model", (await provider.ResponsesAsync(responses)).Model);
                Assert.Equal(before, JsonSerializer.Serialize(responses));
                break;
            default:
                var messages = JsonSerializer.Deserialize<MessagesRequest>(input)!;
                before = JsonSerializer.Serialize(messages);
                if (stream) await Drain(provider.MessagesStreamingAsync(messages, headers));
                else Assert.Equal("inferel/maker/model", (await provider.MessagesAsync(messages, headers)).Model);
                Assert.Equal(before, JsonSerializer.Serialize(messages));
                break;
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Discovery_merges_both_catalogs_and_keeps_discounted_per_token_prices_and_key_scoped_cache()
    {
        var key = "inferel-first";
        var calls = 0;
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var client = Client((request, _) =>
        {
            calls++;
            Assert.Equal($"Bearer {key}", request.Headers.Authorization?.ToString());
            return Task.FromResult(Catalog(request.RequestUri!.AbsolutePath));
        });
        var provider = new InferelProvider(new Keys(() => key), new AsyncCacheHelper(memory), new Factory(client));
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["inferel/maker/model", "inferel/qwen-image-2.0", "inferel/wan2.7-t2v"], models.Select(m => m.Id));
        Assert.Equal(["language", "image", "video"], models.Select(m => m.Type));
        Assert.Equal(0.0000021m, models[0].Pricing!.Input);
        Assert.Equal(0.0000105m, models[0].Pricing!.Output);
        Assert.Equal(0.0000003m, models[0].Pricing!.InputCacheRead);
        Assert.Equal(0.00000375m, models[0].Pricing!.InputCacheWrite);
        await provider.ListModels();
        Assert.Equal(4, calls);
        key = "inferel-second";
        await provider.ListModels();
        Assert.Equal(8, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_streams_deserialize_protocol_events_and_prefix_models(string endpoint)
    {
        using var client = Client((request, _) =>
        {
            if (endpoint == "messages")
                Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            return Task.FromResult(Reply(endpoint == "responses" ? """
                event: response.created
                data: {"type":"response.created","response":{"id":"resp","model":"maker/model","status":"in_progress","output":[]}}

                event: response.output_text.delta
                data: {"type":"response.output_text.delta","item_id":"item","output_index":0,"content_index":0,"delta":"Hello"}

                event: response.completed
                data: {"type":"response.completed","response":{"id":"resp","model":"maker/model","status":"completed","output":[]}}

                """ : """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg","type":"message","role":"assistant","model":"maker/model","content":[],"usage":{"input_tokens":2,"output_tokens":0}}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

                event: message_stop
                data: {"type":"message_stop"}

                """, "text/event-stream"));
        });
        var provider = Provider(client);
        var input = """{"model":"maker/model","input":"Hello","messages":[],"max_tokens":20}""";
        if (endpoint == "responses")
        {
            var parts = await Collect(provider.ResponsesStreamingAsync(JsonSerializer.Deserialize<ResponseRequest>(input)!));
            Assert.Equal(3, parts.Count);
            Assert.Equal("inferel/maker/model", Assert.IsType<AIHappey.Responses.Streaming.ResponseCreated>(parts[0]).Response.Model);
            Assert.Equal("inferel/maker/model", Assert.IsType<AIHappey.Responses.Streaming.ResponseCompleted>(parts[2]).Response.Model);
        }
        else
        {
            var parts = await Collect(provider.MessagesStreamingAsync(JsonSerializer.Deserialize<MessagesRequest>(input)!, []));
            Assert.Equal(3, parts.Count);
            Assert.Equal("inferel/maker/model", parts[0].Message!.Model);
            Assert.Equal("message_stop", parts[2].Type);
        }
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Native_stream_errors_are_not_successful_empty_streams(string endpoint)
    {
        using var client = Client((_, _) => Task.FromResult(Reply(
            "data: {\"error\":{\"message\":\"native failure\"}}\n\n", "text/event-stream")));
        var provider = Provider(client);
        var input = """{"model":"maker/model","input":"Hello","messages":[],"max_tokens":20}""";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (endpoint == "chat") await Drain(provider.CompleteChatStreamingAsync(JsonSerializer.Deserialize<ChatCompletionOptions>(input)!));
            else if (endpoint == "responses") await Drain(provider.ResponsesStreamingAsync(JsonSerializer.Deserialize<ResponseRequest>(input)!));
            else await Drain(provider.MessagesStreamingAsync(JsonSerializer.Deserialize<MessagesRequest>(input)!, []));
        });
        Assert.Contains("native failure", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_text_keeps_exact_provider_metadata_including_typed_nulls(bool streaming)
    {
        using var client = Client(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Catalog(request.RequestUri!.AbsolutePath);
            var body = await Body(request, ct);
            Assert.Equal("maker/model:pool", body.GetProperty("model").GetString());
            foreach (var property in Element(Native).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            Assert.False(body.TryGetProperty("other", out _));
            return Reply(streaming ? """
                data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"maker/model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}

                data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"maker/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"maker/model","choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

                data: [DONE]

                """ : ChatReply, streaming ? "text/event-stream" : "application/json");
        });
        var provider = Provider(client);
        var request = new AIRequest
        {
            ProviderId = "inferel", Model = "inferel/maker/model:pool", Input = new AIInput { Text = "Hello" },
            Temperature = 1, Metadata = new() { ["inferel"] = Element(Native), ["other"] = new { secret = "not-sent" } }
        };
        var before = JsonSerializer.Serialize(request);
        if (streaming)
        {
            var events = await Collect(provider.StreamUnifiedAsync(request));
            Assert.Contains(events, e => e.Event.Type == "text-delta");
            Assert.Contains(events, e => e.Event.Type == "finish");
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal("Hello", Assert.Single(response.Output!.Items![0].Content!.OfType<AITextContentPart>()).Text);
        }
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task UI_stream_keeps_provider_metadata_and_emits_text()
    {
        using var client = Client(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Catalog(request.RequestUri!.AbsolutePath);
            var body = await Body(request, ct);
            foreach (var property in Element(Native).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
            return Reply("""
                data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"maker/model","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}

                data: {"id":"chat","object":"chat.completion.chunk","created":1,"model":"maker/model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """, "text/event-stream");
        });
        var request = new ChatRequest
        {
            Model = "inferel/maker/model",
            Messages = [new UIMessage { Id = "user", Role = Role.user, Parts = [new TextUIPart { Text = "Hello" }] }],
            ProviderMetadata = new() { ["inferel"] = Element(Native) }
        };
        var before = JsonSerializer.Serialize(request);
        var parts = await Collect(Provider(client).StreamAsync(request));
        Assert.Equal("Hello", Assert.Single(parts.OfType<TextDeltaUIMessageStreamPart>()).Delta);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_image_models_route_to_the_queue_not_chat_completions(bool streaming)
    {
        using var client = Client(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Catalog(request.RequestUri!.AbsolutePath);
            Assert.Equal("console.inferel.ai", request.RequestUri!.Host);
            var payload = (await Body(request, ct)).GetProperty("payload");
            foreach (var property in Element(Native).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
            return Reply(ImageSuccess);
        });
        using var media = Media();
        var request = new AIRequest
        {
            ProviderId = "inferel", Model = "inferel/qwen-image-2.0",
            Input = new AIInput { Items = [new AIInputItem
            {
                Role = "user", Content = [new AITextContentPart { Type = "text", Text = "Hello" }]
            }] },
            Metadata = new() { ["inferel"] = Element(Native) }
        };
        var provider = Provider(client, media);
        if (streaming)
        {
            var events = await Collect(provider.StreamUnifiedAsync(request));
            Assert.Contains(events, e => e.Event.Type == "tool-output-available");
            Assert.DoesNotContain(events, e => e.Event.Type == "tool-output-error");
        }
        else
        {
            var result = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal("completed", result.Status);
            Assert.NotEmpty(result.Output!.Items!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Images_use_queue_handle_immediate_or_polled_success_and_keep_raw_payload(bool queued)
    {
        var polls = 0;
        using var client = Client(async (request, ct) =>
        {
            Assert.Equal("Bearer inferel-test", request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.AbsolutePath.Contains("/requests/"))
                {
                    polls++;
                    return Reply(ImageSuccess);
                }
                return Catalog(request.RequestUri!.AbsolutePath);
            }
            Assert.Equal("https://console.inferel.ai/api/v1/queue/apikey/requests", request.RequestUri!.ToString());
            var body = await Body(request, ct);
            Assert.Equal("qwen-image-2.0", body.GetProperty("model").GetString());
            var payload = body.GetProperty("payload");
            Assert.Equal("An apple", payload.GetProperty("text").GetString());
            Assert.Equal(768, payload.GetProperty("width").GetInt32());
            Assert.Equal(512, payload.GetProperty("height").GetInt32());
            Assert.Equal(2, payload.GetProperty("n").GetInt32());
            Assert.Equal(42, payload.GetProperty("seed").GetInt32());
            foreach (var property in Element(Native).EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
            return Reply(queued ? """{"request_id":"job-image","status":"queued"}""" : ImageSuccess);
        });
        using var media = Media();
        var request = new ImageRequest
        {
            Model = "inferel/qwen-image-2.0", Prompt = "An apple", Size = "768x512", Seed = 42, N = 2,
            ProviderOptions = new() { ["inferel"] = Element(Native), ["other"] = Element("""{"secret":"not-sent"}""") }
        };
        var before = JsonSerializer.Serialize(request);
        var result = await Provider(client, media).ImageRequest(request);
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(result.Images!));
        Assert.True(JsonElement.DeepEquals(Element(ImageSuccess), result.ProviderMetadata!["inferel"]));
        Assert.Equal("inferel/qwen-image-2.0", result.Response.ModelId);
        Assert.Equal(queued ? 1 : 0, polls);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Image_edits_upload_binary_without_credentials_and_use_schema_fields()
    {
        var uploadCalls = 0;
        using var client = Client(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Catalog(request.RequestUri!.AbsolutePath);
            var body = await Body(request, ct);
            if (request.RequestUri!.AbsolutePath.EndsWith("upload-url"))
            {
                Assert.Equal("jpg", body.GetProperty("file_type").GetString());
                return Reply("""{"upload_url":"https://storage.example/signed","public_url":"https://storage.example/input.jpg"}""");
            }
            Assert.Equal("https://storage.example/input.jpg", body.GetProperty("payload").GetProperty("image_urls")[0].GetString());
            Assert.Equal("https://input.example/mask.png", body.GetProperty("payload").GetProperty("mask_url").GetString());
            return Reply(ImageSuccess);
        });
        using var media = Client(async (request, ct) =>
        {
            Assert.Null(request.Headers.Authorization);
            if (request.Method == HttpMethod.Put)
            {
                uploadCalls++;
                Assert.Equal("image/jpg", request.Content!.Headers.ContentType!.MediaType);
                Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content.ReadAsByteArrayAsync(ct));
                return Reply("");
            }
            return ReplyBytes("image/png");
        });
        await Provider(client, media).ImageRequest(new ImageRequest
        {
            Model = "qwen-image-2.0", Prompt = "Edit",
            Files = [new ImageFile { Data = "data:image/jpg;base64,AQID" }],
            Mask = new ImageFileUrl { Url = "https://input.example/mask.png" }
        });
        Assert.Equal(1, uploadCalls);
    }

    [Theory]
    [InlineData("queued", "pending")]
    [InlineData("running", "pending")]
    [InlineData("failed", "error")]
    [InlineData("cancelled", "error")]
    [InlineData("success", "completed")]
    public async Task Videos_map_native_parameters_and_return_operation_states(string status, string expected)
    {
        using var client = Client(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = await Body(request, ct);
                Assert.Equal("wan2.7-t2v", body.GetProperty("model").GetString());
                var payload = body.GetProperty("payload");
                Assert.Equal("A surfer", payload.GetProperty("prompt").GetString());
                Assert.Equal(5d, payload.GetProperty("duration").GetDouble());
                Assert.Equal("720p", payload.GetProperty("resolution").GetString());
                Assert.Equal("https://input.example/first.png", payload.GetProperty("first_frame_url").GetString());
                Assert.False(payload.GetProperty("generate_audio").GetBoolean());
                Assert.True(JsonElement.DeepEquals(Element(Native).GetProperty("future_nested"), payload.GetProperty("future_nested")));
                return Reply("""{"request_id":"job-video","status":"queued"}""");
            }
            if (request.RequestUri!.AbsolutePath.Contains("/requests/"))
            {
                Assert.EndsWith("/requests/job-video", request.RequestUri.AbsolutePath);
                return Reply(status == "success" ? VideoSuccess : $$$"""{"status":"{{{status}}}","error":{"message":"native detail"}}""");
            }
            return Catalog(request.RequestUri.AbsolutePath);
        });
        using var media = Media();
        var provider = Provider(client, media);
        var operation = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "inferel/wan2.7-t2v", Prompt = "A surfer", Duration = 5, Resolution = "720p",
            GenerateAudio = false, ProviderOptions = new() { ["inferel"] = Element(Native) },
            FrameImages = [new VideoFrameImage
            {
                FrameType = "first_frame", Image = new VideoFileUrl { Url = "https://input.example/first.png" }
            }]
        });
        var result = await provider.GetVideoOperationStatus(operation.Operation);
        Assert.Equal(expected, result.Status);
        Assert.Equal(status, result.ProviderMetadata!["inferel"].GetProperty("status").GetString());
        Assert.Equal("inferel/wan2.7-t2v", result.Response.ModelId);
        if (result is VideoOperationCompletedResult completed)
            Assert.Equal("AQID", Assert.Single(completed.Videos).Data);
        if (result is VideoOperationErrorResult error) Assert.Contains("native detail", error.Error);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Failed_images_do_not_return_success(string status)
    {
        using var client = Client((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? Reply($$"""{"status":"{{status}}","error":"native failure"}""") : Catalog(request.RequestUri!.AbsolutePath)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).ImageRequest(
            new ImageRequest { Model = "qwen-image-2.0", Prompt = "Hello" }));
        Assert.Contains("native failure", error.Message);
    }

    [Fact]
    public async Task OpenAI_image_adapters_return_images_and_completion_events()
    {
        using var client = Client(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get) return Catalog(request.RequestUri!.AbsolutePath);
            var payload = (await Body(request, ct)).GetProperty("payload");
            Assert.False(payload.TryGetProperty("stream", out _));
            Assert.False(payload.TryGetProperty("response_format", out _));
            Assert.False(payload.TryGetProperty("partial_images", out _));
            return Reply(ImageSuccess);
        });
        using var media = Media();
        var provider = Provider(client, media);
        var generation = new OpenAIImageGenerationRequest
        {
            Model = "qwen-image-2.0", Prompt = "Hello", Stream = true, ResponseFormat = "b64_json", PartialImages = 0
        };
        Assert.NotEmpty((await provider.OpenAIImageGenerationRequestAsync(generation)).Data!);
        Assert.Single(await Collect(provider.OpenAIImageGenerationStreamingAsync(generation)));
        var edit = new OpenAIImageEditRequest
        {
            Model = "qwen-image-2.0", Prompt = "Edit",
            Images = [new OpenAIImageReference { ImageUrl = "https://input.example/image.png" }]
        };
        Assert.NotEmpty((await provider.OpenAIImageEditRequestAsync(edit)).Data!);
        Assert.Single(await Collect(provider.OpenAIImageEditStreamingAsync(edit)));
    }

    [Fact]
    public async Task Errors_missing_credentials_and_cancellation_are_explicit()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("""{"error":{"message":"insufficient credits"}}""")
        }));
        var provider = Provider(client);
        var chat = JsonSerializer.Deserialize<ChatCompletionOptions>("""{"model":"maker/model","messages":[]}""")!;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(chat));
        Assert.Equal(HttpStatusCode.PaymentRequired, error.StatusCode);
        Assert.Contains("insufficient credits", error.Message);
        using var noRequests = Client((_, _) => throw new InvalidOperationException("No HTTP call expected."));
        var missing = new InferelProvider(new Keys(() => null), new AsyncCacheHelper(), new Factory(noRequests));
        Assert.Empty(await missing.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.CompleteChatAsync(chat));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(noRequests).ListModels(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(noRequests).CompleteChatAsync(chat, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetVideoOperationStatus("invalid"));
    }

    [Theory]
    [InlineData("""{"data":[{}]}""")]
    [InlineData("""{"data":[{"id":"model","pricing":{"prompt":"bad"}}]}""")]
    public async Task Malformed_catalogs_fail_explicitly(string catalog)
    {
        using var client = Client((_, _) => Task.FromResult(Reply(catalog)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    private static HttpResponseMessage Catalog(string path) => Reply(path switch
    {
        "/v1/models" => Models,
        "/api/v1/queue/apikey/models" => """{"model_ids":["qwen-image-2.0","wan2.7-t2v"]}""",
        "/api/v1/queue/apikey/models/qwen-image-2.0" => ImageSchema,
        "/api/v1/queue/apikey/models/wan2.7-t2v" => VideoSchema,
        _ => throw new InvalidOperationException($"Unexpected path: {path}")
    });
    private static HttpClient Media() => Client((request, _) =>
    {
        Assert.Equal("storage.example", request.RequestUri!.Host);
        Assert.Null(request.Headers.Authorization);
        return Task.FromResult(ReplyBytes(request.RequestUri.AbsolutePath.EndsWith(".mp4") ? "video/mp4" : "image/png"));
    });
    private static HttpResponseMessage ReplyBytes(string type) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new(type) } }
    };
    private static InferelProvider Provider(HttpClient client, HttpClient? media = null)
        => new(new Keys(() => "inferel-test"), new AsyncCacheHelper(), new Factory(client, media));
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static async Task<JsonElement> Body(HttpRequestMessage request, CancellationToken ct)
        => Element(await request.Content!.ReadAsStringAsync(ct));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new Handler(send));
    private static HttpResponseMessage Reply(string body, string type = "application/json")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };
    private static async Task Drain<T>(IAsyncEnumerable<T> stream) { await foreach (var _ in stream) { } }
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        var result = new List<T>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }
    private sealed class Keys(Func<string?> key) : IApiKeyResolver
    {
        public string? Resolve(string provider) { Assert.Equal("inferel", provider); return key(); }
    }
    private sealed class Factory(HttpClient client, HttpClient? media = null) : IHttpClientFactory
    {
        private int _calls;
        public HttpClient CreateClient(string name) => ++_calls == 1 ? client : media ?? client;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
