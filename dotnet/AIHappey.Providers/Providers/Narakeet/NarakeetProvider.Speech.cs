using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Narakeet;

public sealed partial class NarakeetProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (message, format, voice, usePolling) = CreateSpeechRequest(request);
        using var messageScope = message;
        var timestamp = DateTime.UtcNow;
        byte[] audio;
        Dictionary<string, string> headers;
        if (usePolling)
        {
            (audio, headers) = await RequestLongSpeechAsync(message, cancellationToken);
        }
        else
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            headers = response.GetHeaders();
        }
        if (audio.Length == 0)
            throw new InvalidOperationException("Narakeet returned no speech audio.");
        var metadata = JsonSerializer.SerializeToElement(new { bytes = audio.Length, headers });
        return new SpeechResponse
        {
            Audio = new() { Base64 = Convert.ToBase64String(audio), MimeType = MimeType(format), Format = format },
            Request = new() { Body = request.Text },
            ProviderMetadata = new() { [GetIdentifier()] = metadata },
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = $"{GetIdentifier()}/{voice}",
                Headers = headers,
                Body = metadata
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => (await SpeechRequest(ToSpeechRequest(options), cancellationToken)).ToOpenAISpeechAudio();

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.StreamFormat, "sse", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Narakeet speech events require stream_format 'sse'.");
        var (message, _, _, _) = CreateSpeechRequest(ToSpeechRequest(options), isStreaming: true);
        using var messageScope = message;
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        var receivedAudio = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receivedAudio = true;
            yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(buffer, 0, count) };
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!receivedAudio)
            throw new InvalidOperationException("Narakeet returned no speech audio.");
        yield return new AudioSpeechStreamDone();
    }

    private (HttpRequestMessage Message, string Format, string Voice, bool UsePolling) CreateSpeechRequest(
        SpeechRequest request,
        bool isStreaming = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Model and text are required.", nameof(request));
        if (request.Instructions is not null || request.Language is not null)
            throw new NotSupportedException("Narakeet selects language by voice; instructions and language fields are unsupported.");
        var voice = request.Voice ?? (request.Model.StartsWith("narakeet/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["narakeet/".Length..] : request.Model);
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("A Narakeet voice name is required.", nameof(request));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var native) == true)
        {
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Narakeet provider options must be a query parameter object.", nameof(request));
            foreach (var property in native.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Narakeet query parameter values must be scalar.", nameof(request));
                parameters[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()! : property.Value.GetRawText();
            }
        }
        parameters["voice"] = voice;
        if (request.Speed is not null)
            parameters["voice-speed"] = request.Speed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var format = request.OutputFormat ?? "mp3";
        if (format is not ("mp3" or "m4a" or "wav"))
            throw new NotSupportedException("Narakeet speech supports mp3, m4a, and wav.");
        var usePolling = format == "wav" || Encoding.UTF8.GetByteCount(request.Text) > 1024;
        if (isStreaming && usePolling)
            throw new NotSupportedException("Narakeet streaming speech supports up to 1 KB of mp3 or m4a content.");
        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var message = CreateRequest(HttpMethod.Post, $"text-to-speech/{format}?{query}");
        if (!usePolling)
            message.Headers.Accept.ParseAdd("application/octet-stream");
        message.Content = new StringContent(request.Text, Encoding.UTF8, "text/plain");
        return (message, format, voice, usePolling);
    }

    private async Task<(byte[] Audio, Dictionary<string, string> Headers)> RequestLongSpeechAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var submission = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!submission.RootElement.TryGetProperty("statusUrl", out var statusUrlElement) ||
            statusUrlElement.ValueKind != JsonValueKind.String ||
            !TryGetNarakeetUri(statusUrlElement.GetString(), out var statusUri))
            throw new JsonException("Narakeet long-content response did not contain a valid statusUrl.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var statusResponse = await _client.GetAsync(statusUri, cancellationToken);
            await EnsureSuccessAsync(statusResponse, cancellationToken);
            using var status = await JsonDocument.ParseAsync(
                await statusResponse.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = status.RootElement;
            if (!root.TryGetProperty("finished", out var finished) ||
                finished.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("Narakeet speech status did not contain a valid finished flag.");
            if (finished.ValueKind == JsonValueKind.True)
            {
                if (!root.TryGetProperty("succeeded", out var succeeded) || succeeded.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException(
                        root.TryGetProperty("message", out var error) && error.ValueKind == JsonValueKind.String
                            ? error.GetString()
                            : "Narakeet speech generation failed.");
                if (!root.TryGetProperty("result", out var resultElement) ||
                    resultElement.ValueKind != JsonValueKind.String ||
                    !TryGetNarakeetUri(resultElement.GetString(), out var resultUri))
                    throw new JsonException("Narakeet speech status did not contain a valid result URL.");

                using var audioResponse = await _client.GetAsync(
                    resultUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await EnsureSuccessAsync(audioResponse, cancellationToken);
                var audio = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                return (audio, audioResponse.GetHeaders());
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private static bool TryGetNarakeetUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate is not null &&
            candidate.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(candidate.UserInfo))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string MimeType(string format) => format switch
    {
        "mp3" => "audio/mpeg",
        "m4a" => "audio/mp4",
        "wav" => "audio/wav",
        _ => throw new NotSupportedException($"Unsupported Narakeet audio format '{format}'.")
    };

    private static SpeechRequest ToSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is not null)
            request.ProviderOptions = new() { ["narakeet"] = JsonSerializer.SerializeToElement(options.AdditionalProperties) };
        return request;
    }
}
