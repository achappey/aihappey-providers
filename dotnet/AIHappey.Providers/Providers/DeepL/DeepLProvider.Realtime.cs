using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    private const string DeepLRealtimeRelativeUrl = "v3/voice/realtime";
    private const string DefaultSourceMediaContentType = "audio/pcm;encoding=s16le;rate=16000";

    private static readonly JsonSerializerOptions RealtimeJsonOpts = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(realtimeRequest);

        var payload = BuildRealtimePayload(realtimeRequest);

        using var resp = await SendDeepLAsync(HttpMethod.Post, DeepLRealtimeRelativeUrl,
            () => new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json"), null, retry: true, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        var parsed = JsonSerializer.Deserialize<DeepLRealtimeSessionResponse>(body, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("DeepL realtime session response was empty.");

        if (string.IsNullOrWhiteSpace(parsed.Token))
            throw new InvalidOperationException("DeepL realtime session response does not include a token.");

        if (!Uri.TryCreate(parsed.StreamingUrl, UriKind.Absolute, out var streamingUri) || streamingUri.Scheme != "wss"
            || streamingUri.Port != 443 || streamingUri.UserInfo.Length != 0 || streamingUri.Fragment.Length != 0
            || !(streamingUri.Host == "api.deepl.com" || streamingUri.Host.EndsWith(".deepl.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("DeepL realtime session response does not include a streaming URL.");

        var providerMetadata = new DeepLRealtimeProviderMetadata
        {
            StreamingUrl = parsed.StreamingUrl,
            SessionId = parsed.SessionId
        };

        return new RealtimeResponse
        {
            Value = parsed.Token,
            // DeepL supplies no expiry. This is a conservative local use-by hint,
            // not a provider lifetime guarantee. Clients consume it immediately, once.
            ExpiresAt = _timeProvider.GetUtcNow().AddMinutes(1).ToUnixTimeSeconds(),
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetadata, RealtimeJsonOpts)
            }
        };
    }

    private static JsonElement BuildRealtimePayload(RealtimeRequest realtimeRequest)
    {
        var model = realtimeRequest.Model ?? "";
        if (model.StartsWith("deepl/", StringComparison.Ordinal)) model = model[6..];
        const string prefix = "voice-realtime/translate-to/";
        string? target = null;
        if (model.StartsWith(prefix, StringComparison.Ordinal)
            && Regex.IsMatch(model[prefix.Length..], "^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)) target = model[prefix.Length..];
        else if (model != "voice-realtime") throw new ArgumentException("Select a DeepL Voice realtime transcription model.");

        var options = new JsonObject();
        if (realtimeRequest.ProviderOptions is not null
            && realtimeRequest.ProviderOptions.TryGetValue(nameof(DeepL).ToLowerInvariant(), out var providerPayload)
            && providerPayload.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (providerPayload.ValueKind != JsonValueKind.Object) throw new ArgumentException("DeepL realtime options must be an object.");
            options = JsonNode.Parse(providerPayload.GetRawText())!.AsObject();
        }
        // These models are transcription-only. The chosen model, not arbitrary
        // options, owns the output language; both clients capture mono PCM16.
        options["source_media_content_type"] = DefaultSourceMediaContentType;
        options["message_format"] = "json";
        options["target_languages"] = target is null ? new JsonArray() : new JsonArray(target);
        foreach (var field in new[] { "target_media_languages", "target_media_content_type", "target_media_voice" }) options.Remove(field);
        return JsonSerializer.SerializeToElement(options, RealtimeJsonOpts);
    }

    private sealed class DeepLRealtimeSessionResponse
    {
        [JsonPropertyName("streaming_url")]
        public string? StreamingUrl { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }
    }

    private sealed class DeepLRealtimeProviderMetadata
    {
        [JsonPropertyName("streaming_url")]
        public string? StreamingUrl { get; set; }

        [JsonPropertyName("session_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SessionId { get; set; }
    }
}
