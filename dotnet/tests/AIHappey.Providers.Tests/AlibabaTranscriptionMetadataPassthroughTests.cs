using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Alibaba;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class AlibabaTranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_preserves_provider_key_and_forwards_unknown_json_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/compatible-mode/v1/chat/completions", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"choices":[{"message":{"content":"recognized"}}]}""");
        });

        var metadata = ParseElement(
            """
            {
              "language": "en",
              "enable_itn": true,
              "asr_options": {"future_asr": {"nested": [1, null, {"ok": true}]}},
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "stream": true
            }
            """);
        var request = CreateRequest(metadata);
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("alibaba")));

        var response = await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal("recognized", response.Text);
        Assert.Equal("qwen3-asr-flash", payload.GetProperty("model").GetString());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        Assert.Equal("en", payload.GetProperty("asr_options").GetProperty("language").GetString());
        Assert.True(payload.GetProperty("asr_options").GetProperty("enable_itn").GetBoolean());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("asr_options").GetProperty("future_asr"),
            payload.GetProperty("asr_options").GetProperty("future_asr")));

        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number",
                     "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("alibaba", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
        Assert.False(payload.GetProperty("messages")[0].GetProperty("content")[0]
            .GetProperty("input_audio").TryGetProperty("providerOptions", out _));
        Assert.Equal(10, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Transcription_preserves_omissions_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"choices":[{"message":{"content":"recognized"}}]}""");
        });
        var provider = CreateProvider(client);

        await provider.TranscriptionRequest(CreateRequest(default));
        Assert.False(payload.TryGetProperty("asr_options", out _));
        Assert.Equal(3, payload.EnumerateObject().Count());

        await provider.TranscriptionRequest(CreateRequest(ParseElement(
            """{"language":null,"enable_itn":null,"future_null":null}""")));
        Assert.False(payload.TryGetProperty("asr_options", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(4, payload.EnumerateObject().Count());

        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            CreateRequest(ParseElement("""{"language":42}"""))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            CreateRequest(ParseElement("""{"enable_itn":"true"}"""))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).TranscriptionRequest(
            CreateRequest(ParseElement("""["invalid metadata root"]"""))));

        using var failedClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).TranscriptionRequest(CreateRequest(default)));
        Assert.Equal("Alibaba STT failed (400): upstream failure", error.Message);
    }

    private static TranscriptionRequest CreateRequest(JsonElement metadata)
        => new()
        {
            Model = "qwen3-asr-flash",
            Audio = "data:audio/wav;base64,YXVkaW8=",
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement>
                {
                    ["alibaba"] = metadata,
                    ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
                }
        };

    private static AlibabaProvider CreateProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

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
