using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Prism;

public partial class PrismProvider
{
    private async Task<IEnumerable<Model>> ListPrismModelsAsync(CancellationToken cancellationToken)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "models");
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(
                    GetAuthHeaders()[nameof(HttpRequestHeaders.Authorization)]);
                using var response = await _client.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"Prism API error: {error}", null, response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var models = new List<Model>();

                if (document.RootElement.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out var idElement))
                            continue;

                        var id = idElement.GetString();
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        models.Add(new Model
                        {
                            Id = id.ToModelId(GetIdentifier()),
                            Name = id,
                            OwnedBy = item.TryGetProperty("owned_by", out var owner)
                                ? owner.GetString() ?? nameof(Prism)
                                : nameof(Prism)
                        });
                    }
                }

                var byId = models.ToDictionary(model => model.Id, StringComparer.Ordinal);
                foreach (var model in GetIdentifier().GetModels())
                    byId[model.Id] = model;

                return byId.Values.ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
