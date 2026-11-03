namespace AIHappey.Core.AI;

/// <summary>
/// Explicit read-only input for one async operation, including its stream enumeration.
/// Keeps existing provider method signatures; contains no service locator, shared cache, or global configurable source.
/// </summary>
public sealed class ProviderCatalogScope : IDisposable
{
    private static readonly AsyncLocal<IProviderCatalogSource?> Source = new();
    private static readonly IProviderCatalogSource CompatibilitySource =
        new FileProviderCatalogSource(Path.Combine(AppContext.BaseDirectory, "Catalog"));
    private readonly IProviderCatalogSource? _previous;
    private bool _disposed;

    private ProviderCatalogScope(IProviderCatalogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _previous = Source.Value;
        Source.Value = source;
    }

    public static ProviderCatalogScope Begin(IProviderCatalogSource source) => new(source);

    internal static IProviderCatalogSource Current => Source.Value ?? CompatibilitySource;

    public void Dispose()
    {
        if (_disposed) return;
        Source.Value = _previous;
        _disposed = true;
    }
}
