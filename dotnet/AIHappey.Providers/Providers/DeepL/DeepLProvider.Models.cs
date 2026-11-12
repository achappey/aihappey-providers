using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken)
    {

        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var textLanguages = await GetLanguagesAsync("translate_text", ct);
                var documentLanguages = await GetLanguagesAsync("translate_document", ct);
                var writeLanguages = await GetLanguagesAsync("write", ct);
                var voiceLanguages = await GetLanguagesAsync("voice", ct);
                var models = textLanguages.Concat(documentLanguages)
                    .Where(IsTargetLanguage)
                    .GroupBy(language => language.GetProperty("lang").GetString()!, StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        // V3 uses BCP 47 casing; retain the established uppercase translation slugs.
                        var code = group.Key.ToUpperInvariant();
                        var language = group.First();
                        var resources = new List<string>();
                        if (textLanguages.Any(l => IsTargetLanguage(l) && SameLanguage(l, group.Key))) resources.Add("translate_text");
                        if (documentLanguages.Any(l => IsTargetLanguage(l) && SameLanguage(l, group.Key))) resources.Add("translate_document");
                        return new Model
                        {
                            OwnedBy = nameof(DeepL), Type = "language",
                            Tags = ["translate", code.NormalizeLanguageCode(), .. resources,
                                .. LanguageFeatureTags("translate_text", textLanguages.Where(l => SameLanguage(l, group.Key))),
                                .. LanguageFeatureTags("translate_document", documentLanguages.Where(l => SameLanguage(l, group.Key)))],
                            Id = $"translate-to/{code}".ToModelId(GetIdentifier()),
                            Name = $"DeepL Translate to {language.GetProperty("name").GetString()}",
                            Description = $"Translate to {language.GetProperty("name").GetString()} ({code}). Supported resources: {string.Join(", ", resources)}."
                        };
                    }).ToList();

                foreach (var operation in new[] { "rephrase", "correct" })
                    models.Add(new Model
                    {
                        OwnedBy = nameof(DeepL), Type = "language", Id = operation.ToModelId(GetIdentifier()),
                        Name = operation == "rephrase" ? "Improve text" : "Correct text",
                        Tags = [operation, "write", .. LanguageFeatureTags("write", writeLanguages)],
                        Description = $"{(operation == "rephrase" ? "Improve fluency and readability" : "Fix spelling and grammar with minimal changes")}. Supported target languages: {string.Join(", ", writeLanguages.Where(IsTargetLanguage).Select(l => l.GetProperty("lang").GetString()))}."
                    });

                models.Add(new Model
                {
                    OwnedBy = nameof(DeepL), Type = "transcription", Id = "voice-realtime".ToModelId(GetIdentifier()),
                    Name = "DeepL Voice · Transcription", Tags = ["real-time"],
                    Description = "Live source-language transcription without translation or synthesized audio."
                });
                foreach (var language in voiceLanguages.Where(IsTargetLanguage)
                    .DistinctBy(l => l.GetProperty("lang").GetString()!, StringComparer.OrdinalIgnoreCase))
                {
                    var code = language.GetProperty("lang").GetString()!;
                    models.Add(new Model
                    {
                        OwnedBy = nameof(DeepL), Type = "transcription",
                        Id = $"voice-realtime/translate-to/{code}".ToModelId(GetIdentifier()),
                        Name = $"DeepL Voice · Translate to {language.GetProperty("name").GetString()}",
                        Tags = ["real-time", "translate", code.NormalizeLanguageCode(), .. LanguageFeatureTags("voice", [language])],
                        Description = $"Live speech-to-text translation to {code}. Saves the translation as the transcript; no synthesized audio."
                    });
                }
                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<List<JsonElement>> GetLanguagesAsync(string resource, CancellationToken cancellationToken)
    {
        using var response = await SendDeepLAsync(HttpMethod.Get, $"v3/languages?resource={resource}", null, null, retry: true, cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (body.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("DeepL returned an invalid V3 languages response.");
        return body.RootElement.EnumerateArray().Select(language => language.Clone()).ToList();
    }

    private static bool IsTargetLanguage(JsonElement language)
        => language.TryGetProperty("usable_as_target", out var target) && target.ValueKind == JsonValueKind.True
            && language.TryGetProperty("lang", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString());

    private static bool SameLanguage(JsonElement language, string code)
        => language.TryGetProperty("lang", out var value) && string.Equals(value.GetString(), code, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> LanguageFeatureTags(string resource, IEnumerable<JsonElement> languages)
    {
        foreach (var language in languages)
        {
            var code = language.GetProperty("lang").GetString();
            if (language.TryGetProperty("status", out var status)) yield return $"deepl:{resource}:{code}:{status.GetString()}";
            if (language.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object)
                foreach (var feature in features.EnumerateObject())
                    if (feature.Value.TryGetProperty("status", out var featureStatus))
                        yield return $"deepl:{resource}:{code}:{feature.Name}:{featureStatus.GetString()}";
        }
    }
}
