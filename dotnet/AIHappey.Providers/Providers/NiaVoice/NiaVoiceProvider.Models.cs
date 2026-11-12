using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.NiaVoice;

public sealed partial class NiaVoiceProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var models = (await this.ListModels(_keys.Resolve(ProviderId))).ToList();
        if (string.IsNullOrWhiteSpace(_keys.Resolve(ProviderId)))
            return models;

        using var request = CreateRequest(HttpMethod.Get, "v1/voices");
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        foreach (var language in document.RootElement.GetProperty("languages").EnumerateArray())
        {
            var code = RequiredString(language, "code");
            var label = RequiredString(language, "label");
            foreach (var voice in language.GetProperty("voices").EnumerateArray())
            {
                var id = RequiredString(voice, "id");
                models.Add(new Model
                {
                    Id = $"{ProviderId}/speech/{code}/{id}",
                    Name = $"{label} - {RequiredString(voice, "label")}",
                    OwnedBy = "NiaVoice", Type = "speech"
                });
            }
        }
        return models;
    }
}
