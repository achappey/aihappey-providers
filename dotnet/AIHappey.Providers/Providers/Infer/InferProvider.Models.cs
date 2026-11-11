using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Infer;

public partial class InferProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await _client.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"INFER models API returned {(int)response.StatusCode}: {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var models = new List<Model>();

                var responseBody = document.RootElement;
                if (responseBody.TryGetProperty("data", out var envelope)
                    && envelope.ValueKind == JsonValueKind.Object)
                    responseBody = envelope;

                if (!responseBody.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    return models;

                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                        continue;

                    var id = idElement.GetString()!;
                    var model = new Model
                    {
                        Id = id.ToModelId(GetIdentifier()),
                        Name = id
                    };

                    if (item.TryGetProperty("owned_by", out var ownedBy)
                        && ownedBy.ValueKind == JsonValueKind.String)
                        model.OwnedBy = ownedBy.GetString();

                    if (item.TryGetProperty("pricing", out var pricing)
                        && pricing.ValueKind == JsonValueKind.Object
                        && pricing.TryGetProperty("unit", out var unit)
                        && unit.ValueKind == JsonValueKind.String
                        && string.Equals(unit.GetString(), "per_million_tokens", StringComparison.OrdinalIgnoreCase)
                        && pricing.TryGetProperty("prompt", out var prompt)
                        && pricing.TryGetProperty("completion", out var completion)
                        && prompt.ValueKind == JsonValueKind.Number
                        && completion.ValueKind == JsonValueKind.Number)
                    {
                        model.Pricing = new ModelPricing
                        {
                            Input = prompt.GetDecimal() / 1_000_000m,
                            Output = completion.GetDecimal() / 1_000_000m
                        };
                    }

                    models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
