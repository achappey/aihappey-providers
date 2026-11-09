using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Segmind;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests;

public sealed class SegmindImageTests
{
    [Theory]
    [InlineData("fast-flux-schnell", "image/jpeg")]
    [InlineData("segmind/fast-flux-schnell", "image/png")]
    [InlineData("segmind/z-image-turbo", "image/webp")]
    public async Task Generation_uses_documented_endpoint_authentication_and_binary_response(string model, string mediaType)
    {
        using var client = CreateClient(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://api.segmind.com/v1/{model.Replace("segmind/", "")}", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-api-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/json", request.Content!.Headers.ContentType?.MediaType);
            var payload = Parse(await request.Content.ReadAsStringAsync());
            Assert.Equal("""{"prompt":"A skyline"}""", payload.GetRawText());
            var response = BinaryResponse(mediaType);
            response.Headers.Add("x-seed-value", "42873396");
            return response;
        });
        var result = await CreateProvider(client).ImageRequest(new ImageRequest { Model = model, Prompt = "A skyline" });

        Assert.Equal($"data:{mediaType};base64,AQID", Assert.Single(result.Images!));
        Assert.Equal("segmind/" + model.Replace("segmind/", ""), result.Response.ModelId);
        Assert.Equal("42873396", result.Response.Headers!["x-seed-value"]);
        Assert.Empty(result.Warnings);
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Native_metadata_is_flat_unmodified_and_overrides_optional_generic_fields()
    {
        var metadata = Parse("""
            {
              "steps": 4,
              "seed": 8475609304,
              "aspect_ratio": "16:9",
              "prompt": "metadata prompt",
              "image": "https://example.test/reference.png",
              "future_object": {"nested":[1,true,null,{"a":"b"}]},
              "future_array": [false,3.2],
              "future_number": 12345678901234567890.123456789,
              "future_null": null,
              "base64": false
            }
            """);
        using var client = CreateClient(async (http, _) =>
        {
            var payload = Parse(await http.Content!.ReadAsStringAsync());
            Assert.Equal("A skyline", payload.GetProperty("prompt").GetString());
            foreach (var property in metadata.EnumerateObject().Where(p => p.Name != "prompt"))
                Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
            Assert.Equal(metadata.EnumerateObject().Count(), payload.EnumerateObject().Count());
            return BinaryResponse();
        });
        var request = new ImageRequest
        {
            Model = "segmind/fast-flux-schnell",
            Prompt = "A skyline",
            Seed = 42,
            AspectRatio = "1:1",
            ProviderOptions = new()
            {
                ["segmind"] = metadata,
                ["other"] = Parse("""{"secret":"not forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateProvider(client).ImageRequest(request);

        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Generic_seed_and_aspect_ratio_are_forwarded_without_model_specific_size_defaults()
    {
        using var client = CreateClient(async (http, _) =>
        {
            var payload = Parse(await http.Content!.ReadAsStringAsync());
            Assert.Equal(42, payload.GetProperty("seed").GetInt32());
            Assert.Equal("3:2", payload.GetProperty("aspect_ratio").GetString());
            Assert.Equal(3, payload.EnumerateObject().Count());
            return BinaryResponse();
        });

        var response = await CreateProvider(client).ImageRequest(new ImageRequest
        {
            Model = "fast-flux-schnell", Prompt = "A skyline", Seed = 42, AspectRatio = "3:2", Size = "1024x1024", N = 2
        });

        var warnings = JsonSerializer.SerializeToElement(response.Warnings);
        Assert.Equal(new[] { "n", "size" }, warnings.EnumerateArray().Select(w => w.GetProperty("feature").GetString()));
        Assert.Single(response.Images!);
    }

    [Fact]
    public async Task OpenAI_generation_preserves_extension_data_and_actual_output_format()
    {
        using var client = CreateClient(async (http, token) =>
        {
            var payload = Parse(await http.Content!.ReadAsStringAsync());
            Assert.Equal(4, payload.GetProperty("steps").GetInt32());
            Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
            Assert.False(payload.TryGetProperty("model", out _));
            return BinaryResponse("image/webp");
        });
        var options = JsonSerializer.Deserialize<OpenAIImageGenerationRequest>("""
            {"model":"segmind/fast-flux-schnell","prompt":"A skyline","steps":4,"aspect_ratio":"16:9","future_null":null}
            """)!;
        var before = JsonSerializer.Serialize(options);

        var response = await CreateProvider(client).OpenAIImageGenerationRequestAsync(options);

        Assert.Equal("AQID", Assert.Single(response.Data!).B64Json);
        Assert.Equal("webp", response.OutputFormat);
        Assert.Equal(before, JsonSerializer.Serialize(options));
    }

    [Fact]
    public async Task OpenAI_streaming_emits_only_a_completed_image_with_the_actual_media_type()
    {
        using var client = CreateClient((_, _) => Task.FromResult(BinaryResponse("image/jpeg")));
        var events = new List<IOpenAIImageStreamEvent>();

        await foreach (var item in CreateProvider(client).OpenAIImageGenerationStreamingAsync(
            new OpenAIImageGenerationRequest { Model = "fast-flux-schnell", Prompt = "A skyline", Stream = true }))
            events.Add(item);

        var completed = Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events));
        Assert.Equal("AQID", completed.B64Json);
        Assert.Equal("jpeg", completed.OutputFormat);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_generation_and_streaming_preserve_native_metadata_and_image_type(bool stream)
    {
        var requestCount = 0;
        using var client = CreateClient(async (http, token) =>
        {
            requestCount++;
            var payload = Parse(await http.Content!.ReadAsStringAsync());
            Assert.True(payload.TryGetProperty("steps", out var steps), payload.GetRawText());
            Assert.Equal(4, steps.GetInt32());
            Assert.Equal(8475609304, payload.GetProperty("seed").GetInt64());
            Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
            Assert.True(JsonElement.DeepEquals(Parse("""{"nested":[false,null]}"""), payload.GetProperty("future_object")));
            Assert.False(payload.TryGetProperty("segmind", out _));
            Assert.False(payload.TryGetProperty("other", out _));
            Assert.False(payload.TryGetProperty("secret", out _));
            return BinaryResponse("image/jpeg");
        });
        var provider = CreateProvider(client);
        var request = new AIRequest
        {
            ProviderId = "segmind",
            Model = "segmind/fast-flux-schnell",
            Input = new AIInput
            {
                Items = [new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "A skyline" }] }]
            },
            Metadata = new()
            {
                ["segmind"] = Parse("""
                    {"steps":4,"seed":8475609304,"aspect_ratio":"16:9","future_null":null,"future_object":{"nested":[false,null]}}
                    """),
                ["other"] = Parse("""{"secret":"not forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        if (stream)
        {
            var events = new List<AIStreamEvent>();
            await foreach (var item in provider.StreamUnifiedAsync(request))
                events.Add(item);
            var error = events.Select(e => e.Event.Data).OfType<AIToolOutputErrorEventData>().FirstOrDefault();
            Assert.True(error is null, error?.ErrorText);
            Assert.Equal("finish", events.Last().Event.Type);
            var completed = events.Select(e => e.Event.Data).OfType<AIToolOutputAvailableEventData>().Last();
            var output = Assert.IsType<CallToolResult>(completed.Output);
            Assert.Equal("image/jpeg", Assert.IsType<ImageContentBlock>(Assert.Single(output.Content)).MimeType);
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(response.Output!.Items!).Content!));
            var output = Assert.IsType<CallToolResult>(tool.Output);
            Assert.Equal("image/jpeg", Assert.IsType<ImageContentBlock>(Assert.Single(output.Content)).MimeType);
        }

        Assert.Equal(1, requestCount);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData(400, """{"error":"missing steps"}""")]
    [InlineData(401, """{"error":"invalid API key"}""")]
    [InlineData(405, "<html>Method Not Allowed</html>")]
    [InlineData(429, """{"error":"rate limited"}""")]
    public async Task Upstream_errors_include_status_and_original_body(int status, string body)
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body)
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(client).ImageRequest(new ImageRequest { Model = "fast-flux-schnell", Prompt = "A skyline" }));

