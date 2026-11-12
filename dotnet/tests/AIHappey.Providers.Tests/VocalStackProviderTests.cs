using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.VocalStack;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class VocalStackProviderTests
{
    [Fact]
    public async Task Transcription_uploads_provider_options_polls_and_reads_sentences()
    {
        var metadata = ParseElement("""
            {
              "name": "meeting",
              "settings": {
                "spokenLanguage": "en",
                "speakerLabels": "on"
              },
              "futureOption": {"enabled": true}
            }
            """);
        var calls = new List<string>();
        using var client = CreateClient(message =>
        {
            calls.Add($"{message.Method} {message.RequestUri!.PathAndQuery}");
            Assert.Equal("Bearer test-api-key", message.Headers.Authorization!.ToString());

            if (message.Method == HttpMethod.Post)
            {
                Assert.True(message.Headers.Contains("Idempotency-Key"));
                Assert.IsType<MultipartFormDataContent>(message.Content);
                var parts = ((MultipartFormDataContent)message.Content!)
                    .ToDictionary(
                        part => part.Headers.ContentDisposition!.Name!.Trim('"'),
                        part => part.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert.Equal("application/json", ((MultipartFormDataContent)message.Content!)
                    .Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "options")
                    .Headers.ContentType!.MediaType);
                var filePart = ((MultipartFormDataContent)message.Content!).Single(part =>
                    part.Headers.ContentDisposition!.Name!.Trim('"') == "file");
                Assert.Equal("audio.wav", filePart.Headers.ContentDisposition!.FileName?.Trim('"'));
                Assert.True(JsonElement.DeepEquals(metadata, ParseElement(parts["options"])));
                return JsonResponse("""{"job":{"recordingId":"rec-123"}}""", HttpStatusCode.Created);
            }

            if (message.RequestUri.AbsolutePath.EndsWith("/job", StringComparison.Ordinal))
                return JsonResponse("""{"state":"done"}""");

            return JsonResponse("""
                {
                  "sentences": [
                    {"text":"Hello there.","start":0.2,"end":0.9},
                    {"text":"How are you?","start":1.1,"end":1.8}
                  ],
                  "spokenLanguage":"en"
                }
                """);
        });

        var request = new TranscriptionRequest
        {
            Model = "general",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio bytes")),
            MediaType = "audio/wav",
            ProviderOptions = new() { ["vocalstack"] = metadata }
        };
        var response = await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal("Hello there. How are you?", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal(2, response.Segments.Count());
        Assert.Equal(0.2f, response.Segments.First().StartSecond);
        Assert.Equal("GET /v1/recordings/rec-123/transcript", calls[^1]);
        Assert.Contains("GET /v1/recordings/rec-123/job", calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Request_language_is_merged_without_dropping_metadata_options()
    {
        var metadata = ParseElement("""{"name":"clip","settings":{"speakerLabels":"on"},"custom":42}""");
        string? submittedOptions = null;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                var parts = (MultipartFormDataContent)message.Content!;
                submittedOptions = parts.Single(part =>
                    part.Headers.ContentDisposition!.Name!.Trim('"') == "options")
                    .ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse("""{"job":{"recordingId":"rec-456"}}""", HttpStatusCode.Created);
            }
            if (message.RequestUri!.AbsolutePath.EndsWith("/job", StringComparison.Ordinal))
                return JsonResponse("""{"state":"done"}""");
            return JsonResponse("""{"sentences":[]}""");
        });
        var request = new TranscriptionRequest
        {
            Model = "general",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            Language = "fr",
            ProviderOptions = new() { ["vocalstack"] = metadata }
        };

        await CreateProvider(client).TranscriptionRequest(request);

        using var options = JsonDocument.Parse(submittedOptions!);
        Assert.Equal("clip", options.RootElement.GetProperty("name").GetString());
        Assert.Equal(42, options.RootElement.GetProperty("custom").GetInt32());
        Assert.Equal("fr", options.RootElement.GetProperty("settings").GetProperty("spokenLanguage").GetString());
        Assert.Equal("on", options.RootElement.GetProperty("settings").GetProperty("speakerLabels").GetString());
    }

    private static VocalStackProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
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
