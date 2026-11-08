using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Verbatik;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Verbatik;

public partial class VerbatikProvider
{
    private async Task<SpeechResponse> MusicRequest(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();
        if (prompt is null)
            throw new ArgumentException("Verbatik music generation requires a prompt.", nameof(request));

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<VerbatikSpeechProviderMetadata>(GetIdentifier());
        var rawOptions = GetRawProviderOptions(request);
        var warnings = BuildMusicWarnings(request, metadata);
        var payload = BuildMusicPayload(request, rawOptions, prompt);

        using var createRequest = CreateApiRequest(HttpMethod.Post, "generations");
        createRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
            Encoding.UTF8,
            "application/json");

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} music generation failed ({(int)createResponse.StatusCode}): {createBody}");

        using var createDocument = JsonDocument.Parse(createBody);
        var created = createDocument.RootElement.Clone();
        var completed = await WaitForCompletionAsync("generations", created, cancellationToken);
        var output = GetFirstOutput(completed);

        using var audioRequest = new HttpRequestMessage(HttpMethod.Get, output.Url);
        using var audioResponse = await _client.SendAsync(audioRequest, cancellationToken);
        var audioBytes = await audioResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!audioResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} music download failed ({(int)audioResponse.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        var mimeType = ResolveMimeType(
            audioResponse.Content.Headers.ContentType?.MediaType ?? output.MediaType,
            output.Url,
            null);
        var format = ResolveFormat(mimeType, output.Url, null);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                new { estimated_credits = GetJsonValue(created, "estimated_credits") }),
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = now,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = completed
            }
        };
    }

    private static List<object> BuildMusicWarnings(
        SpeechRequest request,
        VerbatikSpeechProviderMetadata? metadata)
    {
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (request.Speed is not null || metadata?.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat) || !string.IsNullOrWhiteSpace(metadata?.Format))
            warnings.Add(new { type = "unsupported", feature = "output_format" });
        if (metadata?.Tags is { Length: > 0 })
            warnings.Add(new { type = "unsupported", feature = "tags" });
        if (metadata?.Seed is not null
            || metadata?.PromptStrength is not null
            || metadata?.PromptStrengthCamel is not null
            || metadata?.BalanceStrength is not null
            || metadata?.BalanceStrengthCamel is not null
            || metadata?.NumSongs is not null
            || metadata?.NumSongsCamel is not null
            || metadata?.OutputBitRate is not null
            || metadata?.OutputBitRateCamel is not null
            || metadata?.Bpm is not null
            || metadata?.StoreAudio is not null
            || metadata?.StoreAudioCamel is not null
            || !string.IsNullOrWhiteSpace(metadata?.Name))
            warnings.Add(new { type = "unsupported", feature = "legacy_music_options" });
        return warnings;
    }

    private static Dictionary<string, object?> BuildMusicPayload(
        SpeechRequest request,
        JsonElement rawOptions,
        string prompt)
    {
        var payload = CopyUnknownMusicOptions(rawOptions);
        var parameters = CopyRawMusicParameters(rawOptions);
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            parameters["lyrics"] = request.Instructions;

        payload["mode"] = "audio";
        payload["model"] = ResolveNativeMusicModel(NormalizeLocalModelId(request.Model));
        payload["prompt"] = prompt;
        if (parameters.Count > 0)
            payload["parameters"] = parameters;
        payload["delivery"] = "signed_url";
        return payload;
    }

    private static Dictionary<string, object?> CopyUnknownMusicOptions(JsonElement rawOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (rawOptions.ValueKind != JsonValueKind.Object)
            return payload;

        var legacyOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mode", "model", "prompt", "parameters", "delivery", "tags", "lyrics", "seed",
            "prompt_strength", "promptStrength", "balance_strength", "balanceStrength",
            "num_songs", "numSongs", "output_format", "outputFormat", "output_bit_rate",
            "outputBitRate", "bpm", "store_audio", "storeAudio", "name", "voice", "speed",
            "volume", "pitch", "emotion", "englishNormalization", "sampleRate", "bitrate",
            "contentType", "languageBoost", "voiceModifyPitch", "voiceModifyIntensity",
            "voiceModifyTimbre"
        };
        foreach (var property in rawOptions.EnumerateObject())
        {
            if (!legacyOptions.Contains(property.Name))
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static Dictionary<string, object?> CopyRawMusicParameters(JsonElement rawOptions)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (rawOptions.ValueKind == JsonValueKind.Object
            && rawOptions.TryGetProperty("parameters", out var rawParameters)
            && rawParameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rawParameters.EnumerateObject())
                parameters[property.Name] = property.Value.Clone();
        }

        if (rawOptions.ValueKind == JsonValueKind.Object
            && rawOptions.TryGetProperty("lyrics", out var lyrics))
            parameters["lyrics"] = lyrics.Clone();

        return parameters;
    }

    private static string ResolveNativeMusicModel(string model)
        => model.Equals("text-to-music", StringComparison.OrdinalIgnoreCase)
            ? "fal-ai/minimax-music/v2"
            : model.Equals("fal-ai/minimax-music/v2", StringComparison.OrdinalIgnoreCase)
                ? model
                : throw new NotSupportedException(
                    $"{ProviderName} music model '{model}' is not supported. Use 'text-to-music' or 'fal-ai/minimax-music/v2'.");

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }

        return null;
    }
}
