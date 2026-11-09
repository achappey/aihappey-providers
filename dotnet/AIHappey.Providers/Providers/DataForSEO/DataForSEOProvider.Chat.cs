using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.DataForSEO;

public partial class DataForSEOProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }
}
