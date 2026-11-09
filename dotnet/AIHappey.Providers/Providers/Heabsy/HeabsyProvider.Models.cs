using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Heabsy;

public sealed partial class HeabsyProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await _cache.GetOrCreateAsync<IEnumerable<Model>>(this.GetCacheKey(key), async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var data = document.RootElement.GetProperty("data");
            var models = new List<Model>();
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? id : id,
                    OwnedBy = item.TryGetProperty("owned_by", out var owner) ? owner.GetString() : null,
                    Type = "language"
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 60, cancellationToken: cancellationToken);
    }
}
