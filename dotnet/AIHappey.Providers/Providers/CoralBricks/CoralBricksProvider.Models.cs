using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CoralBricks;

public sealed partial class CoralBricksProvider
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
            await EnsureSuccess(response, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            var data = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("data", out var array) && array.ValueKind == JsonValueKind.Array ? array
                : throw new JsonException("CoralBricks model catalog must contain a data array.");
            var models = new List<Model>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = ReadString(item, "name") ?? id,
                    OwnedBy = ReadString(item, "owned_by") ?? "CoralBricks",
                    Description = ReadString(item, "description"),
                    Type = ReadString(item, "type") ?? "language",
                    Created = item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                        && created.TryGetInt64(out var timestamp)
                        ? timestamp : null,
                    ContextWindow = ReadInt(item, "context_window", "context_length", "max_model_len"),
                    MaxTokens = ReadInt(item, "max_tokens", "max_completion_tokens", "max_output_tokens"),
                    Tags = ReadTags(item)
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 0, cancellationToken: cancellationToken);
    }

    private static string? ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
        return null;
    }

    private static IEnumerable<string>? ReadTags(JsonElement item)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "tags", "capabilities", "supported_parameters" })
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array)
                foreach (var tag in value.EnumerateArray())
                    if (tag.ValueKind == JsonValueKind.String) tags.Add(tag.GetString()!);
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var capability in value.EnumerateObject())
                    if (capability.Value.ValueKind == JsonValueKind.True) tags.Add(capability.Name);
        }
        return tags.Count > 0 ? tags : null;
    }
}
