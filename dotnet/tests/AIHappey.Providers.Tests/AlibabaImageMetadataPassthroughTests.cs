using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Alibaba;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class AlibabaImageMetadataPassthroughTests
{
    [Fact]
    public async Task Qwen_image_maps_flat_options_and_forwards_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

            Assert.Equal("/api/v1/services/aigc/multimodal-generation/generation", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"output":{"choices":[{"message":{"content":[{"type":"image","image":"https://images.test/result.png"}]}}]}}""");
        });

        var metadata = ParseElement(
            """
            {
              "negativePrompt": "no blur",
              "promptExtend": false,
              "watermark": false,
              "promptExtendMode": "direct",
              "enableThinking": true,
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
        var request = CreateRequest("qwen-image-3.0", metadata);
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("alibaba")));

        await CreateProvider(client).ImageRequest(request);

        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Equal("qwen-image-3.0", payload.GetProperty("model").GetString());
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
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_null"), metadata.GetProperty("future_null")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_root_option"), metadata.GetProperty("future_root_option")));
        Assert.Equal(JsonValueKind.Null, parameters.GetProperty("future_null").ValueKind);
    }

    [Fact]
    public async Task Z_image_uses_its_api_contract_for_flat_option_mapping()
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
            "z-image-turbo",
            ParseElement("""{"promptExtend":true,"negativePrompt":"untouched","future":null}"""),
            includeStandardOptions: false));

        var parameters = payload.GetProperty("parameters");
        Assert.True(parameters.GetProperty("prompt_extend").GetBoolean());
        Assert.Equal("untouched", parameters.GetProperty("negativePrompt").GetString());
        Assert.Equal(JsonValueKind.Null, parameters.GetProperty("future").ValueKind);
    }

    [Fact]
    public async Task Wan_image_keeps_mode_defaults_and_forwards_unknown_options()
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
              "enableInterleave": true,
              "promptExtend": true,
              "negativePrompt": "no blur",
              "maxImages": 4,
              "future": {"nested": [true, null, 2]}
            }
            """);

        await CreateProvider(client).ImageRequest(CreateRequest("wan2.6-t2i", metadata, includeStandardOptions: false));

        var parameters = payload.GetProperty("parameters");
        Assert.False(parameters.GetProperty("enable_interleave").GetBoolean());
        Assert.True(parameters.GetProperty("prompt_extend").GetBoolean());
        Assert.Equal("no blur", parameters.GetProperty("negative_prompt").GetString());
        Assert.False(parameters.TryGetProperty("max_images", out _));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future"), metadata.GetProperty("future")));
        Assert.Equal(1, parameters.GetProperty("n").GetInt32());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
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

    [Fact]
    public async Task Image_omits_null_mapped_options_but_preserves_unknown_nulls()
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
            ParseElement("""{"negativePrompt":null,"promptExtend":null,"watermark":null,"future_null":null}"""),
            includeStandardOptions: false));

        var parameters = payload.GetProperty("parameters");
        Assert.False(parameters.TryGetProperty("negative_prompt", out _));
        Assert.False(parameters.TryGetProperty("prompt_extend", out _));
        Assert.False(parameters.TryGetProperty("watermark", out _));
        Assert.Equal(JsonValueKind.Null, parameters.GetProperty("future_null").ValueKind);
    }

    [Theory]
    [InlineData("qwen-image-3.0", "[]")]
    [InlineData("qwen-image-3.0", """{"negativePrompt":[]}""")]
    [InlineData("qwen-image-3.0", """{"watermark":"false"}""")]
    [InlineData("wan2.6-t2i", """{"enableInterleave":"true"}""")]
    public async Task Image_rejects_invalid_metadata_shapes_and_known_values(string model, string metadataJson)
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));

        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(client).ImageRequest(
            CreateRequest(model, ParseElement(metadataJson))));
    }

    [Fact]
    public async Task Image_preserves_upstream_error_behavior()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });

        var error = await Assert.ThrowsAsync<Exception>(() => CreateProvider(client).ImageRequest(
            CreateRequest("qwen-image-3.0", default)));

        Assert.Equal("BadRequest: upstream failure", error.Message);
    }

    private static ImageRequest CreateRequest(string model, JsonElement metadata, bool includeStandardOptions = true)
        => new()
        {
            Model = model,
            Prompt = "Make an image",
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
