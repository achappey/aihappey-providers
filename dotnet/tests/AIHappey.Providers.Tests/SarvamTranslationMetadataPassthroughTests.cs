using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Sarvam;
using AIHappey.Unified.Models;

namespace AIHappey.Tests;

public sealed class SarvamTranslationMetadataPassthroughTests
{
    [Fact]
    public async Task Translation_forwards_provider_options_at_the_upstream_root_and_preserves_unknown_json()
    {
        var metadata = ParseElement(
            """
            {
              "speaker_gender": "Female",
              "mode": "formal",
              "output_script": "latin",
              "numerals_format": "international",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "client-model",
              "input": "client-input",
              "source_language_code": "client-source",
              "target_language_code": "client-target"
            }
            """);
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/translate", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"translated_text":"translated"}""");
        });

        await CreateProvider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "sarvam",
            Model = "sarvam/mayura:v1/translate-to/hi-IN",
            Input = new AIInput { Text = "source text" },
            Metadata = new Dictionary<string, object?>
            {
                ["sarvam"] = metadata,
                ["another-provider"] = ParseElement("""{"not_forwarded":true}""")
            }
        });

        Assert.Equal("source text", payload.GetProperty("input").GetString());
        Assert.Equal("auto", payload.GetProperty("source_language_code").GetString());
        Assert.Equal("hi-IN", payload.GetProperty("target_language_code").GetString());
        Assert.Equal("mayura:v1", payload.GetProperty("model").GetString());
        Assert.Equal("Female", payload.GetProperty("speaker_gender").GetString());
        Assert.Equal("formal", payload.GetProperty("mode").GetString());
        Assert.Equal("latin", payload.GetProperty("output_script").GetString());
        Assert.Equal("international", payload.GetProperty("numerals_format").GetString());
        Assert.Equal(14, payload.EnumerateObject().Count());
        Assert.False(payload.TryGetProperty("sarvam", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));

        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }
    }

    [Fact]
    public async Task Translation_preserves_legacy_null_omissions_and_upstream_errors()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("upstream failure", HttpStatusCode.BadRequest);
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(client).ExecuteUnifiedAsync(new AIRequest
            {
                ProviderId = "sarvam",
                Model = "sarvam/mayura:v1/translate-to/hi-IN",
                Input = new AIInput { Text = "source text" },
                Metadata = new Dictionary<string, object?>
                {
                    ["sarvam"] = ParseElement("""{"speaker_gender":null,"future_null":null}""")
                }
            }));

        Assert.Equal("Sarvam translate failed (400): upstream failure", exception.Message);
        Assert.False(payload.TryGetProperty("speaker_gender", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal("source text", payload.GetProperty("input").GetString());
        Assert.Equal("mayura:v1", payload.GetProperty("model").GetString());
    }

    private static SarvamProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
