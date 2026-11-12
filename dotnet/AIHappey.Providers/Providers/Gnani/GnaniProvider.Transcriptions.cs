using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Gnani;

public partial class GnaniProvider
{
    private async Task<TranscriptionResponse> TranscriptionRequestCore(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);

        var model = NormalizeModel(request.Model).ToLowerInvariant();
        if (model is not ("gnani-prisma-v2.5" or "prisma-v2.5" or "prisma-v3.0"))
            throw new NotSupportedException($"Gnani transcription model '{model}' is not supported.");
        var isV25 = model is "gnani-prisma-v2.5" or "prisma-v2.5";
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        ValidateMetadata(metadata);

        var audio = DecodeAudio(request.Audio);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "audio_file", "audio" + request.MediaType.Split(';', 2)[0].Trim().GetAudioExtension());

        if (isV25)
        {
            var languageCode = ReadString(metadata, "language_code") ?? ReadString(metadata, "language");
            if (string.IsNullOrWhiteSpace(languageCode))
                throw new ArgumentException("Gnani Prisma v2.5 requires providerOptions.gnani.language_code.", nameof(request));
            form.Add(new StringContent(languageCode.Trim()), "language_code");
        }
        else
        {
            // Prisma v3.0 accepts an optional request format (for example "transliterate"),
            // and does not require the v2.5 language_code field.
            form.Add(new StringContent("prisma-v3.0"), "model");
        }

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, "model", StringComparison.OrdinalIgnoreCase)
                    || (isV25 && string.Equals(property.Name, "language", StringComparison.OrdinalIgnoreCase))
                    || (isV25 && string.Equals(property.Name, "language_code", StringComparison.OrdinalIgnoreCase)))
                    continue;

                form.Add(new StringContent(ToMultipartValue(property.Value)), property.Name);
            }
        }

        using var httpRequest = CreateAuthenticatedRequest(HttpMethod.Post, "stt/v3");
        httpRequest.Content = form;
        var timestamp = DateTime.UtcNow;
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gnani transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            var message = ReadString(root, "message") ?? ReadString(root, "error") ?? raw;
            throw new InvalidOperationException($"Gnani transcription failed: {message}");
        }

        var transcript = ReadString(root, "transcript");
        if (transcript is null)
            throw new JsonException("Gnani transcription response did not contain a string transcript.");
        var language = ReadString(root, "detected_language_code") ?? ReadString(root, "language_code");

        return new TranscriptionResponse
        {
            Text = transcript,
            Language = language,
            Segments = [],
            Warnings = [],
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Request = new TranscriptionRequestItem
            {
                Body = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["model"] = isV25 ? null : "prisma-v3.0",
                    ["language_code"] = isV25 ? ReadString(metadata, "language_code") ?? ReadString(metadata, "language") : null,
                    ["metadata"] = metadata.ValueKind == JsonValueKind.Object ? metadata.Clone() : null,
                    ["audio_bytes"] = audio.Length
                })
            },
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    private async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestCore(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        var format = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        request.ProviderOptions = string.IsNullOrWhiteSpace(options.Language)
            ? null
            : new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    language_code = options.Language.Length == 2
                        ? options.Language.ToLowerInvariant() + "-IN"
                        : options.Language
                })
            };
        var response = await TranscriptionRequestCore(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(format);
    }

    private static byte[] DecodeAudio(object? audio)
    {
        var value = audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));

        value = value.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                throw new ArgumentException("Audio data URL must contain base64 data.", nameof(audio));
            value = value[(marker + ";base64,".Length)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length > 0
                ? bytes
                : throw new ArgumentException("Audio is required.", nameof(audio));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must contain valid base64 data.", nameof(audio), exception);
        }
    }

    private static string ToMultipartValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
}
