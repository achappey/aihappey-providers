using System.Text.Json;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.NiaVoice;

public sealed partial class NiaVoiceProvider
{
    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
    {
        var path = LocalModel(realtimeRequest.Model) switch
        {
            "nia-realtime-1" => "v1/realtime/session",
            "nia-conversational-en" => "v1/realtime/conversational",
            "nia-realtime-tts-1" => "v1/realtime/tts",
            "nia-realtime-stt-1" => "v1/realtime/stt",
            "nia-live-stt-auto-1" => "v1/realtime/stt/auto",
            "nia-live-stt-lang-1" => "v1/realtime/stt/lang",
            _ => throw new ArgumentException("Select a NiaVoice realtime model.", nameof(realtimeRequest))
        };
        var payload = Options(realtimeRequest.ProviderOptions);
        var issuedAt = DateTimeOffset.UtcNow;
        var raw = await SendJson(path, payload, cancellationToken);
        var token = RequiredString(raw, "token");
        if (string.IsNullOrWhiteSpace(token))
            throw new JsonException("NiaVoice returned an empty realtime token.");
        var expiresAt = raw.TryGetProperty("expires_at", out var expires)
            ? DateTimeOffset.Parse(expires.GetString()!).ToUnixTimeSeconds()
            : issuedAt.AddSeconds(raw.GetProperty("expires_in").GetDouble()).ToUnixTimeSeconds();
        // Keep native signaling URLs, ICE servers, resolved settings and future fields intact.
        var metadata = Options(raw);
        metadata.Remove("token");
        return new RealtimeResponse
        {
            Value = token, ExpiresAt = expiresAt,
            ProviderMetadata = new() { [ProviderId] = JsonSerializer.SerializeToElement(metadata) }
        };
    }
}
