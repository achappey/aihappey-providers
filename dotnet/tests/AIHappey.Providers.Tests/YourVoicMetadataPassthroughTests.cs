using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.YourVoic;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class YourVoicMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_preserves_known_fields_and_forwards_unknown_options_as_multipart_fields()
    {
        var metadata = ParseElement("""
            {
              "language": "en",
              "response_format": "verbose_json",
              "prompt": "context",
              "timestamp_granularities": "word",
              "diarize": false,
              "smart_format": true,
              "punctuate": false,
              "keywords": "term one",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12.5,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "file": "metadata-file"
            }
            """);
        Dictionary<string, string> fields = [];
        using var client = CreateClient(message =>
        {
            Assert.Equal("/api/v1/stt/transcribe", message.RequestUri!.AbsolutePath);
            Assert.IsType<MultipartFormDataContent>(message.Content);
            foreach (var part in (MultipartFormDataContent)message.Content!)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                fields[name] = part.ReadAsStringAsync().GetAwaiter().GetResult();
            }

            return JsonResponse("""{"text":"transcribed"}""");
        });
        var request = new TranscriptionRequest
        {
            Model = "general-stt",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["yourvoic"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":true}""")
            }
        };
        var clientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(metadata, clientRequest.GetProperty("providerOptions").GetProperty("yourvoic")));
        Assert.False(clientRequest.TryGetProperty("yourvoic", out _));

        var response = await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal("transcribed", response.Text);
        Assert.Equal("general-stt", fields["model"]);
        Assert.Equal("en", fields["language"]);
        Assert.Equal("verbose_json", fields["response_format"]);
        Assert.Equal("context", fields["prompt"]);
        Assert.Equal("word", fields["timestamp_granularities"]);
        Assert.Equal("false", fields["diarize"]);
        Assert.Equal("true", fields["smart_format"]);
        Assert.Equal("false", fields["punctuate"]);
        Assert.Equal("term one", fields["keywords"]);
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_object"), ParseElement(fields["future_object"])));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_array"), ParseElement(fields["future_array"])));
        Assert.Equal("unchanged", fields["future_string"]);
        Assert.Equal("12.5", fields["future_number"]);
        Assert.Equal("false", fields["future_boolean"]);
        Assert.DoesNotContain("future_null", fields.Keys);
        Assert.Equal("general-stt", fields["model"]);
        Assert.DoesNotContain("metadata-file", fields.Values);
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.Equal(15, fields.Count);
    }

    [Fact]
    public async Task Null_and_empty_options_remain_omitted_and_invalid_options_keep_json_errors()
    {
        var request = CreateRequest(ParseElement("""
            {
              "language": null,
              "response_format": " ",
              "prompt": null,
              "timestamp_granularities": null,
              "diarize": null,
              "smart_format": null,
              "punctuate": null,
              "keywords": null,
              "future_null": null
            }
            """));
        Dictionary<string, string> fields = [];
        using var client = CreateClient(message =>
        {
            foreach (var part in (MultipartFormDataContent)message.Content!)
                fields[part.Headers.ContentDisposition!.Name!.Trim('"')] = part.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"text":"transcribed"}""");
        });

        await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal(new[] { "file", "model" }.Order(), fields.Keys.Order());

        var requestCount = 0;
        using var invalidClient = CreateClient(_ =>
        {
            requestCount++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        request = CreateRequest(ParseElement("""{"language":42}"""));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).TranscriptionRequest(request));
        request = CreateRequest(ParseElement("[]"));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).TranscriptionRequest(request));
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task Upstream_errors_keep_the_existing_exception_shape()
    {
        using var client = CreateClient(_ => JsonResponse("upstream failure", HttpStatusCode.BadRequest));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(client).TranscriptionRequest(CreateRequest(default)));

        Assert.Equal("YourVoic STT failed (400): upstream failure", error.Message);
    }

    private static TranscriptionRequest CreateRequest(JsonElement metadata)
        => new()
        {
            Model = "general-stt",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new() { ["yourvoic"] = metadata }
        };

    private static YourVoicProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

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
