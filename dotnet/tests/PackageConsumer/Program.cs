using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// This executable is copied to an isolated directory and restored from packages only.
// It intentionally never creates a web host, HTTP context, application storage, or a live network connection.
var factory = new FakeFactory();
var client = new ProviderClient(factory);
IReadOnlyDictionary<string, string> keys = new Dictionary<string, string>
{
    ["inceptron"] = "compatible-key", ["anthropic"] = "native-key", ["openai"] = "media-key", ["replicate"] = "catalog-key"
};
Check(typeof(IModelProvider).Assembly.GetName().Name == "AIHappey.Providers", "Unexpected assembly ownership.");

var request = Read<ChatCompletionOptions>("""
    {"model":"model","messages":[{"role":"user","content":"hello"}],
     "future_field":{"nested":[true,null,1.25]}}
    """);
var completion = await client.ExecuteAsync("inceptron", keys, (p, ct) => p.CompleteChatAsync(request, ct));
Check(completion.Model == "inceptron/model", "Compatible completion did not preserve model mapping.");
Console.WriteLine("Compatible request and raw provider options passed.");

var native = await client.ExecuteAsync("anthropic", keys, (p, ct) => p.MessagesAsync(
    Read<MessagesRequest>("""{"model":"claude-test","max_tokens":32,"messages":[{"role":"user","content":"hello"}]}"""), [], ct));
Check(JsonSerializer.Serialize(native).Contains("native-ok"), "Native provider response was not mapped.");
Console.WriteLine("Native Messages request passed.");

var streamed = new List<ChatCompletionUpdate>();
await foreach (var chunk in client.StreamAsync("inceptron", keys,
                   (p, ct) => p.CompleteChatStreamingAsync(Read<ChatCompletionOptions>("""{"model":"model","messages":[{"role":"user","content":"hello"}]}"""), ct)))
    streamed.Add(chunk);
Check(streamed.Count == 2, "Compatible streaming did not deliver both chunks.");
Check(factory.LastStream?.Disposed == true, "Completed stream was not disposed.");
Console.WriteLine("Streaming completion and disposal passed.");

await using (var enumerator = client.StreamAsync("inceptron", keys,
                 (p, ct) => p.CompleteChatStreamingAsync(Read<ChatCompletionOptions>("""{"model":"model","messages":[{"role":"user","content":"hello"}]}"""), ct))
                 .GetAsyncEnumerator())
    Check(await enumerator.MoveNextAsync(), "Early-disposal stream returned no data.");
Check(factory.LastStream?.Disposed == true, "Early-disposed stream retained its upstream response.");
Console.WriteLine("Early stream disposal passed.");

using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try
    {
        await client.ExecuteAsync("inceptron", keys, (p, ct) => p.CompleteChatAsync(
            Read<ChatCompletionOptions>("""{"model":"model","messages":[{"role":"user","content":"hello"}]}"""), ct), cancellationToken: canceled.Token);
        throw new InvalidOperationException("Cancellation was not propagated.");
    }
    catch (OperationCanceledException) { }
}

var audio = await client.ExecuteAsync("openai", keys, (p, ct) => p.OpenAISpeechRequestAsync(
    Read<AudioSpeechRequest>("""{"model":"tts-test","input":"hello","voice":"alloy"}"""), ct));
Check(audio.Audio.SequenceEqual(new byte[] { 1, 2, 3 }) && audio.MimeType == "audio/mpeg", "Media bytes or content type changed.");
Console.WriteLine("Cancellation and media request passed.");

var models = await client.ExecuteAsync("replicate", keys, (p, ct) => p.ListModels(ct), catalog: new CatalogInput());
Check(models.Single().Id == "replicate/owner/model", "Caller-supplied catalog was not used.");
var empty = await client.ExecuteAsync("replicate", keys, (p, ct) => p.ListModels(ct));
Check(!empty.Any(), "Catalog data leaked to the next operation.");

var packagedCatalog = new FileProviderCatalogSource(Path.Combine(AppContext.BaseDirectory, "Catalog"));
Check(packagedCatalog.ReadModels("zai").Any(), "Packaged model catalog was not copied to output/publish.");
Check(packagedCatalog.ReadPricing("anthropic") is { Length: > 0 }, "Packaged base pricing is missing.");
Check(packagedCatalog.ReadPricingVariants("google").Any(), "Packaged nested pricing variants are missing.");
using (ProviderCatalogScope.Begin(packagedCatalog))
{
    Check("zai".GetModels().Any(m => m.Id.StartsWith("zai/")), "Packaged model catalog is not consumable.");
    Check("anthropic".GetPricing()!.Count > 0, "Packaged pricing is not consumable.");
}
Check("zai".GetModels().Count > 0, "Existing default catalog output path is not supported.");

Console.WriteLine("Verified package-only compatible/native requests, raw options, streaming, cancellation, disposal, media, and explicit catalog input without a gateway.");

static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)!;
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

sealed class FakeFactory : IHttpClientFactory
{
    public TrackingStream? LastStream;
    public HttpClient CreateClient(string name) => new(new FakeHandler(this));

    private sealed class FakeHandler(FakeFactory owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            if (uri.Host == "api.anthropic.com" && uri.AbsolutePath == "/v1/messages")
            {
                if (request.Headers.GetValues("x-api-key").Single() != "native-key") throw new InvalidOperationException("Native credential mismatch.");
                return Json("""{"id":"msg","type":"message","role":"assistant","model":"claude-test","content":[{"type":"text","text":"native-ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":2}}""");
            }
            if (uri.Host == "api.openai.com" && uri.AbsolutePath == "/v1/audio/speech")
            {
                if (request.Headers.Authorization?.Parameter != "media-key") throw new InvalidOperationException("Media credential mismatch.");
                var content = new ByteArrayContent([1, 2, 3]);
                content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                return new(HttpStatusCode.OK) { Content = content };
            }
            if (uri.Host != "api.inceptron.io" || uri.AbsolutePath != "/v1/chat/completions")
                throw new InvalidOperationException($"Unexpected outbound request: {uri}.");
            if (request.Headers.Authorization?.Parameter != "compatible-key") throw new InvalidOperationException("Compatible credential mismatch.");
            if (body.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            {
                owner.LastStream = new TrackingStream(Encoding.UTF8.GetBytes("""
                    data: {"id":"c","object":"chat.completion.chunk","model":"model","choices":[{"index":0,"delta":{"content":"ok"}}]}

                    data: {"id":"c","object":"chat.completion.chunk","model":"model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: [DONE]


                    """));
                var content = new StreamContent(owner.LastStream);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return new(HttpStatusCode.OK) { Content = content };
            }
            if (!body.TryGetProperty("future_field", out var future) || future.GetProperty("nested")[2].GetDouble() != 1.25)
                throw new InvalidOperationException("Raw provider options were lost.");
            return Json("""{"id":"c","object":"chat.completion","model":"model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""");
        }

        private static HttpResponseMessage Json(string text)
            => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
{
    public bool Disposed { get; private set; }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}

sealed class CatalogInput : IProviderCatalogSource
{
    public string CachePartition => "consumer-fixture";
    public IEnumerable<KeyValuePair<string, string>> ReadModels(string provider)
        => provider == "replicate" ? [new("replicate.json", """{"data":[{"id":"replicate/owner/model","type":"image"}]}""")] : [];
    public string? ReadPricing(string provider) => null;
    public IEnumerable<KeyValuePair<string, string>> ReadPricingVariants(string provider) => [];
}
