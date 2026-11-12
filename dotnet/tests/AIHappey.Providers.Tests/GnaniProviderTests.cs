using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Gnani;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests;

public sealed class GnaniProviderTests
{
    [Fact]
    public void Provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<ProviderRegistry>().HasModelProvider("gnani"));
    }

    [Theory]
    [InlineData("gnani/prisma-v3.0", "prisma-v3.0")]
    [InlineData("gnani/gnani-prisma-v2.5", null)]
    public async Task Transcription_sends_multipart_options_and_maps_response(string model, string? nativeModel)
    {
        var fields = new Dictionary<string, string>();
        byte[]? audio = null;
        using var client = CreateClient(async message =>
        {
            Assert.Equal("https://api.vachana.ai/stt/v3", message.RequestUri!.ToString());
            Assert.Equal("test-key", message.Headers.GetValues("X-API-Key-ID").Single());
            foreach (var part in Assert.IsType<MultipartFormDataContent>(message.Content))
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "audio_file")
                    audio = await part.ReadAsByteArrayAsync();
                else
                    fields.Add(name, await part.ReadAsStringAsync());
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"success":true,"transcript":"नमस्ते","detected_language_code":"hi-IN","request_id":"r1"}""",
                    Encoding.UTF8, "application/json")
            };
        });
        var result = await CreateProvider(client).TranscriptionRequest(new()
        {
            Model = model,
            Audio = Convert.ToBase64String([1, 2, 3]),
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["gnani"] = Parse("""{"language_code":"hi-IN","keyterms":["खाता","bank"],"enable_substitution":false,"future":{"x":1}}""")
            }
        });

        Assert.Equal(new byte[] { 1, 2, 3 }, audio);
        Assert.Equal("hi-IN", fields["language_code"]);
        Assert.True(JsonElement.DeepEquals(Parse("""["खाता","bank"]"""), Parse(fields["keyterms"])));
        Assert.Equal("false", fields["enable_substitution"]);
        Assert.True(JsonElement.DeepEquals(Parse("""{"x":1}"""), Parse(fields["future"])));
        if (nativeModel is null)
            Assert.False(fields.ContainsKey("model"));
        else
            Assert.Equal(nativeModel, fields["model"]);
        Assert.Equal("नमस्ते", result.Text);
        Assert.Equal("hi-IN", result.Language);
        Assert.False(client.DefaultRequestHeaders.Contains("X-API-Key-ID"));
    }

    [Fact]
    public async Task Speech_sends_native_metadata_and_returns_binary_audio()
    {
        JsonElement payload = default;
        using var client = CreateClient(async message =>
        {
            Assert.Equal("https://api.vachana.ai/api/v1/tts/inference", message.RequestUri!.ToString());
            Assert.Equal("test-key", message.Headers.GetValues("X-API-Key-ID").Single());
            payload = Parse(await message.Content!.ReadAsStringAsync());
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            return new(HttpStatusCode.OK) { Content = content };
        });
        var response = await CreateProvider(client).SpeechRequest(new()
        {
            Model = "gnani/vachana-vc-v1",
            Text = "Hello",
            ProviderOptions = new()
            {
                ["gnani"] = Parse("""{"speaker_embedding":{"embedding":"abc","shape":[1,768],"dtype":"torch.bfloat16"},"audio_config":{"sample_rate":44100,"container":"wav"},"future":{"enabled":true}}""")
            }
        });

        Assert.Equal("vachana-vc-v1", payload.GetProperty("model").GetString());
        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal("abc", payload.GetProperty("speaker_embedding").GetProperty("embedding").GetString());
        Assert.Equal(44100, payload.GetProperty("audio_config").GetProperty("sample_rate").GetInt32());
        Assert.True(payload.GetProperty("future").GetProperty("enabled").GetBoolean());
        Assert.Equal("AQID", response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.False(client.DefaultRequestHeaders.Contains("X-API-Key-ID"));
    }

    [Fact]
    public async Task Prisma_v3_transliteration_can_omit_language_code()
    {
        using var client = CreateClient(async message =>
        {
            var names = Assert.IsType<MultipartFormDataContent>(message.Content)
                .Select(part => part.Headers.ContentDisposition!.Name!.Trim('"'))
                .ToArray();
            Assert.DoesNotContain("language_code", names);
            await Task.CompletedTask;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true,"transcript":"namaste"}""")
            };
        });

        var result = await CreateProvider(client).TranscriptionRequest(new()
        {
            Model = "gnani/prisma-v3.0",
            Audio = "AQID",
            MediaType = "audio/wav",
            ProviderOptions = new() { ["gnani"] = Parse("""{"format":"transliterate"}""") }
        });
        Assert.Equal("namaste", result.Text);
    }

    [Fact]
    public async Task Upstream_errors_are_not_reported_as_success()
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(client).SpeechRequest(
            new SpeechRequest { Model = "gnani/timbre-v2.5", Text = "Hello", Voice = "Nalini" }));
        Assert.Contains("upstream failure", error.Message);
    }

    [Fact]
    public async Task Speech_rejects_invalid_native_options_before_sending()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(client).SpeechRequest(new()
        {
            Model = "gnani/timbre-v2.5",
            Text = "Hello",
            ProviderOptions = new() { ["gnani"] = Parse("""{"speed":"fast"}""") }
        }));
    }

    [Fact]
    public async Task Speech_rejects_json_instead_of_audio()
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"success":false,"message":"invalid voice"}""", Encoding.UTF8, "application/json")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(client).SpeechRequest(new()
        {
            Model = "gnani/timbre-v2.5",
            Text = "Hello",
            Voice = "Nalini"
        }));
        Assert.Contains("invalid voice", error.Message);
    }

    [Fact]
    public async Task Transcription_rejects_unsuccessful_json_responses()
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"success":false,"message":"invalid audio"}""", Encoding.UTF8, "application/json")
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(client).TranscriptionRequest(
            new TranscriptionRequest
            {
                Model = "gnani/prisma-v3.0",
                Audio = "AQID",
                MediaType = "audio/wav",
                ProviderOptions = new() { ["gnani"] = Parse("""{"language_code":"hi-IN"}""") }
            }));
        Assert.Contains("invalid audio", error.Message);
    }

    [Fact]
    public async Task Openai_transcription_maps_language_to_native_language_code()
    {
        using var client = CreateClient(async message =>
        {
            var parts = Assert.IsType<MultipartFormDataContent>(message.Content);
            var language = parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "language_code");
            Assert.Equal("hi-IN", await language.ReadAsStringAsync());
            Assert.DoesNotContain(parts, part => part.Headers.ContentDisposition!.Name!.Trim('"') == "language");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true,"transcript":"नमस्ते"}""")
            };
        });
        using var file = new MemoryStream([1, 2, 3]);
        var result = await CreateProvider(client).OpenAITranscriptionRequestAsync(new()
        {
            Model = "gnani/prisma-v3.0",
            Language = "hi",
            File = new FormFile(file, 0, file.Length, "file", "test.wav")
            {
                Headers = new HeaderDictionary(),
                ContentType = "audio/wav"
            }
        });
        Assert.Equal("नमस्ते", result.Text);
    }

    [Fact]
    public async Task Openai_speech_forwards_native_voice_cloning_options()
    {
        using var client = CreateClient(async message =>
        {
            var payload = Parse(await message.Content!.ReadAsStringAsync());
            Assert.Equal("abc", payload.GetProperty("speaker_embedding").GetProperty("embedding").GetString());
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        var (audio, _) = await CreateProvider(client).OpenAISpeechRequestAsync(new()
        {
            Model = "gnani/vachana-vc-v1",
            Input = "Hello",
            AdditionalProperties = new()
            {
                ["speaker_embedding"] = Parse("""{"embedding":"abc","shape":[1,768],"dtype":"torch.bfloat16"}""")
            }
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, audio);
    }

    [Fact]
    public async Task Streaming_speech_emits_native_sse_audio_chunks()
    {
        using var client = CreateClient(async message =>
        {
            Assert.Equal("https://api.vachana.ai/api/v1/tts/sse", message.RequestUri!.ToString());
            Assert.Equal("test-key", message.Headers.GetValues("X-API-Key-ID").Single());
            var payload = Parse(await message.Content!.ReadAsStringAsync());
            Assert.Equal("Hello world", payload.GetProperty("text").GetString());
            Assert.Equal("Nalini", payload.GetProperty("voice").GetString());
            Assert.True(payload.GetProperty("future").GetBoolean());
            Assert.Equal(24000, payload.GetProperty("audio_config").GetProperty("sample_rate").GetInt32());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: start\ndata: {\"status\":\"streaming_started\"}\n\n" +
                    "event: chunk\ndata: {\"chunk_index\":1,\"audio\":\"AQID\",\"is_final\":false}\n\n" +
                    "event: complete\ndata: {\"chunk_index\":2,\"audio\":\"BAU=\",\"is_final\":true}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        });
        var parts = new List<StreamingSpeechPart>();
        await foreach (var part in CreateProvider(client).SpeechStreamingAsync(new()
        {
            Model = "gnani/timbre-v2.5",
            Voice = "Nalini",
            Text = [new() { Text = "Hello " }, new() { Text = "world" }],
            OutputFormat = new() { Type = "wav", Rate = 24000 },
            ProviderOptions = new() { ["gnani"] = Parse("""{"future":true}""") }
        }))
            parts.Add(part);

        var audio = parts.OfType<SpeechAudioPart>().ToArray();
        Assert.Equal(new[] { "AQID", "BAU=" }, audio.Select(part => part.Audio));
        Assert.All(audio, part => Assert.Equal(24000, part.AudioFormat.Rate));
        Assert.Single(parts.OfType<SpeechFinishPart>());
    }

    [Theory]
    [InlineData("event: error\ndata: {\"type\":\"error\",\"message\":\"bad configuration\"}\n\n")]
    [InlineData("event: chunk\ndata: {\"audio\":\"AQID\",\"is_final\":false}\n\n")]
    public async Task Streaming_speech_rejects_error_or_incomplete_events(string events)
    {
        using var client = CreateClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in CreateProvider(client).SpeechStreamingAsync(new()
            {
                Model = "gnani/timbre-v2.5",
                Voice = "Nalini",
                Text = [new() { Text = "Hello" }]
            })) { }
        });
    }

    private static GnaniProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new TestHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        => new(new TestHandler(send));

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
