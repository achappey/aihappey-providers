using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(
            options.Model,
            GetIdentifier(),
            cancellationToken);
        request.ProviderOptions = CreateSmallestAiOpenAITranscriptionOptions(options, request);

        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private Dictionary<string, JsonElement>? CreateSmallestAiOpenAITranscriptionOptions(
        OpenAITranscriptionRequest options,
        TranscriptionRequest request)
    {
        var existing = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (existing.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            throw new JsonException("SmallestAI provider options must be a JSON object.");

        var wordTimestampsRequested = options.TimestampGranularities?.Any(granularity =>
            string.Equals(granularity?.Trim(), "word", StringComparison.OrdinalIgnoreCase)) == true;

        var metadata = existing.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(existing.GetRawText())!.AsObject()
            : new JsonObject();

        // Consume the shared OpenAI option here; SmallestAI uses wordTimestamps instead.
        metadata.Remove("timestamp_granularities");

        var existingLanguage = GetMetadataValue<string?>(existing, "language");
        var language = !string.IsNullOrWhiteSpace(options.Language)
            ? options.Language.Trim()
            : existingLanguage;

        if (language is not null)
            SetMetadataProperty(metadata, "language", JsonValue.Create(language));

        if (wordTimestampsRequested)
            SetMetadataProperty(metadata, "wordTimestamps", JsonValue.Create(true));

        var hasOptions = metadata.Any(property =>
            property.Value is not null
            || (!KnownMetadataProperties.Contains(property.Key)
                && !string.Equals(property.Key, "model", StringComparison.OrdinalIgnoreCase)));

        return !hasOptions
            ? null
            : new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
            };
    }

    private static void SetMetadataProperty(JsonObject metadata, string name, JsonNode? value)
    {
        foreach (var existingName in metadata.Select(property => property.Key)
                     .Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            metadata.Remove(existingName);

        metadata[name] = value;
    }
}
