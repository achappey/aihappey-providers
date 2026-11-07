using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SundayPyjamas;

public partial class SundayPyjamasProvider
{
    private const string ChatModel = "llama-3.3-70b-versatile";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var raw = await SendJsonAsync(HttpMethod.Get, "managed-agents/agents", null, ct);
            if (!raw.TryGetProperty("agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
                throw new JsonException("SundayPyjamas catalog is missing an agents array.");
            var models = new List<Model>
            {
                new() { Id = ChatModel.ToModelId(GetIdentifier()), Name = ChatModel, OwnedBy = "SundayPyjamas", Type = "chat" }
            };
            foreach (var agent in agents.EnumerateArray())
            {
                var id = RequiredString(agent, "id");
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = String(agent, "name") ?? id,
                    Description = String(agent, "description") ?? String(agent, "businessUseCase"),
                    OwnedBy = "SundayPyjamas", Type = "chat", Tags = ["agent"]
                });
            }
            return (IEnumerable<Model>)models;
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
