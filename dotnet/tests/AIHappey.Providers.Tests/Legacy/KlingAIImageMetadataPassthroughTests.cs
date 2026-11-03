using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.KlingAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class KlingAIImageMetadataPassthroughTests
{
    [Fact]
    public async Task KlingAI_image_options_keep_known_mapping_and_forward_unknown_json_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/images/generations", request.RequestUri!.AbsolutePath);
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"code":0,"data":{"task_id":"task-1"}}""");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/task-1", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"code":0,"data":{"task_status":"succeed","task_result":{"images":[{"url":"https://kling.test/image"}]}}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var metadata = ParseElement("""
            {
              "negative_prompt": "no text",
              "resolution": "2k",
              "image_reference": "subject",
              "image_fidelity": 0.8,
              "human_fidelity": 0.7,
              "watermark_info": {"enabled": false},
              "callback_url": "https://client.test/callback",
              "external_task_id": "client-task",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "prompt": "metadata prompt",
              "model_name": "metadata model",
              "n": 99,
              "aspect_ratio": "9:16"
            }
            """);
        var request = new ImageRequest
        {
            Model = "klingai/kling-v2",
            Prompt = "request prompt",
            N = 1,
            AspectRatio = "16:9",
            ProviderOptions = new()
            {
                ["klingai"] = metadata,
                ["other-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var serializedRequest = JsonSerializer.Serialize(request);

        await CreateProvider(client).ImageRequest(request);

        Assert.Equal("request prompt", payload.GetProperty("prompt").GetString());
        Assert.Equal("kling-v2", payload.GetProperty("model_name").GetString());
        Assert.Equal(1, payload.GetProperty("n").GetInt32());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal("no text", payload.GetProperty("negative_prompt").GetString());
        Assert.Equal("2k", payload.GetProperty("resolution").GetString());
        Assert.Equal("subject", payload.GetProperty("image_reference").GetString());
        Assert.Equal(0.8f, payload.GetProperty("image_fidelity").GetSingle());
        Assert.Equal(0.7f, payload.GetProperty("human_fidelity").GetSingle());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("watermark_info"), payload.GetProperty("watermark_info")));
        Assert.Equal("https://client.test/callback", payload.GetProperty("callback_url").GetString());
        Assert.Equal("client-task", payload.GetProperty("external_task_id").GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("klingai", out _));
        Assert.False(payload.TryGetProperty("other-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(serializedRequest, JsonSerializer.Serialize(request));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions!["klingai"]));
    }

    [Fact]
    public async Task KlingAI_omni_options_preserve_null_omission_and_defaults()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"code":0,"data":{"task_id":"task-1"}}""");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/task-1", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"code":0,"data":{"task_status":"succeed","task_result":{"series_images":[{"url":"https://kling.test/image"}]}}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        });

        await CreateProvider(client).ImageRequest(new ImageRequest
        {
            Model = "klingai/kling-v3-omni",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["klingai"] = ParseElement(
                    """{"negative_prompt":null,"image_fidelity":null,"watermark_info":null,"result_type":"url","series_amount":2,"element_list":[{"element_id":"one","future":true}],"future_null":null}""")
            }
        });

        Assert.Equal("kling-v3-omni", payload.GetProperty("model_name").GetString());
        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.False(payload.TryGetProperty("negative_prompt", out _));
        Assert.False(payload.TryGetProperty("image_fidelity", out _));
        Assert.False(payload.TryGetProperty("image_reference", out _));
        Assert.False(payload.TryGetProperty("human_fidelity", out _));
        Assert.False(payload.TryGetProperty("watermark_info", out _));
        Assert.Equal("url", payload.GetProperty("result_type").GetString());
        Assert.Equal(2, payload.GetProperty("series_amount").GetInt32());
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""[{"element_id":"one","future":true}]"""), payload.GetProperty("element_list")));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var invalidKnownValue = new ImageRequest
        {
            Model = "klingai/kling-v2",
            Prompt = "A landscape",
            ProviderOptions = new() { ["klingai"] = ParseElement("""{"image_fidelity":"high"}""") }
        };
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).ImageRequest(invalidKnownValue));
        invalidKnownValue.ProviderOptions["klingai"] = ParseElement("""[]""");
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).ImageRequest(invalidKnownValue));
    }

    [Fact]
    public async Task KlingAI_upstream_errors_keep_the_existing_exception_message()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });

        var error = await Assert.ThrowsAsync<Exception>(() => CreateProvider(client).ImageRequest(new ImageRequest
        {
            Model = "klingai/kling-v2",
            Prompt = "A landscape"
        }));

        Assert.Equal("BadRequest: upstream error", error.Message);
    }

    private static KlingAIProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-ak:test-sk";
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
