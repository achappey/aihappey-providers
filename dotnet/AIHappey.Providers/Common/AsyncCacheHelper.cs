using Microsoft.Extensions.Caching.Memory;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.AI;

public sealed class AsyncCacheHelper(IMemoryCache? memoryCache = null)
{
    private readonly IMemoryCache? _memoryCache = memoryCache;

    public bool TryGetValue<T>(string key, out T? value)
    {
        if (_memoryCache is not null && _memoryCache.TryGetValue(PartitionKey(key), out var raw) && raw is T typed)
        {
            value = CopyModels(typed);
            return true;
        }

        value = default;
        return false;
    }

    public void Set<T>(string key, T value, TimeSpan ttl)
        => _memoryCache?.Set(PartitionKey(key), CopyModels(value), ttl);

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan baseTtl,
        int jitterMinutes = 0,
        CancellationToken cancellationToken = default)
    {
        if (_memoryCache is null)
            return await factory(cancellationToken);

        var value = await _memoryCache.GetOrCreateAsync(PartitionKey(key), async entry =>
        {
            var ttl = baseTtl;

            if (jitterMinutes > 0)
                ttl += TimeSpan.FromMinutes(Random.Shared.Next(0, jitterMinutes));

            entry.AbsoluteExpirationRelativeToNow = ttl;

            return CopyModels(await factory(cancellationToken));
        });

        return CopyModels(value!);
    }

    private static string PartitionKey(string key)
        => $"{ProviderCatalogScope.Current.CachePartition.Length}:{ProviderCatalogScope.Current.CachePartition}:{key}";

    private static T CopyModels<T>(T value)
    {
        if (value is not IEnumerable<Model> models)
            return value;
        var copy = models.Select(model => JsonSerializer.Deserialize<Model>(
            JsonSerializer.SerializeToUtf8Bytes(model, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!).ToList();
        if (copy is T typed) return typed;
        if (copy.ToArray() is T array) return array;
        // Preserve ordered-enumerable return types without retaining mutable model instances.
        if (copy.OrderBy(_ => 0) is T ordered) return ordered;
        throw new InvalidOperationException($"Unsupported model cache result type: {typeof(T)}.");
    }
}
