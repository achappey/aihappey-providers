using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.FrontiereAI;

public sealed partial class FrontiereAIProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => _cache.GetOrCreateAsync<IEnumerable<Model>>(this.GetCacheKey(), async ct =>
        {
            // Model discovery is public and deliberately does not resolve a key.
            using var request = PublicRequest(HttpMethod.Get, "v1/models");
            using var response = await _client.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"FrontiereAI model discovery failed: {raw}", null, response.StatusCode);

            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new JsonException("FrontiereAI models response must contain a data array.");

            List<Model> models = [];
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || ReadString(item, "id") is not { Length: > 0 } id
                    || string.IsNullOrWhiteSpace(id))
                    continue;

                var type = ReadString(item, "type")?.ToLowerInvariant();
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = ReadString(item, "name") ?? id,
                    OwnedBy = ReadString(item, "owned_by") ?? "FrontiereAI",
                    Description = ReadString(item, "description"),
                    Created = item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var timestamp)
                        ? timestamp : null,
                    Type = type switch
                    {
                        "embedding" or "embeddings" => "embedding",
                        "image" => "image",
                        "chat" or "language" => "language",
                        _ => id.GuessModelType()
                    }
                });
            }

            return models.DistinctBy(model => model.Id).ToArray();
        }, TimeSpan.FromHours(4), cancellationToken: cancellationToken);

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
