using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Verda;

//https://docs.verda.com/inference/image-models
public sealed class VerdaImageProviderMetadata
{
    [JsonPropertyName("flux_1")]
    public JsonElement? Flux1 { get; set; }

    [JsonPropertyName("flux_2")]
    public JsonElement? Flux2 { get; set; }

    [JsonPropertyName("flux_2_klein")]
    public JsonElement? Flux2Klein { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

}
