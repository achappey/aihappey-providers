using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DeepL;
using Xunit;

namespace AIHappey.Providers.Tests;

public sealed class DeepLRealtimeTests
{
    [Theory]
    [InlineData("deepl/voice-realtime", null)]
    [InlineData("deepl/voice-realtime/translate-to/nl", "nl")]
    [InlineData("voice-realtime/translate-to/en-US", "en-US")]
    public async Task Model_owns_text_only_output_and_capture_format(string model, string? language)
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("api.deepl.com", request.RequestUri!.Host);
            Assert.Equal("/v3/voice/realtime", request.RequestUri.AbsolutePath);
            Assert.Equal("DeepL-Auth-Key", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("json", root.GetProperty("message_format").GetString());
            Assert.Equal("audio/pcm;encoding=s16le;rate=16000", root.GetProperty("source_media_content_type").GetString());
            Assert.Equal(language is null ? "[]" : $"[\"{language}\"]", root.GetProperty("target_languages").GetRawText());
            Assert.False(root.TryGetProperty("target_media_languages", out _));
            Assert.Equal("nl", root.GetProperty("source_language").GetString());
            Assert.True(root.GetProperty("future").GetProperty("keep").GetBoolean());
            return Json("""{"token":"ephemeral","streaming_url":"wss://voice.deepl.com/current?route=2","session_id":"internal"}""");
        }));
        var response = await Provider(client, "free:fx").GetRealtimeToken(new RealtimeRequest
        {
            Model = model, ProviderOptions = new() { ["deepl"] = Element("""{"source_language":"nl","message_format":"msgpack","source_media_content_type":"audio/auto","target_languages":["fr"],"target_media_languages":["de"],"future":{"keep":true}}""") }
        }, default);
        Assert.Equal("ephemeral", response.Value);
        Assert.Equal("wss://voice.deepl.com/current?route=2", response.ProviderMetadata!["deepl"].GetProperty("streaming_url").GetString());
    }

    [Theory]
    [InlineData(400, 1)]
    [InlineData(456, 1)]
    [InlineData(429, 4)]
    [InlineData(503, 4)]
    public async Task Errors_use_safe_shared_transport_and_bounded_retries(int status, int expected)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            var result = Json("""{"message":"failed secret-key","code":"voice_denied"}""", (HttpStatusCode)status);
            result.Headers.Add("X-Trace-ID", "trace-fixture"); return Task.FromResult(result);
        }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Provider(client).GetRealtimeToken(new() { Model = "deepl/voice-realtime" }, default));
        Assert.Equal(expected, calls); Assert.Contains("trace-fixture", error.Message); Assert.Contains("voice_denied", error.Message);
        Assert.DoesNotContain("secret-key", error.Message);
    }

    [Theory]
    [InlineData("deepl/translate-to/DE")]
    [InlineData("deepl/voice-realtime/translate-to/")]
    [InlineData("deepl/voice-realtime/translate-to/nl?x=y")]
    public async Task Invalid_models_never_create_sessions(string model)
    {
        using var client = new HttpClient(new Handler(_ => throw new Exception("Unexpected request.")));
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(client).GetRealtimeToken(new() { Model = model }, default));
    }

    [Theory]
    [InlineData("wss://evil.example/stream")]
    [InlineData("wss://api.deepl.com.evil.example/stream")]
    [InlineData("ws://api.deepl.com/stream")]
    public async Task Untrusted_streaming_addresses_are_rejected(string address)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(Json(JsonSerializer.Serialize(new { token = "ephemeral", streaming_url = address })))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).GetRealtimeToken(new() { Model = "deepl/voice-realtime" }, default));
    }

    private static DeepLProvider Provider(HttpClient client, string key = "secret-key") => new(new Keys(key), new AsyncCacheHelper(), new Factory(client));
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Keys(string key) : IApiKeyResolver { public string? Resolve(string provider) => key; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
}
