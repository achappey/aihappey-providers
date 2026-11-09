using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.JouleCloud;

public sealed partial class JouleCloudProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var models = new List<Model>();
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var type = ReadString(item, "type") ?? ReadString(item, "task");
            models.Add(new Model
            {
                Id = $"{GetIdentifier()}/{LocalModel(id)}",
                Name = ReadString(item, "display_name") ?? id,
                OwnedBy = ReadString(item, "owned_by") ?? "JouleCloud",
                Description = ReadString(item, "description") ?? string.Empty,
                Type = type?.ToLowerInvariant() switch
                {
                    "image" or "image-generation" or "text-to-image" => "image",
                    "speech" or "tts" or "text-to-speech" => "speech",
                    "transcription" or "asr" or "speech-to-text" => "transcription",
                    "embedding" or "embeddings" => "embedding",
                    "language" or "chat" or "text-generation" => "language",
                    _ => id.GuessModelType()
                },
                ContextWindow = item.TryGetProperty("context_length", out var context)
                    && context.ValueKind == JsonValueKind.Number && context.TryGetInt32(out var length)
                    ? length : null
            });
        }

        if (!models.Any(m => m.Id == "joulecloud/auto"))
            models.Add(new Model { Id = "joulecloud/auto", Name = "auto", Type = "language", OwnedBy = "JouleCloud" });
        return models.DistinctBy(m => m.Id).ToArray();
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
