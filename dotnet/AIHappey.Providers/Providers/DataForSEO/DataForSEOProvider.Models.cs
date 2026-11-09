using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.DataForSEO;

public partial class DataForSEOProvider
{
    private static readonly string[] Endpoints =
    [
        "chat_gpt",
        "claude",
        "gemini",
        "perplexity"
    ];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return [];

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var models = new List<Model>();

                foreach (var endpoint in Endpoints)
                {
                    using var req = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"v3/ai_optimization/{endpoint}/llm_responses/models");
                    ApplyAuthHeader(req);

                    using var resp = await _client.SendAsync(req, ct);

                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"DataForSEO {endpoint} model discovery failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}", null, resp.StatusCode);

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    var root = doc.RootElement;
                    if (root.TryGetProperty("status_code", out _))
                        EnsureSuccess(root, "model discovery");

                    if (!root.TryGetProperty("tasks", out var tasksEl) ||
                        tasksEl.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException($"DataForSEO {endpoint} model discovery is missing its tasks array.");

                    foreach (var task in tasksEl.EnumerateArray())
                    {
                        if (task.TryGetProperty("status_code", out _))
                            EnsureSuccess(task, "model discovery task");
                        if (!task.TryGetProperty("result", out var resultEl) ||
                            resultEl.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var res in resultEl.EnumerateArray())
                        {
                            if (!res.TryGetProperty("model_name", out var nameEl))
                                continue;

                            var name = nameEl.GetString();

                            if (string.IsNullOrWhiteSpace(name))
                                continue;

                            models.Add(new Model
                            {
                                Id = name.ToModelId(GetIdentifier()),
                                Name = name,
                                OwnedBy = endpoint
                            });
                        }
                    }
                }

                // remove duplicates
                return models
                    .GroupBy(m => m.Id)
                    .Select(g => g.First())
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(6),
            jitterMinutes: 720,
            cancellationToken: cancellationToken);
    }
}