using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Messages;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Keln;

public sealed partial class KelnProvider
{
    private void PrepareChat(ChatCompletionOptions request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Model = NativeModel(request.Model);
        request.Stream = stream;
        request.Headers = SafeHeaders(request.Headers);
        ValidateChatOptions(request.AdditionalProperties);
        if (request.NumberOfChoices is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(request), "Keln chat n must be between 1 and 8.");
        ValidateTier(request.ServiceTier);
    }

    private void PrepareResponses(ResponseRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Model = NativeModel(request.Model);
        request.Stream = stream;
        request.Headers = SafeHeaders(request.Headers);
        ValidateResponsesOptions(JsonSerializer.SerializeToElement(request));
        if (request.ServiceTier == "standard_only") request.ServiceTier = "standard";
        ValidateTier(request.ServiceTier);
    }

    private static void ValidateResponsesOptions(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            if ((property.Name is "store" or "background" && property.Value.ValueKind == JsonValueKind.True)
                || (property.Name == "previous_response_id" && property.Value.ValueKind != JsonValueKind.Null)
                || (property.Name == "input" && property.Value.ValueKind == JsonValueKind.Array
                    && property.Value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out var type) && type.GetString() == "item_reference")))
                throw new NotSupportedException("Keln Responses is stateless: store=true, background=true, previous_response_id and item_reference are unsupported.");
            if (property.Name == "service_tier" && property.Value.ValueKind != JsonValueKind.Null)
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Keln service_tier must be standard or lite.");
                ValidateTier(property.Value.GetString());
            }
        }
    }

    private MessagesRequest PrepareMessages(MessagesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Model = NativeModel(request.Model);
        request.Headers = SafeHeaders(request.Headers);
        if (request.ServiceTier == "standard_only") request.ServiceTier = "standard";
        ValidateTier(request.ServiceTier);
        ValidateTier(request.AdditionalProperties);
        return request;
    }

    private static bool IsSafeTransportHeader(string name)
        => !ProviderCredentialHeaders.IsProviderKey(name)
            && !name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("host", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string>? SafeHeaders(Dictionary<string, string>? headers)
        => headers?.Where(p => IsSafeTransportHeader(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static void ValidateChatOptions(Dictionary<string, JsonElement>? properties)
    {
        ValidateTier(properties);
        if (properties?.TryGetValue("n", out var n) == true
            && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var count) || count is < 1 or > 8))
            throw new ArgumentOutOfRangeException(nameof(properties), "Keln chat n must be between 1 and 8.");
    }

    private static void ValidateTier(Dictionary<string, JsonElement>? properties)
    {
        if (properties?.TryGetValue("service_tier", out var tier) == true)
        {
            if (tier.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Keln service_tier must be standard or lite.");
            ValidateTier(tier.GetString());
        }
    }

    private static void ValidateTier(string? tier)
    {
        if (tier is not (null or "standard" or "lite"))
            throw new ArgumentException("Keln service_tier must be standard or lite.");
    }
}
