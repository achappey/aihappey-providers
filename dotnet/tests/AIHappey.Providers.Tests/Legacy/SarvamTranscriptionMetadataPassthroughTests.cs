using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Sarvam;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class SarvamTranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_preserves_provider_key_maps_known_options_and_forwards_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/speech-to-text/job/v1")
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        });
        var metadata = ParseElement(
            """
            {
              "model": "sarvam/saarika:v2.5",
              "language_code": "en-IN",
              "input_audio_codec": "wav",
              "with_timestamps": false,
              "mode": "translate",
              "with_diarization": true,
              "num_speakers": 2,
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
            clientBoundary.GetProperty("providerOptions").GetProperty("sarvam")));
        Assert.False(clientBoundary.TryGetProperty("sarvam", out _));

        await CreateProvider(client).TranscriptionRequest(request);

        Assert.Single(payload.EnumerateObject());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("sarvam", out _));
        var parameters = payload.GetProperty("job_parameters");
        Assert.Equal("saarika:v2.5", parameters.GetProperty("model").GetString());
        Assert.Equal("en-IN", parameters.GetProperty("language_code").GetString());
        Assert.Equal("wav", parameters.GetProperty("input_audio_codec").GetString());
        Assert.False(parameters.GetProperty("with_timestamps").GetBoolean());
        Assert.Equal("translate", parameters.GetProperty("mode").GetString());
        Assert.True(parameters.GetProperty("with_diarization").GetBoolean());
        Assert.Equal(2, parameters.GetProperty("num_speakers").GetInt32());
        Assert.Equal(13, parameters.EnumerateObject().Count());
        Assert.False(parameters.TryGetProperty("another-provider", out _));
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), parameters.GetProperty(name)), name);
        }
    }

    [Fact]
    public async Task Transcription_preserves_defaults_omissions_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/speech-to-text/job/v1")
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        });

        var provider = CreateProvider(client);
        await provider.TranscriptionRequest(CreateRequest(default, model: ""));

        var parameters = payload.GetProperty("job_parameters");
        Assert.Equal("saaras:v4", parameters.GetProperty("model").GetString());
        Assert.Equal("unknown", parameters.GetProperty("language_code").GetString());
        Assert.Equal("transcribe", parameters.GetProperty("mode").GetString());
        Assert.True(parameters.GetProperty("with_timestamps").GetBoolean());
        Assert.False(parameters.GetProperty("with_diarization").GetBoolean());
        Assert.False(parameters.TryGetProperty("num_speakers", out _));
        Assert.False(parameters.TryGetProperty("input_audio_codec", out _));

        await provider.TranscriptionRequest(CreateRequest(
            ParseElement("""{"model":null,"language_code":null,"input_audio_codec":null,"with_timestamps":null,"mode":null,"with_diarization":null,"num_speakers":null,"future_null":null}"""),
            model: ""));
        parameters = payload.GetProperty("job_parameters");
        Assert.Equal("saaras:v4", parameters.GetProperty("model").GetString());
        Assert.Equal("unknown", parameters.GetProperty("language_code").GetString());
        Assert.Equal("transcribe", parameters.GetProperty("mode").GetString());
        Assert.True(parameters.GetProperty("with_timestamps").GetBoolean());
        Assert.False(parameters.GetProperty("with_diarization").GetBoolean());
        Assert.Equal(JsonValueKind.Null, parameters.GetProperty("future_null").ValueKind);
        Assert.False(parameters.TryGetProperty("num_speakers", out _));
        Assert.False(parameters.TryGetProperty("input_audio_codec", out _));

        var requestCount = 0;
        using var unusedClient = CreateClient(_ => requestCount++);
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient)
            .TranscriptionRequest(CreateRequest(ParseElement("""{"language_code":42}"""))));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient)
            .TranscriptionRequest(CreateRequest(ParseElement("""["invalid metadata root"]"""))));
        Assert.Equal(0, requestCount);

        using var failedClient = CreateClient(failInitialRequest: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).TranscriptionRequest(CreateRequest(default)));
        Assert.Equal("Sarvam batch STT failed (400): upstream failure", error.Message);
    }

    private static TranscriptionRequest CreateRequest(JsonElement metadata, string model = "request-model")
        => new()
        {
            Model = model,
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement>
                {
                    ["sarvam"] = metadata,
                    ["another-provider"] = ParseElement("""{"secret":true}""")
                }
        };

    private static SarvamProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(
        Action<HttpRequestMessage>? observe = null,
        bool failInitialRequest = false)
        => new(new DelegateHandler(request =>
        {
            observe?.Invoke(request);
            var path = request.RequestUri!.AbsolutePath;
            if (failInitialRequest && request.Method == HttpMethod.Post && path == "/speech-to-text/job/v1")
                return JsonResponse("upstream failure", HttpStatusCode.BadRequest);

            if (request.Method == HttpMethod.Post && path == "/speech-to-text/job/v1")
                return JsonResponse("""{"job_id":"job-1"}""");
            if (request.Method == HttpMethod.Post && path == "/speech-to-text/job/v1/upload-files")
                return JsonResponse("""{"upload_urls":{"audio.wav":{"file_url":"https://storage.example/upload"}}}""");
            if (request.Method == HttpMethod.Put && path == "/upload")
                return JsonResponse("{}");
            if (request.Method == HttpMethod.Post && path == "/speech-to-text/job/v1/job-1/start")
                return JsonResponse("{}");
            if (request.Method == HttpMethod.Get && path == "/speech-to-text/job/v1/job-1/status")
                return JsonResponse("""{"job_state":"Completed","job_details":[{"outputs":[{"file_name":"result.json"}]}]}""");
            if (request.Method == HttpMethod.Post && path == "/speech-to-text/job/v1/download-files")
                return JsonResponse("""{"download_urls":{"result.json":{"file_url":"https://storage.example/download"}}}""");
            if (request.Method == HttpMethod.Get && path == "/download")
                return JsonResponse("""{"transcript":"hello","language_code":"en"}""");

            return JsonResponse("{}", HttpStatusCode.NotFound);
        }));

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
