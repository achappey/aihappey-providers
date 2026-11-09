using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Raven;

public sealed partial class RavenProvider
{
    public async Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Query is required.", nameof(request));
        if (request.Documents?.Values.ValueKind != JsonValueKind.Array || request.Documents.Values.GetArrayLength() == 0)
            throw new ArgumentException("Documents.values must be a non-empty array.", nameof(request));
        if (request.TopN is <= 0)
            throw new ArgumentException("TopN must be positive.", nameof(request));

        var documents = request.Documents.Values.EnumerateArray().Select(document => document.ValueKind switch
        {
            JsonValueKind.String => document.GetString()!,
            JsonValueKind.Object when document.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                => text.GetString()!,
            _ => throw new ArgumentException("Documents must be strings or objects with a text field.", nameof(request))
        }).ToArray();
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in request.GetProviderMetadata<Dictionary<string, JsonElement>>(GetIdentifier()) ?? [])
            payload[name] = value.Clone();
        payload["model"] = JsonSerializer.SerializeToElement(LocalModel(request.Model));
        payload["query"] = JsonSerializer.SerializeToElement(request.Query);
        payload["documents"] = JsonSerializer.SerializeToElement(documents);
        if (request.TopN is not null)
            payload["top_n"] = JsonSerializer.SerializeToElement(request.TopN);

        using var http = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "v1/rerank")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        foreach (var (name, value) in Headers())
            http.Headers.TryAddWithoutValidation(name, value);
        var now = DateTime.UtcNow;
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Raven rerank failed ({(int)response.StatusCode}): {raw}", null, response.StatusCode);

        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        // TEI may return its rankings directly instead of wrapping a results array.
        var results = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("results", out var values) ? values
            : throw new JsonException("Raven rerank response must contain results.");
        if (results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Raven rerank results must be an array.");
        var ranking = results.EnumerateArray().Select(result => new RerankingRanking
        {
            Index = result.GetProperty("index").GetInt32(),
            RelevanceScore = (float)(result.TryGetProperty("relevance_score", out var score)
                ? score.GetDouble() : result.GetProperty("score").GetDouble())
        }).ToList();

        return new RerankingResponse
        {
            Ranking = ranking,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Id = root.ValueKind == JsonValueKind.Object ? root.TryGetId() : null,
                Timestamp = now,
                ModelId = LocalModel(request.Model).ToModelId(GetIdentifier()),
                Headers = response.GetHeaders(),
                Body = root.Clone()
            }
        };
    }
}
