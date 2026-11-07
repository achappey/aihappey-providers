using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Novita;

public sealed class NovitaSpeechProviderMetadata
{
    [JsonPropertyName("glm")]
    public JsonElement? Glm { get; set; }

    [JsonPropertyName("txt2speech")]
    public NovitaText2SpeechSpeechProviderMetadata? Text2Speech { get; set; }

    [JsonPropertyName("minimax")]
    public NovitaMiniMaxSpeechProviderMetadata? MiniMax { get; set; }
}