        Assert.Contains(status.ToString(), error.Message);
        Assert.Contains(body, error.Message);
    }

    [Theory]
    [InlineData("application/json", 3)]
    [InlineData("text/html", 3)]
    [InlineData("image/png", 0)]
    public async Task Invalid_success_responses_are_not_returned_as_images(string mediaType, int length)
    {
        using var client = CreateClient((_, _) => Task.FromResult(BinaryResponse(mediaType, new byte[length])));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(client).ImageRequest(new ImageRequest { Model = "fast-flux-schnell", Prompt = "A skyline" }));
    }

    [Fact]
    public async Task Missing_credentials_fail_before_sending()
    {
        using var client = CreateClient((_, _) => throw new InvalidOperationException("Request should not be sent."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SegmindProvider(new KeyResolver(null), new ClientFactory(client))
                .ImageRequest(new ImageRequest { Model = "fast-flux-schnell", Prompt = "A skyline" }));

        Assert.Contains("API key", error.Message);
    }

    [Fact]
    public async Task Cancellation_reaches_the_provider_request()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = CreateClient(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return BinaryResponse();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider(client).ImageRequest(
                new ImageRequest { Model = "fast-flux-schnell", Prompt = "A skyline" }, cancellation.Token));
    }

    [Fact]
    public async Task Model_specific_image_inputs_are_not_silently_discarded()
    {
        using var client = CreateClient((_, _) => throw new InvalidOperationException("Request should not be sent."));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "stable-diffusion-img2img", Prompt = "A skyline", Files = [new ImageFile { Data = "AQID", MediaType = "image/png" }]
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIImageEditRequestAsync(new OpenAIImageEditRequest()));
        Assert.Throws<NotSupportedException>(() => provider.OpenAIImageEditStreamingAsync(new OpenAIImageEditRequest()));
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage BinaryResponse(string mediaType = "image/png", byte[]? bytes = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? [1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private static SegmindProvider CreateProvider(HttpClient client)
        => new(new KeyResolver("test-api-key"), new ClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new Handler(responder));

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
