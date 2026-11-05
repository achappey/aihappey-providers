using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.StabilityAI;

public partial class StabilityAIProvider
{
    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = GetMetadataObject(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        var now = DateTime.UtcNow;

        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var metadataOutputFormat = GetMetadataProperty<string>(metadata, "output_format");
        var outputFormat = request.OutputFormat ?? metadataOutputFormat ?? "mp3";

        using var form = new MultipartFormDataContent();
        form.Add(NamedField("prompt", request.Text));
        form.Add(NamedField("output_format", outputFormat));
        form.Add(NamedField("model", request.Model));

        var duration = GetMetadataProperty<double?>(metadata, "duration");
        if (duration is not null)
            form.Add(NamedField(
                "duration",
                duration.Value.ToString(CultureInfo.InvariantCulture)));

        var seed = GetMetadataProperty<uint?>(metadata, "seed");
        if (seed is not null)
            form.Add(NamedField("seed", seed.Value.ToString(CultureInfo.InvariantCulture)));

        var steps = GetMetadataProperty<int?>(metadata, "steps");
        if (steps is not null)
            form.Add(NamedField("steps", steps.Value.ToString(CultureInfo.InvariantCulture)));

        var cfgScale = GetMetadataProperty<double?>(metadata, "cfg_scale");
        if (cfgScale is not null)
            form.Add(NamedField("cfg_scale", cfgScale.Value.ToString(CultureInfo.InvariantCulture)));

        AddRawMetadataFields(
            form,
            metadata,
            "output_format",
            "duration",
            "seed",
            "steps",
            "cfg_scale");

        // sanity check (copy from image MCP)
        foreach (var part in form)
        {
            var cd = part.Headers.ContentDisposition;
            if (cd?.Name is null)
                throw new InvalidOperationException($"Form part missing name. Headers: {part.Headers}");
        }

        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.PostAsync("audio/stable-audio-2/text-to-audio", form, cancellationToken);
        var bytesOut = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytesOut);
            throw new Exception($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {TryExtractErrorMessage(text)}");
        }

        var mime = outputFormat.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            _ => resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
        };

        var base64 = Convert.ToBase64String(bytesOut);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = outputFormat ?? "mp3"
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
            }
        };
    }
}
