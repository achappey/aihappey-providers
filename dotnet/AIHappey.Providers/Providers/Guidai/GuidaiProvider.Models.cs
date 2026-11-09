using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Guidai;

public sealed partial class GuidaiProvider
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
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new JsonException("GUID.AI model catalog must contain a data array.");

            var models = new List<Model>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(idElement.GetString()))
                    continue;
                var id = idElement.GetString()!;
                var endpoints = item.TryGetProperty("supported_endpoint_types", out var types) && types.ValueKind == JsonValueKind.Array
                    ? types.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList()
                    : [];
                // Only text endpoints (openai / openai-response / completion) are implemented here.
                if (endpoints.Count > 0 && !endpoints.Any(e => e.StartsWith("openai", StringComparison.OrdinalIgnoreCase)
                    || e.Equals("chat", StringComparison.OrdinalIgnoreCase)
                    || e.Equals("completion", StringComparison.OrdinalIgnoreCase)))
                    continue;
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = id,
                    OwnedBy = item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                        ? owner.GetString()! : "GUID.AI",
                    Type = "language",
                    Created = item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                        && created.TryGetInt64(out var timestamp) ? timestamp : null
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 0, cancellationToken: cancellationToken);
    }
}
