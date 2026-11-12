using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Gnani;

public partial class GnaniProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier()))
            ? Enumerable.Empty<Model>()
            : GetIdentifier().GetModels());
    }
}
