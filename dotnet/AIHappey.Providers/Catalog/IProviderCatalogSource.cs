namespace AIHappey.Core.AI;

/// <summary>Read-only catalog input. Documents are returned as JSON so existing model/pricing contracts stay unchanged.</summary>
public interface IProviderCatalogSource
{
    /// <summary>Stable identifier for the data revision/tenant when a host enables caching.</summary>
    string CachePartition { get; }
    IEnumerable<KeyValuePair<string, string>> ReadModels(string provider);
    string? ReadPricing(string provider);
    IEnumerable<KeyValuePair<string, string>> ReadPricingVariants(string provider);
}
