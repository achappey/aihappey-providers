using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.BlackForestLabs;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests;

public sealed class BlackForestLabsMediaTests
{
    private const string PollingUrl = "https://api.eu.bfl.ai/v1/get_result?id=task-123";

    [Theory]
    [InlineData("flux-3-image")]
    [InlineData("flux-3-image-klein")]
    public async Task Flux3_images_use_native_payload_and_download_without_api_key(string model)
    {
        using var transport = new Transport();
        var request = new ImageRequest
        {
            Model = $"blackforestlabs/{model}", Prompt = "A rabbit rides a pumpkin.", AspectRatio = "16:9", Seed = 7,
            ProviderOptions = Options("""{"resolution":"4k","future":{"large":9007199254740993,"values":[true,null]},"nullable":null}""")
        };
        var before = JsonSerializer.Serialize(request);
        var response = await transport.Provider.ImageRequest(request);
        var submit = transport.Requests[0];

        Assert.Equal($"/v1/{model}", submit.Uri.AbsolutePath);
        Assert.Equal("16:9", submit.Body.GetProperty("aspect_ratio").GetString());
        Assert.Equal("4k", submit.Body.GetProperty("resolution").GetString());
        Assert.Equal(9007199254740993L, submit.Body.GetProperty("future").GetProperty("large").GetInt64());
        Assert.Equal(JsonValueKind.Null, submit.Body.GetProperty("nullable").ValueKind);
        AssertAbsent(submit.Body, "width", "height", "input_image", "mode", "model", "providerOptions", "other");
        if (model.EndsWith("klein")) Assert.Equal(7, submit.Body.GetProperty("seed").GetInt32());
        else
        {
            AssertAbsent(submit.Body, "seed");
            Assert.Contains("seed", Features(response.Warnings));
        }
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
        Assert.Equal($"blackforestlabs/{model}", response.Response.ModelId);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        AssertAuthentication(transport);
    }

