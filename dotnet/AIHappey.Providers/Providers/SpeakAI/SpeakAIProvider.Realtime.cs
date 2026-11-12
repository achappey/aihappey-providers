using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.SpeakAI;

public sealed partial class SpeakAIProvider
{
    // Socket.IO clients pass the minted token as the `speak-api-key` handshake query parameter.
    private const string LiveHost = "wss://listen.speakai.co";
    private const string LiveNamespace = "/v1/live";

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);
        if (LocalModel(realtimeRequest.Model) != "live-transcription")
            throw new ArgumentException("Select the SpeakAI live-transcription model.", nameof(realtimeRequest));

        var issuedAt = DateTimeOffset.UtcNow;
        var raw = await Send(HttpMethod.Post, "live-transcription/token", Options(realtimeRequest.ProviderOptions), cancellationToken);
        var data = Unwrap(raw);
        var token = GetString(data, "token", "accessToken", "jwt")
            ?? throw new JsonException("SpeakAI returned no live transcription token.");

        var expiresAt = ReadExpiry(data, issuedAt) ?? ReadJwtExpiry(token)
            ?? issuedAt.AddMinutes(5).ToUnixTimeSeconds();

        var metadata = JsonNode.Parse(raw.GetRawText())!.AsObject();
        RemoveToken(metadata);
        metadata["connection"] = new JsonObject
        {
            ["url"] = LiveHost, ["namespace"] = LiveNamespace, ["protocol"] = "socket.io",
            ["tokenQueryParameter"] = "speak-api-key"
        };

        return new RealtimeResponse
        {
            Value = token, ExpiresAt = expiresAt,
            ProviderMetadata = new() { [ProviderId] = JsonSerializer.SerializeToElement(metadata) }
        };
    }

    private static void RemoveToken(JsonObject root)
    {
        foreach (var name in new[] { "token", "accessToken", "jwt" })
            root.Remove(name);
        if (root["data"] is JsonObject nested)
            RemoveToken(nested);
    }

    private static long? ReadExpiry(JsonElement data, DateTimeOffset issuedAt)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "expiresIn", "expires_in", "ttl" })
            if (data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return issuedAt.AddSeconds(value.GetDouble()).ToUnixTimeSeconds();

        foreach (var name in new[] { "expiresAt", "expires_at" })
        {
            if (!data.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetInt64();
            if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
                return parsed.ToUnixTimeSeconds();
        }

        return null;
    }

    private static long? ReadJwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number
                ? exp.GetInt64() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
