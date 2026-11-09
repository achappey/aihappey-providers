using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Conway;

public sealed partial class ConwayProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "models");
            foreach (var (name, value) in Headers(key: key))
                request.Headers.TryAddWithoutValidation(name, value);
            using var response = await _client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Conway model catalog must be an object.");
            var data = root.TryGetProperty("data", out var array) && array.ValueKind == JsonValueKind.Array ? array
                : root.TryGetProperty("models", out array) && array.ValueKind == JsonValueKind.Array ? array
                : throw new JsonException("Conway model catalog must contain a data or models array.");

            var models = new List<Model>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || item.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False)
                    continue;
                var id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var tags = new List<string>();
                if (item.TryGetProperty("supports_tools", out var tools) && tools.ValueKind == JsonValueKind.True)
                    tags.Add("tools");
                if (item.TryGetProperty("supports_vision", out var vision) && vision.ValueKind == JsonValueKind.True)
                    tags.Add("vision");
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = ReadString(item, "display_name") ?? ReadString(item, "name") ?? id,
                    OwnedBy = ReadString(item, "provider") ?? ReadString(item, "owned_by") ?? "Conway",
                    Description = ReadString(item, "description"),
                    Type = "language",
                    Created = item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                        && created.TryGetInt64(out var timestamp) ? timestamp : null,
                    ContextWindow = ReadInt(item, "context_window"),
                    MaxTokens = ReadInt(item, "max_tokens"),
                    Tags = tags.Count > 0 ? tags : null,
                    Pricing = ReadPricing(item)
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 0, cancellationToken: cancellationToken);
    }

    private static string? ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    private static ModelPricing? ReadPricing(JsonElement item)
    {
        if (!item.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
            return null;
        var input = ReadPrice(pricing, "input");
        var output = ReadPrice(pricing, "output");
        return input is null && output is null ? null : new ModelPricing { Input = input ?? 0m, Output = output ?? 0m };
    }

    private static decimal? ReadPrice(JsonElement pricing, string direction)
    {
        foreach (var suffix in new[] { "_per_million", "_per_1m_tokens_usd", "_per_1k" })
            if (pricing.TryGetProperty(direction + suffix, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDecimal(out var price))
                return price / (suffix == "_per_1k" ? 1_000m : 1_000_000m);
        return null;
    }
}