    [Theory]
    [InlineData("flux-3-image")]
    [InlineData("flux-3-image-klein")]
    public async Task Flux3_edits_preserve_ten_references_in_order(string model)
    {
        using var transport = new Transport();
        var files = Enumerable.Range(0, 10).Select(index => index switch
        {
            0 => new ImageFileUrl { Url = "https://images.example/first.png" },
            1 => new ImageFile { Data = "data:image/png;base64,AQID" },
            _ => new ImageFile { Data = Convert.ToBase64String([(byte)index]) }
        }).ToArray();
        var response = await transport.Provider.ImageRequest(new ImageRequest
        {
            Model = model, Prompt = "Combine all ten references with these bounding boxes.", Files = files,
            ProviderOptions = Options(model == "flux-3-image" ? """{"resolution":"match_input"}""" : "{}")
        });
        var body = transport.Requests[0].Body;
        var images = body.GetProperty("images").EnumerateArray().Select(image => image.GetString()).ToArray();
        Assert.Equal(10, images.Length);
        Assert.Equal("https://images.example/first.png", images[0]);
        Assert.Equal("AQID", images[1]);
        Assert.Equal("CQ==", images[9]);
        AssertAbsent(body, "input_image", "input_image_2", "width", "height", "aspect_ratio");
        Assert.Empty(response.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flux3_rejects_more_than_ten_references_without_submitting(bool native)
    {
        using var transport = new Transport();
        var request = new ImageRequest { Model = "flux-3-image", Prompt = "Combine them." };
        if (native) request.ProviderOptions = Options(JsonSerializer.Serialize(new { images = Enumerable.Repeat("AQID", 11) }));
        else request.Files = Enumerable.Repeat(new ImageFile { Data = "AQID" }, 11);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.Provider.ImageRequest(request));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Klein_supports_explicit_small_dimensions_seed_steps_and_output_format()
    {
        using var transport = new Transport(mime: "image/webp");
        var response = await transport.Provider.ImageRequest(new ImageRequest
        {
            Model = "flux-3-image-klein", Prompt = "An icon.", Size = "64x64", Seed = 42,
            ProviderOptions = Options("""{"steps":16,"output_format":"webp"}""")
        });
        var body = transport.Requests[0].Body;
        Assert.Equal(64, body.GetProperty("width").GetInt32());
        Assert.Equal(64, body.GetProperty("height").GetInt32());
        Assert.Equal(42, body.GetProperty("seed").GetInt32());
        Assert.Equal(16, body.GetProperty("steps").GetInt32());
        Assert.Equal("webp", body.GetProperty("output_format").GetString());
        Assert.Equal("data:image/webp;base64,AQID", Assert.Single(response.Images!));
    }

    [Fact]
    public async Task Flux3_native_overrides_are_applied_before_prompt_validation_and_n_is_local()
    {
        using var transport = new Transport();
        var response = await transport.Provider.ImageRequest(new ImageRequest
        {
            Model = "flux-3-image", N = 2,
            ProviderOptions = Options("""{"prompt":"Native prompt","images":["https://images.example/ref.png"],"width":1800,"height":1000}""")
        });
        Assert.Equal(2, response.Images!.Count());
        Assert.Equal(2, transport.Requests.Count(request => request.Method == HttpMethod.Post));
        foreach (var submit in transport.Requests.Where(request => request.Method == HttpMethod.Post))
        {
            Assert.Equal("Native prompt", submit.Body.GetProperty("prompt").GetString());
            Assert.Equal(1800, submit.Body.GetProperty("width").GetInt32());
            AssertAbsent(submit.Body, "n");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAI_generation_preserves_raw_fields_without_forwarding_standard_transport_controls(bool streaming)
    {
        using var transport = new Transport();
        var options = new OpenAIImageGenerationRequest
        {
            Model = "blackforestlabs/flux-3-image-klein", Prompt = "Make a landscape.", Stream = streaming,
            OutputFormat = "png", ResponseFormat = "b64_json",
            AdditionalProperties = Native("""{"resolution":"2k","seed":123,"steps":16,"images":["https://images.example/ref.png"],"future":{"v":[false,null,9007199254740993]},"nullable":null}""")
        };
        var before = JsonSerializer.Serialize(options);
        if (streaming)
        {
            var events = new List<IOpenAIImageStreamEvent>();
            await foreach (var item in transport.Provider.OpenAIImageGenerationStreamingAsync(options)) events.Add(item);
            Assert.Equal("AQID", Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events)).B64Json);
        }
        else
        {
            var response = await transport.Provider.OpenAIImageGenerationRequestAsync(options);
            Assert.Equal("AQID", Assert.Single(response.Data!).B64Json);
        }
        var body = transport.Requests[0].Body;
        Assert.Equal("https://images.example/ref.png", body.GetProperty("images")[0].GetString());
        Assert.Equal("2k", body.GetProperty("resolution").GetString());
        Assert.Equal(123, body.GetProperty("seed").GetInt32());
        Assert.Equal("png", body.GetProperty("output_format").GetString());
        Assert.Equal(9007199254740993L, body.GetProperty("future").GetProperty("v")[2].GetInt64());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nullable").ValueKind);
        AssertAbsent(body, "stream", "response_format", "size", "n", "model", "providerOptions", "additionalProperties");
        Assert.Equal(before, JsonSerializer.Serialize(options));
    }

    [Fact]
    public async Task OpenAI_full_image_warns_for_unsupported_standard_controls_but_preserves_native_fields()
    {
        using var transport = new Transport();
        var response = await transport.Provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = "flux-3-image", Prompt = "A rabbit.", OutputFormat = "jpeg", Quality = "high", Style = "vivid",
            AdditionalProperties = Native("""{"future":true,"seed":5}""")
        });
        var body = transport.Requests[0].Body;
        AssertAbsent(body, "output_format", "quality", "style");
        Assert.Equal(5, body.GetProperty("seed").GetInt32()); // Explicit native options are not silently filtered.
        Assert.True(body.GetProperty("future").GetBoolean());
        var warnings = response.AdditionalProperties!["warnings"].EnumerateArray().Select(warning => warning.GetProperty("feature").GetString());
        Assert.Contains("output_format", warnings);
        Assert.Contains("quality", warnings);
        Assert.Contains("style", warnings);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OpenAI_edits_support_uploads_and_image_references(bool streaming, bool upload)
    {
        using var transport = new Transport();
        using var fileStream = new MemoryStream([1, 2, 3]);
        var options = new OpenAIImageEditRequest
        {
            Model = "flux-3-image", Prompt = "Make it winter.", Stream = streaming,
            Images = upload ? null : [new() { ImageUrl = "https://images.example/first.png" }, new() { ImageUrl = "data:image/png;base64,AQID" }],
            ImageFiles = upload ? [new FormFile(fileStream, 0, 3, "image", "input.png") { Headers = new HeaderDictionary(), ContentType = "image/png" }] : null,
            AdditionalProperties = Native("""{"resolution":"match_input","future":{"enabled":true}}""")
        };
        var before = JsonSerializer.Serialize(options);
        if (streaming)
        {
            var events = new List<IOpenAIImageStreamEvent>();
            await foreach (var item in transport.Provider.OpenAIImageEditStreamingAsync(options)) events.Add(item);
            Assert.Equal("AQID", Assert.IsType<OpenAIImageEditCompleted>(Assert.Single(events)).B64Json);
        }
        else Assert.Equal("AQID", Assert.Single((await transport.Provider.OpenAIImageEditRequestAsync(options)).Data!).B64Json);
        var body = transport.Requests[0].Body;
        Assert.Equal(upload ? "AQID" : "https://images.example/first.png", body.GetProperty("images")[0].GetString());
        Assert.Equal(upload ? 1 : 2, body.GetProperty("images").GetArrayLength());
        Assert.Equal("match_input", body.GetProperty("resolution").GetString());
        Assert.True(body.GetProperty("future").GetProperty("enabled").GetBoolean());
        AssertAbsent(body, "input_image", "stream", "imageFiles");
        Assert.Equal(before, JsonSerializer.Serialize(options));
    }

    [Theory]
    [InlineData("flux-tools/video-edit-v1", "video")]
    [InlineData("flux-tools/video-upscale-v1", "input_video")]
    public async Task Video_tools_use_their_own_endpoint_warn_for_generation_controls_and_return_base64(string model, string sourceField)
    {
        using var transport = new Transport(mime: "video/mp4");
        var request = new VideoRequest
        {
            Model = $"blackforestlabs/{model}", Prompt = "Remove the bucket.",
            InputReferences = [new VideoFileUrl { Url = "https://videos.example/clip.mp4" }, new() { Data = "AQID", MediaType = "video/mp4" }],
            AspectRatio = "16:9", Duration = 5, Resolution = "4k", Fps = 60, Seed = 123, N = 2, GenerateAudio = true,
            Image = new VideoFile { Data = "AQID", MediaType = "image/png" },
            ProviderOptions = Options("""{"safety_tolerance":2,"future":{"v":[null,true,9007199254740993]},"nullable":null}""")
        };
        var before = JsonSerializer.Serialize(request);
        var started = await transport.Provider.StartVideoOperation(request);
        var submit = transport.Requests[0];
        Assert.Equal($"/v1/{model}", submit.Uri.AbsolutePath);
        Assert.Equal("https://videos.example/clip.mp4", submit.Body.GetProperty(sourceField).GetString());
        Assert.Equal("Remove the bucket.", submit.Body.GetProperty("prompt").GetString());
        Assert.Equal(9007199254740993L, submit.Body.GetProperty("future").GetProperty("v")[2].GetInt64());
        Assert.Equal(JsonValueKind.Null, submit.Body.GetProperty("nullable").ValueKind);
        AssertAbsent(submit.Body, "mode", "duration", "resolution", "aspect_ratio", "seed", "generate_audio", "keyframes", "fps", "n", "other");
        Assert.Equal(new[] { "aspect_ratio", "duration", "fps", "generate_audio", "image inputs", "inputReferences", "n", "resolution", "seed" }, Features(started.Warnings).Order().ToArray());
        var completed = Assert.IsType<VideoOperationCompletedResult>(await transport.Provider.GetVideoOperationStatus(started.Operation));
        var video = Assert.Single(completed.Videos);
        Assert.Equal("base64", video.Type);
        Assert.Equal("AQID", video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Null(video.Url);
        Assert.Equal($"blackforestlabs/{model}", completed.Response.ModelId);
        Assert.Equal(1.23m, completed.ProviderMetadata!["gateway"].GetProperty("cost").GetDecimal());
        Assert.Equal(before, JsonSerializer.Serialize(request));
        AssertAuthentication(transport);
    }

    [Theory]
    [InlineData("flux-tools/video-edit-v1", "video")]
    [InlineData("flux-tools/video-upscale-v1", "input_video")]
    public async Task Video_tools_accept_native_sources_and_overrides_before_validation(string model, string sourceField)
    {
        using var transport = new Transport();
        var native = Native("""{"prompt":"Native instruction","upscale_factor":2.5,"creativity":0,"webhook_url":"https://callbacks.example/done","webhook_secret":"secret","mode":"explicit-native-value"}""");
        native[sourceField] = Element("\"https://videos.example/native.mp4\"");
        var started = await transport.Provider.StartVideoOperation(new VideoRequest
        {
            Model = model, ProviderOptions = new() { ["blackforestlabs"] = JsonSerializer.SerializeToElement(native) }
        });
        var body = transport.Requests[0].Body;
        Assert.Equal("https://videos.example/native.mp4", body.GetProperty(sourceField).GetString());
        foreach (var property in native) Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Key)));
        Assert.Empty(started.Warnings);
    }

    [Theory]
    [InlineData("AQID")]
    [InlineData("data:video/mp4;base64,AQID")]
    [InlineData("https://videos.example/clip.mp4")]
    public async Task Video_upscale_accepts_inline_sources_and_needs_no_prompt(string source)
    {
        using var transport = new Transport();
        await transport.Provider.StartVideoOperation(new VideoRequest
        {
            Model = "flux-tools/video-upscale-v1", InputReferences = [new() { Data = source }]
        });
        var body = transport.Requests[0].Body;
        Assert.Equal(source.StartsWith("data:") ? "AQID" : source, body.GetProperty("input_video").GetString());
        Assert.Equal(1, body.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("flux-tools/video-edit-v1", "{}")]
    [InlineData("flux-tools/video-edit-v1", "{\"video\":\"AQID\"}")]
    [InlineData("flux-tools/video-upscale-v1", "{}")]
    [InlineData("flux-tools/video-upscale-v1", "{\"input_video\":null}")]
    public async Task Video_tools_reject_missing_required_native_inputs(string model, string native)
    {
        using var transport = new Transport();
        await Assert.ThrowsAsync<ArgumentException>(() => transport.Provider.StartVideoOperation(new VideoRequest
        {
            Model = model, ProviderOptions = Options(native)
        }));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Reasoning")]
    [InlineData("Generating")]
    public async Task Video_tools_keep_in_progress_statuses_pending_without_billing(string status)
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Get
            ? Json(JsonSerializer.Serialize(new { status, cost = 999 })) : null);
        var started = await transport.Provider.StartVideoOperation(UpscaleRequest());
        var pending = Assert.IsType<VideoOperationPendingResult>(await transport.Provider.GetVideoOperationStatus(started.Operation));
        Assert.False(pending.ProviderMetadata!.ContainsKey("gateway"));
        Assert.Equal(PollingUrl, transport.Requests[1].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Request Moderated")]
    [InlineData("Content Moderated")]
    [InlineData("Task not found")]
    public async Task Video_terminal_failure_json_is_read_even_for_http_503(string status)
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Get
            ? Json(JsonSerializer.Serialize(new { status, detail = "Provider failure detail", cost = 0 }), HttpStatusCode.ServiceUnavailable) : null);
        var started = await transport.Provider.StartVideoOperation(UpscaleRequest());
        var error = Assert.IsType<VideoOperationErrorResult>(await transport.Provider.GetVideoOperationStatus(started.Operation));
        Assert.Contains(status, error.Error);
        Assert.Contains("Provider failure detail", error.Error);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Request Moderated")]
    [InlineData("Content Moderated")]
    [InlineData("Task not found")]
    public async Task Image_terminal_failure_json_is_read_even_for_http_503(string status)
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Get
            ? Json(JsonSerializer.Serialize(new { status, detail = "Provider failure detail" }), HttpStatusCode.ServiceUnavailable) : null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Provider.ImageRequest(new ImageRequest { Model = "flux-3-image", Prompt = "A rabbit." }));
        Assert.Contains(status, error.Message);
        Assert.Contains("Provider failure detail", error.Message);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Non_json_poll_failure_preserves_http_status_and_body()
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Temporary outage") } : null);
        var started = await transport.Provider.StartVideoOperation(UpscaleRequest());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Provider.GetVideoOperationStatus(started.Operation));
        Assert.Contains("503", error.Message);
        Assert.Contains("Temporary outage", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submission_validation_errors_retain_provider_details(bool video)
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Post
            ? Json("""{"detail":[{"loc":["body","future"],"msg":"Extra inputs are not permitted"}]}""", HttpStatusCode.UnprocessableEntity) : null);
        var error = video
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Provider.StartVideoOperation(UpscaleRequest()))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Provider.ImageRequest(new ImageRequest { Model = "flux-3-image", Prompt = "A rabbit." }));
        Assert.Contains("422", error.Message);
        Assert.Contains("Extra inputs are not permitted", error.Message);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_download_content_type_uses_the_model_media_default(bool video)
    {
        using var transport = new Transport(request => request.Uri.Host == "delivery.bfl.ai"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) } : null);
        if (video)
        {
            var started = await transport.Provider.StartVideoOperation(UpscaleRequest());
            var completed = Assert.IsType<VideoOperationCompletedResult>(await transport.Provider.GetVideoOperationStatus(started.Operation));
            Assert.Equal("video/mp4", Assert.Single(completed.Videos).MediaType);
        }
        else
        {
            var response = await transport.Provider.ImageRequest(new ImageRequest { Model = "flux-3-image", Prompt = "A rabbit." });
            Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
        }
        AssertAuthentication(transport);
    }

    [Theory]
    [InlineData("AQID")]
    [InlineData("data:video/mp4;base64,AQID")]
    public async Task Video_tools_accept_inline_completed_samples(string sample)
    {
        using var transport = new Transport(request => request.Method == HttpMethod.Get
            ? Json(JsonSerializer.Serialize(new { status = "Ready", result = new { sample } })) : null);
        var started = await transport.Provider.StartVideoOperation(UpscaleRequest());
        var completed = Assert.IsType<VideoOperationCompletedResult>(await transport.Provider.GetVideoOperationStatus(started.Operation));
        var output = Assert.Single(completed.Videos);
        Assert.Equal("base64", output.Type);
        Assert.Equal("AQID", output.Data);
        Assert.Equal("video/mp4", output.MediaType);
        Assert.Null(output.Url);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Video_edit_rejects_overlong_instructions()
    {
        using var transport = new Transport();
        await Assert.ThrowsAsync<ArgumentException>(() => transport.Provider.StartVideoOperation(new VideoRequest
        {
            Model = "flux-tools/video-edit-v1", Prompt = new string('x', 4097), InputReferences = [new() { Data = "AQID" }]
        }));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Image_polling_continues_through_all_documented_in_progress_statuses()
    {
        var statuses = new Queue<string>(["Pending", "Reasoning", "Generating"]);
        using var transport = new Transport(request => request.Method == HttpMethod.Get && statuses.TryDequeue(out var status)
            ? Json(JsonSerializer.Serialize(new { status })) : null);
        var response = await transport.Provider.ImageRequest(new ImageRequest { Model = "flux-3-image", Prompt = "A rabbit." });
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
        Assert.Equal(6, transport.Requests.Count);
        Assert.All(transport.Requests.Skip(1).Take(4), request => Assert.Equal(PollingUrl, request.Uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("flux-tools/deblur-v1")]
    [InlineData("flux-tools/erase-v1")]
    [InlineData("flux-tools/outpainting-v1")]
    public async Task Existing_image_tools_retain_their_endpoint_and_input_shapes(string model)
    {
        using var transport = new Transport();
        await transport.Provider.ImageRequest(new ImageRequest
        {
            Model = model, Prompt = "Extend the scene.", Size = "1024x1024", Files = [new() { Data = "AQID" }],
            Mask = model.EndsWith("erase-v1") ? new ImageFile { Data = "BAUG" } : null,
            ProviderOptions = Options("""{"future":{"enabled":true}}""")
        });
        var submit = transport.Requests[0];
        Assert.Equal($"/v1/{model}", submit.Uri.AbsolutePath);
        Assert.Equal("AQID", submit.Body.GetProperty(model.EndsWith("outpainting-v1") ? "input_image" : "image").GetString());
        if (model.EndsWith("erase-v1")) Assert.Equal("BAUG", submit.Body.GetProperty("mask").GetString());
        Assert.True(submit.Body.GetProperty("future").GetProperty("enabled").GetBoolean());
        AssertAuthentication(transport);
    }

    [Fact]
    public async Task Cancellation_stops_image_polling()
    {
        using var cancellation = new CancellationTokenSource();
        using var transport = new Transport(request =>
        {
            if (request.Method != HttpMethod.Get) return null;
            cancellation.Cancel();
            return Json("""{"status":"Reasoning"}""");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.Provider.ImageRequest(new ImageRequest
        {
            Model = "flux-3-image", Prompt = "A rabbit."
        }, cancellation.Token));
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Existing_video_generation_still_selects_i2v_and_uses_typed_urls()
    {
        using var transport = new Transport();
        await transport.Provider.StartVideoOperation(new VideoRequest
        {
            Model = "flux-3-video", Prompt = "Pan left.", Image = new VideoFileUrl { Url = "https://images.example/frame.png" },
            ProviderOptions = Options("""{"draft":true}""")
        });
        var body = transport.Requests[0].Body;
        Assert.Equal("/v1/flux-3-video", transport.Requests[0].Uri.AbsolutePath);
        Assert.Equal("i2v", body.GetProperty("mode").GetString());
        Assert.Equal("https://images.example/frame.png", body.GetProperty("keyframes")[0].GetString());
        Assert.True(body.GetProperty("draft").GetBoolean());
    }

    [Fact]
    public async Task Existing_default_image_model_is_unchanged()
    {
        using var transport = new Transport();
        await transport.Provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest { Prompt = "A rabbit." });
        Assert.Equal("/v1/flux-2-pro", transport.Requests[0].Uri.AbsolutePath);
    }

    private static VideoRequest UpscaleRequest() => new()
    {
        Model = "flux-tools/video-upscale-v1", InputReferences = [new() { Data = "AQID" }]
    };

    private static void AssertAuthentication(Transport transport)
    {
        Assert.Equal(3, transport.Requests.Count);
        Assert.Equal("test-api-key", transport.Requests[0].ApiKey);
        Assert.Equal("test-api-key", transport.Requests[1].ApiKey);
        Assert.Equal(PollingUrl, transport.Requests[1].Uri.AbsoluteUri);
        Assert.Null(transport.Requests[2].ApiKey);
        Assert.Null(transport.Requests[2].Authorization);
        Assert.Equal("delivery.bfl.ai", transport.Requests[2].Uri.Host);
    }

    private static void AssertAbsent(JsonElement body, params string[] fields)
    {
        foreach (var field in fields) Assert.False(body.TryGetProperty(field, out _), $"Unexpected provider field: {field}");
    }

    private static IEnumerable<string> Features(IEnumerable<object> warnings)
        => JsonSerializer.SerializeToElement(warnings).EnumerateArray().Select(warning => warning.GetProperty("feature").GetString()!);

    private static Dictionary<string, JsonElement> Options(string native) => new()
    {
        ["blackforestlabs"] = Element(native), ["other"] = Element("""{"should_not_leak":true}""")
    };

    private static Dictionary<string, JsonElement> Native(string json)
        => Element(json).EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Request(HttpMethod Method, Uri Uri, JsonElement Body, string? ApiKey, string? Authorization);

    private sealed class Transport : HttpMessageHandler, IHttpClientFactory, IApiKeyResolver
    {
        private readonly HttpClient _client;
        private readonly Func<Request, HttpResponseMessage?>? _responder;
        private readonly string _mime;
        public List<Request> Requests { get; } = [];
        public BlackForestLabsProvider Provider { get; }

        public Transport(Func<Request, HttpResponseMessage?>? responder = null, string mime = "image/png")
        {
            _responder = responder;
            _mime = mime;
            _client = new HttpClient(this, disposeHandler: false);
            Provider = new(this, this);
        }

        public HttpClient CreateClient(string name) => _client;
        public string? Resolve(string provider) => "test-api-key";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = message.Content is null ? null : await message.Content.ReadAsStringAsync(cancellationToken);
            var request = new Request(message.Method, message.RequestUri!, raw is null ? default : Element(raw),
                message.Headers.TryGetValues("x-key", out var keys) ? keys.Single() : null,
                message.Headers.Authorization?.ToString());
            Requests.Add(request);
            if (_responder?.Invoke(request) is { } supplied) return supplied;
            if (request.Method == HttpMethod.Post)
                return Json(JsonSerializer.Serialize(new { id = "task-123", polling_url = PollingUrl, cost = 123 }));
            if (request.Uri.Host == "api.eu.bfl.ai")
                return Json("""{"status":"Ready","cost":123,"result":{"sample":"https://delivery.bfl.ai/sample?sig=signed","prompt":"https://images.example/do-not-download-this-prompt","duration":1.5}}""");
            Assert.Equal("delivery.bfl.ai", request.Uri.Host);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(_mime);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _client.Dispose();
            base.Dispose(disposing);
        }
    }
}
