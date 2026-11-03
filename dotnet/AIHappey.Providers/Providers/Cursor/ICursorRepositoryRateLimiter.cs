namespace AIHappey.Core.Providers.Cursor;

/// <summary>Caller-owned policy for Cursor's unusually restricted repository-list endpoint.</summary>
public interface ICursorRepositoryRateLimiter
{
    void CheckAndRecord(string credentialPartition);
}
