using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AIHappey.Providers.Tests;

public sealed class OpenAIImageNativeContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("url")]
    public async Task Native_generation_sends_official_fields_without_injecting_or_mutating_response_format(string? responseFormat)
    {
        var options = new OpenAIImageGenerationRequest
        {
            Model = "gpt-image-2.6-flare", Prompt = "Animate a tree", OutputFormat = "gif",
            Complexity = "high", FrameCount = 12, FrameDurationMs = 2500, SafetyIdentifier = "safe-user",
            ResponseFormat = responseFormat,
            AdditionalProperties = new() { ["future_option"] = JsonSerializer.SerializeToElement(new { enabled = true }) }
        };
        var before = JsonSerializer.SerializeToElement(options);
        using var client = CreateClient(http =>
        {
            Assert.Equal("/v1/images/generations", http.RequestUri!.AbsolutePath);
            var body = JsonSerializer.Deserialize<JsonElement>(http.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.True(JsonElement.DeepEquals(before, body));
            Assert.Equal(responseFormat is not null, body.TryGetProperty("response_format", out _));
            return JsonResponse("""{"created":123,"output_format":"gif","safety_identifier":"safe-user","data":[{"b64_json":"AQID"}],"svg_usage":{"model":"author","input_tokens":10,"output_tokens":20,"reasoning_tokens":3,"total_tokens":30}}""");
        });

        var response = await client.OpenAICompatibleImageGenerationRequestAsync(options);

        Assert.Equal("gif", response.OutputFormat);
        Assert.Equal("safe-user", response.SafetyIdentifier);
        Assert.Equal("author", response.SvgUsage?.Model);
        Assert.Equal(30, response.SvgUsage?.TotalTokens);
        Assert.True(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(options)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_edit_JSON_and_multipart_serialize_the_same_official_fields_and_extension_data(bool multipart)
    {
        var options = EditOptions();
        if (multipart)
            options.ImageFiles = [FormImage()];
        else
            options.Images = [new() { FileId = "file-source" }];
        var before = JsonSerializer.SerializeToElement(options);
        using var client = CreateClient(http =>
        {
            Assert.Equal("/v1/images/edits", http.RequestUri!.AbsolutePath);
            if (multipart)
            {
                var parts = Assert.IsType<MultipartFormDataContent>(http.Content);
                var fields = parts.Where(part => part.Headers.ContentDisposition?.FileName is null)
                    .ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'),
                        part => part.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal(before.EnumerateObject().Count(), fields.Count);
                foreach (var field in before.EnumerateObject())
                    Assert.Equal(field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText(), fields[field.Name]);
                var image = Assert.Single(parts, part => part.Headers.ContentDisposition?.FileName is not null);
                Assert.Equal("image[]", image.Headers.ContentDisposition!.Name!.Trim('"'));
                Assert.Equal(new byte[] { 1, 2, 3 }, image.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            }
            else
            {
                var body = JsonSerializer.Deserialize<JsonElement>(http.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.True(JsonElement.DeepEquals(before, body));
            }
            return JsonResponse("""{"created":123,"output_format":"svg","data":[{"b64_json":"AQID"}]}""");
        });

        var response = await client.OpenAICompatibleImageEditRequestAsync(options);

        Assert.Equal("svg", response.OutputFormat);
        Assert.True(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(options)));
    }

    [Fact]
    public void Multipart_parser_reads_new_official_edit_fields_into_native_models()
    {
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["model"] = "gpt-image-2.6-flare", ["prompt"] = "Animate a tree", ["output_format"] = "gif",
            ["complexity"] = "medium", ["frame_count"] = "12", ["frame_duration_ms"] = "2500",
            ["safety_identifier"] = "safe-user", ["svg"] = """{"model":"author","reasoning":{"effort":"max","future":true}}""",
            ["markup"] = """{"file_id":"file-markup"}"""
        }, new FormFileCollection { FormImage() });

        var options = form.ToOpenAIImageEditRequest();

        Assert.Equal("gif", options.OutputFormat);
        Assert.Equal("medium", options.Complexity);
        Assert.Equal(12, options.FrameCount);
        Assert.Equal(2500, options.FrameDurationMs);
        Assert.Equal("safe-user", options.SafetyIdentifier);
        Assert.Equal("author", options.Svg?.Model);
        Assert.Equal("max", options.Svg?.Reasoning?.Effort);
        Assert.True(options.Svg?.Reasoning?.AdditionalProperties?["future"].GetBoolean());
        Assert.Equal("file-markup", options.Markup?.FileId);
        Assert.Single(options.ImageFiles!);
    }

    [Fact]
    public async Task Streaming_transport_changes_only_wire_stream_flag_without_mutating_the_native_request()
    {
        var options = new OpenAIImageGenerationRequest
        {
            Model = "gpt-image-2.6-flare", Prompt = "A tree", OutputFormat = "png", Stream = false
        };
        using var client = CreateClient(http =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(http.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.True(body.GetProperty("stream").GetBoolean());
            Assert.False(body.TryGetProperty("response_format", out _));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("event: image_generation.completed\ndata: {\"type\":\"image_generation.completed\",\"b64_json\":\"AQID\",\"created_at\":123}\n\n")
            };
        });
        var events = new List<IOpenAIImageStreamEvent>();
        await foreach (var item in client.OpenAICompatibleImageGenerationStreamingAsync(options))
            events.Add(item);

        Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events));
        Assert.False(options.Stream);
        Assert.Null(options.ResponseFormat);
    }

    [Fact]
    public async Task Native_to_generic_adapter_preserves_official_options_without_a_second_options_contract()
    {
        var native = EditOptions();
        native.Images = [new() { ImageUrl = "data:image/png;base64,AQID" }];
        native.Mask = new() { ImageUrl = "data:image/png;base64,AQID" };
        native.N = 2;
        native.Size = "1536x864";
        var wire = JsonSerializer.SerializeToElement(native);

        var generic = await native.ToImageRequest(native.Model, "openai");

        Assert.Equal(native.Model, generic.Model);
        Assert.Equal(native.Prompt, generic.Prompt);
        Assert.Equal(native.Size, generic.Size);
        Assert.Equal(2, generic.N);
        Assert.Single(generic.Files!);
        Assert.NotNull(generic.Mask);
        var options = generic.ProviderOptions!["openai"];
        foreach (var field in wire.EnumerateObject())
        {
            if (field.Name is "model" or "prompt" or "size" or "n" or "images" or "mask")
                Assert.False(options.TryGetProperty(field.Name, out _));
            else
                Assert.True(JsonElement.DeepEquals(field.Value, options.GetProperty(field.Name)), field.Name);
        }
    }

    private static OpenAIImageEditRequest EditOptions()
        => new()
        {
            Model = "gpt-image-2.6-flare", Prompt = "Edit a tree", OutputFormat = "svg", Complexity = "high",
            FrameCount = 12, FrameDurationMs = 2500, OutputCompression = 0, SafetyIdentifier = "safe-user",
            Markup = new() { ImageUrl = "data:image/png;base64,AQID" },
            Svg = new()
            {
                Model = "author", Reasoning = new() { Effort = "max" },
                AdditionalProperties = new() { ["future"] = JsonSerializer.SerializeToElement(true) }
            },
            AdditionalProperties = new() { ["future_option"] = JsonSerializer.SerializeToElement(new { enabled = true }) }
        };

    private static IFormFile FormImage()
        => new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "image", "image.png")
        {
            Headers = new HeaderDictionary(), ContentType = "image/png"
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder)) { BaseAddress = new Uri("https://api.openai.com/") };

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
