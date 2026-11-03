namespace AIHappey.Core.Contracts;

/// <summary>Explicit, immutable host inputs for one provider operation. No incoming HTTP request is required.</summary>
public sealed record ProviderRequestOptions
{
    public string? DustWorkspaceId { get; init; }
    public string? YandexFolderId { get; init; }
    public string? SunoCallbackUrl { get; init; }
}
