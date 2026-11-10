using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Inferel;

public sealed partial class InferelProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var catalog = await SendJson(HttpMethod.Get, "v1/models", key, null, ct);
            var models = new List<Model>();
            foreach (var row in catalog.GetProperty("data").EnumerateArray())
            {
                var id = RequiredText(row, "id");
                models.Add(new Model
                {
                    Id = $"inferel/{NativeModel(id)}", Name = Text(row, "name") ?? id,
                    OwnedBy = Text(row, "owned_by") ?? "Inferel", Type = "language",
                    Description = Text(row, "description"),
                    ContextWindow = row.TryGetProperty("context_length", out var context) ? context.GetInt32() : null,
                    MaxTokens = row.TryGetProperty("max_completion_tokens", out var max) ? max.GetInt32() : null,
                    Pricing = row.TryGetProperty("pricing", out var pricing) ? new ModelPricing
                    {
                        Input = Price(pricing, "prompt") ?? throw new JsonException("Inferel pricing is missing 'prompt'."),
                        Output = Price(pricing, "completion") ?? throw new JsonException("Inferel pricing is missing 'completion'."),
                        InputCacheRead = Price(pricing, "input_cache_read"),
                        InputCacheWrite = Price(pricing, "input_cache_write")
                    } : null
                });
            }
            var generation = await SendJson(HttpMethod.Get, QueueUrl + "models", key, null, ct);
            foreach (var row in generation.GetProperty("model_ids").EnumerateArray())
            {
                var id = row.GetString();
                if (string.IsNullOrWhiteSpace(id)) throw new JsonException("Inferel returned an empty generation model ID.");
                var detail = await ModelDetail(id, key, ct);
                var type = Text(detail, "model_type") ?? Text(detail, "type") ?? id.GuessModelType();
                if (type is not ("image" or "video"))
                    throw new JsonException($"Inferel generation model '{id}' has an unknown media type: {type}.");
                models.Add(new Model
                {
                    Id = $"inferel/{NativeModel(id)}", Name = Text(detail, "name") ?? id,
                    OwnedBy = "Inferel", Type = type, Description = Text(detail, "description")
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 30, cancellationToken: cancellationToken);
    }

    private Task<JsonElement> ModelDetail(string model, string key, CancellationToken ct)
        => _cache.GetOrCreateAsync($"{this.GetCacheKey(key)}:generation:{NativeModel(model)}",
            token => SendJson(HttpMethod.Get, QueueUrl + "models/" + Uri.EscapeDataString(NativeModel(model)),
                key, null, token),
            baseTtl: TimeSpan.FromHours(4), jitterMinutes: 30, cancellationToken: ct);

    private async Task<JsonElement> SendJson(HttpMethod method, string path, string key, object? body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Inferel API error ({(int)response.StatusCode}): {raw}", null, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(raw);
    }

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RequiredText(JsonElement row, string name)
        => Text(row, name) is { } text && !string.IsNullOrWhiteSpace(text) ? text
            : throw new JsonException($"Inferel response is missing '{name}': {row.GetRawText()}");

    private static decimal? Price(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price < 0)
            throw new JsonException($"Invalid Inferel price '{name}': {value}");
        return price;
    }
}
