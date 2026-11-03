using AIHappey.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.AI;

/// <summary>
/// Small direct-use entry point. Each call owns its provider instances, frozen credentials, HTTP client headers,
/// and stream lifetime. The HTTP handler factory may be shared; provider instances are never shared across calls.
/// </summary>
public sealed class ProviderClient(IHttpClientFactory httpClientFactory)
{
    public async Task<T> ExecuteAsync<T>(
        string providerId,
        IReadOnlyDictionary<string, string> credentials,
        Func<IModelProvider, CancellationToken, Task<T>> execute,
        ProviderRequestOptions? requestOptions = null,
        IProviderCatalogSource? catalog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        cancellationToken.ThrowIfCancellationRequested();
        using var source = ProviderCatalogScope.Begin(catalog ?? EmptyProviderCatalogSource.Instance);
        await using var container = CreateContainer(credentials, requestOptions);
        await using var scope = container.CreateAsyncScope();
        var provider = Resolve(container, scope.ServiceProvider, providerId);
        return await execute(provider, cancellationToken);
    }

    public async IAsyncEnumerable<T> StreamAsync<T>(
        string providerId,
        IReadOnlyDictionary<string, string> credentials,
        Func<IModelProvider, CancellationToken, IAsyncEnumerable<T>> stream,
        ProviderRequestOptions? requestOptions = null,
        IProviderCatalogSource? catalog = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        using var source = ProviderCatalogScope.Begin(catalog ?? EmptyProviderCatalogSource.Instance);
        await using var container = CreateContainer(credentials, requestOptions);
        await using var scope = container.CreateAsyncScope();
        var provider = Resolve(container, scope.ServiceProvider, providerId);
        await foreach (var part in stream(provider, cancellationToken).WithCancellation(cancellationToken))
            yield return part;
    }

    private ServiceProvider CreateContainer(IReadOnlyDictionary<string, string> credentials, ProviderRequestOptions? requestOptions)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var services = new ServiceCollection();
        services.AddSingleton<IApiKeyResolver>(new FrozenCredentials(credentials));
        services.AddScoped<IHttpClientFactory>(_ => new OperationHttpClientFactory(httpClientFactory));
        services.AddSingleton(requestOptions ?? new ProviderRequestOptions());
        services.AddProviders();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static IModelProvider Resolve(ServiceProvider container, IServiceProvider scope, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return container.GetRequiredService<ProviderRegistry>().GetModelProvider(scope, providerId)
            ?? throw new ArgumentException($"Unknown provider '{providerId}'.", nameof(providerId));
    }

    private sealed class FrozenCredentials(IReadOnlyDictionary<string, string> values) : IApiKeyResolver
    {
        private readonly IReadOnlyDictionary<string, string> _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        public string? Resolve(string provider) => _values.GetValueOrDefault(provider);
    }

    private sealed class OperationHttpClientFactory(IHttpClientFactory inner) : IHttpClientFactory, IDisposable
    {
        private readonly List<HttpClient> _clients = [];
        private bool _disposed;

        public HttpClient CreateClient(string name)
        {
            lock (_clients)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var client = inner.CreateClient(name);
                _clients.Add(client);
                return client;
            }
        }

        public void Dispose()
        {
            HttpClient[] clients;
            lock (_clients)
            {
                if (_disposed) return;
                _disposed = true;
                clients = _clients.ToArray();
                _clients.Clear();
            }
            foreach (var client in clients)
                client.Dispose();
        }
    }
}
