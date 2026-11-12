using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.WavenAI;

public sealed partial class WavenAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keys.Resolve(ProviderId);
        if (string.IsNullOrWhiteSpace(key)) return [];
        return await _cache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var models = ProviderId.GetModels().ToList();
            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiBase, "voices"));
            var root = await SendJson(message, ct);
            foreach (var voice in root.GetProperty("voices").EnumerateArray())
            {
                var id = ReadString(voice, "id");
                var engine = ReadString(voice, "engine");
                if (string.IsNullOrWhiteSpace(id) || engine is not ("kokoro" or "chatterbox")) continue;
                models.Add(new Model
                {
                    Id = $"{engine}/{id}".ToModelId(ProviderId),
                    OwnedBy = "WavenAI",
                    Type = "speech",
                    Name = $"WavenAI {engine} - {ReadString(voice, "display_name") ?? id}",
                    Description = ReadString(voice, "label"),
                    Tags = new[] { "voice", ReadString(voice, "language"), ReadString(voice, "gender") }
                        .OfType<string>().ToArray()
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 60, cancellationToken: cancellationToken);
    }
}
