using AIHappey.Core.AI;

namespace AIHappey.Tests;

internal sealed class TestCatalogSource(string provider, string models, string? pricing = null, string? partition = null)
    : IProviderCatalogSource
{
    public string CachePartition { get; } = partition ?? Guid.NewGuid().ToString("n");
    public IEnumerable<KeyValuePair<string, string>> ReadModels(string id)
        => id == provider ? [new(provider + ".json", models)] : [];
    public string? ReadPricing(string id) => id == provider ? pricing : null;
    public IEnumerable<KeyValuePair<string, string>> ReadPricingVariants(string id) => [];
}
