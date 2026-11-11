using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Blade;

public partial class BladeProvider
{
    private async Task<IEnumerable<Model>> ListBladeModels(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                foreach (var (name, value) in RequestHeaders())
                    request.Headers.TryAddWithoutValidation(name, value);

                using var response = await _client.SendAsync(request, ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Blade model listing failed ({(int)response.StatusCode}): {raw}");

                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                var items = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().ToArray()
                    : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                        ? data.EnumerateArray().ToArray()
                        : [];

                return items.SelectMany(CreateBladeModels).ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private IEnumerable<Model> CreateBladeModels(JsonElement element)
    {
        var id = ReadBladeModelString(element, "id");
        if (string.IsNullOrWhiteSpace(id))
            yield break;

        var types = ReadModelTypes(element).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (types.Length == 0)
            types = ["language"];

        foreach (var type in types)
        {
            yield return new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = ReadBladeModelString(element, "name") ?? id,
                Description = ReadBladeModelString(element, "description"),
                OwnedBy = ReadBladeModelString(element, "owned_by") ?? ReadBladeModelString(element, "ownedBy") ?? GetIdentifier(),
                Created = ReadLong(element, "created"),
                ContextWindow = ReadInt(element, "context_length")
                    ?? ReadInt(element, "context_window")
                    ?? ReadInt(element, "max_input_tokens"),
                MaxTokens = ReadInt(element, "max_output_tokens")
                    ?? ReadInt(element, "max_completion_tokens"),
                Type = type,
                Pricing = ReadPricing(element),
                Tags = ReadStringArray(element, "supported_parameters")
                    .Concat(ReadStringArray(element, "parameters"))
                    .Select(parameter => $"parameter:{parameter}")
                    .Concat(ReadStringArray(element, "capabilities").Select(capability => $"capability:{capability}"))
                    .ToArray()
            };
        }
    }

    private static IEnumerable<string> ReadModelTypes(JsonElement element)
    {
        var inputModalities = ReadStringArray(element, "input_modalities")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textInput = inputModalities.Contains("text");
        var audioInput = inputModalities.Contains("audio") || inputModalities.Contains("speech");

        foreach (var value in ReadStringArray(element, "output_modalities"))
        {
            var type = MapModelType(value, audioInput && !textInput);
            if (type is not null) yield return type;
        }

        foreach (var value in ReadStringArray(element, "modalities")
            .Concat(ReadStringArray(element, "capabilities"))
            .Concat(ReadStringArray(element, "supported_modalities")))
        {
            var type = MapModelType(value, audioInput && !textInput);
            if (type is not null) yield return type;
        }

        var declaredType = ReadBladeModelString(element, "type")?.ToLowerInvariant();
        if (declaredType is not null)
        {
            var type = MapModelType(declaredType, audioInput && !textInput);
            if (type is not null) yield return type;
        }
    }

    private static string? MapModelType(string value, bool audioToText)
        => value.ToLowerInvariant() switch
        {
            "text" => audioToText ? "transcription" : "language",
            "chat" or "responses" or "language" => "language",
            "image" => "image",
            "speech" or "audio" => "speech",
            "transcription" or "audio_transcription" => "transcription",
            "video" => "video",
            "embedding" or "embeddings" => "embedding",
            "rerank" or "reranking" => "reranking",
            "decision" or "decisions" => "decision",
            _ => null
        };

    private static ModelPricing? ReadPricing(JsonElement element)
    {
        if (!element.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
            return null;

        var input = ReadDecimal(pricing, "input") ?? ReadDecimal(pricing, "prompt");
        var output = ReadDecimal(pricing, "output") ?? ReadDecimal(pricing, "completion");
        if ((input is null && output is null) || input is < 0m || output is < 0m)
            return null;

        return new ModelPricing
        {
            Input = input ?? 0m,
            Output = output ?? 0m,
            InputCacheRead = ReadDecimal(pricing, "input_cache_read"),
            InputCacheWrite = ReadDecimal(pricing, "input_cache_write")
        };
    }

    private static string? ReadBladeModelString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

    private static long? ReadLong(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : null;

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static IEnumerable<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>();
    }
}
