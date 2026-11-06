using AIHappey.Core.AI;
using AIHappey.Interactions;
using System.Text.Json;
using AIHappey.Interactions.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private const decimal GoogleOmniVideoOutputTokenPrice = 12m / 1_000_000m;

    private static Responses.ResponseResult EnrichResponseWithGatewayCost(
        Responses.ResponseResult response,
        string? requestModel = null,
        string? requestServiceTier = null,
        Interaction? interaction = null)
    {
        var effectiveModelId = string.IsNullOrWhiteSpace(response.Model)
            ? interaction?.Model ?? interaction?.Agent ?? requestModel
            : response.Model;

        var effectiveServiceTier = string.IsNullOrWhiteSpace(response.ServiceTier)
            ? interaction?.ServiceTier ?? requestServiceTier
            : response.ServiceTier;

        // Unified responses/finish events already carry the modality-aware cost.
        // Endpoint mapping can flatten usage; do not replace that cost with text pricing.
        if (interaction is null && IsGoogleOmniFlashModel(effectiveModelId)
            && response.Metadata is not null
            && TryGetGatewayMetadata(response.Metadata, out var gateway)
            && gateway.TryGetValue("cost", out var existingCost)
            && JsonSerializer.SerializeToElement(existingCost, JsonSerializerOptions.Web) is
                { ValueKind: JsonValueKind.Number } costElement
            && costElement.TryGetDecimal(out _))
        {
            return response;
        }

        response.Metadata = EnrichGoogleUsageWithGatewayCost(
            response.Usage, response.Metadata, effectiveModelId, effectiveServiceTier, interaction?.Usage);

        return response;
    }

    private static bool IsGoogleOmniFlashModel(string? model)
        => string.Equals(NormalizeGoogleModelOrAgentId(model), "gemini-omni-1.2-flash", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object?> EnrichGoogleUsageWithGatewayCost(
        object? usage,
        Dictionary<string, object?>? metadata,
        string? model,
        string? serviceTier,
        InteractionUsage? interactionUsage = null)
    {
        var isOmni = IsGoogleOmniFlashModel(model);
        var pricing = GoogleTieredPricingResolver.Resolve(
            isOmni ? NormalizeGoogleModelOrAgentId(model) : model,
            serviceTier,
            ModelCostMetadataEnricher.GetTotalTokens(usage));

        var normalizedUsage = NormalizeGoogleUsage(usage, interactionUsage);
        var costMetadata = metadata is null ? new Dictionary<string, object?>() : new(metadata);
        // Preserve gateway fields even when metadata was deserialized as JSON, and
        // avoid mutating the caller's nested dictionary during repeated enrichment.
        if (metadata is not null && TryGetGatewayMetadata(metadata, out var gateway))
            costMetadata["gateway"] = gateway;

        costMetadata = ModelCostMetadataEnricher.AddCostFromUsage(normalizedUsage, costMetadata, pricing);

        if (!isOmni || pricing is null
            || !TryNormalizeUsageFromObject(normalizedUsage, out var counts)
            || !TryGetGoogleOmniVideoOutputTokens(interactionUsage ?? usage, out var videoTokens))
        {
            return costMetadata;
        }

        var inputTokens = (int)counts["input_tokens"]!;
        var outputTokens = (int)counts["output_tokens"]!;
        var cachedTokens = (int)counts["cached_input_tokens"]!;
        if (outputTokens == 0 && (int)counts["total_tokens"]! > 0)
            outputTokens = Math.Max(0, (int)counts["total_tokens"]! - inputTokens - cachedTokens);

        // Invalid or contradictory modality counts must not create negative text
        // charges. Without usable reported counts, retain catalog-based pricing.
        if (inputTokens < 0 || videoTokens > outputTokens)
            return costMetadata;

        // Total output already includes video tokens: replace their text-rate
        // charge, rather than charging for the same output twice. This also bills
        // reported video-only usage when the input count is explicitly zero.
        var cost = ModelCostMetadataEnricher.ComputeCost(pricing, inputTokens, outputTokens, cachedTokens, 0)
            + videoTokens * (GoogleOmniVideoOutputTokenPrice - pricing.Output);
        return ModelCostMetadataEnricher.AddCost(costMetadata, cost);
    }

    private static bool TryGetGoogleOmniVideoOutputTokens(object? usage, out int videoTokens)
    {
        videoTokens = 0;
        if (usage is null)
            return false;

        try
        {
            var json = usage is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
            if (!TryGetProperty(json, "output_tokens_by_modality", out var modalities)
                || modalities.ValueKind != JsonValueKind.Array)
                return false;

            long total = 0;
            foreach (var modality in modalities.EnumerateArray())
            {
                if (!string.Equals(TryGetString(modality, "modality"), "video", StringComparison.OrdinalIgnoreCase))
                    continue;

                var tokens = TryGetUsageInt(modality, "tokens");
                if (!tokens.HasValue || tokens.Value < 0)
                    return false;

                total += tokens.Value;
                if (total > int.MaxValue)
                    return false;
            }

            videoTokens = (int)total;
            return videoTokens > 0;
        }
        catch
        {
            return false;
        }
    }

    private static AIResponse EnrichGoogleOmniUnifiedResponseWithGatewayCost(
        AIResponse response, Interaction interaction, string? requestModel, string? requestServiceTier)
    {
        var model = interaction.Model ?? requestModel;
        if (!IsGoogleOmniFlashModel(model))
            return response;

        return new AIResponse
        {
            ProviderId = response.ProviderId,
            Model = response.Model,
            Status = response.Status,
            Output = response.Output,
            Usage = response.Usage,
            Metadata = EnrichGoogleUsageWithGatewayCost(
                interaction.Usage, response.Metadata, model, interaction.ServiceTier ?? requestServiceTier)
        };
    }

    private static Dictionary<string, JsonElement>? AddGoogleOmniChatGatewayMetadata(
        Dictionary<string, JsonElement>? additionalProperties,
        Dictionary<string, object?>? metadata,
        string? model)
    {
        if (!IsGoogleOmniFlashModel(model) || metadata is null
            || !TryGetGatewayMetadata(metadata, out var gateway))
            return additionalProperties;

        var additional = additionalProperties is null
            ? new Dictionary<string, JsonElement>() : new(additionalProperties);
        var chatMetadata = additional.TryGetValue("metadata", out var existingMetadata)
            && existingMetadata.ValueKind == JsonValueKind.Object
                ? existingMetadata.Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web) ?? []
                : new Dictionary<string, object?>();
        chatMetadata["gateway"] = gateway;
        additional["metadata"] = JsonSerializer.SerializeToElement(chatMetadata, JsonSerializerOptions.Web);
        return additional;
    }

    private static object? NormalizeGoogleUsage(object? usage, InteractionUsage? interactionUsage)
    {
        if (TryNormalizeUsageFromObject(usage, out var normalizedFromUsage))
            return normalizedFromUsage;

        if (interactionUsage is null)
            return usage;

        var inputTokens = interactionUsage.TotalInputTokens;
        if (!inputTokens.HasValue)
            return usage;

        return new Dictionary<string, object?>
        {
            ["input_tokens"] = inputTokens.Value,
            ["output_tokens"] = interactionUsage.TotalOutputTokens ?? 0,
            ["total_tokens"] = interactionUsage.TotalTokens ?? 0,
            ["cached_input_tokens"] = interactionUsage.TotalCachedTokens ?? 0
        };
    }

    private static bool TryNormalizeUsageFromObject(object? usage, out Dictionary<string, object?> normalized)
    {
        normalized = [];

        if (usage is null)
            return false;

        JsonElement usageElement;

        try
        {
            usageElement = usage switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            return false;
        }

        if (usageElement.ValueKind != JsonValueKind.Object)
            return false;

        var inputTokens = TryGetUsageInt(usageElement, "input_tokens")
            ?? TryGetUsageInt(usageElement, "inputTokens")
            ?? TryGetUsageInt(usageElement, "prompt_tokens")
            ?? TryGetUsageInt(usageElement, "promptTokens")
            ?? TryGetUsageInt(usageElement, "total_input_tokens");

        if (!inputTokens.HasValue)
            return false;

        var outputTokens = TryGetUsageInt(usageElement, "output_tokens")
            ?? TryGetUsageInt(usageElement, "outputTokens")
            ?? TryGetUsageInt(usageElement, "completion_tokens")
            ?? TryGetUsageInt(usageElement, "completionTokens")
            ?? TryGetUsageInt(usageElement, "total_output_tokens")
            ?? 0;

        var totalTokens = TryGetUsageInt(usageElement, "total_tokens")
            ?? TryGetUsageInt(usageElement, "totalTokens")
            ?? 0;

        var cachedInputTokens = TryGetUsageInt(usageElement, "cached_input_tokens")
            ?? TryGetUsageInt(usageElement, "cachedInputTokens")
            ?? TryGetUsageInt(usageElement, "total_cached_tokens")
            ?? 0;

        normalized["input_tokens"] = inputTokens.Value;
        normalized["output_tokens"] = outputTokens;
        normalized["total_tokens"] = totalTokens;
        normalized["cached_input_tokens"] = cachedInputTokens;
        return true;
    }

    private static int? TryGetUsageInt(JsonElement usage, string key)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in usage.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var intValue))
                return intValue;

            if (property.Value.ValueKind == JsonValueKind.String
                && int.TryParse(property.Value.GetString(), out var parsed))
            {
                return parsed;
            }

            return null;
        }

        return null;
    }

    private AIStreamEvent EnrichUnifiedFinishEventWithGatewayCost(AIStreamEvent streamEvent)
    {
        if (!string.Equals(streamEvent.Event.Type, "finish", StringComparison.OrdinalIgnoreCase)
            || streamEvent.Event.Data is not AIFinishEventData finishData
            || finishData.Response is null)
        {
            return streamEvent;
        }

        Interaction? interaction;

        try
        {
            interaction = finishData.Response switch
            {
                Interaction typed => typed,
                JsonElement json when json.ValueKind == JsonValueKind.Object
                    => JsonSerializer.Deserialize<Interaction>(json.GetRawText(), JsonSerializerOptions.Web),
                _ => JsonSerializer.SerializeToElement(finishData.Response, JsonSerializerOptions.Web)
                    .Deserialize<Interaction>(JsonSerializerOptions.Web)
            };
        }
        catch
        {
            return streamEvent;
        }

        if (interaction is null)
            return streamEvent;

        var response = EnrichResponseWithGatewayCost(
            interaction.ToUnifiedResponse(GetIdentifier()).ToResponseResult(),
            requestModel: interaction.Model ?? interaction.Agent,
            requestServiceTier: interaction.ServiceTier,
            interaction: interaction);

        if (response.Metadata is null
            || !TryGetGatewayMetadata(response.Metadata, out var gateway))
        {
            return streamEvent;
        }

        var finishMetadata = finishData.MessageMetadata?.ToDictionary() ?? [];
        finishMetadata["gateway"] = gateway;

        var enrichedFinishData = new AIFinishEventData
        {
            FinishReason = finishData.FinishReason,
            MessageMetadata = AIFinishMessageMetadata.FromDictionary(
                finishMetadata,
                fallbackModel: NormalizeGoogleProviderModelId(finishData.Model ?? interaction.Model ?? interaction.Agent),
                fallbackTimestamp: DateTimeOffset.UtcNow),
            Model = NormalizeGoogleProviderModelId(finishData.Model ?? interaction.Model ?? interaction.Agent),
            CompletedAt = finishData.CompletedAt,
            InputTokens = finishData.InputTokens,
            OutputTokens = finishData.OutputTokens,
            TotalTokens = finishData.TotalTokens,
            SequenceNumber = finishData.SequenceNumber,
            Response = finishData.Response,
            StopSequence = finishData.StopSequence
        };

        return new AIStreamEvent
        {
            ProviderId = streamEvent.ProviderId,
            Metadata = streamEvent.Metadata,
            Event = new AIEventEnvelope
            {
                Type = streamEvent.Event.Type,
                Id = streamEvent.Event.Id,
                Timestamp = streamEvent.Event.Timestamp,
                Input = streamEvent.Event.Input,
                Output = streamEvent.Event.Output,
                Data = enrichedFinishData,
                Metadata = streamEvent.Event.Metadata
            }
        };
    }

    private static bool TryGetGatewayMetadata(Dictionary<string, object?> metadata, out Dictionary<string, object?> gateway)
    {
        gateway = [];

        if (!metadata.TryGetValue("gateway", out var gatewayObj) || gatewayObj is null)
            return false;

        if (gatewayObj is Dictionary<string, object?> dict)
        {
            gateway = new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase);
            return true;
        }

        try
        {
            gateway = JsonSerializer.SerializeToElement(gatewayObj, JsonSerializerOptions.Web)
                .Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web)
                ?? [];

            return gateway.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeGoogleProviderModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return model;

        const string providerPrefix = "google/";
        return model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? model
            : model.ToModelId(providerPrefix.TrimEnd('/'));
    }
}
