using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Sarvam;

public partial class SarvamProvider
{
    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one speech text part is required.", nameof(request));
        if (request.OutputFormat?.Rate is <= 0)
            throw new ArgumentException("Speech output sample rate must be positive.", nameof(request));

        JsonElement? configuration = null;
        Dictionary<string, object?>? payload = null;
        string? outputAudioCodec = null;
        var text = new StringBuilder();

        foreach (var part in request.Text)
        {
            if (part is null || part.Type != "text" || part.Text is null)
                throw new ArgumentException("Sarvam speech parts must be text with non-null content.", nameof(request));

            var metadata = MergeSarvamOptions(request.ProviderOptions, part.ProviderOptions);
            if (request.OutputFormat?.Rate is { } rate)
                metadata["speech_sample_rate"] = JsonSerializer.SerializeToElement(rate);

            var current = new SpeechRequest
            {
                Model = request.Model,
                Text = "validation",
                Voice = part.Voice ?? request.Voice,
                Speed = request.Speed,
                Language = request.Language,
                OutputFormat = request.OutputFormat?.Type,
                ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata) }
            };
            var currentPayload = BuildSarvamSpeechPayload(current,
                current.GetProviderMetadata<JsonElement>(GetIdentifier()));
            var serializedConfiguration = JsonSerializer.SerializeToElement(currentPayload, SpeechJson);
            if (configuration is not null && !JsonElement.DeepEquals(configuration.Value, serializedConfiguration))
                throw new NotSupportedException("Sarvam streaming TTS requires one effective voice and provider-options configuration across text parts.");

            configuration = serializedConfiguration;
            payload = currentPayload;
            outputAudioCodec = NormalizeSarvamCodec(
                request.OutputFormat?.Type ?? ReadSpeechMetadataString(
                    JsonSerializer.SerializeToElement(metadata), "output_audio_codec"));
            text.Append(part.Text);
        }

        var input = text.ToString();
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Sarvam speech text cannot be empty.", nameof(request));
        if (input.Length > 3500)
            throw new ArgumentException("Sarvam streaming TTS accepts at most 3500 characters.", nameof(request));
        var finalPayload = payload ?? throw new InvalidOperationException("Sarvam speech request configuration was not created.");
        finalPayload["text"] = input;

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("No Sarvam API key.");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "text-to-speech/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(finalPayload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Add("api-subscription-key", apiKey);
        httpRequest.Headers.Accept.ParseAdd(GuessMimeType(outputAudioCodec) ?? "application/octet-stream");

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Sarvam streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        var responseHeaders = response.GetHeaders();
        var audioType = response.Content.Headers.ContentType?.MediaType
            ?? GuessMimeType(outputAudioCodec)
            ?? "audio/mpeg";
        var sampleRate = ReadSpeechMetadataInt32(
            JsonSerializer.SerializeToElement(finalPayload), "speech_sample_rate") ?? 22050;

        yield return new SpeechStreamStartPart { Warnings = warnings };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model.ToModelId(GetIdentifier()),
            Headers = responseHeaders
        };

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];
        long bytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            bytes += read;
            var audio = Convert.ToBase64String(buffer.AsSpan(0, read));
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = audio };
            yield return new SpeechAudioPart
            {
                Audio = audio,
                AudioFormat = new AudioFormat { Type = audioType, Rate = sampleRate }
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (bytes == 0)
            throw new InvalidOperationException("Sarvam streaming TTS returned no audio.");

        yield return new SpeechFinishPart
        {
            ProviderMetadata = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = finalPayload["model"],
                    output_audio_codec = outputAudioCodec,
                    speech_sample_rate = sampleRate,
                    bytes
                })
            }
        };
    }

    private static Dictionary<string, JsonElement> MergeSarvamOptions(
        Dictionary<string, JsonElement>? defaults,
        Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue("sarvam", out var native) != true)
                continue;
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.sarvam must be an object.");
            foreach (var property in native.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return values;
    }
}
