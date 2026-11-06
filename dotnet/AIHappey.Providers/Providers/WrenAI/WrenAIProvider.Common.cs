using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.WrenAI;

public sealed partial class WrenAIProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private sealed record WrenFrame(string Event, JsonElement Data);

    private static JsonElement Serialize(object? value) => value is JsonElement element
        ? element.Clone() : JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) ? property.Clone() : null;
    private static string? String(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static string? Text(JsonObject value, string name) => value[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
    private static JsonObject Object(object? value, string name) => JsonNode.Parse(Serialize(value).GetRawText()) as JsonObject
        ?? throw new ArgumentException($"{name} must be a JSON object.");

    private static string ProjectId(string? model)
    {
        var local = model ?? "";
        if (local.StartsWith("wrenai/", StringComparison.OrdinalIgnoreCase)) local = local[7..];
        var parts = local.Split('/');
        if (parts.Length != 2 || parts[0] != "projects" || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException("WrenAI models must be wrenai/projects/{projectId}.");
        return parts[1];
    }

    // Native Wren options live under metadata.wrenai and are passed through unchanged.
    private static JsonObject Options(AIRequest request)
        => request.Metadata?.TryGetValue("wrenai", out var direct) == true ? Object(direct, "WrenAI options") : [];

    private static JsonNode ProjectValue(string project)
        => long.TryParse(project, out var id) ? JsonValue.Create(id) : JsonValue.Create(project);

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(ct);
        var exception = new HttpRequestException($"WrenAI HTTP {(int)response.StatusCode}: {raw}", null, response.StatusCode);
        exception.Data["wrenai.raw"] = raw;
        if (response.Headers.RetryAfter is { } retry)
            exception.Data["Retry-After"] = retry.ToString();
        throw exception;
    }

    private async Task<JsonElement> SendJson(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var http = CreateRequest(method, path);
        if (body is not null)
            http.Content = new StringContent(body.ToJsonString(Json), System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return Serialize(new { });
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static Dictionary<string, Dictionary<string, object>> Scoped(JsonElement raw)
        => new() { ["wrenai"] = new() { ["raw"] = raw.Clone() } };

    private static Dictionary<string, object?> Metadata(string project) => new()
    {
        ["wrenai"] = new Dictionary<string, object?> { ["projectId"] = project }
    };

    private static AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?>? metadata = null) => new()
    {
        ProviderId = "wrenai",
        Event = new AIEventEnvelope
        {
            Type = type,
            Id = id,
            Data = data,
            Metadata = metadata,
            Timestamp = DateTimeOffset.UtcNow
        },
        Metadata = metadata
    };

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var value = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(value)) return [];
        var (orgId, _) = Credentials();
        // Projects can only be listed with an organization key and its orgId.
        if (orgId is null) return [];

        return await _cache.GetOrCreateAsync(this.GetCacheKey(value), async ct =>
        {
            var raw = await SendJson(HttpMethod.Get, $"projects?orgId={Uri.EscapeDataString(orgId)}", null, ct);
            var items = raw.ValueKind == JsonValueKind.Array ? raw
                : Property(raw, "data") ?? Property(raw, "projects") ?? default;
            if (items.ValueKind != JsonValueKind.Array) return (IEnumerable<Model>)[];

            return items.EnumerateArray()
                .Select(project => (Project: project, Id: Property(project, "id")?.ToString() ?? Property(project, "projectId")?.ToString()))
                // Only agentic projects accept agent turns.
                .Where(item => !string.IsNullOrWhiteSpace(item.Id)
                    && string.Equals(String(item.Project, "projectType") ?? String(item.Project, "type") ?? "AGENTIC",
                        "AGENTIC", StringComparison.OrdinalIgnoreCase))
                .Select(item => new Model
                {
                    Id = $"projects/{item.Id}".ToModelId(GetIdentifier()),
                    Name = String(item.Project, "displayName") ?? String(item.Project, "name") ?? item.Id!,
                    Type = "language",
                    OwnedBy = "WrenAI",
                    Tags = ["agent"],
                    Description = String(item.Project, "description")
                        ?? $"Wren AI agentic project {item.Id}. Threads, memory and data access belong to this project."
                }).DistinctBy(model => model.Id).ToArray();
        }, baseTtl: TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken: cancellationToken);
    }
}
