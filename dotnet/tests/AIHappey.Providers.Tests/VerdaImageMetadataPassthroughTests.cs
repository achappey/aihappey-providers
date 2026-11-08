using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Verda;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class VerdaImageMetadataPassthroughTests
{
    [Fact]
    public async Task Flux1_options_merge_into_input_and_preserve_known_and_unknown_values()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/flux-dev/predict", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ImageResponse();
        });
        var metadata = ParseElement("""
            {
              "flux_1": {
                "num_inference_steps": 8,
                "guidance_scale": 2.5,
                "enable_safety_checker": false,
                "output_format": "png",
                "output_quality": 81,
                "future_object": {"nested": [{"value": true}, [null, 1]]},
                "future_array": [1, {"value": "kept"}],
                "future_number": 1.2345678901234567890123456789,
                "future_boolean": false,
                "future_null": null
              },
              "future_root": {"items": [true, null]}
            }
            """);
        var request = new ImageRequest
        {
            Model = "flux-dev",
            Prompt = "request prompt",
            Seed = 17,
            Size = "512x512",
            ProviderOptions = new() { ["verda"] = metadata }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("verda")));

        var response = await CreateProvider(client).ImageRequest(request);
        var input = payload.GetProperty("input");

        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images));
        Assert.Equal("request prompt", input.GetProperty("prompt").GetString());
        Assert.Equal("512*512", input.GetProperty("size").GetString());
        Assert.Equal(17, input.GetProperty("seed").GetInt32());
        Assert.Equal(8, input.GetProperty("num_inference_steps").GetInt32());
        Assert.Equal(2.5, input.GetProperty("guidance_scale").GetDouble());
        Assert.False(input.GetProperty("enable_safety_checker").GetBoolean());
        Assert.Equal("png", input.GetProperty("output_format").GetString());
        Assert.Equal(81, input.GetProperty("output_quality").GetInt32());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_number", "future_boolean",
                     "future_null", "future_root"
                 })
        {
            var source = metadata.GetProperty("flux_1").TryGetProperty(name, out var nested)
                ? nested
                : metadata.GetProperty(name);
            Assert.True(JsonElement.DeepEquals(source, input.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, input.GetProperty("future_null").ValueKind);
        Assert.True(input.GetProperty("enable_base64_output").GetBoolean());
        Assert.False(input.TryGetProperty("flux_1", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.Equal(
            new[]
            {
                "prompt", "size", "seed", "num_inference_steps", "guidance_scale",
                "enable_safety_checker", "output_format", "output_quality",
                "enable_base64_output", "future_object", "future_array",
                "future_number", "future_boolean", "future_null", "future_root"
            }.Order(),
            input.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Flux1Kontext_options_map_known_values_and_forward_unknown_values_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/bfl/flux-kontext-pro", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var metadata = ParseElement("""
            {
              "flux_1": {
                "num_inference_steps": 12,
                "guidance_scale": 3.25,
                "output_format": "webp",
                "enable_safety_checker": false,
                "output_quality": 72,
                "future_nested": {"items": [null, true, {"deep": "kept"}]},
                "future_array": [1, {"value": null}],
                "future_null": null
              }
            }
            """);
        var request = new ImageRequest
        {
            Model = "flux-kontext-pro",
            Prompt = "request prompt",
            ProviderOptions = new() { ["verda"] = metadata }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("verda")));

        var response = await CreateProvider(client).ImageRequest(request);

        Assert.Equal("data:image/webp;base64,AQID", Assert.Single(response.Images));
        Assert.Equal("request prompt", payload.GetProperty("prompt").GetString());
        Assert.Equal(12, payload.GetProperty("steps").GetInt32());
        Assert.Equal(3.25, payload.GetProperty("guidance").GetDouble());
        Assert.True(payload.GetProperty("prompt_upsampling").GetBoolean());
        Assert.False(payload.GetProperty("enable_safety_checker").GetBoolean());
        Assert.Equal(72, payload.GetProperty("output_quality").GetInt32());
        foreach (var name in new[] { "future_nested", "future_array", "future_null" })
            Assert.True(JsonElement.DeepEquals(
                metadata.GetProperty("flux_1").GetProperty(name),
                payload.GetProperty(name)), name);

        Assert.False(payload.TryGetProperty("num_inference_steps", out _));
        Assert.False(payload.TryGetProperty("guidance_scale", out _));
        Assert.False(payload.TryGetProperty("output_format", out _));
        Assert.False(payload.TryGetProperty("flux_1", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
    }

    [Fact]
    public async Task Flux1_defaults_null_omission_and_known_value_validation_remain_compatible()
    {
        JsonElement? payload = null;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ImageResponse();
        });
        var provider = CreateProvider(client);

        await provider.ImageRequest(new ImageRequest { Model = "flux-dev", Prompt = "default prompt" });

        Assert.NotNull(payload);
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"input":{"prompt":"default prompt","enable_base64_output":true}}"""),
            payload.Value));

        await Assert.ThrowsAsync<JsonException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "flux-dev",
            Prompt = "invalid metadata",
            ProviderOptions = new()
            {
                ["verda"] = ParseElement("""{"flux_1":{"num_inference_steps":"invalid"}}""")
            }
        }));
    }

    [Fact]
    public async Task Flux2_options_flatten_at_root_and_preserve_known_and_unknown_values()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/flux2-dev/runsync", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ImageResponse();
        });
        var metadata = ParseElement("""
            {
              "flux_2": {
                "steps": 8,
                "guidance": 2.5,
                "output_format": "png",
                "future_object": {"nested": [{"value": true}, [null, 1]]},
                "future_array": [1, {"value": "kept"}],
                "future_number": 1.2345678901234567890123456789,
                "future_boolean": false,
                "future_null": null
              },
              "future_root": {"items": [true, null]}
            }
            """);
        var request = new ImageRequest
        {
            Model = "flux2-dev",
            Prompt = "request prompt",
            ProviderOptions = new() { ["verda"] = metadata }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("verda")));

        var response = await CreateProvider(client).ImageRequest(request);

        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images));
        Assert.Equal("request prompt", payload.GetProperty("prompt").GetString());
        Assert.Equal(8, payload.GetProperty("steps").GetInt32());
        Assert.Equal(2.5, payload.GetProperty("guidance").GetDouble());
        Assert.Equal("png", payload.GetProperty("output_format").GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_number", "future_boolean",
                     "future_null", "future_root"
                 })
        {
            var source = metadata.GetProperty("flux_2").TryGetProperty(name, out var nested)
                ? nested
                : metadata.GetProperty(name);
            Assert.True(JsonElement.DeepEquals(source, payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.True(payload.GetProperty("enable_base64_output").GetBoolean());
        Assert.False(payload.TryGetProperty("flux_2", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.Equal(
            new[]
            {
                "steps", "guidance", "output_format", "future_object", "future_array",
                "future_number", "future_boolean", "future_null", "future_root", "prompt",
                "enable_base64_output"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Flux2Klein_options_flatten_at_root_and_keep_request_overrides()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/flux2-klein-base-9b/generate", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ImageResponse();
        });
        var metadata = ParseElement("""
            {
              "flux_2_klein": {
                "prompt": "metadata prompt",
                "num_steps": 12,
                "guidance": 3.25,
                "seed": 99,
                "enable_safety_checker": false,
                "output_format": "webp",
                "output_quality": 81,
                "future_nested": {"items": [null, true, {"deep": "kept"}]},
                "future_array": [1, {"value": null}],
                "future_null": null
              },
              "future_root": {"enabled": true}
            }
            """);
        var request = new ImageRequest
        {
            Model = "flux2-klein-base-9b",
            Prompt = "request prompt",
            Seed = 17,
            Size = "512x512",
            ProviderOptions = new() { ["verda"] = metadata }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("verda")));

        var response = await CreateProvider(client).ImageRequest(request);

        Assert.Equal("data:image/webp;base64,AQID", Assert.Single(response.Images));
        Assert.Equal("request prompt", payload.GetProperty("prompt").GetString());
        Assert.Equal(512, payload.GetProperty("width").GetInt32());
        Assert.Equal(512, payload.GetProperty("height").GetInt32());
        Assert.Equal(17, payload.GetProperty("seed").GetInt32());
        Assert.Equal(12, payload.GetProperty("num_steps").GetInt32());
        Assert.Equal(3.25, payload.GetProperty("guidance").GetDouble());
        Assert.False(payload.GetProperty("enable_safety_checker").GetBoolean());
        Assert.Equal("webp", payload.GetProperty("output_format").GetString());
        Assert.Equal(81, payload.GetProperty("output_quality").GetInt32());
        foreach (var name in new[] { "future_nested", "future_array", "future_null", "future_root" })
        {
            var source = metadata.GetProperty("flux_2_klein").TryGetProperty(name, out var nested)
                ? nested
                : metadata.GetProperty(name);
            Assert.True(JsonElement.DeepEquals(source, payload.GetProperty(name)), name);
        }

        Assert.True(payload.GetProperty("enable_base64_output").GetBoolean());
        Assert.False(payload.TryGetProperty("flux_2_klein", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
    }

    [Fact]
    public async Task Null_omission_invalid_known_values_and_upstream_errors_remain_compatible()
    {
        JsonElement? payload = null;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return ImageResponse();
        });
        var provider = CreateProvider(client);

        await provider.ImageRequest(new ImageRequest
        {
            Model = "flux2-dev",
            Prompt = "default prompt"
        });
        Assert.NotNull(payload);
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"prompt":"default prompt","output_format":"jpeg","enable_base64_output":true}"""),
            payload.Value));

        payload = null;
        await provider.ImageRequest(new ImageRequest
        {
            Model = "flux2-klein-base-9b",
            Prompt = "default prompt",
            ProviderOptions = new()
            {
                ["verda"] = ParseElement("""{"flux_2_klein":{"num_steps":null,"guidance":null,"output_quality":null}}""")
            }
        });
        Assert.NotNull(payload);
        Assert.False(payload.Value.TryGetProperty("num_steps", out _));
        Assert.False(payload.Value.TryGetProperty("guidance", out _));
        Assert.False(payload.Value.TryGetProperty("output_quality", out _));

        payload = null;
        await provider.ImageRequest(new ImageRequest
        {
            Model = "flux2-klein-4b",
            Prompt = "fixed-guidance model",
            ProviderOptions = new()
            {
                ["verda"] = ParseElement(
                    """{"flux_2_klein":{"num_steps":12,"guidance":3.25,"future_option":[1,null]}}""")
            }
        });
        Assert.NotNull(payload);
        Assert.False(payload.Value.TryGetProperty("num_steps", out _));
        Assert.False(payload.Value.TryGetProperty("guidance", out _));
        Assert.True(payload.Value.GetProperty("future_option").ValueKind == JsonValueKind.Array);

        await Assert.ThrowsAsync<JsonException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "flux2-klein-base-9b",
            Prompt = "invalid metadata",
            ProviderOptions = new()
            {
                ["verda"] = ParseElement("""{"flux_2_klein":{"num_steps":"invalid"}}""")
            }
        }));

        using var failedClient = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<Exception>(() => CreateProvider(failedClient).ImageRequest(new ImageRequest
        {
            Model = "flux2-dev",
            Prompt = "request prompt"
        }));
        Assert.Equal("BadRequest: upstream failure", error.Message);
    }

    private static VerdaProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static HttpResponseMessage ImageResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"output":["AQID"]}""", Encoding.UTF8, "application/json")
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
