using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.StabilityAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class StabilityAIMetadataPassthroughTests
{
    [Fact]
    public async Task Image_metadata_keeps_known_fields_and_forwards_unknown_multipart_values()
    {
        var metadata = ParseElement("""
            {
              "negative_prompt": "no text",
              "style_preset": "cinematic",
              "seed": 99,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "prompt": "metadata prompt",
              "model": "metadata model"
            }
            """);
        Dictionary<string, string> fields = [];
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v2beta/stable-image/generate/sd3", message.RequestUri!.AbsolutePath);
            foreach (var part in (MultipartFormDataContent)message.Content!)
                fields[part.Headers.ContentDisposition!.Name!.Trim('"')] = part.ReadAsStringAsync().GetAwaiter().GetResult();

            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var request = new ImageRequest
        {
            Model = "stabilityai/sd3.5-large",
            Prompt = "A landscape",
            Seed = 12,
            ProviderOptions = new()
            {
                ["stabilityai"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":true}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateProvider(client).ImageRequest(request);

        Assert.Equal("A landscape", fields["prompt"]);
        Assert.Equal("sd3.5-large", fields["model"]);
        Assert.Equal("png", fields["output_format"]);
        Assert.Equal("1:1", fields["aspect_ratio"]);
        Assert.Equal("text-to-image", fields["mode"]);
        Assert.Equal("12", fields["seed"]);
        Assert.Equal("no text", fields["negative_prompt"]);
        Assert.Equal("cinematic", fields["style_preset"]);
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_object"), ParseElement(fields["future_object"])));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_array"), ParseElement(fields["future_array"])));
        Assert.Equal("unchanged", fields["future_string"]);
        Assert.Equal("12345678901234567890.123456789", fields["future_number"]);
        Assert.Equal("false", fields["future_boolean"]);
        Assert.Equal("null", fields["future_null"]);
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("secret", fields.Keys);
        Assert.Equal(
            new[]
            {
                "prompt", "output_format", "aspect_ratio", "mode", "model", "seed",
                "negative_prompt", "style_preset", "future_object", "future_array",
                "future_string", "future_number", "future_boolean", "future_null"
            }.Order(),
            fields.Keys.Order());
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["stabilityai"]));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Speech_metadata_keeps_known_fields_and_request_overrides_and_forwards_unknown_values()
    {
        var metadata = ParseElement("""
            {
              "output_format": "wav",
              "duration": 12.5,
              "seed": 17,
              "steps": 31,
              "cfg_scale": 4.25,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "prompt": "metadata prompt",
              "model": "metadata model"
            }
            """);
        Dictionary<string, string> fields = [];
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v2beta/audio/stable-audio-2/text-to-audio", message.RequestUri!.AbsolutePath);
            foreach (var part in (MultipartFormDataContent)message.Content!)
                fields[part.Headers.ContentDisposition!.Name!.Trim('"')] = part.ReadAsStringAsync().GetAwaiter().GetResult();

            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });
        var request = new SpeechRequest
        {
            Model = "stable-audio-2",
            Text = "A calm melody",
            OutputFormat = "mp3",
            ProviderOptions = new()
            {
                ["stabilityai"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":true}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("A calm melody", fields["prompt"]);
        Assert.Equal("stable-audio-2", fields["model"]);
        Assert.Equal("mp3", fields["output_format"]);
        Assert.Equal("12.5", fields["duration"]);
        Assert.Equal("17", fields["seed"]);
        Assert.Equal("31", fields["steps"]);
        Assert.Equal("4.25", fields["cfg_scale"]);
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_object"), ParseElement(fields["future_object"])));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_array"), ParseElement(fields["future_array"])));
        Assert.Equal("unchanged", fields["future_string"]);
        Assert.Equal("12345678901234567890.123456789", fields["future_number"]);
        Assert.Equal("false", fields["future_boolean"]);
        Assert.Equal("null", fields["future_null"]);
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("secret", fields.Keys);
        Assert.Equal(
            new[]
            {
                "prompt", "output_format", "model", "duration", "seed", "steps", "cfg_scale",
                "future_object", "future_array", "future_string", "future_number",
                "future_boolean", "future_null"
            }.Order(),
            fields.Keys.Order());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Null_omission_defaults_validation_and_upstream_errors_remain_compatible()
    {
        Dictionary<string, string> fields = [];
        using var client = CreateClient(message =>
        {
            foreach (var part in (MultipartFormDataContent)message.Content!)
                fields[part.Headers.ContentDisposition!.Name!.Trim('"')] = part.ReadAsStringAsync().GetAwaiter().GetResult();

            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio"))
            };
        });
        var provider = CreateProvider(client);
        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "stable-audio-2",
            Text = "A calm melody",
            ProviderOptions = new()
            {
                ["stabilityai"] = ParseElement(
                    """{"output_format":null,"duration":null,"seed":null,"steps":null,"cfg_scale":null}""")
            }
        });

        Assert.Equal("mp3", fields["output_format"]);
        Assert.Equal(new[] { "prompt", "output_format", "model" }.Order(), fields.Keys.Order());

        fields.Clear();
        await provider.ImageRequest(new ImageRequest
        {
            Model = "stabilityai/stable-image-core",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["stabilityai"] = ParseElement("""{"negative_prompt":null,"style_preset":""}""")
            }
        });
        Assert.Equal("A landscape", fields["prompt"]);
        Assert.Equal("png", fields["output_format"]);
        Assert.Equal("1:1", fields["aspect_ratio"]);
        Assert.Equal("text-to-image", fields["mode"]);
        Assert.Equal("sd3.5-flash", fields["model"]);
        Assert.Equal(
            new[] { "prompt", "output_format", "aspect_ratio", "mode", "model" }.Order(),
            fields.Keys.Order());

        var requestsSent = 0;
        using var invalidClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).SpeechRequest(new SpeechRequest
        {
            Model = "stable-audio-2",
            Text = "A calm melody",
            ProviderOptions = new()
            {
                ["stabilityai"] = ParseElement("""{"duration":"long"}""")
            }
        }));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).ImageRequest(new ImageRequest
        {
            Model = "stabilityai/sd3.5-large",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["stabilityai"] = ParseElement("""{"negative_prompt":42}""")
            }
        }));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).ImageRequest(new ImageRequest
        {
            Model = "stabilityai/sd3.5-large",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["stabilityai"] = ParseElement("""[]""")
            }
        }));
        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"message":"upstream error"}""", Encoding.UTF8, "application/json")
        });
        var error = await Assert.ThrowsAsync<Exception>(() => CreateProvider(failedClient).ImageRequest(new ImageRequest
        {
            Model = "stabilityai/sd3.5-large",
            Prompt = "A landscape"
        }));
        Assert.Equal("400 Bad Request: upstream error", error.Message);
    }

    private static StabilityAIProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

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
            => Task.FromResult(responder(request));
    }
}
