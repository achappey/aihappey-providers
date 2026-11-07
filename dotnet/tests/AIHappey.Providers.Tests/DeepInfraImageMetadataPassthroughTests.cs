using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepInfra;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class DeepInfraImageMetadataPassthroughTests
{
    [Fact]
    public async Task Bria_image_options_flatten_at_root_and_preserve_unknown_values_and_request_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/inference/deepinfra/Bria/fibo", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ImageResponse();
        });
        var metadata = ParseElement("""
            {
              "bria": {
                "speed": "fast",
                "structured_prompt": "structured",
                "future_object": {"nested": [{"value": true}, [null, 1]]},
                "future_array": [1, {"value": "kept"}],
                "future_boolean": false,
                "future_null": null
              },
              "future_root": {"items": [true, null]},
              "prompt": "metadata prompt",
              "seed": 99,
              "bria_enhance": {"other_operation": true}
            }
            """);
        var request = new ImageRequest
        {
            Model = "deepinfra/Bria/fibo",
            Prompt = "request prompt",
            Seed = 7,
            AspectRatio = "16:9",
            Files = [new ImageFile { MediaType = "image/png", Data = "AQID" }],
            ProviderOptions = new()
            {
                ["deepinfra"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var clientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientRequest.GetProperty("providerOptions").GetProperty("deepinfra")));

        await CreateProvider(client).ImageRequest(request, CancellationToken.None);

        Assert.Equal("request prompt", payload.GetProperty("prompt").GetString());
        Assert.Equal(7, payload.GetProperty("seed").GetInt32());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal("fast", payload.GetProperty("speed").GetString());
        Assert.Equal("structured", payload.GetProperty("structured_prompt").GetString());
        foreach (var name in new[] { "future_object", "future_array", "future_boolean", "future_null", "future_root" })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty("bria").TryGetProperty(name, out var nested)
                ? nested
                : metadata.GetProperty(name), payload.GetProperty(name)), name);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("bria", out _));
        Assert.False(payload.TryGetProperty("bria_enhance", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "future_object", "future_array", "future_boolean", "future_null", "future_root",
                "prompt", "num_results", "aspect_ratio", "seed", "image", "speed", "structured_prompt"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Bria_enhance_options_preserve_known_mapping_null_omission_and_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "deepinfra/Bria/enhance",
            Seed = 0,
            Files = [new ImageFile { MediaType = "image/png", Data = "AQID" }],
            ProviderOptions = new()
            {
                ["deepinfra"] = ParseElement("""
                    {
                      "bria_enhance": {
                        "resolution": "2k",
                        "preserve_alpha": false,
                        "visual_input_content_moderation": true,
                        "visual_output_content_moderation": false,
                        "steps_num": 0,
                        "future_nested": {"array": [null, {"enabled": true}]},
                        "future_value": 1.2345678901234567890123456789,
                        "future_null": null
                      }
                    }
                    """)
            }
        };

        await CreateProvider(client).ImageRequest(request, CancellationToken.None);

        Assert.Equal("2k", payload.GetProperty("resolution").GetString());
        Assert.False(payload.GetProperty("preserve_alpha").GetBoolean());
        Assert.True(payload.GetProperty("visual_input_content_moderation").GetBoolean());
        Assert.False(payload.GetProperty("visual_output_content_moderation").GetBoolean());
        Assert.Equal(0, payload.GetProperty("steps_num").GetInt32());
        Assert.Equal(0, payload.GetProperty("seed").GetInt32());
        Assert.True(JsonElement.DeepEquals(
            request.ProviderOptions["deepinfra"].GetProperty("bria_enhance").GetProperty("future_nested"),
            payload.GetProperty("future_nested")));
        Assert.True(JsonElement.DeepEquals(
            request.ProviderOptions["deepinfra"].GetProperty("bria_enhance").GetProperty("future_value"),
            payload.GetProperty("future_value")));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("bria_enhance", out _));

        request.ProviderOptions["deepinfra"] = ParseElement(
            """{"bria_enhance":{"resolution":null,"preserve_alpha":null,"steps_num":null}}""");
        payload = default;
        await CreateProvider(client).ImageRequest(request, CancellationToken.None);
        foreach (var name in new[] { "resolution", "preserve_alpha", "steps_num" })
            Assert.False(payload.TryGetProperty(name, out _), name);

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions["deepinfra"] = ParseElement("""{"bria_enhance":{"steps_num":"invalid"}}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient)
            .ImageRequest(request, CancellationToken.None));
    }

    [Fact]
    public async Task Generic_image_generation_forwards_raw_provider_options_without_injecting_defaults()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "deepinfra/image-model",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["deepinfra"] = ParseElement(
                    """{"new_parameter":"unchanged","nested":[{"value":true},null],"explicit_null":null}""")
            }
        };

        await CreateProvider(client).ImageRequest(request, CancellationToken.None);

        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal(1, payload.GetProperty("num_results").GetInt32());
        Assert.Equal("unchanged", payload.GetProperty("new_parameter").GetString());
        Assert.True(JsonElement.DeepEquals(
            request.ProviderOptions["deepinfra"].GetProperty("nested"),
            payload.GetProperty("nested")));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("explicit_null").ValueKind);
        Assert.Equal(
            new[] { "prompt", "num_results", "new_parameter", "nested", "explicit_null" }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Image_upstream_errors_keep_existing_exception_behavior()
    {
        using var failedClient = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });

        var error = await Assert.ThrowsAsync<Exception>(() => CreateProvider(failedClient)
            .ImageRequest(new ImageRequest
            {
                Model = "deepinfra/image-model",
                Prompt = "A landscape",
                ProviderOptions = new()
                {
                    ["deepinfra"] = ParseElement("""{"future_parameter":true}""")
                }
            }, CancellationToken.None));

        Assert.Equal("BadRequest: upstream failure", error.Message);
    }

    private static DeepInfraProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage ImageResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"images":["data:image/png;base64,AQID"]}""")
        };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
