using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.MiniMax;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class MiniMaxImageMetadataPassthroughTests
{
    [Fact]
    public async Task Provider_options_preserve_known_mapping_and_forward_unknown_root_values()
    {
        var metadata = ParseElement("""
            {
              "prompt_optimizer": false,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "prompt": "metadata-prompt"
            }
            """);
        JsonElement upstream = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/image_generation", request.RequestUri!.AbsolutePath);
            upstream = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"data":{"image_base64":["AQID"]}}""");
        });
        var request = new ImageRequest
        {
            Model = "minimax/image-01",
            Prompt = "request prompt",
            ProviderOptions = new()
            {
                ["minimax"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":true}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateProvider(client).ImageRequest(request);

        Assert.Equal("image-01", upstream.GetProperty("model").GetString());
        Assert.Equal("request prompt", upstream.GetProperty("prompt").GetString());
        Assert.Equal("base64", upstream.GetProperty("response_format").GetString());
        Assert.False(upstream.GetProperty("prompt_optimizer").GetBoolean());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number",
                     "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), upstream.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Object, upstream.ValueKind);
        Assert.False(upstream.TryGetProperty("providerOptions", out _));
        Assert.False(upstream.TryGetProperty("minimax", out _));
        Assert.False(upstream.TryGetProperty("another-provider", out _));
        Assert.False(upstream.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "model", "prompt", "response_format", "prompt_optimizer", "future_object",
                "future_array", "future_string", "future_number", "future_boolean", "future_null"
            }.Order(),
            upstream.EnumerateObject().Select(property => property.Name).Order());
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["minimax"]));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Null_options_defaults_validation_and_upstream_errors_remain_compatible()
    {
        JsonElement? upstream = null;
        using var client = CreateClient(request =>
        {
            upstream = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"data":{"image_base64":["AQID"]}}""");
        });
        await CreateProvider(client).ImageRequest(new ImageRequest
        {
            Model = "minimax/image-01",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["minimax"] = ParseElement("""{"prompt_optimizer":null}""")
            }
        });

        Assert.NotNull(upstream);
        Assert.Equal("image-01", upstream.Value.GetProperty("model").GetString());
        Assert.Equal("A landscape", upstream.Value.GetProperty("prompt").GetString());
        Assert.Equal("base64", upstream.Value.GetProperty("response_format").GetString());
        Assert.False(upstream.Value.TryGetProperty("prompt_optimizer", out _));
        Assert.Equal(new[] { "model", "prompt", "response_format" }.Order(),
            upstream.Value.EnumerateObject().Select(property => property.Name).Order());

        var requestsSent = 0;
        using var invalidClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        foreach (var invalidOptions in new[] { """{"prompt_optimizer":"false"}""", "[]" })
        {
            await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).ImageRequest(new ImageRequest
            {
                Model = "minimax/image-01",
                Prompt = "A landscape",
                ProviderOptions = new() { ["minimax"] = ParseElement(invalidOptions) }
            }));
        }

        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"upstream error"}""", Encoding.UTF8, "application/json")
        });
        var error = await Assert.ThrowsAsync<Exception>(() => CreateProvider(failedClient).ImageRequest(new ImageRequest
        {
            Model = "minimax/image-01",
            Prompt = "A landscape"
        }));
        Assert.Equal("BadRequest: {\"message\":\"upstream error\"}", error.Message);
    }

    private static MiniMaxProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
