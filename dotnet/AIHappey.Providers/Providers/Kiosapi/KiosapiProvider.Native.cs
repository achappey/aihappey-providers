using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Kiosapi;

public sealed partial class KiosapiProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, string key,
        CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Kiosapi HTTP {(int)response.StatusCode}: {text}", null, response.StatusCode);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Kiosapi response must be an object.");
        return document.RootElement.Clone();
    }

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string RequiredString(JsonElement value, string name)
        => String(value, name) is { Length: > 0 } text ? text
            : throw new JsonException($"Kiosapi response is missing '{name}'.");

    private static string PathId(string id) => Uri.EscapeDataString(id);

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _cache.GetOrCreateAsync<IEnumerable<Model>>(this.GetCacheKey(key), async ct =>
        {
            var root = await SendAsync(HttpMethod.Get, "agents", null, key, ct);
            if (!root.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
                throw new JsonException("Kiosapi agents response is missing the agents array.");
            return agents.EnumerateArray().Select(agent =>
            {
                var id = RequiredString(agent, "id");
                return new Model
                {
                    Id = id.ToModelId(GetIdentifier()), Name = String(agent, "name") ?? id,
                    Description = String(agent, "system_prompt"), OwnedBy = "Kiosapi",
                    Type = "language", Tags = ["agent", "stateful", "tools"]
                };
            }).ToArray();
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }

    private static JsonElement Options(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("kiosapi", out var native) != true || native is null)
            return JsonSerializer.SerializeToElement(new { });
        var value = JsonSerializer.SerializeToElement(native, Json);
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Kiosapi provider metadata must be an object.");
        return value;
    }

    private static Dictionary<string, object?> ObjectOption(JsonElement options, string name)
    {
        if (!options.TryGetProperty(name, out var value)) return new();
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Kiosapi '{name}' must be an object.");
        return value.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }
}
