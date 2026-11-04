using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tilde;

public sealed partial class TildeProvider
{
    private AIRequest ToTildeUnifiedRequest(ResponseRequest request)
        => request.ToUnifiedRequest(GetIdentifier());

    private AIRequest ToTildeUnifiedRequest(MessagesRequest request)
    {
        if (request.OutputConfig?.Format is not null)
            throw new NotSupportedException("Tilde supports text translation, not response formats.");
        return request.ToUnifiedRequest(GetIdentifier());
    }
}
