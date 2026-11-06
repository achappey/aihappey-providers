using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AgencyOSX;

public sealed partial class AgencyOSXProvider
{
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AgencyOSX HTTP {(int)response.StatusCode}: {text}", null, response.StatusCode);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("AgencyOSX response must be a JSON object.");
        return document.RootElement.Clone();
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _cache.GetOrCreateAsync<IEnumerable<Model>>(this.GetCacheKey(key), async ct =>
        {
            var models = new List<Model>();
            var offset = 0;
            while (true)
            {
                var root = await SendAsync(HttpMethod.Get,
                    $"agents?limit=100&offset={offset.ToString(CultureInfo.InvariantCulture)}", null, key, ct);
                if (!root.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("pagination", out var pagination) || pagination.ValueKind != JsonValueKind.Object
                    || !pagination.TryGetProperty("hasMore", out var hasMore)
                    || hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new JsonException("Invalid AgencyOSX agents-list response.");

                foreach (var agent in agents.EnumerateArray())
                {
                    var id = String(agent, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        throw new JsonException("AgencyOSX agent is missing an ID.");
                    models.Add(new Model
                    {
                        Id = id.ToModelId(GetIdentifier()), Name = String(agent, "name") ?? id,
                        Description = String(agent, "description") ?? String(agent, "title"),
                        OwnedBy = "AgencyOSX", Type = "chat", Tags = ["agent"]
                    });
                }

                if (!hasMore.GetBoolean()) return models;
                if (agents.GetArrayLength() == 0
                    || !pagination.TryGetProperty("offset", out var pageOffset) || pageOffset.ValueKind != JsonValueKind.Number
                    || !pageOffset.TryGetInt32(out var current)
                    || current != offset || !pagination.TryGetProperty("limit", out var pageLimit)
                    || pageLimit.ValueKind != JsonValueKind.Number || !pageLimit.TryGetInt32(out var limit)
                    || limit <= 0 || offset > int.MaxValue - limit)
                    throw new JsonException("AgencyOSX agents pagination did not advance.");
                offset += limit;
            }
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
