namespace AIHappey.Core.AI;

/// <summary>No catalog data. Direct requests do not need gateway catalogs; catalog-only discovery remains empty.</summary>
public sealed class EmptyProviderCatalogSource : IProviderCatalogSource
{
    public static EmptyProviderCatalogSource Instance { get; } = new();
    public string CachePartition => "empty";
    public IEnumerable<KeyValuePair<string, string>> ReadModels(string provider) => [];
    public string? ReadPricing(string provider) => null;
    public IEnumerable<KeyValuePair<string, string>> ReadPricingVariants(string provider) => [];
}
