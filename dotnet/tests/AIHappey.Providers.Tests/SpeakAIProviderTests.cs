using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SpeakAI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class SpeakAIProviderTests
{
    [Fact]
    public async Task Transcription_uploads_polls_and_passes_native_options_through()
    {
        var calls = new List<string>();
        using var client = Client(async request =>
        {
            calls.Add($"{request.Method} {request.RequestUri!.Host}{request.RequestUri.AbsolutePath}");
            switch (request.RequestUri.AbsolutePath)
            {
                case "/v1/media/upload/signedurl":
                    Assert.Equal("test-key", request.Headers.GetValues("x-speakai-key").Single());
                    return Json("""{"status":"success","data":{"preSignedUrl":"https://storage.example/f.mp3?sig=abc"}}""");
                case "/f.mp3":
                    Assert.False(request.Headers.Contains("x-speakai-key"));
                    Assert.Equal([1, 2, 3], await request.Content!.ReadAsByteArrayAsync());
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case "/v1/media/upload":
                    var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                    Assert.Equal("https://storage.example/f.mp3", payload.GetProperty("url").GetString());
                    Assert.Equal("nl-NL", payload.GetProperty("sourceLanguage").GetString());
                    Assert.True(payload.GetProperty("future").GetProperty("on").GetBoolean());
                    Assert.False(payload.TryGetProperty("deleteMedia", out _));
                    return Json("""{"data":{"mediaId":"m1"}}""");
                case "/v1/media/status/m1":
                    return Json("""{"data":{"state":"processed"}}""");
                case "/v1/media/transcript/m1":
                    return Json("""
                        {"data":{"sourceLanguage":"nl-NL","duration":{"inSecond":4.5},
                        "insight":{"transcript":[
                          {"text":"Hallo","instances":[{"startInSec":0.5,"endInSec":1.5}]},
                          {"text":"wereld","instances":[{"startInSec":2,"endInSec":4.5}]}]}}}
                        """);
                case "/v1/media/m1":
                    return Json("{}");
            }
            throw new InvalidOperationException(request.RequestUri.ToString());
        });

        var response = await Provider(client).TranscriptionRequest(new TranscriptionRequest
        {
            Model = "speakai/transcription", MediaType = "audio/mpeg",
            Audio = Convert.ToBase64String([1, 2, 3]),
            ProviderOptions = new()
            {
                ["speakai"] = JsonSerializer.SerializeToElement(new
                {
                    sourceLanguage = "nl-NL", future = new { on = true }, deleteMedia = true
                })
            }
        });

        Assert.Equal("Hallo wereld", response.Text);
        Assert.Equal("nl-NL", response.Language);
        Assert.Equal(4.5f, response.DurationInSeconds);
        Assert.Equal(2, response.Segments!.Count());
        Assert.Equal(2f, response.Segments!.Last().StartSecond);
        Assert.Equal("m1", response.ProviderMetadata!["speakai"].GetProperty("mediaId").GetString());
        Assert.Equal("DELETE api.speakai.co/v1/media/m1", calls.Last());
        Assert.Empty(client.DefaultRequestHeaders);
    }

    [Fact]
    public async Task Chat_sends_prompt_with_native_options_and_returns_answer()
    {
        using var client = Client(async request =>
        {
            Assert.Equal("https://api.speakai.co/v1/prompt", request.RequestUri!.ToString());
            var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("Be brief.\n\nSummarize", payload.GetProperty("prompt").GetString());
            Assert.Equal("m1", payload.GetProperty("mediaIds")[0].GetString());
            return Json("""{"status":"success","data":{"promptId":"p1","answer":"Done."}}""");
        });

        var response = await Provider(client).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "speakai", Model = "speakai/ai-chat", Instructions = "Be brief.",
            Input = new() { Text = "Summarize" },
            Metadata = new() { ["speakai"] = JsonSerializer.SerializeToElement(new { mediaIds = new[] { "m1" } }) }
        });

        Assert.Equal("Done.", response.Output!.Items![0].Content!.OfType<AITextContentPart>().Single().Text);
    }

    [Fact]
    public async Task Realtime_token_reads_jwt_expiry_and_keeps_native_response()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"exp":{{exp}}}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var client = Client(request =>
        {
            Assert.Equal("https://api.speakai.co/v1/live-transcription/token", request.RequestUri!.ToString());
            return Task.FromResult(Json($$$"""{"data":{"token":"h.{{{payload}}}.s","extra":1}}"""));
        });

        var response = await Provider(client).GetRealtimeToken(new RealtimeRequest { Model = "speakai/live-transcription" });

        Assert.Equal($"h.{payload}.s", response.Value);
        Assert.Equal(exp, response.ExpiresAt);
        var metadata = response.ProviderMetadata!["speakai"];
        Assert.Equal(1, metadata.GetProperty("data").GetProperty("extra").GetInt32());
        Assert.False(metadata.GetProperty("data").TryGetProperty("token", out _));
        Assert.Equal("/v1/live", metadata.GetProperty("connection").GetProperty("namespace").GetString());
    }

    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<ProviderRegistry>().HasModelProvider("speakai"));
    }

    private static SpeakAIProvider Provider(HttpClient client) => new(new Keys(), new Clients(client));
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
