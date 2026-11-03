namespace AIHappey.Core.AI;

/// <summary>Optional compatibility reader for host-owned catalog files. Never writes or downloads data.</summary>
public sealed class FileProviderCatalogSource(string catalogDirectory) : IProviderCatalogSource
{
    private readonly string _root = Path.GetFullPath(catalogDirectory);

    public string CachePartition => _root;

    public IEnumerable<KeyValuePair<string, string>> ReadModels(string provider)
    {
        Validate(provider);
        var directory = Path.Combine(_root, "Models", "providers");
        var legacy = Path.Combine(directory, provider + ".json");
        if (File.Exists(legacy))
            yield return new(Path.GetFileName(legacy), File.ReadAllText(legacy));
        foreach (var document in ReadDirectory(Path.Combine(directory, provider)))
            yield return document;
    }

    public string? ReadPricing(string provider)
    {
        Validate(provider);
        var path = Path.Combine(_root, "Pricing", "providers", provider + ".json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public IEnumerable<KeyValuePair<string, string>> ReadPricingVariants(string provider)
    {
        Validate(provider);
        return ReadDirectory(Path.Combine(_root, "Pricing", "providers", provider));
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            yield return new(Path.GetFileName(path), File.ReadAllText(path));
    }

    private static void Validate(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (provider is "." or ".." || provider.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("Expected a provider identifier, not a path.", nameof(provider));
    }
}
