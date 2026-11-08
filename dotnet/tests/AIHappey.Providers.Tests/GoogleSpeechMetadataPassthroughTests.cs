using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests;

public sealed class GoogleSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_provider_options_remain_raw_at_the_upstream_request_root()
    {
        var metadata = Json("""
            {
              "speech_config": [{"voice": "MetadataVoice"}],
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        JsonElement sent = default;
        using var client = CreateClient(request =>
        {
            sent = JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"output_audio":{"data":"YXVkaW8=","mime_type":"audio/L16"}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var request = new SpeechRequest
        {
            Model = "gemini-2.5-flash-preview-tts/Zephyr",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["google"] = metadata,
                ["another-provider"] = Json("""{"should_not_leak":true}""")
            }
        };

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("gemini-2.5-flash-preview-tts", sent.GetProperty("model").GetString());
        Assert.Equal("Hello", sent.GetProperty("input").GetString());
        Assert.Equal("audio", sent.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(
            "MetadataVoice",
            sent.GetProperty("generation_config").GetProperty("speech_config")[0].GetProperty("voice").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_object"), sent.GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_array"), sent.GetProperty("future_array")));
        Assert.Equal("unchanged", sent.GetProperty("future_string").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_number"), sent.GetProperty("future_number")));
        Assert.False(sent.GetProperty("future_boolean").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("future_null").ValueKind);
        Assert.False(sent.TryGetProperty("speech_config", out _));
        Assert.False(sent.TryGetProperty("another-provider", out _));
        Assert.False(sent.TryGetProperty("should_not_leak", out _));
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private static GoogleAIProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), NullLogger<GoogleAIProvider>.Instance, new TestClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new TestHandler(response));

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
