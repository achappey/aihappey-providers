using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Alibaba;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class AlibabaVideoMetadataPassthroughTests
{
    [Fact]
    public async Task Video_forwards_unknown_provider_options_unchanged_and_maps_known_options()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/api/v1/services/aigc/video-generation/video-synthesis", request.RequestUri!.AbsolutePath);
            Assert.Equal("enable", request.Headers.GetValues("X-DashScope-Async").Single());
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"output":{"task_id":"task-123"}}""");
        });

        var metadata = ParseElement(
            """
            {
              "wan": {
                "prompt_extend": true,
                "negative_prompt": "no blur",
                "watermark": false,
                "shot_type": "multi",
                "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                "future_array": [1, {"more": [false, 2.5]}, []],
                "future_string": "unchanged",
                "future_number": 12345678901234567890.123456789,
                "future_boolean": false,
                "future_null": null,
                "resolution": "metadata-resolution",
                "duration": 99,
                "seed": 99
              },
              "future_root_option": {"root": [1, null]}
            }
            """);
        var request = CreateRequest(metadata);
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("alibaba")));

        var response = await CreateProvider(client).StartVideoOperation(request);

        Assert.Equal("task-123", response.Operation);
        Assert.Equal("wan2.6-t2v", payload.GetProperty("model").GetString());
        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("alibaba", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));

        var input = payload.GetProperty("input");
        Assert.Equal("Make a video", input.GetProperty("prompt").GetString());
        Assert.Equal("no blur", input.GetProperty("negative_prompt").GetString());

        var parameters = payload.GetProperty("parameters");
        Assert.True(parameters.GetProperty("prompt_extend").GetBoolean());
        Assert.False(parameters.GetProperty("watermark").GetBoolean());
        Assert.Equal("multi", parameters.GetProperty("shot_type").GetString());
        Assert.Equal("720P", parameters.GetProperty("resolution").GetString());
        Assert.Equal(5, parameters.GetProperty("duration").GetInt32());
        Assert.Equal(42, parameters.GetProperty("seed").GetInt32());
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_object"), metadata.GetProperty("wan").GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_array"), metadata.GetProperty("wan").GetProperty("future_array")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_string"), metadata.GetProperty("wan").GetProperty("future_string")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_number"), metadata.GetProperty("wan").GetProperty("future_number")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_boolean"), metadata.GetProperty("wan").GetProperty("future_boolean")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_null"), metadata.GetProperty("wan").GetProperty("future_null")));
        Assert.True(JsonElement.DeepEquals(parameters.GetProperty("future_root_option"), metadata.GetProperty("future_root_option")));
        Assert.Equal(JsonValueKind.Null, parameters.GetProperty("future_null").ValueKind);
        Assert.False(parameters.TryGetProperty("negative_prompt", out _));
    }

    [Fact]
    public async Task Video_preserves_omissions_and_rejects_invalid_provider_option_shapes()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"output":{"task_id":"task-123"}}""");
        });
        var provider = CreateProvider(client);
        var request = CreateRequest(ParseElement("""{"wan":null}"""));

        await provider.StartVideoOperation(request);

        Assert.Equal(3, payload.EnumerateObject().Count());
        Assert.Equal(1, payload.GetProperty("input").EnumerateObject().Count());
        Assert.Empty(payload.GetProperty("parameters").EnumerateObject());

        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient)
            .StartVideoOperation(CreateRequest(ParseElement("""{"wan":[]}"""))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient)
            .StartVideoOperation(CreateRequest(ParseElement("[]"))));

        using var failedClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(failedClient)
            .StartVideoOperation(CreateRequest(default)));
        Assert.Equal("Wan video create failed (400): upstream failure", error.Message);
    }

    private static VideoRequest CreateRequest(JsonElement metadata)
        => new()
        {
            Model = "wan2.6-t2v",
            Prompt = "Make a video",
            Resolution = "720P",
            Duration = 5,
            Seed = 42,
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
