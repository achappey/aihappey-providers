using AIHappey.Core.AI;
using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.APIyi;

public partial class APIyiProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(APIyi)} API key.");

        var cacheKey = this.GetCacheKey(key);
        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var resp = await _client.SendAsync(req, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException($"APIyi model listing failed ({(int)resp.StatusCode}): {err}");
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                var root = doc.RootElement;

                var arr = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
                        ? dataEl.EnumerateArray()
                        : Enumerable.Empty<JsonElement>();

                foreach (var el in arr)
                {
                    if (!el.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                        continue;

                    var id = idElement.GetString()!;
                    models.Add(new Model
                    {
                        Id = id.ToModelId(GetIdentifier()),
                        Name = id,
                        OwnedBy = el.TryGetProperty("owned_by", out var ownerElement)
                                  && ownerElement.ValueKind == JsonValueKind.String
                            ? ownerElement.GetString()
                            : null
                    });
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}