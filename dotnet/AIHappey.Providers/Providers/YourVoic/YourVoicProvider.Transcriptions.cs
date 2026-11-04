using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.YourVoic;

public partial class YourVoicProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            throw new JsonException("YourVoic provider options must be a JSON object.");

        var language = GetStringOption(metadata, "language");
        var responseFormat = GetStringOption(metadata, "response_format");
        var prompt = GetStringOption(metadata, "prompt");
        var timestampGranularities = GetStringOption(metadata, "timestamp_granularities");
        var diarize = GetBooleanOption(metadata, "diarize");
        var smartFormat = GetBooleanOption(metadata, "smart_format");
        var punctuate = GetBooleanOption(metadata, "punctuate");
        var keywords = GetStringOption(metadata, "keywords");
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var model = request.Model.Trim();

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(model), "model");

        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        var isCipher = model.StartsWith("cipher-", StringComparison.OrdinalIgnoreCase);
        var isLucid = model.StartsWith("lucid-", StringComparison.OrdinalIgnoreCase);

        if (isCipher)
        {
            if (!string.IsNullOrWhiteSpace(responseFormat))
                form.Add(new StringContent(responseFormat), "response_format");

            if (!string.IsNullOrWhiteSpace(prompt))
                form.Add(new StringContent(prompt), "prompt");

            if (!string.IsNullOrWhiteSpace(timestampGranularities))
                form.Add(new StringContent(timestampGranularities), "timestamp_granularities");

            if (diarize is not null)
                warnings.Add(new { type = "ignored", feature = "diarize", reason = "Only supported for lucid-* models." });
            if (smartFormat is not null)
                warnings.Add(new { type = "ignored", feature = "smart_format", reason = "Only supported for lucid-* models." });
            if (punctuate is not null)
                warnings.Add(new { type = "ignored", feature = "punctuate", reason = "Only supported for lucid-* models." });
            if (!string.IsNullOrWhiteSpace(keywords))
                warnings.Add(new { type = "ignored", feature = "keywords", reason = "Only supported for lucid-* models." });
        }
        else if (isLucid)
        {
            if (diarize is not null)
                form.Add(new StringContent(diarize.Value.ToString().ToLowerInvariant()), "diarize");

            if (smartFormat is not null)
                form.Add(new StringContent(smartFormat.Value.ToString().ToLowerInvariant()), "smart_format");

            if (punctuate is not null)
                form.Add(new StringContent(punctuate.Value.ToString().ToLowerInvariant()), "punctuate");

            if (!string.IsNullOrWhiteSpace(keywords))
                form.Add(new StringContent(keywords), "keywords");

            if (!string.IsNullOrWhiteSpace(responseFormat))
                warnings.Add(new { type = "ignored", feature = "response_format", reason = "Only supported for cipher-* models." });
            if (!string.IsNullOrWhiteSpace(prompt))
                warnings.Add(new { type = "ignored", feature = "prompt", reason = "Only supported for cipher-* models." });
            if (!string.IsNullOrWhiteSpace(timestampGranularities))
                warnings.Add(new { type = "ignored", feature = "timestamp_granularities", reason = "Only supported for cipher-* models." });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(responseFormat))
                form.Add(new StringContent(responseFormat), "response_format");

            if (!string.IsNullOrWhiteSpace(prompt))
                form.Add(new StringContent(prompt), "prompt");

            if (!string.IsNullOrWhiteSpace(timestampGranularities))
                form.Add(new StringContent(timestampGranularities), "timestamp_granularities");

            if (diarize is not null)
                form.Add(new StringContent(diarize.Value.ToString().ToLowerInvariant()), "diarize");

            if (smartFormat is not null)
                form.Add(new StringContent(smartFormat.Value.ToString().ToLowerInvariant()), "smart_format");

            if (punctuate is not null)
                form.Add(new StringContent(punctuate.Value.ToString().ToLowerInvariant()), "punctuate");

            if (!string.IsNullOrWhiteSpace(keywords))
                form.Add(new StringContent(keywords), "keywords");
        }

        AddUnknownOptions(form, metadata);

        var endpoint = isCipher
            ? "stt/cipher/transcribe"
            : isLucid
                ? "stt/lucid/transcribe"
                : "stt/transcribe";

        using var resp = await _client.PostAsync(endpoint, form, cancellationToken);
        var responseText = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"YourVoic STT failed ({(int)resp.StatusCode}): {responseText}");

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        return ConvertTranscriptionResponse(responseText, GetIdentifier(), resp.GetHeaders(),
            request.Model, now, warnings);
    }

    private static string? GetStringOption(JsonElement metadata, string name)
        => GetOption(metadata, name) is { } value && value.ValueKind != JsonValueKind.Null
            ? value.Deserialize<string?>()
            : null;

    private static bool? GetBooleanOption(JsonElement metadata, string name)
        => GetOption(metadata, name) is { } value && value.ValueKind != JsonValueKind.Null
            ? value.Deserialize<bool?>()
            : null;

    private static JsonElement? GetOption(JsonElement metadata, string name)
    {
        JsonElement? result = null;
        if (metadata.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                result = property.Value;
        }

        return result;
    }

    private static void AddUnknownOptions(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name.Equals("file", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("model", StringComparison.OrdinalIgnoreCase)
                || IsMappedOption(property.Name)
                || property.Value.ValueKind == JsonValueKind.Null)
                continue;

            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()!
                : property.Value.GetRawText();
            form.Add(new StringContent(value), property.Name);
        }
    }

    private static bool IsMappedOption(string name)
        => name.Equals("language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("response_format", StringComparison.OrdinalIgnoreCase)
            || name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timestamp_granularities", StringComparison.OrdinalIgnoreCase)
            || name.Equals("diarize", StringComparison.OrdinalIgnoreCase)
            || name.Equals("smart_format", StringComparison.OrdinalIgnoreCase)
            || name.Equals("punctuate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("keywords", StringComparison.OrdinalIgnoreCase);

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string responseBody,
        string providerId,
        Dictionary<string, string>? headers,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
    {

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        var language = root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String
            ? langEl.GetString()
            : null;

        float? duration = null;
        if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            duration = (float)durEl.GetDouble();

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("utterances", out var utterancesEl) && utterancesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var utterance in utterancesEl.EnumerateArray())
            {
                var segmentText = utterance.TryGetProperty("text", out var ut) && ut.ValueKind == JsonValueKind.String
                    ? ut.GetString() ?? string.Empty
                    : string.Empty;

                if (utterance.TryGetProperty("speaker", out var speakerEl))
                {
                    var speaker = speakerEl.ValueKind == JsonValueKind.String
                        ? speakerEl.GetString()
                        : speakerEl.ValueKind == JsonValueKind.Number
                            ? speakerEl.GetInt32().ToString()
                            : null;

                    if (!string.IsNullOrWhiteSpace(speaker) && !string.IsNullOrWhiteSpace(segmentText))
                        segmentText = $"speaker_{speaker}: {segmentText}";
                }

                var start = utterance.TryGetProperty("start", out var us) && us.ValueKind == JsonValueKind.Number
                    ? (float)us.GetDouble()
                    : 0f;

                var end = utterance.TryGetProperty("end", out var ue) && ue.ValueKind == JsonValueKind.Number
                    ? (float)ue.GetDouble()
                    : start;

                if (!string.IsNullOrWhiteSpace(segmentText))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = segmentText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
        }
        else if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsEl.EnumerateArray())
            {
                var segmentText = segment.TryGetProperty("text", out var st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString() ?? string.Empty
                    : string.Empty;

                var start = segment.TryGetProperty("start", out var ss) && ss.ValueKind == JsonValueKind.Number
                    ? (float)ss.GetDouble()
                    : 0f;

                var end = segment.TryGetProperty("end", out var se) && se.ValueKind == JsonValueKind.Number
                    ? (float)se.GetDouble()
                    : start;

                if (!string.IsNullOrWhiteSpace(segmentText))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = segmentText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
        }
        else if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var word in wordsEl.EnumerateArray())
            {
                var segmentText = word.TryGetProperty("word", out var wt) && wt.ValueKind == JsonValueKind.String
                    ? wt.GetString() ?? string.Empty
                    : string.Empty;

                var start = word.TryGetProperty("start", out var ws) && ws.ValueKind == JsonValueKind.Number
                    ? (float)ws.GetDouble()
                    : 0f;

                var end = word.TryGetProperty("end", out var we) && we.ValueKind == JsonValueKind.Number
                    ? (float)we.GetDouble()
                    : start;

                if (!string.IsNullOrWhiteSpace(segmentText))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = segmentText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = providerId.CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = model.ToModelId(providerId),
                Headers = headers,
                Body = doc.RootElement.Clone()
            }
        };

    }
}
