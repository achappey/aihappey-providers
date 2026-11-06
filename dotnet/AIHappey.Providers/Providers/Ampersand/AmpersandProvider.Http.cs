using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Ampersand;

public sealed partial class AmpersandProvider
{
    private const string Api = "https://ampersand.humansand.ai/v1/";

    private string Key() => _keys.Resolve(GetIdentifier()) is { } key && !string.IsNullOrWhiteSpace(key)
        ? key : throw new InvalidOperationException("No Ampersand API key.");

    private static HttpRequestMessage Http(HttpMethod method, string path, string key, object? body = null)
    {
        var http = new HttpRequestMessage(method, Api + path);
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (!path.StartsWith("models", StringComparison.Ordinal))
            http.Headers.Add("OpenAI-Beta", "agents=v1");
        if (body is not null) http.Content = JsonContent.Create(body);
        return http;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Ampersand HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
    }

    private async Task<JsonElement> Send(HttpMethod method, string path, string key, object? body, CancellationToken ct)
    {
        using var http = Http(method, path, key, body);
        using var response = await _client.SendAsync(http, ct);
        await EnsureSuccess(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Ampersand response must be an object.");
        return document.RootElement.Clone();
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_keys.Resolve(GetIdentifier()))) return [];
        var raw = await Send(HttpMethod.Get, "models", Key(), null, cancellationToken);
        return Array(raw, "data").Select(model => new Model
        {
            Id = $"{GetIdentifier()}/{Required(model, "id")}", Name = Required(model, "id"),
            OwnedBy = String(model, "owned_by") ?? "Ampersand", Type = "chat", Tags = ["agent"]
        }).ToList();
    }

    private async Task<List<JsonElement>> Page(string path, string key, string? after, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        if (after is not null) cursors.Add(after);
        while (true)
        {
            var page = await Send(HttpMethod.Get, path + "?limit=100&order=asc" +
                (after is null ? "" : "&after=" + Uri.EscapeDataString(after)), key, null, ct);
            result.AddRange(Array(page, "data").Select(item => item.Clone()));
            if (!page.TryGetProperty("has_more", out var more) || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("Ampersand page requires has_more.");
            if (!more.GetBoolean()) return result;
            after = Required(page, "last_id");
            if (!cursors.Add(after)) throw new JsonException("Ampersand page repeated its cursor.");
        }
    }

    private static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string Required(JsonElement value, string name)
        => String(value, name) is { Length: > 0 } text ? text : throw new JsonException($"Ampersand requires {name}.");
    private static JsonElement.ArrayEnumerator Array(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray() : throw new JsonException($"Ampersand requires a {name} array.");
}
