using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CreeksideCompute;

public sealed partial class CreeksideComputeProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = ResolveKey();
        cancellationToken.ThrowIfCancellationRequested();
        return await cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var models = new List<Model>();
            foreach (var row in document.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = Text(row, "id");
                if (string.IsNullOrWhiteSpace(id))
                    throw new JsonException("CreeksideCompute returned a model without an ID.");
                models.Add(new Model
                {
                    Id = $"{GetIdentifier()}/{NativeModel(id)}",
                    Name = Text(row, "name") ?? id,
                    OwnedBy = Text(row, "owned_by"),
                    Description = Text(row, "description"),
                    ContextWindow = Number(row, "context_window") ?? Number(row, "context_length"),
                    MaxTokens = Number(row, "max_output_tokens") ?? Number(row, "max_tokens"),
                    Created = row.TryGetProperty("created", out var created)
                        && created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var timestamp) ? timestamp : null
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 30, cancellationToken: cancellationToken);
    }

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;
}
