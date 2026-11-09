using System.Net;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Providers.Tests;

public sealed class OpenAIImageOutputFormatTests
{
    private static readonly string[] Routes = ["generation", "inline-edit", "file-id-edit", "remote-edit"];

    public static IEnumerable<object[]> Formats()
    {
        foreach (var route in Routes)
        foreach (var (format, mime) in new[]
        {
            ("png", "image/png"), ("jpeg", "image/jpeg"), ("webp", "image/webp"),
            ("gif", "image/gif"), ("svg", "image/svg+xml")
        })
        foreach (var includeResponseFormat in new[] { true, false })
            yield return [route, format, mime, includeResponseFormat];
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Selected_format_reaches_OpenAI_and_labels_the_returned_bytes(
        string route, string format, string mime, bool includeResponseFormat)
    {
        using var client = CreateClient(http =>
        {
            var payload = ReadPayload(http, route);
            Assert.Equal(format, payload.GetProperty("output_format").GetString());
            Assert.False(payload.TryGetProperty("response_format", out _));
            Assert.False(payload.TryGetProperty("stream", out _));
            return ImageResponse(route, includeResponseFormat ? format : null);
        });

        var result = await CreateProvider(client).ImageRequest(Request(route,
            JsonSerializer.SerializeToElement(new { output_format = format })));

        Assert.Equal($"data:{mime};base64,AQID", Assert.Single(result.Images));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("inline-edit")]
    [InlineData("file-id-edit")]
    [InlineData("remote-edit")]
    public async Task Related_options_pass_through_even_when_the_combination_is_incompatible(string route)
    {
        var metadata = JsonSerializer.SerializeToElement(new
        {
            output_format = "gif", complexity = "high", frame_count = 12, frame_duration_ms = 2500,
            output_compression = 0, background = "transparent", moderation = "low", quality = "max",
            input_fidelity = "high",
            svg = new { model = "svg-author", reasoning = new { effort = "max", future = true }, future = new[] { 1, 2 } },
            markup = new { image_url = "data:image/png;base64,AQID" }
        });
        using var client = CreateClient(http =>
        {
            var payload = ReadPayload(http, route);
            foreach (var field in new[]
            {
                "output_format", "complexity", "frame_count", "frame_duration_ms", "output_compression",
                "background", "moderation", "quality", "svg"
            })
                Assert.True(JsonElement.DeepEquals(metadata.GetProperty(field), payload.GetProperty(field)), field);

            if (route != "generation")
            {
                Assert.Equal("high", payload.GetProperty("input_fidelity").GetString());
                Assert.True(JsonElement.DeepEquals(metadata.GetProperty("markup"), payload.GetProperty("markup")));
            }
            return ImageResponse(route, "gif");
        });
        var request = Request(route, metadata);
        var result = await CreateProvider(client).ImageRequest(request);

        Assert.Equal("data:image/gif;base64,AQID", Assert.Single(result.Images));
        Assert.Empty(result.Warnings ?? []);
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions!["openai"]));
    }

    [Fact]
    public async Task Unknown_provider_options_merge_into_the_OpenAI_request_root_without_filtering()
    {
        using var expected = JsonDocument.Parse(
            """{"future_object":{"enabled":true,"nested":[1,null,{"label":"keep"}]},"future_array":[false,2,"three"],"future_count":7,"future_null":null}""");
        using var client = CreateClient(http =>
        {
            var payload = ReadPayload(http, "generation");
            foreach (var property in expected.RootElement.EnumerateObject())
                Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
            Assert.False(payload.TryGetProperty("providerOptions", out _));
            return ImageResponse("generation", null);
        });

        var result = await CreateProvider(client).ImageRequest(Request("generation", expected.RootElement.Clone()));

        Assert.Equal("data:image/png;base64,AQID", Assert.Single(result.Images));
    }

    public static IEnumerable<object?[]> Defaults()
    {
        foreach (var route in Routes)
        foreach (var metadata in new string?[]
        {
            null, "{}",
            """{"output_format":null,"complexity":null,"frame_count":null,"frame_duration_ms":null,"output_compression":null,"svg":null}"""
        })
            yield return [route, metadata];
    }

    [Theory]
    [MemberData(nameof(Defaults))]
    public async Task Unspecified_options_keep_PNG_default_without_inventing_optional_values(string route, string? metadataJson)
    {
        using var client = CreateClient(http =>
        {
            var payload = ReadPayload(http, route);
            Assert.Equal("png", payload.GetProperty("output_format").GetString());
            foreach (var field in new[] { "complexity", "frame_count", "frame_duration_ms", "output_compression", "svg", "markup" })
                Assert.False(payload.TryGetProperty(field, out _), field);
            return ImageResponse(route, null);
        });
        var metadata = metadataJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(metadataJson);
        var result = await CreateProvider(client).ImageRequest(Request(route, metadata));

        Assert.Equal("data:image/png;base64,AQID", Assert.Single(result.Images));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("inline-edit")]
    [InlineData("file-id-edit")]
    [InlineData("remote-edit")]
    public async Task Explicit_response_format_takes_precedence_over_requested_format(string route)
    {
        using var client = CreateClient(http =>
        {
            Assert.Equal("gif", ReadPayload(http, route).GetProperty("output_format").GetString());
            return ImageResponse(route, "jpeg");
        });
        var result = await CreateProvider(client).ImageRequest(Request(route,
            JsonSerializer.SerializeToElement(new { output_format = "gif" })));

        Assert.Equal("data:image/jpeg;base64,AQID", Assert.Single(result.Images));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("inline-edit")]
    [InlineData("file-id-edit")]
    [InlineData("remote-edit")]
    public async Task Generic_request_fields_override_native_options_and_safety_identifier_stays_in_the_correct_contract(string route)
    {
        var metadata = JsonSerializer.SerializeToElement(new
        {
            model = "ignored-model", prompt = "ignored-prompt", n = 8, size = "ignored-size",
            output_format = "svg", safety_identifier = "safe-user"
        });
        using var client = CreateClient(http =>
        {
            var payload = ReadPayload(http, route);
            Assert.Equal("1536x864", payload.GetProperty("size").GetString());
            if (route == "remote-edit")
            {
                var root = JsonSerializer.Deserialize<JsonElement>(http.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal("safe-user", root.GetProperty("safety_identifier").GetString());
                Assert.False(payload.TryGetProperty("safety_identifier", out _));
                Assert.False(payload.TryGetProperty("n", out _));
            }
            else
            {
                Assert.Equal("A tree moving in the wind", payload.GetProperty("prompt").GetString());
                Assert.Equal(1, payload.GetProperty("n").GetInt32());
                Assert.Equal("safe-user", payload.GetProperty("safety_identifier").GetString());
            }
            return ImageResponse(route, "svg");
        });
        var request = Request(route, metadata);
        request.Size = "1536x864";
        var result = await CreateProvider(client).ImageRequest(request);

        Assert.Equal("data:image/svg+xml;base64,AQID", Assert.Single(result.Images));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions!["openai"]));
    }

    [Theory]
    [InlineData("generation", "data:image/svg+xml;base64,AQID")]
    [InlineData("inline-edit", "data:image/gif;base64,AQID")]
    [InlineData("remote-edit", "data:image/webp;base64,AQID")]
    [InlineData("generation", "https://example.test/output.gif")]
    [InlineData("remote-edit", "https://example.test/output.svg")]
    public async Task Already_qualified_outputs_are_not_rewritten(string route, string image)
    {
        using var client = CreateClient(_ => ImageResponse(route, "png", image));
        var result = await CreateProvider(client).ImageRequest(Request(route));

        Assert.Equal(image, Assert.Single(result.Images));
    }

    [Fact]
    public async Task Legacy_generation_keeps_base64_response_format_without_GPT_output_options()
    {
        using var client = CreateClient(http =>
        {
            var payload = ReadPayload(http, "generation", "dall-e-3");
            Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
            foreach (var field in new[] { "output_format", "complexity", "frame_count", "frame_duration_ms", "output_compression", "svg" })
                Assert.False(payload.TryGetProperty(field, out _), field);
            return ImageResponse("generation", null);
        });
        var request = Request("generation", JsonSerializer.SerializeToElement(new { output_format = "gif", frame_count = 12 }));
        request.Model = "dall-e-3";
        var result = await CreateProvider(client).ImageRequest(request);

        Assert.Equal("data:image/png;base64,AQID", Assert.Single(result.Images));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("inline-edit")]
    [InlineData("file-id-edit")]
    [InlineData("remote-edit")]
    public async Task Incompatible_settings_reach_OpenAI_and_upstream_validation_errors_are_surfaced(string route)
    {
        var sent = false;
        using var client = CreateClient(http =>
        {
            var payload = ReadPayload(http, route);
            Assert.Equal("gif", payload.GetProperty("output_format").GetString());
            Assert.Equal(17, payload.GetProperty("frame_count").GetInt32());
            sent = true;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"GIF preview requires frame_count between 2 and 16"}}""")
            };
        });

        var error = await Assert.ThrowsAnyAsync<Exception>(() => CreateProvider(client).ImageRequest(Request(route,
            JsonSerializer.SerializeToElement(new { output_format = "gif", frame_count = 17 }))));

        Assert.True(sent);
        Assert.Contains("GIF preview", error.Message);
    }

    private static ImageRequest Request(string route, JsonElement? metadata = null)
        => new()
        {
            Model = "gpt-image-2.6-flare",
            Prompt = "A tree moving in the wind",
            N = 1,
            Files = route switch
            {
                "inline-edit" => [new ImageFile { MediaType = "image/png", Data = "AQID" }],
                "file-id-edit" => [new ImageFile { Type = "file_id", Data = "file-source" }],
                "remote-edit" => [new ImageFileUrl { Url = "https://example.test/source.png" }],
                _ => null
            },
            ProviderOptions = metadata is { } value ? new() { ["openai"] = value } : null
        };

    private static JsonElement ReadPayload(HttpRequestMessage http, string route, string model = "gpt-image-2.6-flare")
    {
        Assert.Equal(HttpMethod.Post, http.Method);
        Assert.Equal(route switch
        {
            "generation" => "/v1/images/generations",
            "remote-edit" => "/v1/responses",
            _ => "/v1/images/edits"
        }, http.RequestUri?.AbsolutePath);
        using var document = JsonDocument.Parse(http.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = document.RootElement;
        var payload = route == "remote-edit" ? Assert.Single(root.GetProperty("tools").EnumerateArray()) : root;
        Assert.Equal(model, payload.GetProperty("model").GetString());
        if (route == "file-id-edit")
            Assert.Equal("file-source", Assert.Single(payload.GetProperty("images").EnumerateArray()).GetProperty("file_id").GetString());
        return payload.Clone();
    }

    private static HttpResponseMessage ImageResponse(string route, string? format, string result = "AQID")
    {
        var image = route == "remote-edit"
            ? new Dictionary<string, object?> { ["id"] = "ig-test", ["type"] = "image_generation_call", ["status"] = "completed", ["result"] = result }
            : new Dictionary<string, object?> { [result.StartsWith("http", StringComparison.Ordinal) ? "url" : "b64_json"] = result };
        var body = route == "remote-edit"
            ? new Dictionary<string, object?> { ["id"] = "resp-test", ["object"] = "response", ["status"] = "completed", ["model"] = "gpt-6-luna", ["output"] = new[] { image } }
            : new Dictionary<string, object?> { ["created"] = 1748372400, ["data"] = new[] { image } };
        if (format is not null)
            (route == "remote-edit" ? image : body)["output_format"] = format;
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }

    private static OpenAIProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new NullEndUserIdResolver());

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class NullEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
