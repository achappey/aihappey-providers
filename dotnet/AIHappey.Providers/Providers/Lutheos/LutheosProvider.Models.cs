using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Lutheos;

public partial class LutheosProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var models = new List<Model>();
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(idElement.GetString()))
                    continue;

                var id = idElement.GetString()!;
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = id,
                    OwnedBy = item.TryGetProperty("owned_by", out var owner) ? owner.GetString() : null,
                    Description = item.TryGetProperty("description", out var description) ? description.GetString() : null
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 60, cancellationToken: cancellationToken);
    }
}
