using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.DeepSeek;

public sealed partial class DeepSeekProvider
{
    private void NormalizeResponseOptions(ResponseRequest options)
    {
        if (options.AdditionalProperties is not { } additional)
            return;

        var config = JsonSerializer.SerializeToNode(additional, JsonSerializerOptions.Web)!.AsObject();

        // Translate the provider's root thinking/reasoning controls to Responses.
        if (!config.ContainsKey("reasoning") && options.Reasoning is null)
        {
            var disabled = config["thinking"] is JsonObject thinking
                && thinking["type"]?.GetValue<string>() == "disabled";
            if (disabled || config.ContainsKey("reasoning_effort") || config.ContainsKey("thinking"))
                config["reasoning"] = new JsonObject
                {
                    ["effort"] = disabled ? "none" : config["reasoning_effort"]?.GetValue<string>() ?? "high"
                };
        }

        config.Remove("thinking");
        config.Remove("reasoning_effort");
        if (options.Reasoning is null && config["reasoning"] is { } reasoning)
            options.Reasoning = reasoning.Deserialize<Reasoning>();
        config.Remove("reasoning");
        options.AdditionalProperties = config.Deserialize<Dictionary<string, JsonElement>>();
    }
}
