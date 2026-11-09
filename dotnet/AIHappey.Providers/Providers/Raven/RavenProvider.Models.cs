using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Raven;

public sealed partial class RavenProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        return await cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "v1/models");
            foreach (var (name, value) in Headers(key: key))
                request.Headers.TryAddWithoutValidation(name, value);
            using var response = await _client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new JsonException("Raven model discovery must contain a data array.");

            var models = new List<Model>();
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(idElement.GetString())) continue;
                var id = idElement.GetString()!;
                var model = new Model { Id = id.ToModelId(GetIdentifier()), Name = id };
                if (item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String)
                    model.OwnedBy = owner.GetString()!;
                if (item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                    && created.TryGetInt64(out var timestamp))
                    model.Created = timestamp;
                models.Add(model);
            }
            return (IEnumerable<Model>)models.DistinctBy(model => model.Id).ToArray();
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 480, cancellationToken: cancellationToken);
    }
}
