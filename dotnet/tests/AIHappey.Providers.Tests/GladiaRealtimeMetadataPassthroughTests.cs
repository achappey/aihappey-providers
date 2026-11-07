using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Gladia;

namespace AIHappey.Tests;

public sealed class GladiaRealtimeMetadataPassthroughTests
{
    [Fact]
    public async Task Realtime_forwards_provider_options_without_losing_unknown_json_or_nesting()
    {
        var metadata = ParseElement("""
            {
              "encoding": "wav/alaw",
              "sample_rate": 44100,
              "model": "metadata-model",
              "future_parameter": "unchanged",
              "future_number": 12.5,
              "future_boolean": false,
              "future_null": null,
              "future_object": {"nested": [{"value": true}, [null, 3]]},
              "future_array": [1, "two", {"three": null}],
              "language_config": {"languages": ["en", "es"], "future_language": true},
              "pre_processing": {
                "audio_enhancer": true,
                "future_preprocessing": {"preserved": true}
              },
              "realtime_processing": {
                "custom_vocabulary_config": {
                  "vocabulary": [{"value": "Faro", "future_item": [false, null]}],
                  "future_vocabulary": {"weight": 4}
                }
              },
              "post_processing": {
                "summarization_config": {"future_summary": "kept"}
              }
            }
            """);
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/v2/live", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"id":"session-1","url":"wss://gladia.example/session"}""");
        });
        var request = new RealtimeRequest
        {
            Model = "solaria-2",
            ProviderOptions = new()
            {
                ["gladia"] = metadata,
                ["another-provider"] = ParseElement("""{"must_not_forward":true}""")
            }
        };

        var clientWireRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientWireRequest.GetProperty("providerOptions").GetProperty("gladia")));

        var response = await CreateProvider(client).GetRealtimeToken(request, CancellationToken.None);

        Assert.Equal("wss://gladia.example/session", response.Value);
        Assert.Equal("solaria-2", payload.GetProperty("model").GetString());
        Assert.Equal("wav/alaw", payload.GetProperty("encoding").GetString());
        Assert.Equal(44100, payload.GetProperty("sample_rate").GetInt32());
        Assert.Equal(16, payload.GetProperty("bit_depth").GetInt32());
        Assert.Equal(1, payload.GetProperty("channels").GetInt32());
        Assert.Equal(0.05, payload.GetProperty("endpointing").GetDouble());
        Assert.Equal(5, payload.GetProperty("maximum_duration_without_endpointing").GetDouble());
        foreach (var name in new[]
                 {
                     "future_parameter", "future_number", "future_boolean", "future_null",
                     "future_object", "future_array"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("pre_processing").GetProperty("future_preprocessing"),
            payload.GetProperty("pre_processing").GetProperty("future_preprocessing")));
        Assert.True(payload.GetProperty("pre_processing").GetProperty("audio_enhancer").GetBoolean());
        Assert.Equal(0.6, payload.GetProperty("pre_processing").GetProperty("speech_threshold").GetDouble());
        Assert.False(payload.GetProperty("language_config").GetProperty("code_switching").GetBoolean());
        Assert.Equal(0.5, payload.GetProperty("realtime_processing")
            .GetProperty("custom_vocabulary_config").GetProperty("default_intensity").GetDouble());
        Assert.False(payload.GetProperty("realtime_processing").GetProperty("custom_vocabulary").GetBoolean());
        Assert.Equal("general", payload.GetProperty("post_processing")
            .GetProperty("summarization_config").GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("language_config").GetProperty("future_language"),
            payload.GetProperty("language_config").GetProperty("future_language")));
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("realtime_processing").GetProperty("custom_vocabulary_config")
                .GetProperty("future_vocabulary"),
            payload.GetProperty("realtime_processing").GetProperty("custom_vocabulary_config")
                .GetProperty("future_vocabulary")));
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("realtime_processing").GetProperty("custom_vocabulary_config")
                .GetProperty("vocabulary")[0].GetProperty("future_item"),
            payload.GetProperty("realtime_processing").GetProperty("custom_vocabulary_config")
                .GetProperty("vocabulary")[0].GetProperty("future_item")));
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("post_processing").GetProperty("summarization_config")
                .GetProperty("future_summary"),
            payload.GetProperty("post_processing").GetProperty("summarization_config")
                .GetProperty("future_summary")));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("must_not_forward", out _));
    }

    [Fact]
    public async Task Realtime_defaults_and_known_null_omission_remain_compatible()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"id":"session-1","url":"wss://gladia.example/session"}""");
        });
        var provider = CreateProvider(client);

        await provider.GetRealtimeToken(new RealtimeRequest { Model = "solaria-1" }, CancellationToken.None);

        Assert.Equal("solaria-1", payload.GetProperty("model").GetString());
        Assert.Equal("wav/pcm", payload.GetProperty("encoding").GetString());
        Assert.Equal(16, payload.GetProperty("bit_depth").GetInt32());
        Assert.Equal(16000, payload.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1, payload.GetProperty("channels").GetInt32());
        Assert.Equal(0.05, payload.GetProperty("endpointing").GetDouble());
        Assert.Equal(5, payload.GetProperty("maximum_duration_without_endpointing").GetDouble());
        Assert.Equal(7, payload.EnumerateObject().Count());

        var rawMetadata = ParseElement("""{"encoding":null,"model":null,"future_null":null}""");
        payload = default;
        await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "solaria-3",
            ProviderOptions = new() { ["gladia"] = rawMetadata }
        }, CancellationToken.None);

        Assert.False(payload.TryGetProperty("encoding", out _));
        Assert.Equal("solaria-3", payload.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
    }

    [Fact]
    public async Task Realtime_upstream_error_and_invalid_metadata_keep_existing_failures()
    {
        using var failedClient = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<Exception>(() => CreateProvider(failedClient)
            .GetRealtimeToken(new RealtimeRequest { Model = "solaria-1" }, CancellationToken.None));
        Assert.Equal("upstream failure", error.Message);

        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient)
            .GetRealtimeToken(new RealtimeRequest
            {
                Model = "solaria-1",
                ProviderOptions = new() { ["gladia"] = ParseElement("[]") }
            }, CancellationToken.None));

        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient)
            .GetRealtimeToken(new RealtimeRequest
            {
                Model = "solaria-1",
                ProviderOptions = new() { ["gladia"] = ParseElement("""{"sample_rate":"invalid"}""") }
            }, CancellationToken.None));
    }

    private static GladiaProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
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
