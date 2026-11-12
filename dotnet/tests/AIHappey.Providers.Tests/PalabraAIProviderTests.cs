using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Providers.PalabraAI;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class PalabraAIProviderTests
{
    [Fact]
    public async Task Realtime_session_forwards_native_options_and_returns_stt_connection_metadata()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal("https://api.palabra.ai/session-storage/v2/sessions", request.RequestUri!.ToString());
            Assert.Equal("Bearer test-api-key", request.Headers.Authorization?.ToString());
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("stt", payload.RootElement.GetProperty("intent").GetString());
            Assert.True(payload.RootElement.GetProperty("single_use").GetBoolean());
            Assert.Equal(300, payload.RootElement.GetProperty("expires_in_seconds").GetInt32());
            Assert.Equal("client-17", payload.RootElement.GetProperty("client_reference_id").GetString());
            Assert.True(payload.RootElement.GetProperty("future_option").GetProperty("enabled").GetBoolean());

            return JsonResponse(
                """{"ok":true,"data":{"id":"ses_123","token":"session-token","intent":"stt","connection_type":"ws","single_use":true,"status":"pending","connect_by":"2026-11-12T13:10:00Z","active_stream_count":0}}""",
                HttpStatusCode.Created);
        });
        var provider = new PalabraAIProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var result = await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "palabraai/stt",
            ProviderOptions = new()
            {
                ["palabraai"] = JsonSerializer.SerializeToElement(new
                {
                    single_use = true,
                    expires_in_seconds = 300,
                    client_reference_id = "client-17",
                    future_option = new { enabled = true }
                })
            }
        });

        Assert.Equal("session-token", result.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T13:10:00Z").ToUnixTimeSeconds(), result.ExpiresAt);
        Assert.Empty(client.DefaultRequestHeaders);

        var metadata = result.ProviderMetadata!["palabraai"];
        Assert.Equal(
            "wss://stream.palabra.ai/asr/v1/speech-to-text/stream",
            metadata.GetProperty("ws_url").GetString());
        Assert.Equal("token", metadata.GetProperty("token_parameter").GetString());
        Assert.False(metadata.TryGetProperty("token", out _));
    }

    [Fact]
    public async Task Realtime_webrtc_session_returns_native_room_details()
    {
        using var client = CreateClient(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("s2s", payload.RootElement.GetProperty("intent").GetString());
            Assert.Equal("webrtc", payload.RootElement.GetProperty("connection_type").GetString());
            return JsonResponse(
                """{"ok":true,"data":{"id":"ses_456","token":"publisher-token","intent":"s2s","connection_type":"webrtc","status":"pending","connect_by":"2026-11-12T13:10:00Z","webrtc":{"url":"https://api.palabra.ai/livekit/","room_name":"room-1","subscriber_keys":["listener-token"]}}}""",
                HttpStatusCode.Created);
        });
        var provider = new PalabraAIProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var result = await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "palabraai/s2s",
            ProviderOptions = new()
            {
                ["palabraai"] = JsonSerializer.SerializeToElement(new
                {
                    connection_type = "webrtc",
                    subscriber_count = 1
                })
            }
        });

        Assert.Equal("publisher-token", result.Value);
        var metadata = result.ProviderMetadata!["palabraai"];
        Assert.False(metadata.TryGetProperty("ws_url", out _));
        Assert.Equal("room-1", metadata.GetProperty("webrtc").GetProperty("room_name").GetString());
        Assert.Equal("listener-token", metadata.GetProperty("webrtc").GetProperty("subscriber_keys")[0].GetString());
    }

    [Fact]
    public async Task Realtime_tts_uses_the_dedicated_websocket_endpoint()
    {
        using var client = CreateClient(_ => Task.FromResult(JsonResponse(
            """{"ok":true,"data":{"id":"ses_789","token":"tts-token","intent":"tts","connection_type":"ws","connect_by":"2026-11-12T13:10:00Z"}}""",
            HttpStatusCode.Created)));
        var provider = new PalabraAIProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var result = await provider.GetRealtimeToken(new RealtimeRequest { Model = "palabraai/tts" });

        Assert.Equal(
            "wss://stream.palabra.ai/tts-api/v1/text-to-speech/stream",
            result.ProviderMetadata!["palabraai"].GetProperty("ws_url").GetString());
    }

    [Fact]
    public async Task Realtime_s2s_websocket_uses_a_unique_translation_endpoint()
    {
        using var client = CreateClient(_ => Task.FromResult(JsonResponse(
            """{"ok":true,"data":{"id":"ses_321","token":"translation-token","intent":"s2s","connection_type":"ws","connect_by":"2026-11-12T13:10:00Z"}}""",
            HttpStatusCode.Created)));
        var provider = new PalabraAIProvider(new TestKeyResolver(), new TestHttpClientFactory(client));

        var result = await provider.GetRealtimeToken(new RealtimeRequest { Model = "palabraai/s2s" });
        var wsUrl = new Uri(result.ProviderMetadata!["palabraai"].GetProperty("ws_url").GetString()!);

        Assert.Equal("streaming.palabra.ai", wsUrl.Host);
        Assert.Matches(
            "^/streaming-api/[0-9a-f]{32}/v1/speech-to-speech/stream$",
            wsUrl.AbsolutePath);
    }

    [Fact]
    public async Task Webrtc_is_rejected_for_non_translation_intents()
    {
        var provider = new PalabraAIProvider(
            new TestKeyResolver(),
            new TestHttpClientFactory(CreateClient(_ => throw new InvalidOperationException("Request should not be sent."))));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "palabraai/tts",
            ProviderOptions = new()
            {
                ["palabraai"] = JsonSerializer.SerializeToElement(new { connection_type = "webrtc" })
            }
        }));
    }

    [Fact]
    public void Provider_is_registered_for_routing()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        using var serviceProvider = services.BuildServiceProvider();

        Assert.True(serviceProvider.GetRequiredService<ProviderRegistry>().HasModelProvider("palabraai"));
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
        => new(new TestHttpMessageHandler(sendAsync));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => sendAsync(request);
    }
}
