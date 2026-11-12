using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Speechmatics;

public partial class SpeechmaticsProvider
{
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default)
        => MintRealtimeTokenAsync(request.Model, request.ProviderOptions, cancellationToken);

    public async Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
    {
        var config = request.SessionConfig;
        if (config is not null && (config.Instructions is not null || config.Voice is not null || config.OutputModalities is not null
            || config.InputAudioFormat is not null || config.OutputAudioFormat is not null || config.InputAudioTranscription is not null
            || config.OutputAudioTranscription is not null || config.HasTurnDetection || config.Tools is not null))
            throw new NotSupportedException("Speechmatics recognition settings must be sent over the client WebSocket.");
        var local = NormalizeRealtimeModel(model);
        var host = "global";
        if (config?.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true && options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty("region", out var region) && region.ValueKind != JsonValueKind.Null)
            host = region.ValueKind == JsonValueKind.String ? region.GetString()! : "";
        if (host is not ("global" or "eu" or "us" or "au")) throw new ArgumentException("Speechmatics region must be global, eu, us, or au.");
        var token = await MintRealtimeTokenAsync(model, config?.ProviderOptions, cancellationToken);
        return new() { Token = token.Value, ExpiresAt = token.ExpiresAt,
            Url = $"wss://{host}.rt.speechmatics.com/v2" + (local == "linden-1" ? "/agent" : "") };
    }

    private static string NormalizeRealtimeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var local = NormalizeTranscriptionModelId(model);
        if (!RealtimeCapabilities.IsSpeechmaticsRealtimeTranscription(local))
            throw new ArgumentException("Select a supported Speechmatics realtime transcription model.", nameof(model));
        return local;
    }

    private async Task<RealtimeResponse> MintRealtimeTokenAsync(string model, Dictionary<string, JsonElement>? providerOptions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = NormalizeRealtimeModel(model);
        var ttl = 60;
        if (providerOptions?.TryGetValue(GetIdentifier(), out var options) == true && options.ValueKind != JsonValueKind.Null)
        {
            if (options.ValueKind != JsonValueKind.Object) throw new ArgumentException("Speechmatics provider options must be an object.");
            if (options.TryGetProperty("ttl", out var value) && value.ValueKind != JsonValueKind.Null
                && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out ttl) || ttl < 60 || ttl > 86400))
                throw new ArgumentException("Speechmatics ttl must be an integer from 60 to 86400 seconds.");
        }
        // The API returns only key_value. Start the lifetime before the request so
        // our advertised expiry cannot overstate the upstream token's lifetime.
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl).ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://mp.speechmatics.com/v1/jwts?type=rt")
            { Content = JsonContent.Create(new { ttl }) };
        try { ApplyAuthHeader(request); }
        catch (Exception error) when (error is FormatException or ArgumentException)
        { throw new InvalidOperationException("Speechmatics API key configuration is invalid."); }
        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speechmatics realtime token creation failed (HTTP {(int)response.StatusCode}). Check credentials and temporary-key access for your account.");
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var token = document.RootElement.GetProperty("key_value").GetString();
            if (string.IsNullOrWhiteSpace(token) || token.Length > 64000 || token.Any(char.IsWhiteSpace)
                || token == request.Headers.Authorization?.Parameter || expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                throw new InvalidOperationException();
            return new() { Value = token, ExpiresAt = expiresAt };
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("Speechmatics returned an invalid or expired temporary key.");
        }
    }
}
