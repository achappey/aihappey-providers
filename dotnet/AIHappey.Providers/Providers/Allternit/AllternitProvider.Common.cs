using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Allternit;

public sealed partial class AllternitProvider
{
    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string RequiredString(JsonElement value, string name)
        => String(value, name) is { Length: > 0 } text ? text
            : throw new JsonException($"Allternit response is missing {name}.");

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Allternit identifiers must contain only ASCII letters, digits, underscores, or hyphens.");
        return value;
    }

    private HttpRequestMessage HttpRequest(HttpMethod method, string path, object? body = null)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Allternit API key.");
        var request = new HttpRequestMessage(method, "https://api.allternit.com/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Allternit HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
    }

    private async Task<JsonElement> SendJson(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = HttpRequest(method, path, body);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_keys.Resolve(GetIdentifier()))) return [];
        var models = new List<Model>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            var page = await SendJson(HttpMethod.Get, "agents?limit=100"
                + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)), null, cancellationToken);
            if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                throw new JsonException("Allternit agent list must contain a data array.");
            foreach (var agent in data.EnumerateArray())
            {
                var id = Segment(RequiredString(agent, "id"));
                models.Add(new Model
                {
                    Id = $"allternit/{id}", Name = String(agent, "name") ?? id,
                    OwnedBy = "Allternit", Type = "language", Tags = ["agent", "stateful", "tools"]
                });
            }
            if (!page.TryGetProperty("has_more", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("Allternit agent list must contain has_more.");
            if (!more.GetBoolean()) break;
            after = data.GetArrayLength() > 0 ? RequiredString(data[data.GetArrayLength() - 1], "id") : null;
            if (after is null || !cursors.Add(after))
                throw new JsonException("Allternit agent list did not advance its cursor.");
        } while (true);
        return models;
    }

    private static Dictionary<string, object?> Metadata(string conversationId, JsonElement raw)
        => new() { ["allternit"] = new { conversation_id = conversationId, raw } };

    private static Dictionary<string, object> LooseMetadata(string conversationId, JsonElement raw)
        => new() { ["allternit"] = new { conversation_id = conversationId, raw } };

    private static Dictionary<string, Dictionary<string, object>> ScopedMetadata(string conversationId, JsonElement raw)
        => new() { ["allternit"] = new() { ["conversation_id"] = conversationId, ["raw"] = raw } };

    private AIStreamEvent Event(string type, string id, object data, string conversationId, JsonElement raw)
    {
        var metadata = Metadata(conversationId, raw);
        return new AIStreamEvent
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata
            }
        };
    }
}
