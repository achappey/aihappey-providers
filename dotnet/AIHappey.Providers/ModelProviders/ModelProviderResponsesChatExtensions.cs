using AIHappey.Common.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Messages.Mapping;
using AIHappey.ChatCompletions.Models;
using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.ChatCompletions.Mapping;

namespace AIHappey.Core.AI;

public static class ModelProviderResponsesChatExtensions
{

    public static void ApplyProviderOptions(
    this IModelProvider provider,
    Dictionary<string, object?>? metadata,
    IDictionary<string, JsonElement>? additional, HashSet<string>? exclude = null)
    {
        if (metadata is null || additional is null)
            return;

        if (!metadata.TryGetValue(provider.GetIdentifier(), out var obj))
            return;

        if (obj is not JsonElement json)
            return;

        foreach (var prop in json.EnumerateObject())
        {
            if (exclude?.Contains(prop.Name) == true)
                continue;

            additional[prop.Name] = prop.Value;
        }
    }




    public static void SetDefaultInteractionProperties(
       this IModelProvider modelProvider, InteractionRequest responseRequest, HashSet<string>? exclude = null)
    {
        responseRequest.Tools = [.. responseRequest.Tools ?? [],
            .. responseRequest.Metadata.GetInteractionToolDefinitions(modelProvider.GetIdentifier())?
            .Where(p => responseRequest.Tools?.Any(e => e.Type == p.Type) != true) ?? []];

        modelProvider.ApplyProviderOptions(responseRequest.Metadata, responseRequest.AdditionalProperties ??=
                [], [.. exclude ?? [], "tools", "headers"]);

        responseRequest.Metadata = null;
    }





}
