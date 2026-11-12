using System.Net;
using System.Text;
using System.Text.Json;
using System.Reflection;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Speechmatics;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class SpeechmaticsProviderAuthTests
{
    [Theory]
    [InlineData("standard")]
    [InlineData("enhanced")]
    [InlineData("melia-1")]
    [InlineData("oak-1")]
    [InlineData("linden-1")]
    public async Task Realtime_token_contracts_use_current_rt_JWT_endpoint_and_request_scoped_auth(string model)
    {
        var calls = 0;
        using var client = CreateClient(async request =>
        {
            calls++;
            Assert.Equal("https://mp.speechmatics.com/v1/jwts?type=rt", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(60, body.RootElement.GetProperty("ttl").GetInt32());
            Assert.Single(body.RootElement.EnumerateObject());
            return JsonResponse("""{"key_value":"temporary.jwt.fixture"}""");
        });
        var provider = CreateProvider(client); var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var legacy = await provider.GetRealtimeToken(new() { Model = "speechmatics/" + model });
        var generic = await provider.CreateRealtimeTokenAsync(model, new());
        Assert.Equal("temporary.jwt.fixture", legacy.Value); Assert.Equal(legacy.Value, generic.Token);
        Assert.InRange(legacy.ExpiresAt, before + 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60);
        Assert.Equal("wss://global.rt.speechmatics.com/v2" + (model == "linden-1" ? "/agent" : ""), generic.Url);
        Assert.Equal(2, calls); Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("59")]
    [InlineData("86401")]
    [InlineData("60.5")]
    [InlineData("\"60\"")]
    [InlineData("true")]
    public async Task Realtime_rejects_invalid_TTL_before_network(string ttl)
    {
        using var client = CreateClient(_ => throw new Exception("No network request expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(client).GetRealtimeToken(new()
        { Model = "standard", ProviderOptions = new() { ["speechmatics"] = JsonSerializer.Deserialize<JsonElement>("{\"ttl\":" + ttl + "}") } }));
    }

    [Theory]
    [InlineData(60, "eu")]
    [InlineData(86400, "au")]
    [InlineData(120, "us")]
    public async Task Realtime_generic_forwards_only_TTL_and_selects_regional_endpoint(int ttl, string region)
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal("{\"ttl\":" + ttl + "}", await request.Content!.ReadAsStringAsync());
            return JsonResponse("""{"key_value":"temporary.jwt.fixture"}""");
        });
        var response = await CreateProvider(client).CreateRealtimeTokenAsync("linden-1", new() { SessionConfig = new() { ProviderOptions = new()
        { ["speechmatics"] = JsonSerializer.SerializeToElement(new { ttl, region, transcription_config = new { language = "nl" } }) } } });
        Assert.Equal($"wss://{region}.rt.speechmatics.com/v2/agent", response.Url);
    }

    [Theory]
    [InlineData("tts")]
    [InlineData("unknown")]
    [InlineData("speechmatics/tts/voice/sarah")]
    public async Task Realtime_rejects_non_transcription_models(string model)
    {
        using var client = CreateClient(_ => throw new Exception("No network request expected."));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(client).GetRealtimeToken(new() { Model = model }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"key_value\":null}")]
    [InlineData("{\"key_value\":42}")]
    [InlineData("{\"key_value\":\"test-api-key\"}")]
    [InlineData("test-api-key temporary.jwt.fixture")]
    public async Task Realtime_invalid_success_responses_are_redacted(string body)
    {
        using var client = CreateClient(_ => Task.FromResult(JsonResponse(body)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(client).GetRealtimeToken(new() { Model = "standard" }));
        Assert.DoesNotContain("test-api-key", error.ToString()); Assert.DoesNotContain("temporary.jwt.fixture", error.ToString());
    }

    [Fact]
    public async Task Realtime_failures_and_cancellation_are_safe_and_Agent_model_cannot_use_batch()
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("test-api-key temporary.jwt.fixture") }));
        var provider = CreateProvider(client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(new() { Model = "linden-1" }));
        Assert.Contains("403", error.Message); Assert.DoesNotContain("test-api-key", error.ToString()); Assert.DoesNotContain("temporary.jwt.fixture", error.ToString());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRealtimeToken(new() { Model = "standard" }, cancel.Token));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(new() { Model = "speechmatics/linden-1" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateRealtimeTokenAsync("standard", new() { SessionConfig = new() { Voice = "sarah" } }));
    }

    [Theory]
    [InlineData("melia-1")]
    [InlineData("oak-1")]
    public void Batch_multilingual_models_force_multi_and_keep_native_options(string model)
    {
        var method = typeof(SpeechmaticsProvider).GetMethod("BuildConfigJson", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var result = JsonDocument.Parse((string)method.Invoke(null, ["speechmatics/" + model, JsonSerializer.Deserialize<JsonElement>("""{"language":"nl","language_hints":["nl"]}""")])!);
        var config = result.RootElement.GetProperty("transcription_config");
        Assert.Equal(model, config.GetProperty("model").GetString()); Assert.Equal("multi", config.GetProperty("language").GetString());
        Assert.Equal("nl", config.GetProperty("language_hints")[0].GetString());
    }

    [Fact]
    public async Task Batch_transcription_attaches_auth_to_each_request_only()
    {
        var requests = new List<string>();
        using var client = CreateClient(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(request.Method switch
            {
                var method when method == HttpMethod.Post => JsonResponse("""{"id":"job-1"}"""),
                _ when request.RequestUri!.AbsolutePath.EndsWith("/transcript", StringComparison.Ordinal)
                    => JsonResponse("""{"results":[],"metadata":{"transcription_config":{"language":"en"}}}"""),
                _ => JsonResponse("""{"job":{"status":"done","duration":1}}""")
            });
        });
        var provider = CreateProvider(client);

        await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "speechmatics/en",
            Audio = Convert.ToBase64String([1, 2, 3]),
            MediaType = "audio/wav"
        });

        Assert.Equal(new[]
        {
            "POST /v2/jobs",
            "GET /v2/jobs/job-1",
            "GET /v2/jobs/job-1/transcript"
        }, requests);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Speech_attaches_auth_to_the_request_only()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        });
        var provider = CreateProvider(client);

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "speechmatics/en/voice/sarah",
            Voice = "sarah",
            Text = "Hello"
        });

        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static SpeechmaticsProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new TestHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
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

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }
}
