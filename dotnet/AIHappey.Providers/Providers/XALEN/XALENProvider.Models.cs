using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.XALEN;

public sealed partial class XALENProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = GetApiKey();
        return asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await _client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"XALEN model discovery failed with status {(int)response.StatusCode}: {error}",
                        null, response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("XALEN model discovery must contain a data array.");

                var models = new List<Model>();
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("id", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(idElement.GetString()))
                        throw new JsonException("XALEN model discovery returned a model without an id.");

                    var id = idElement.GetString()!;
                    var model = new Model
                    {
                        Id = id.ToModelId(GetIdentifier()),
                        Name = GetString(item, "name") ?? id,
                        OwnedBy = GetString(item, "owned_by") ?? GetIdentifier(),
                        Description = GetString(item, "description"),
                        Type = "language"
                    };

                    if (item.TryGetProperty("created", out var created) &&
                        created.ValueKind == JsonValueKind.Number &&
                        created.TryGetInt64(out var timestamp))
                        model.Created = timestamp;

                    models.Add(model);
                }

                return (IEnumerable<Model>)models.DistinctBy(model => model.Id).ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string? GetString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
