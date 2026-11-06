using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ElevenLabs;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ElevenLabsTranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_preserves_provider_namespace_and_forwards_unknown_multipart_values()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? capturedUri = null;
        using var client = CreateClient(request =>
        {
            capturedUri = request.RequestUri?.ToString();
            CaptureFields(request, fields);
            return JsonResponse("""{"text":"recognized"}""");
        });
        var metadata = ParseElement(
            """
            {
              "language_code": "fr",
              "language": "de",
              "enable_logging": true,
              "tag_audio_events": false,
              "num_speakers": 2,
              "timestamps_granularity": "character",
              "timestamp_granularities": ["word", "segment"],
              "diarize": false,
              "diarization_threshold": 0.4,
              "file_format": "pcm_s16le_16",
              "temperature": 0.25,
              "seed": 42,
              "use_multi_channel": true,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = CreateRequest(metadata);
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("elevenlabs")));
        Assert.False(clientBoundary.GetProperty("providerOptions").TryGetProperty("future_object", out _));

        var response = await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal("recognized", response.Text);
        Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text?enable_logging=true", capturedUri);
        Assert.Equal("request-model", Single(fields, "model_id"));
        Assert.Equal("audio", Single(fields, "file"));
        Assert.Equal("fr", Single(fields, "language_code"));
        Assert.Equal("false", Single(fields, "tag_audio_events"));
        Assert.Equal("2", Single(fields, "num_speakers"));
        Assert.Equal("character", Single(fields, "timestamps_granularity"));
        Assert.Equal("false", Single(fields, "diarize"));
        Assert.Equal("0.4", Single(fields, "diarization_threshold"));
        Assert.Equal("pcm_s16le_16", Single(fields, "file_format"));
        Assert.Equal("0.25", Single(fields, "temperature"));
        Assert.Equal("42", Single(fields, "seed"));
        Assert.Equal("true", Single(fields, "use_multi_channel"));
        Assert.Equal(metadata.GetProperty("future_object").GetRawText(), Single(fields, "future_object"));
        Assert.Equal(metadata.GetProperty("future_array").GetRawText(), Single(fields, "future_array"));
        Assert.Equal("unchanged", Single(fields, "future_string"));
        Assert.Equal("12345678901234567890.123456789", Single(fields, "future_number"));
        Assert.Equal("false", Single(fields, "future_boolean"));
        Assert.Equal("null", Single(fields, "future_null"));
        Assert.Equal(18, fields.Count);
        Assert.DoesNotContain("language", fields.Keys);
        Assert.DoesNotContain("enable_logging", fields.Keys);
        Assert.DoesNotContain("timestamp_granularities", fields.Keys);
    }

    [Fact]
    public async Task Transcription_preserves_omissions_validation_and_upstream_errors()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var client = CreateClient(request =>
        {
            CaptureFields(request, fields);
            return JsonResponse("""{"text":"recognized"}""");
        });
        var provider = CreateProvider(client);

        await provider.TranscriptionRequest(CreateRequest(default));

        Assert.Equal("request-model", Single(fields, "model_id"));
        Assert.Equal("audio", Single(fields, "file"));
        Assert.Equal(2, fields.Count);

        fields.Clear();
        await provider.TranscriptionRequest(CreateRequest(ParseElement("""{"language":null,"enable_logging":null,"future_null":null}""")));
        Assert.Equal("null", Single(fields, "future_null"));
        Assert.False(fields.ContainsKey("language_code"));
        Assert.Equal(3, fields.Count);

        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            CreateRequest(ParseElement("""{"language":42}"""))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            CreateRequest(ParseElement("""{"timestamp_granularities":"word"}"""))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            CreateRequest(ParseElement("""["not","an","object"]"""))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            CreateRequest(ParseElement("""{"model_id":"override"}"""))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            CreateRequest(ParseElement("""{"file":"override"}"""))));

        using var failedClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).TranscriptionRequest(CreateRequest(default)));
    }

    private static TranscriptionRequest CreateRequest(JsonElement metadata)
        => new()
        {
            Model = "request-model",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement> { ["elevenlabs"] = metadata }
        };

    private static ElevenLabsProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static void CaptureFields(HttpRequestMessage request, Dictionary<string, List<string>> fields)
    {
        var form = Assert.IsType<MultipartFormDataContent>(request.Content);
        foreach (var part in form)
        {
            var name = part.Headers.ContentDisposition?.Name?.Trim('"');
            if (string.IsNullOrEmpty(name))
                continue;

            if (!fields.TryGetValue(name, out var values))
                fields[name] = values = [];
            values.Add(part.ReadAsStringAsync().GetAwaiter().GetResult());
        }
    }

    private static string Single(Dictionary<string, List<string>> fields, string name)
        => Assert.Single(fields[name]);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
