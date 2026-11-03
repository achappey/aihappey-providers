using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Gradium;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class GradiumStreamingSpeechTests
{
    [Fact]
    public async Task Gradium_streams_WebSocket_audio_with_voice_settings_and_raw_events()
    {
        var socket = new ScriptedWebSocket(
            Text("""{"type":"ready"}"""),
            Text("""{"type":"audio","audio":"AQI="}"""),
            Text("""{"type":"text","text":"Hello world."}"""),
            Text("""{"type":"end_of_stream"}"""));
        Uri? connectedUri = null;
        string? connectedKey = null;
        var provider = new GradiumProvider(new StaticApiKeyResolver(), 
            new Core.AI.AsyncCacheHelper(),
            new StaticHttpClientFactory());
        provider.SpeechSocketFactory = (uri, key, _) =>
        {
            connectedUri = uri;
            connectedKey = key;
            return Task.FromResult<WebSocket>(socket);
        };
        var metadata = JsonDocument.Parse("""
            {
              "json_config":{"temp":0.3},
              "future":{"enabled":true}
            }
            """).RootElement.Clone();
        var request = new StreamingSpeechRequest
        {
            Model = "gradium/default/voice-1",
            Text = [new() { Text = "Hello " }, new() { Text = "world." }],
            OutputFormat = new() { Type = "pcm", Rate = 24000 },
            ProviderOptions = new() { ["gradium"] = metadata },
            IncludeRawChunks = true
        };
        var originalOptions = request.ProviderOptions["gradium"].Clone();

        var parts = await CollectAsync(provider.SpeechStreamingAsync(request));

        Assert.Equal("wss://eu.api.gradium.ai/api/speech/tts", connectedUri!.ToString());
        Assert.Equal("test-key", connectedKey);
        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["gradium"]));
        Assert.Equal(3, socket.SentText.Count);
        using (var setup = JsonDocument.Parse(socket.SentText[0]))
        {
            var root = setup.RootElement;
            Assert.Equal("setup", root.GetProperty("type").GetString());
            Assert.Equal("default", root.GetProperty("model_name").GetString());
            Assert.Equal("voice-1", root.GetProperty("voice_id").GetString());
            Assert.Equal("pcm_24000", root.GetProperty("output_format").GetString());
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty("json_config"), root.GetProperty("json_config")));
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future"), root.GetProperty("future")));
        }
        Assert.Equal(
            """{"type":"text","text":"Hello world."}""",
            socket.SentText[1]);
        Assert.Equal("""{"type":"end_of_stream"}""", socket.SentText[2]);

        var audio = Assert.Single(parts.OfType<SpeechAudioPart>());
        Assert.Equal(Convert.ToBase64String([1, 2]), audio.Audio);
        Assert.Equal("audio/pcm", audio.AudioFormat.Type);
        Assert.Equal(24000, audio.AudioFormat.Rate);
        Assert.Equal(4, parts.OfType<SpeechRawPart>().Count());
        Assert.Single(parts.OfType<SpeechStreamStartPart>());
        Assert.Collection(parts.OfType<SpeechResponseMetadataPart>(),
            ready => AssertResponseMetadata(ready, request.Model, """{"type":"ready"}"""),
            text => AssertResponseMetadata(text, request.Model, """{"type":"text","text":"Hello world."}"""));
        var finish = Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.NotNull(finish.ProviderMetadata);
        Assert.Equal(4, finish.ProviderMetadata["gradium"].GetProperty("events").GetArrayLength());
        Assert.Equal(WebSocketMessageType.Text, socket.SentTypes[0]);
        Assert.Equal(WebSocketMessageType.Text, socket.SentTypes[1]);
        Assert.Equal(WebSocketMessageType.Text, socket.SentTypes[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gradium_emits_only_ready_metadata_without_text_or_flush_events(bool includeRawChunks)
    {
        string[] sourceEvents =
        [
            """{"type":"ready","request_id":"request-1"}""",
            """{"type":"audio","audio":"AQI="}""",
            """{"type":"end_of_stream"}"""
        ];
        var socket = new ScriptedWebSocket(sourceEvents.Select(Text).ToArray());
        var provider = new GradiumProvider(new StaticApiKeyResolver(),
            new Core.AI.AsyncCacheHelper(),
            new StaticHttpClientFactory())
        {
            SpeechSocketFactory = (_, _, _) => Task.FromResult<WebSocket>(socket)
        };
        var request = new StreamingSpeechRequest
        {
            Model = "gradium/default/voice-1",
            Text = [new() { Text = "Hello world." }],
            IncludeRawChunks = includeRawChunks
        };

        var parts = await CollectAsync(provider.SpeechStreamingAsync(request));

        var metadata = Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        AssertResponseMetadata(metadata, request.Model, sourceEvents[0]);
        Assert.Single(parts.OfType<SpeechAudioPart>());
        AssertStreamEvents(parts, sourceEvents, includeRawChunks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gradium_preserves_repeated_text_and_flush_metadata_in_order(bool includeRawChunks)
    {
        string[] sourceEvents =
        [
            """{"type":"ready","request_id":"request-1"}""",
            """{"type":"audio","audio":"AQI="}""",
            """{"type":"text","text":"Hello ","start":0,"stop":0.1}""",
            """{"type":"text","text":"world.","start":0.1,"stop":0.3}""",
            """{"type":"flushed","flush_id":1}""",
            """{"type":"end_of_stream"}"""
        ];
        var socket = new ScriptedWebSocket(sourceEvents.Select(Text).ToArray());
        var provider = new GradiumProvider(new StaticApiKeyResolver(),
            new Core.AI.AsyncCacheHelper(),
            new StaticHttpClientFactory())
        {
            SpeechSocketFactory = (_, _, _) => Task.FromResult<WebSocket>(socket)
        };
        var request = new StreamingSpeechRequest
        {
            Model = "gradium/default/voice-1",
            Text = [new() { Text = "Hello world." }],
            IncludeRawChunks = includeRawChunks
        };

        var parts = await CollectAsync(provider.SpeechStreamingAsync(request));

        Assert.Collection(parts.OfType<SpeechResponseMetadataPart>(),
            ready => AssertResponseMetadata(ready, request.Model, sourceEvents[0]),
            firstText => AssertResponseMetadata(firstText, request.Model, sourceEvents[2]),
            secondText => AssertResponseMetadata(secondText, request.Model, sourceEvents[3]),
            flushed => AssertResponseMetadata(flushed, request.Model, sourceEvents[4]));
        Assert.Single(parts.OfType<SpeechAudioPart>());
        AssertStreamEvents(parts, sourceEvents, includeRawChunks);
    }

    private static void AssertResponseMetadata(
        SpeechResponseMetadataPart metadata,
        string model,
        string expectedBody)
    {
        Assert.Equal(model, metadata.ModelId);
        Assert.NotNull(metadata.Timestamp);
        var body = Assert.IsType<JsonElement>(metadata.Body);
        using var expected = JsonDocument.Parse(expectedBody);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, body));
    }

    private static void AssertStreamEvents(
        List<StreamingSpeechPart> parts,
        string[] sourceEvents,
        bool includeRawChunks)
    {
        var start = Assert.Single(parts.OfType<SpeechStreamStartPart>());
        Assert.Same(start, parts[0]);
        var finish = Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.Same(finish, parts[^1]);
        Assert.NotNull(finish.ProviderMetadata);
        var events = finish.ProviderMetadata["gradium"].GetProperty("events");
        Assert.Equal(sourceEvents.Length, events.GetArrayLength());
        var rawParts = parts.OfType<SpeechRawPart>().ToArray();
        Assert.Equal(includeRawChunks ? sourceEvents.Length : 0, rawParts.Length);
        for (var i = 0; i < sourceEvents.Length; i++)
        {
            using var expected = JsonDocument.Parse(sourceEvents[i]);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, events[i]));
            if (includeRawChunks)
            {
                var raw = Assert.IsType<JsonElement>(rawParts[i].RawValue);
                Assert.True(JsonElement.DeepEquals(expected.RootElement, raw));
            }
        }
    }

    private static async Task<List<StreamingSpeechPart>> CollectAsync(
        IAsyncEnumerable<StreamingSpeechPart> stream)
    {
        var parts = new List<StreamingSpeechPart>();
        await foreach (var part in stream)
            parts.Add(part);
        return parts;
    }

    private static SocketMessage Text(string value)
        => new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(value));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed record SocketMessage(WebSocketMessageType Type, byte[] Payload);

    private sealed class ScriptedWebSocket(params SocketMessage[] messages) : WebSocket
    {
        private readonly Queue<SocketMessage> _messages = new(messages);
        private WebSocketState _state = WebSocketState.Open;

        public List<string> SentText { get; } = [];
        public List<WebSocketMessageType> SentTypes { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_messages.Count == 0)
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

            var message = _messages.Dequeue();
            if (message.Payload.Length > buffer.Count)
                throw new InvalidOperationException("The scripted message exceeds the test buffer.");
            message.Payload.AsSpan().CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(message.Payload.Length, message.Type, true));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SentTypes.Add(messageType);
            if (messageType == WebSocketMessageType.Text)
                SentText.Add(Encoding.UTF8.GetString(buffer.AsSpan()));
            return Task.CompletedTask;
        }
    }
}
