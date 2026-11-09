using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Deepgram;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class DeepgramProviderStreamingSpeechTests
{
    [Fact]
    public async Task Deepgram_streams_WebSocket_audio_and_preserves_query_options_and_events()
    {
        var socket = new ScriptedWebSocket(
            Text("""{"type":"Metadata","request_id":"request-1","model_name":"aura-2-thalia-en"}"""),
            Binary([1, 2, 3]),
            Text("""{"type":"Warning","code":"TEXT_LENGTH_WARNING"}"""),
            Text("""{"type":"Flushed","sequence_id":1}"""));
        Uri? connectedUri = null;
        string? connectedKey = null;
        var provider = new DeepgramProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory());
        provider.SpeechSocketFactory = (uri, key, _) =>
        {
            connectedUri = uri;
            connectedKey = key;
            return Task.FromResult<WebSocket>(socket);
        };
        var metadata = JsonDocument.Parse("""
            {
              "encoding":"linear16",
              "sample_rate":16000,
              "tag":["speech test","regression"],
              "future":{"enabled":true}
            }
            """).RootElement.Clone();
        var request = new StreamingSpeechRequest
        {
            Model = "aura-2-thalia-en",
            Text = [new() { Text = "Hello " }, new() { Text = "world!" }],
            Speed = 1.1f,
            OutputFormat = new() { Type = "pcm", Rate = 24000 },
            ProviderOptions = new() { ["deepgram"] = metadata },
            IncludeRawChunks = true
        };
        var originalOptions = request.ProviderOptions["deepgram"].Clone();

        var parts = await CollectAsync(provider.SpeechStreamingAsync(request));

        Assert.Equal("wss://api.deepgram.com/v1/speak", connectedUri!.GetLeftPart(UriPartial.Path));
        Assert.Equal("test-key", connectedKey);
        Assert.Contains("model=aura-2-thalia-en", connectedUri.Query);
        Assert.Contains("encoding=linear16", connectedUri.Query);
        Assert.Contains("sample_rate=16000", connectedUri.Query);
        Assert.Contains("speed=1.1", connectedUri.Query);
        Assert.Contains("tag=speech%20test", connectedUri.Query);
        Assert.Contains("tag=regression", connectedUri.Query);
        Assert.Contains("future=%7B%22enabled%22%3Atrue%7D", connectedUri.Query);
        Assert.Equal(
            new[]
            {
                """{"type":"Speak","text":"Hello world!"}""",
                """{"type":"Flush"}""",
                """{"type":"Close"}"""
            },
            socket.SentText);
        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["deepgram"]));

        var audio = Assert.Single(parts.OfType<SpeechAudioPart>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), audio.Audio);
        Assert.Equal("audio/L16", audio.AudioFormat.Type);
        Assert.Equal(16000, audio.AudioFormat.Rate);
        Assert.Equal(4, parts.OfType<SpeechRawPart>().Count());
        Assert.Single(parts.OfType<SpeechStreamStartPart>());
        Assert.Single(parts.OfType<SpeechResponseMetadataPart>());
        var finish = Assert.Single(parts.OfType<SpeechFinishPart>());
        Assert.NotNull(finish.ProviderMetadata);
        Assert.Equal(3, finish.ProviderMetadata["deepgram"].GetProperty("events").GetArrayLength());
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

    private static SocketMessage Binary(byte[] value)
        => new(WebSocketMessageType.Binary, value);

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
