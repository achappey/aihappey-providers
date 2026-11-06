using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.SmallestAI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class SmallestAITranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_preserves_provider_namespace_and_forwards_unknown_query_values()
    {
        var metadata = ParseElement("""
            {
              "language": " en ",
              "wordTimestamps": true,
              "diarize": false,
              "genderDetection": true,
              "emotionDetection": false,
              "redactPii": true,
              "redactPci": false,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "must-not-override-request"
            }
            """);
        Dictionary<string, List<string>> query = [];
        using var client = CreateClient(request =>
        {
            Assert.Equal("/waves/v1/stt/", request.RequestUri!.AbsolutePath);
            Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
            query = ParseQuery(request.RequestUri);
            return JsonResponse("""{"transcription":"recognized","language":"en"}""");
        });
        var request = CreateRequest(metadata);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("smallestai")));

        var response = await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal("recognized", response.Text);
        Assert.Equal("pulse", Single(query, "model"));
        Assert.Equal("en", Single(query, "language"));
        Assert.Equal("true", Single(query, "word_timestamps"));
        Assert.Equal("false", Single(query, "diarize"));
        Assert.Equal("true", Single(query, "gender_detection"));
        Assert.Equal("false", Single(query, "emotion_detection"));
        Assert.Equal("true", Single(query, "redact_pii"));
        Assert.Equal("false", Single(query, "redact_pci"));
        Assert.Equal(metadata.GetProperty("future_object").GetRawText(), Single(query, "future_object"));
        Assert.Equal(metadata.GetProperty("future_array").GetRawText(), Single(query, "future_array"));
        Assert.Equal("unchanged", Single(query, "future_string"));
        Assert.Equal("12345678901234567890.123456789", Single(query, "future_number"));
        Assert.Equal("false", Single(query, "future_boolean"));
        Assert.Equal("null", Single(query, "future_null"));
        Assert.Equal(14, query.Count);
        Assert.Equal(1, query["model"].Count);
        Assert.DoesNotContain("providerOptions", query.Keys);
        Assert.DoesNotContain("smallestai", query.Keys);
        Assert.DoesNotContain("another-provider", query.Keys);
        Assert.Equal(JsonValueKind.Object, ParseElement(Single(query, "future_object")).ValueKind);
        Assert.Equal(JsonValueKind.Array, ParseElement(Single(query, "future_array")).ValueKind);
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["smallestai"]));
    }

    [Fact]
    public async Task Transcription_preserves_defaults_omissions_validation_and_errors()
    {
        Dictionary<string, List<string>> query = [];
        using var client = CreateClient(request =>
        {
            query = ParseQuery(request.RequestUri!);
            return JsonResponse("""{"transcription":"recognized"}""");
        });
        var provider = CreateProvider(client);

        await provider.TranscriptionRequest(CreateRequest(default));
        Assert.Equal("pulse", Single(query, "model"));
        Assert.Equal("en", Single(query, "language"));
        Assert.Equal(2, query.Count);

        query.Clear();
        await provider.TranscriptionRequest(CreateRequest(ParseElement(
            """{"language":null,"wordTimestamps":null,"future_null":null}""")));
        Assert.Equal("en", Single(query, "language"));
        Assert.Equal("null", Single(query, "future_null"));
        Assert.False(query.ContainsKey("word_timestamps"));
        Assert.Equal(3, query.Count);

        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var invalidProvider = CreateProvider(unusedClient);
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.TranscriptionRequest(CreateRequest(ParseElement(
            """{"language":42}"""))));
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.TranscriptionRequest(CreateRequest(ParseElement(
            """{"diarize":"true"}"""))));
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.TranscriptionRequest(CreateRequest(ParseElement(
            """["not-an-object"]"""))));
        await Assert.ThrowsAsync<ArgumentException>(() => invalidProvider.TranscriptionRequest(CreateRequest(ParseElement(
            """{"webhookMethod":"PUT"}"""), "pulse-pro")));
        await Assert.ThrowsAsync<ArgumentException>(() => invalidProvider.TranscriptionRequest(CreateRequest(ParseElement(
            """{"webhookUrl":"https://example.test/hook"}"""), "pulse")));

        using var failedClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).TranscriptionRequest(CreateRequest(default)));
        Assert.Equal("SmallestAI pulse transcription failed (400): upstream failure", error.Message);
    }

    [Fact]
    public async Task Pulse_pro_preserves_webhook_mapping_and_override_rules()
    {
        Dictionary<string, List<string>> query = [];
        using var client = CreateClient(request =>
        {
            query = ParseQuery(request.RequestUri!);
            return JsonResponse("""{"transcription":"recognized"}""");
        });
        var metadata = ParseElement("""
            {
              "language": "en",
              "webhookUrl": "https://example.test/hook?source=client",
              "webhookMethod": " post ",
              "webhookExtra": "{\"task\":\"transcribe\"}"
            }
            """);

        await CreateProvider(client).TranscriptionRequest(CreateRequest(metadata, "pulse-pro"));

        Assert.Equal("pulse-pro", Single(query, "model"));
        Assert.Equal("en", Single(query, "language"));
        Assert.Equal("https://example.test/hook?source=client", Single(query, "webhook_url"));
        Assert.Equal("POST", Single(query, "webhook_method"));
        Assert.Equal("{\"task\":\"transcribe\"}", Single(query, "webhook_extra"));
        Assert.Equal(5, query.Count);
    }

    [Fact]
    public async Task Openai_transcription_options_override_provider_parameters()
    {
        Dictionary<string, List<string>> query = [];
        using var client = CreateClient(request =>
        {
            query = ParseQuery(request.RequestUri!);
            return JsonResponse("""{"transcription":"recognized","language":"fr"}""");
        });
        using var audio = new MemoryStream([1, 2, 3]);
        var openAIRequest = new OpenAITranscriptionRequest
        {
            Model = "pulse",
            Language = "fr",
            TimestampGranularities = ["word"],
            File = new FormFile(audio, 0, audio.Length, "file", "audio.wav")
            {
                Headers = new HeaderDictionary(),
                ContentType = "audio/wav"
            }
        };

        await CreateProvider(client).OpenAITranscriptionRequestAsync(openAIRequest);

        Assert.Equal("fr", Single(query, "language"));
        Assert.Equal("true", Single(query, "word_timestamps"));
        Assert.Equal("pulse", Single(query, "model"));
        Assert.Equal(3, query.Count);
    }

    private static TranscriptionRequest CreateRequest(JsonElement metadata, string model = "pulse")
        => new()
        {
            Model = model,
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement> { ["smallestai"] = metadata }
        };

    private static SmallestAIProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static Dictionary<string, List<string>> ParseQuery(Uri uri)
        => uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .GroupBy(parts => Uri.UnescapeDataString(parts[0]), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(parts => Uri.UnescapeDataString(parts.ElementAtOrDefault(1) ?? string.Empty)).ToList(),
                StringComparer.Ordinal);

    private static string Single(Dictionary<string, List<string>> query, string name)
        => Assert.Single(query[name]);

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
