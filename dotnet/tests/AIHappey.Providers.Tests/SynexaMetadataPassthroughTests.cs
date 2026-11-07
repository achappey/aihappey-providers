using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Synexa;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class SynexaMetadataPassthroughTests
{
    [Fact]
    public async Task Image_metadata_keeps_vercel_namespace_and_forwards_unknown_json_with_request_precedence()
    {
        var payloads = new List<JsonElement>();
        var waitTimeouts = new List<JsonElement>();
        using var client = CreateClient(message =>
        {
            var body = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            if (message.RequestUri!.AbsolutePath == "/v1/predictions")
            {
                payloads.Add(body);
                return PredictionResponse("starting");
            }

            Assert.Equal("/v1/predictions/prediction-id/wait", message.RequestUri.AbsolutePath);
            waitTimeouts.Add(body);
            return PredictionResponse("succeeded", """["data:image/png;base64,AQID"]""");
        });

        var metadata = ParseElement("""
            {
              "prompt": "metadata prompt",
              "size": "square",
              "future_object": {"nested": [{"value": true}, [null, 1]]},
              "future_array": [1, {"value": "kept"}],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "wait": {"type": "block"}
            }
            """);
        var request = new ImageRequest
        {
            Model = "synexa/image-model",
            Prompt = "request prompt",
            ProviderOptions = new()
            {
                ["synexa"] = metadata,
                ["another-provider"] = ParseElement("""{"not-forwarded":true}""")
            }
        };

        var clientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(metadata,
            clientRequest.GetProperty("providerOptions").GetProperty("synexa")));

        var response = await CreateProvider(client).ImageRequest(request, CancellationToken.None);

        Assert.NotNull(response.Images);
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images));
        var createPayload = Assert.Single(payloads);
        var input = createPayload.GetProperty("input");
        Assert.Equal("image-model", createPayload.GetProperty("model").GetString());
        Assert.Equal("request prompt", input.GetProperty("prompt").GetString());
        Assert.Equal("square", input.GetProperty("size").GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), input.GetProperty(name)), name);
        Assert.Equal(JsonValueKind.Null, input.GetProperty("future_null").ValueKind);
        Assert.False(input.TryGetProperty("wait", out _));
        Assert.Equal(new[]
            {
                "prompt", "size", "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
            }.Order(),
            input.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(120, Assert.Single(waitTimeouts).GetProperty("timeout").GetInt32());
        Assert.Equal(new[] { "model", "input" }.Order(),
            createPayload.EnumerateObject().Select(property => property.Name).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Image_metadata_fills_omitted_optional_fields_but_explicit_request_fields_win(bool supplyRequestFields)
    {
        JsonElement input = default;
        using var client = CreateClient(message =>
        {
            if (message.RequestUri!.AbsolutePath == "/v1/predictions")
            {
                var body = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                input = body.GetProperty("input").Clone();
                return PredictionResponse("starting");
            }

            Assert.Equal("/v1/predictions/prediction-id/wait", message.RequestUri.AbsolutePath);
            return PredictionResponse("succeeded", """["data:image/png;base64,AQID"]""");
        });
        var request = new ImageRequest
        {
            Model = "image-model",
            Prompt = "request prompt",
            ProviderOptions = new()
            {
                ["synexa"] = ParseElement("""
                    {
                      "prompt": "metadata prompt",
                      "size": "square",
                      "aspect_ratio": "1:1",
                      "seed": 42,
                      "image": "data:image/png;base64,BwgJ",
                      "mask": "data:image/png;base64,CgsM",
                      "wait": {"type": "block"}
                    }
                    """)
            }
        };
        if (supplyRequestFields)
        {
            request.Size = "1024x1024";
            request.AspectRatio = "16:9";
            request.Seed = 0;
            request.Files = [new ImageFile { MediaType = "image/png", Data = "AQID" }];
            request.Mask = new() { MediaType = "image/png", Data = "BAUG" };
        }

        var response = await CreateProvider(client).ImageRequest(request, CancellationToken.None);

        Assert.Equal("request prompt", input.GetProperty("prompt").GetString());
        Assert.Equal(supplyRequestFields ? "1024x1024" : "square", input.GetProperty("size").GetString());
        Assert.Equal(supplyRequestFields ? "16:9" : "1:1", input.GetProperty("aspect_ratio").GetString());
        Assert.Equal(supplyRequestFields ? 0 : 42, input.GetProperty("seed").GetInt32());
        Assert.Equal(supplyRequestFields ? "data:image/png;base64,AQID" : "data:image/png;base64,BwgJ",
            input.GetProperty("image").GetString());
        Assert.Equal(supplyRequestFields ? "data:image/png;base64,BAUG" : "data:image/png;base64,CgsM",
            input.GetProperty("mask").GetString());
        Assert.False(input.TryGetProperty("wait", out _));
        Assert.NotNull(response.Images);
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images));
    }

    [Fact]
    public async Task Wait_options_map_clamped_timeout_and_invalid_wait_values_keep_json_errors()
    {
        JsonElement waitPayload = default;
        using var client = CreateClient(message =>
        {
            if (message.RequestUri!.AbsolutePath == "/v1/predictions")
                return PredictionResponse("starting");

            waitPayload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return PredictionResponse("succeeded", """["data:image/png;base64,AQID"]""");
        });
        var provider = CreateProvider(client);
        var request = new ImageRequest
        {
            Model = "image-model",
            Prompt = "request",
            ProviderOptions = new()
            {
                ["synexa"] = ParseElement("""{"wait":{"type":"block","timeoutSeconds":900}}""")
            }
        };

        await provider.ImageRequest(request, CancellationToken.None);
        Assert.Equal(600, waitPayload.GetProperty("timeout").GetInt32());

        request.ProviderOptions["synexa"] = ParseElement("""{"wait":{"timeoutSeconds":"invalid"}}""");
        await Assert.ThrowsAsync<JsonException>(() => provider.ImageRequest(request, CancellationToken.None));
    }

    [Fact]
    public async Task Prediction_errors_keep_existing_exception_behavior()
    {
        using var client = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(client)
            .ImageRequest(new ImageRequest
            {
                Model = "image-model",
                Prompt = "request",
                ProviderOptions = new()
                {
                    ["synexa"] = ParseElement("""{"future_parameter":{"value":true}}""")
                }
            }, CancellationToken.None));

        Assert.Equal("Synexa prediction create failed (400): upstream failure", error.Message);
    }

    private static SynexaProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static HttpResponseMessage PredictionResponse(string status, string output = "[]")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"id":"prediction-id","model":"image-model","status":"{{status}}","output":{{output}}}""",
                Encoding.UTF8,
                "application/json")
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
