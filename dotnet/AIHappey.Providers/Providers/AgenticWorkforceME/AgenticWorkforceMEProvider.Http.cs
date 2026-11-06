using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AgenticWorkforceME;

public sealed partial class AgenticWorkforceMEProvider
{
    private const string Api = "https://console.agenticworkforce.me/v1/";

    private string Key()
    {
        var key = _keys.Resolve(GetIdentifier());
        return !string.IsNullOrWhiteSpace(key) ? key : throw new InvalidOperationException("No AgenticWorkforceME API key.");
    }

    private static string Tenant(string? tenant)
        => !string.IsNullOrWhiteSpace(tenant) ? tenant
            : throw new InvalidOperationException("AgenticWorkforceME requires TenantId configuration or tenant_id provider metadata.");

    private HttpRequestMessage Http(HttpMethod method, string path, string key, string tenant, object? body = null)
    {
        var http = new HttpRequestMessage(method, Api + path);
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.Headers.Add("X-Tenant-Id", tenant);
        if (body is not null) http.Content = JsonContent.Create(body);
        return http;
    }

    private async Task<JsonElement> Send(HttpMethod method, string path, string key, string tenant, object? body, CancellationToken ct,
        string? idempotencyKey = null)
    {
        using var http = Http(method, path, key, tenant, body);
        if (idempotencyKey is not null) http.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await _client.SendAsync(http, ct);
        await EnsureSuccess(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("AgenticWorkforceME response must be a JSON object.");
        return document.RootElement.Clone();
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"AgenticWorkforceME HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_keys.Resolve(GetIdentifier()))) return [];
        var key = Key();
        var tenant = Tenant(_options.TenantId);
        var models = new List<Model>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await Send(HttpMethod.Get, "agents?limit=100" +
                (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), key, tenant, null, cancellationToken);
            if (!page.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new JsonException("AgenticWorkforceME agent page must contain an items array.");
            foreach (var agent in items.EnumerateArray())
            {
                var id = Required(agent, "id");
                foreach (var oneOff in new[] { false, true })
                    models.Add(new Model
                    {
                        Id = $"{GetIdentifier()}/{id}" + (oneOff ? "/run" : ""),
                        Name = (String(agent, "name") ?? id) + (oneOff ? " (one-off run)" : ""),
                        Description = String(agent, "description"), OwnedBy = "AgenticWorkforceME", Type = "chat", Tags = ["agent"]
                    });
            }
            if (page.TryGetProperty("next_cursor", out var next) && next.ValueKind != JsonValueKind.Null
                && (next.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(next.GetString())))
                throw new JsonException("AgenticWorkforceME agent page has an invalid next_cursor.");
            cursor = String(page, "next_cursor");
            if (cursor is not null && !cursors.Add(cursor))
                throw new JsonException("AgenticWorkforceME agent page returned a repeated cursor.");
        } while (cursor is not null);
        return models;
    }

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string Required(JsonElement value, string name)
        => String(value, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text
            : throw new JsonException($"AgenticWorkforceME response is missing {name}.");
}
