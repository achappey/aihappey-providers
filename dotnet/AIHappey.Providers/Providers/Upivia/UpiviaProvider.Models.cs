using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Upivia;

public partial class UpiviaProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var raw = await SendJson(HttpMethod.Get, "agents", null, ct);
            var agents = Property(raw, "agents");
            if (agents.ValueKind != JsonValueKind.Array)
                throw new JsonException("Upivia list agents response requires an agents array.");
            return agents.EnumerateArray().Select(agent => new Model
            {
                Id = RequiredString(agent, "id").ToModelId(GetIdentifier()),
                Name = String(agent, "name") ?? RequiredString(agent, "id"),
                Description = String(agent, "description"),
                OwnedBy = "Upivia", Type = "chat", Tags = ["agent"]
            }).ToList();
        }, baseTtl: TimeSpan.FromHours(1), jitterMinutes: 30, cancellationToken: cancellationToken);
    }
}
