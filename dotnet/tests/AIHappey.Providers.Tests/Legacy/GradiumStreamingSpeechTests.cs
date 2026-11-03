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
        var provider = new GradiumProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory());
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
        Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        var finish = Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.Equal(4, finish.ProviderMetadata["gradium"].GetProperty("events").GetArrayLength());
        Assert.Equal(WebSocketMessageType.Text, socket.SentTypes[0]);
        Assert.Equal(WebSocketMessageType.Text, socket.SentTypes[1]);
        Assert.Equal(WebSocketMessageType.Text, socket.SentTypes[2]);
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
