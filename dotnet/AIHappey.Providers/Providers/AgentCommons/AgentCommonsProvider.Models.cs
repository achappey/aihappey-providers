using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AgentCommons;

public partial class AgentCommonsProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var raw = await SendJson(HttpMethod.Get, "v1/agents", null, ct);
            var agents = Data(raw);
            if (agents.ValueKind != JsonValueKind.Array)
                throw new JsonException("AgentCommons list agents response requires a data array.");
            return agents.EnumerateArray().Select(agent => new Model
            {
                Id = RequiredString(agent, "id").ToModelId(GetIdentifier()),
                Name = String(agent, "name") ?? RequiredString(agent, "id"),
                Description = String(agent, "description"),
                OwnedBy = "AgentCommons", Type = "chat", Tags = ["agent"]
            }).ToList();
        }, baseTtl: TimeSpan.FromHours(1), jitterMinutes: 30, cancellationToken: cancellationToken);
    }
}
