using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.EverypixelLabs;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class EverypixelLabsAuthenticationTests
{
    [Fact]
    public async Task Submissions_encode_client_credentials_and_keep_authentication_off_status_and_download_requests()
    {
        var expectedParameter = Convert.ToBase64String(Encoding.UTF8.GetBytes("client-id:client-secret"));
        using var client = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath is "/v1/image_generate" or "/v1/video_generate"
                or "/v1/tts_create" or "/v1/transcribe")
            {
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                Assert.Equal(expectedParameter, request.Headers.Authorization?.Parameter);
                var taskId = request.RequestUri.AbsolutePath switch
                {
                    "/v1/image_generate" => "image-task",
                    "/v1/video_generate" => "video-task",
                    "/v1/tts_create" => "speech-task",
                    _ => "transcription-task"
                };
                return Task.FromResult(JsonResponse($$"""{"task_id":"{{taskId}}"}"""));
            }

            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri.AbsolutePath == "/v1/status")
            {
                var response = request.RequestUri.Query switch
                {
                    "?task_id=image-task" => """{"status":"SUCCESS","result":{"url":"https://files.example/image.png"}}""",
                    "?task_id=speech-task" => """{"status":"SUCCESS","result":"https://files.example/audio.wav"}""",
                    "?task_id=transcription-task" => """{"status":"SUCCESS","result":"recognized words"}""",
                    _ => throw new InvalidOperationException($"Unexpected task status request: {request.RequestUri}")
                };
                return Task.FromResult(JsonResponse(response));
            }

            Assert.Equal("files.example", request.RequestUri.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        });
        var provider = new EverypixelLabsProvider(
            new TestKeyResolver("client-id:client-secret"),
            new TestHttpClientFactory(client));

        await provider.ImageRequest(new ImageRequest
        {
            Model = "everypixellabs/flux2",
            Prompt = "A landscape"
        });
        await provider.StartVideoOperation(new VideoRequest
        {
            Model = "everypixellabs/ltx23",
            Prompt = "A landscape",
            Duration = 5
        });
        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "everypixellabs/tts_create",
            Text = "Hello",
            Voice = "speaker-1"
        });
        var transcription = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "everypixellabs/transcribe",
            Audio = JsonSerializer.SerializeToElement("AQID"),
            MediaType = "audio/wav"
        });

        Assert.Equal("recognized words", transcription.Text);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Existing_basic_auth_tokens_remain_unchanged()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/video_generate", request.RequestUri!.AbsolutePath);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("pre-encoded-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse("""{"task_id":"video-task"}"""));
        });
        var provider = new EverypixelLabsProvider(
            new TestKeyResolver("pre-encoded-token"),
            new TestHttpClientFactory(client));

        await provider.StartVideoOperation(new VideoRequest
        {
            Model = "everypixellabs/ltx23",
            Prompt = "A landscape",
            Duration = 5
        });

        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class TestKeyResolver(string key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
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
