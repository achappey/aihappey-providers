using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Alibaba;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class AlibabaImageMetadataPassthroughTests
{
    [Theory]
    [InlineData("qwen-image-3.0")]
    [InlineData("z-image-turbo")]
    [InlineData("wan2.6-t2i")]
    [InlineData("wan2.6-image")]
    public async Task Image_forwards_flat_native_options_and_unknown_json(string model)
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

            Assert.Equal("dashscope-intl.aliyuncs.com", request.RequestUri!.Host);
            Assert.Equal("/api/v1/services/aigc/multimodal-generation/generation", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"output":{"choices":[{"message":{"content":[{"type":"image","image":"https://images.test/result.png"}]}}]}}""");
        });

        var metadata = ParseElement(
            """
            {
              "negative_prompt": "no blur",
              "prompt_extend": false,
              "watermark": false,
              "prompt_extend_mode": "direct",
              "enable_thinking": true,
              "enable_interleave": false,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "future_root_option": {"root": [1, null]},
              "size": "metadata-size",
              "seed": 99,
              "n": 7
            }
            """);
        var request = CreateRequest(model, metadata);
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("alibaba")));

        var response = await CreateProvider(client).ImageRequest(request);

        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Equal(model, payload.GetProperty("model").GetString());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("alibaba", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));

        var parameters = payload.GetProperty("parameters");
        Assert.Equal("1024*1024", parameters.GetProperty("size").GetString());
        Assert.Equal(42, parameters.GetProperty("seed").GetInt32());
        Assert.Equal(2, parameters.GetProperty("n").GetInt32());
        Assert.Equal("no blur", parameters.GetProperty("negative_prompt").GetString());
        Assert.False(parameters.GetProperty("prompt_extend").GetBoolean());
        Assert.False(parameters.GetProperty("watermark").GetBoolean());
        Assert.Equal("direct", parameters.GetProperty("prompt_extend_mode").GetString());
        Assert.True(parameters.GetProperty("enable_thinking").GetBoolean());
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_object"), metadata.GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_array"), metadata.GetProperty("future_array")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_string"), metadata.GetProperty("future_string")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_number"), metadata.GetProperty("future_number")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_boolean"), metadata.GetProperty("future_boolean")));
        Assert.False(parameters.TryGetProperty("future_null", out _));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_root_option"), metadata.GetProperty("future_root_option")));
        Assert.False(parameters.GetProperty("enable_interleave").GetBoolean());
        Assert.Equal(metadata.EnumerateObject().Count() - 1, parameters.EnumerateObject().Count());
        var message = Assert.Single(payload.GetProperty("input").GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Make an image", Assert.Single(message.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
    }

    [Theory]
    [InlineData("qwen-image-3.0")]
    [InlineData("z-image-turbo")]
    [InlineData("wan2.6-t2i")]
    [InlineData("wan2.6-image")]
    public async Task Image_preserves_native_standard_options_when_generic_fields_are_absent(string model)
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"output":{"choices":[{"message":{"content":[{"type":"image","image":"https://images.test/result.png"}]}}]}}""");
        });

        var metadata = ParseElement("""{"size":"native-size","seed":0,"n":7,"prompt_extend":true,"negative_prompt":"untouched","enable_interleave":false}""");
        await CreateProvider(client).ImageRequest(CreateRequest(
            model,
            metadata,
            includeStandardOptions: false));

        Assert.True(JsonElement.DeepEquals(metadata, payload.GetProperty("parameters")));
    }

    [Theory]
    [InlineData("wan2.6-t2i")]
    [InlineData("wan2.6-image")]
    public async Task Wan_image_preserves_explicit_native_options_without_mode_filtering(string model)
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"output":{"choices":[{"message":{"content":[{"type":"image","image":"https://images.test/result.png"}]}}]}}""");
        });
        var metadata = ParseElement(
            """
            {
              "enable_interleave": true,
              "prompt_extend": true,
              "negative_prompt": "no blur",
              "max_images": 4,
              "n": 3,
              "future": {"nested": [true, null, 2]}
            }
            """);

        var response = await CreateProvider(client).ImageRequest(CreateRequest(model, metadata, includeStandardOptions: false));

        var parameters = payload.GetProperty("parameters");
        Assert.True(JsonElement.DeepEquals(metadata, parameters));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.Empty(response.Warnings!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"negative_prompt":null}""")]
    [InlineData("""{"prompt_extend":null}""")]
    [InlineData("""{"size":null,"seed":null,"n":null,"watermark":null,"future":null}""")]
    [InlineData("""{"negativePrompt":null}""")]
    [InlineData("""{"promptExtend":null}""")]
    public async Task Image_omits_empty_metadata_options(string? metadataJson)
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"output":{"choices":[{"message":{"content":[{"type":"image","image":"https://images.test/result.png"}]}}]}}""");
        });

        await CreateProvider(client).ImageRequest(CreateRequest(
            "qwen-image-3.0",
            metadataJson is null ? default : ParseElement(metadataJson),
            includeStandardOptions: false));

        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Empty(payload.GetProperty("parameters").EnumerateObject());
    }

    [Theory]
    [InlineData("qwen-image-3.0")]
    [InlineData("z-image-turbo")]
    [InlineData("wan2.6-t2i")]
    [InlineData("wan2.6-image")]
    public async Task Image_omits_null_options_but_preserves_nested_nulls(string model)
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"output":{"choices":[{"message":{"content":[{"type":"image","image":"https://images.test/result.png"}]}}]}}""");
        });

        await CreateProvider(client).ImageRequest(CreateRequest(
            model,
            ParseElement("""{"negative_prompt":null,"prompt_extend":null,"watermark":null,"future_null":null,"size":null,"seed":null,"n":null,"enable_interleave":null,"nested":{"value":null,"array":[null,false]}}"""),
            includeStandardOptions: false));

        var parameters = payload.GetProperty("parameters");
        Assert.False(parameters.TryGetProperty("negative_prompt", out _));
        Assert.False(parameters.TryGetProperty("prompt_extend", out _));
        Assert.False(parameters.TryGetProperty("watermark", out _));
        Assert.False(parameters.TryGetProperty("future_null", out _));
        Assert.False(parameters.TryGetProperty("size", out _));
        Assert.False(parameters.TryGetProperty("seed", out _));
        Assert.DoesNotContain(parameters.EnumerateObject(), property => property.Value.ValueKind == JsonValueKind.Null);
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"value":null,"array":[null,false]}"""),
            parameters.GetProperty("nested")));
    }

    [Theory]
    [InlineData("qwen-image-3.0", "[]")]
    [InlineData("qwen-image-3.0", "true")]
    [InlineData("qwen-image-3.0", "123")]
    [InlineData("qwen-image-3.0", "\"text\"")]
    [InlineData("z-image-turbo", "[]")]
    [InlineData("wan2.6-t2i", "[]")]
    [InlineData("wan2.6-image", "[]")]
    public async Task Image_rejects_only_non_object_metadata(string model, string metadataJson)
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));

        var error = await Assert.ThrowsAsync<JsonException>(() => CreateProvider(client).ImageRequest(
            CreateRequest(model, ParseElement(metadataJson))));
        Assert.Equal("Alibaba provider options must be a JSON object.", error.Message);
    }

    [Theory]
    [InlineData("qwen-image-3.0")]
    [InlineData("z-image-turbo")]
    [InlineData("wan2.6-t2i")]
    [InlineData("wan2.6-image")]
    public async Task Image_preserves_upstream_error_behavior(string model)
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });

        var error = await Assert.ThrowsAsync<Exception>(() => CreateProvider(client).ImageRequest(
            CreateRequest(model, default)));

        Assert.Equal("BadRequest: upstream failure", error.Message);
    }

    [Theory]
    [InlineData("qwen-image-3.0")]
    [InlineData("z-image-turbo")]
    [InlineData("wan2.6-t2i")]
    [InlineData("wan2.6-image")]
    public async Task Image_does_not_interpret_legacy_wrappers_aliases_or_case_variants(string model)
    {
        JsonElement payload = default;
        using var client = CreateImageClient(body => payload = body);
        var metadata = ParseElement(
            """
            {
              "negative_prompt": "native",
              "negativePrompt": ["not", "renamed"],
              "promptExtend": "not validated",
              "promptExtendMode": {"untouched": null},
              "enableThinking": [],
              "enableInterleave": true,
              "maxImages": "not dropped",
              "enable_interleave": false,
              "n": 1,
              "qwen": {"negativePrompt": "not unwrapped"},
              "wan": {"enableInterleave": true},
              "zImage": {"promptExtend": true},
              "WATERMARK": "case-sensitive",
              "watermark": false,
              "SIZE": "not a standard-field alias"
            }
            """);

        await CreateProvider(client).ImageRequest(CreateRequest(model, metadata, includeStandardOptions: false));

        Assert.True(JsonElement.DeepEquals(metadata, payload.GetProperty("parameters")));
    }

    [Theory]
    [InlineData("qwen-image-3.0")]
    [InlineData("z-image-turbo")]
    [InlineData("wan2.6-t2i")]
    [InlineData("wan2.6-image")]
    public async Task Image_leaves_native_option_types_and_constraints_to_upstream(string model)
    {
        JsonElement payload = default;
        using var client = CreateImageClient(body => payload = body);
        var metadata = ParseElement(
            """
            {
              "negative_prompt": [],
              "prompt_extend": "false",
              "watermark": "false",
              "enable_thinking": {"future": true},
              "enable_interleave": "true",
              "max_images": {"future": 4},
              "size": [1, 2],
              "seed": "native value",
              "n": 99,
              "empty_string": "",
              "empty_array": [],
              "empty_object": {}
            }
            """);

        await CreateProvider(client).ImageRequest(CreateRequest(model, metadata, includeStandardOptions: false));

        Assert.True(JsonElement.DeepEquals(metadata, payload.GetProperty("parameters")));
    }

    [Theory]
    [InlineData("wan2.6-t2i", false, null)]
    [InlineData("wan2.6-image", false, true)]
    [InlineData("wan2.6-image", true, false)]
    public async Task Wan_image_retains_defaults_only_when_native_options_are_absent(
        string model, bool includeImage, bool? expectedInterleave)
    {
        JsonElement payload = default;
        using var client = CreateImageClient(body => payload = body);

        var response = await CreateProvider(client).ImageRequest(CreateRequest(
            model, default, includeStandardOptions: false,
            files: includeImage ? [new ImageFile { Data = "AQID", MediaType = "image/png" }] : null));

        var parameters = payload.GetProperty("parameters");
        Assert.Equal(1, parameters.GetProperty("n").GetInt32());
        Assert.Equal(expectedInterleave.HasValue, parameters.TryGetProperty("enable_interleave", out var interleave));
        if (expectedInterleave.HasValue)
            Assert.Equal(expectedInterleave.Value, interleave.GetBoolean());
        Assert.Equal(expectedInterleave.HasValue ? 2 : 1, parameters.EnumerateObject().Count());
        Assert.Equal(expectedInterleave == true ? 1 : 0, response.Warnings!.Count());
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
    }

    [Theory]
    [InlineData("qwen-image-3.0", "AQID", "data:image/png;base64,AQID")]
    [InlineData("qwen-image-3.0", "data:image/jpeg;base64,AQID", "data:image/jpeg;base64,AQID")]
    [InlineData("qwen-image-3.0", "https://images.test/source.png", "https://images.test/source.png")]
    [InlineData("wan2.6-image", "AQID", "data:image/png;base64,AQID")]
    [InlineData("wan2.6-image", "data:image/jpeg;base64,AQID", "data:image/jpeg;base64,AQID")]
    [InlineData("wan2.6-image", "https://images.test/source.png", "https://images.test/source.png")]
    public async Task Image_keeps_reference_images_in_native_message_content(string model, string source, string expected)
    {
        JsonElement payload = default;
        using var client = CreateImageClient(body => payload = body);

        var response = await CreateProvider(client).ImageRequest(CreateRequest(
            model, ParseElement("""{"negative_prompt":"no blur"}"""), includeStandardOptions: false,
            files: [new ImageFile { Data = source, MediaType = "image/png" }]));

        var message = Assert.Single(payload.GetProperty("input").GetProperty("messages").EnumerateArray());
        var content = message.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(2, content.Length);
        Assert.Equal(expected, Assert.Single(content, item => item.TryGetProperty("image", out _)).GetProperty("image").GetString());
        Assert.Equal("Make an image", Assert.Single(content, item => item.TryGetProperty("text", out _)).GetProperty("text").GetString());
        Assert.Equal("no blur", payload.GetProperty("parameters").GetProperty("negative_prompt").GetString());
        Assert.Empty(response.Warnings!);
    }

    [Theory]
    [InlineData("qwen-image-3.0", 3)]
    [InlineData("wan2.6-image", 4)]
    public async Task Image_keeps_multiple_reference_images(string model, int imageCount)
    {
        JsonElement payload = default;
        using var client = CreateImageClient(body => payload = body);
        var files = Enumerable.Range(1, imageCount)
            .Select(index => new ImageFile { Data = $"https://images.test/source-{index}.png", MediaType = "image/png" })
            .ToArray();

        await CreateProvider(client).ImageRequest(CreateRequest(model, default, includeStandardOptions: false, files: files));

        var content = Assert.Single(payload.GetProperty("input").GetProperty("messages").EnumerateArray())
            .GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(imageCount + 1, content.Length);
        Assert.Equal(files.Select(file => file.Data), content.Where(item => item.TryGetProperty("image", out _))
            .Select(item => item.GetProperty("image").GetString()));
    }

    [Theory]
    [InlineData("qwen-image")]
    [InlineData("z-image-turbo")]
    [InlineData("wan2.6-t2i")]
    public async Task Image_keeps_unsupported_reference_image_warning(string model)
    {
        JsonElement payload = default;
        using var client = CreateImageClient(body => payload = body);

        var response = await CreateProvider(client).ImageRequest(CreateRequest(
            model, default, includeStandardOptions: false,
            files: [new ImageFile { Data = "AQID", MediaType = "image/png" }]));

        var content = Assert.Single(payload.GetProperty("input").GetProperty("messages").EnumerateArray())
            .GetProperty("content").EnumerateArray();
        Assert.Equal("Make an image", Assert.Single(content).GetProperty("text").GetString());
        var warning = JsonSerializer.SerializeToElement(Assert.Single(response.Warnings!));
        Assert.Equal("unsupported", warning.GetProperty("type").GetString());
        Assert.Equal("files", warning.GetProperty("feature").GetString());
    }

    [Fact]
    public async Task Wan_image_keeps_mixed_mode_reference_images_and_text_response_metadata()
    {
        JsonElement payload = default;
        using var client = CreateImageClient(body => payload = body,
            """{"output":{"choices":[{"message":{"content":[{"type":"text","text":"Image description"},{"type":"image","image":"https://images.test/result.png"}]}}]}}""");

        var response = await CreateProvider(client).ImageRequest(CreateRequest(
            "wan2.6-image", ParseElement("""{"enable_interleave":true,"max_images":4,"n":1}"""),
            includeStandardOptions: false, files: [new ImageFile { Data = "AQID", MediaType = "image/png" }]));

        Assert.True(payload.GetProperty("parameters").GetProperty("enable_interleave").GetBoolean());
        Assert.Equal(4, payload.GetProperty("parameters").GetProperty("max_images").GetInt32());
        Assert.Equal("Image description", Assert.Single(response.ProviderMetadata!["wan_text"].EnumerateArray()).GetString());
        var warning = JsonSerializer.SerializeToElement(Assert.Single(response.Warnings!));
        Assert.Equal("dropped", warning.GetProperty("type").GetString());
        Assert.Equal("text_output", warning.GetProperty("feature").GetString());
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));
    }

    [Theory]
    [InlineData("qwen-image-3.0", false, false)]
    [InlineData("qwen-image-3.0", false, true)]
    [InlineData("qwen-image-3.0", true, false)]
    [InlineData("qwen-image-3.0", true, true)]
    [InlineData("wan2.6-image", false, false)]
    [InlineData("wan2.6-image", false, true)]
    [InlineData("wan2.6-image", true, false)]
    [InlineData("wan2.6-image", true, true)]
    public async Task OpenAI_image_endpoints_forward_raw_extension_data_and_return_base64(
        string model, bool edit, bool streaming)
    {
        JsonElement payload = default;
        using var client = CreateImageClient(body => payload = body);
        var provider = CreateProvider(client);
        var metadata = ParseElement(
            """{"negative_prompt":"no blur","prompt_extend":false,"seed":0,"future":{"array":[true,null,1.25]},"future_null":null}""");
        var extensionData = metadata.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        var options = edit ? null : new OpenAIImageGenerationRequest
        {
            Model = model, Prompt = "Make an image", Size = "1024x1024", N = 2,
            AdditionalProperties = extensionData
        };
        var editOptions = !edit ? null : new OpenAIImageEditRequest
        {
            Model = model, Prompt = "Make an image", Size = "1024x1024", N = 2,
            Images = [new() { ImageUrl = "data:image/png;base64,AQID" }],
            AdditionalProperties = extensionData
        };
        var before = edit ? JsonSerializer.SerializeToElement(editOptions) : JsonSerializer.SerializeToElement(options);

        if (streaming)
        {
            var events = new List<IOpenAIImageStreamEvent>();
            var stream = edit ? provider.OpenAIImageEditStreamingAsync(editOptions!)
                : provider.OpenAIImageGenerationStreamingAsync(options!);
            await foreach (var item in stream)
                events.Add(item);

            var completed = Assert.Single(events);
            if (edit)
                Assert.Equal("AQID", Assert.IsType<OpenAIImageEditCompleted>(completed).B64Json);
            else
                Assert.Equal("AQID", Assert.IsType<OpenAIImageGenerationCompleted>(completed).B64Json);
        }
        else
        {
            var response = edit ? await provider.OpenAIImageEditRequestAsync(editOptions!)
                : await provider.OpenAIImageGenerationRequestAsync(options!);
            var image = Assert.Single(JsonSerializer.SerializeToElement(response).GetProperty("data").EnumerateArray());
            Assert.Equal("AQID", image.GetProperty("b64_json").GetString());
            Assert.False(image.TryGetProperty("url", out _));
        }

        var parameters = payload.GetProperty("parameters");
        Assert.Equal("1024*1024", parameters.GetProperty("size").GetString());
        Assert.Equal(2, parameters.GetProperty("n").GetInt32());
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
                Assert.False(parameters.TryGetProperty(property.Name, out _));
            else
                Assert.True(JsonElement.DeepEquals(property.Value, parameters.GetProperty(property.Name)), property.Name);
        }
        var content = Assert.Single(payload.GetProperty("input").GetProperty("messages").EnumerateArray())
            .GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(edit ? 2 : 1, content.Length);
        if (edit)
            Assert.Equal("data:image/png;base64,AQID", Assert.Single(content, item => item.TryGetProperty("image", out _))
                .GetProperty("image").GetString());
        Assert.True(JsonElement.DeepEquals(before,
            edit ? JsonSerializer.SerializeToElement(editOptions) : JsonSerializer.SerializeToElement(options)));
    }

    private static ImageRequest CreateRequest(
        string model, JsonElement metadata, bool includeStandardOptions = true, ImageFile[]? files = null)
        => new()
        {
            Model = model,
            Prompt = "Make an image",
            Files = files,
            Size = includeStandardOptions ? "1024x1024" : null,
            Seed = includeStandardOptions ? 42 : null,
            N = includeStandardOptions ? 2 : null,
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement>
                {
                    ["alibaba"] = metadata,
                    ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
                }
        };

    private static HttpClient CreateImageClient(Action<JsonElement> capture,
        string responseJson = """{"output":{"choices":[{"message":{"content":[{"type":"image","image":"https://images.test/result.png"}]}}]}}""")
        => CreateClient(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("dashscope-intl.aliyuncs.com", request.RequestUri!.Host);
            Assert.Equal("/api/v1/services/aigc/multimodal-generation/generation", request.RequestUri.AbsolutePath);
            capture(ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return JsonResponse(responseJson);
        });

    private static AlibabaProvider CreateProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
