using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Speechmatics;

public partial class SpeechmaticsProvider
{
    private async IAsyncEnumerable<StreamingSpeechPart> StreamSpeechAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is not { Length: > 0 })
            throw new ArgumentException("At least one Speechmatics speech text part is required.", nameof(request));

        var (modelId, modelVoice) = ParseSpeechModelAndVoice(request.Model);
        var text = new StringBuilder();
        string? voice = modelVoice ?? request.Voice?.Trim();
        foreach (var part in request.Text)
        {
            if (part is null || !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase) || part.Text is null)
                throw new ArgumentException("Speechmatics speech parts must be text with non-null content.", nameof(request));
            if (!string.IsNullOrWhiteSpace(part.Voice))
            {
                if (voice is not null && !string.Equals(voice, part.Voice.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("Speechmatics streaming speech supports one voice per request.");
                voice ??= part.Voice.Trim();
            }
            text.Append(part.Text);
        }

        if (string.IsNullOrWhiteSpace(text.ToString()))
            throw new ArgumentException("Speechmatics speech text cannot be empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("A Speechmatics voice is required. Use a '/voice/[voice]' model id or set Voice.", nameof(request));

        var outputFormat = NormalizeOutputFormat(request.OutputFormat?.Type);
        if (request.OutputFormat?.Rate is { } rate && rate != 16000)
            throw new NotSupportedException("Speechmatics TTS supports a 16 kHz sample rate.");

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Speechmatics)} API key.");

        var query = Uri.EscapeDataString(outputFormat);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{TtsBaseUrl}/generate/{Uri.EscapeDataString(voice)}?output_format={query}")
        {
            Content = JsonContent.Create(new { text = text.ToString() })
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Speechmatics TTS failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }

        yield return new SpeechStreamStartPart { Warnings = warnings };
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = modelId.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };

        var receivedAudio = false;
        var buffer = new byte[16 * 1024];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            receivedAudio = true;
            var chunk = buffer.AsSpan(0, read).ToArray();
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = Convert.ToBase64String(chunk) };
            yield return new SpeechAudioPart
            {
                Audio = Convert.ToBase64String(chunk),
                AudioFormat = new AudioFormat
                {
                    Type = outputFormat == "pcm_16000" ? "audio/pcm" : "audio/wav",
                    Rate = 16000
                }
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("Speechmatics returned no speech audio.");
        yield return new SpeechFinishPart();
    }

    private static string NormalizeOutputFormat(string? format)
        => (format ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "wav" or "wave" or "wav_16000" or "audio/wav" or "audio/x-wav" => "wav_16000",
            "pcm" or "pcm_16000" or "audio/pcm" or "audio/l16" => "pcm_16000",
            _ => throw new NotSupportedException($"Speechmatics TTS does not document output format '{format}'.")
        };
}
