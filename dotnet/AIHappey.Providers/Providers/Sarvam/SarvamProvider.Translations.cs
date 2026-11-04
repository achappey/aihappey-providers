using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

[assembly: InternalsVisibleTo("AIHappey.Providers.Tests")]

namespace AIHappey.Core.Providers.Sarvam;

public partial class SarvamProvider
{
    private const string MayuraTranslatePrefix = "mayura:v1/translate-to/";
    private const string SarvamTranslatePrefix = "sarvam-translate:v1/translate/";
    private const int MayuraMaxInputCharacters = 1000;
    private const int SarvamTranslateMaxInputCharacters = 2000;

    internal readonly record struct TranslationChunk(string Text, string SeparatorAfter);

    private sealed class SarvamTranslateResponse
    {
        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("translated_text")]
        public string? TranslatedText { get; set; }

        [JsonPropertyName("source_language_code")]
        public string? SourceLanguageCode { get; set; }
    }

    private static (string Model, string SourceLanguage, string TargetLanguage) ParseTranslationModel(string modelId)
    {
        var normalized = NormalizeModelId(modelId);

        if (normalized.StartsWith(MayuraTranslatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var target = normalized[MayuraTranslatePrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("Mayura translation target language is missing.", nameof(modelId));

            return ("mayura:v1", "auto", target);
        }

        if (normalized.StartsWith(SarvamTranslatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var pair = normalized[SarvamTranslatePrefix.Length..]
                .Split("/to/", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
                throw new ArgumentException("Sarvam translation model must contain source and target languages.", nameof(modelId));

            return ("sarvam-translate:v1", pair[0], pair[1]);
        }

        throw new ArgumentException($"Unsupported Sarvam translation model '{modelId}'.", nameof(modelId));
    }

    internal static int GetTranslationCharacterLimit(string modelId)
        => ParseTranslationModel(modelId).Model == "mayura:v1"
            ? MayuraMaxInputCharacters
            : SarvamTranslateMaxInputCharacters;

    internal static List<TranslationChunk> SplitTranslationText(string text, string modelId)
    {
        ArgumentNullException.ThrowIfNull(text);
        var limit = GetTranslationCharacterLimit(modelId);
        if (text.Length <= limit)
            return [new TranslationChunk(text, string.Empty)];

        var textElementStarts = StringInfo.ParseCombiningCharacters(text);
        var chunks = new List<TranslationChunk>();
        var start = 0;

        while (start < text.Length)
        {
            var maximumEnd = Math.Min(start + limit, text.Length);
            if (maximumEnd == text.Length)
            {
                chunks.Add(new TranslationChunk(text[start..], string.Empty));
                break;
            }

            var end = FindTextElementBoundary(textElementStarts, maximumEnd);
            if (end <= start)
                throw new ArgumentException("A text element exceeds Sarvam's maximum translation input length.", nameof(text));

            var sentenceSearchStart = Math.Max(start, end - limit / 4);
            var splitAt = FindSentenceBoundary(text, textElementStarts, sentenceSearchStart, end);
            if (splitAt < 0)
                splitAt = FindWhitespaceBoundary(text, textElementStarts, start + 1, end);

            if (splitAt < 0)
            {
                chunks.Add(new TranslationChunk(text[start..end], string.Empty));
                start = end;
                continue;
            }

            var separatorEnd = splitAt;
            while (separatorEnd < text.Length && char.IsWhiteSpace(text[separatorEnd]))
                separatorEnd++;

            chunks.Add(new TranslationChunk(text[start..splitAt], text[splitAt..separatorEnd]));
            start = separatorEnd;
        }

        return chunks;
    }

    private static int FindTextElementBoundary(int[] textElementStarts, int maximumEnd)
    {
        var index = Array.BinarySearch(textElementStarts, maximumEnd);
        if (index >= 0)
            return maximumEnd;

        index = ~index - 1;
        return index >= 0 ? textElementStarts[index] : 0;
    }

    private static int FindSentenceBoundary(string text, int[] textElementStarts, int searchStart, int end)
    {
        for (var index = end - 1; index >= searchStart; index--)
        {
            if (text[index] is '.' or '!' or '?' or '\u3002' or '\uff01' or '\uff1f'
                && Array.BinarySearch(textElementStarts, index + 1) >= 0)
                return index + 1;
        }

        return -1;
    }

    private static int FindWhitespaceBoundary(string text, int[] textElementStarts, int searchStart, int end)
    {
        for (var index = end - 1; index >= searchStart; index--)
        {
            if (char.IsWhiteSpace(text[index]) && Array.BinarySearch(textElementStarts, index) >= 0)
                return index;
        }

        return -1;
    }

    private static List<string> ExtractTranslationTexts(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return [request.Input.Text];

        return request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))?
            .Content?
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList() ?? [];
    }

    private async Task<(SarvamTranslateResponse Response, string Text)> TranslateAsync(
        string text,
        string modelId,
        JsonElement metadata,
        CancellationToken cancellationToken)
    {
        var (model, sourceLanguage, targetLanguage) = ParseTranslationModel(modelId);

        var payload = new Dictionary<string, object?>();
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in metadata.EnumerateObject())
            {
                if (option.Value.ValueKind == JsonValueKind.Null
                    && option.Name is "speaker_gender" or "mode" or "output_script" or "numerals_format")
                    continue;

                payload[option.Name] = option.Value.Clone();
            }
        }

        payload["input"] = text;
        payload["source_language_code"] = sourceLanguage;
        payload["target_language_code"] = targetLanguage;
        payload["model"] = model;

        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "translate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sarvam translate failed ({(int)response.StatusCode}): {body}");

        var result = JsonSerializer.Deserialize<SarvamTranslateResponse>(body, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Sarvam returned an invalid translation response.");
        return (result, result.TranslatedText ?? string.Empty);
    }

    internal async Task<AIResponse> ExecuteTranslationUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modelId = request.Model ?? throw new ArgumentException("Model is required.", nameof(request));
        var metadata = request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier());
        var texts = ExtractTranslationTexts(request);
        if (texts.Count == 0)
            throw new ArgumentException("Translation requires text in the latest user message.", nameof(request));

        var translated = new List<string>(texts.Count);
        SarvamTranslateResponse? lastResponse = null;
        foreach (var text in texts)
        {
            var chunks = SplitTranslationText(text, modelId);
            var translatedChunks = new List<string>(chunks.Count);
            foreach (var chunk in chunks)
            {
                var result = await TranslateAsync(chunk.Text, modelId, metadata, cancellationToken);
                lastResponse = result.Response;
                translatedChunks.Add(result.Text);
            }

            translated.Add(string.Concat(chunks.Select((chunk, index) =>
                translatedChunks[index] + chunk.SeparatorAfter)));
        }

        var (_, source, target) = ParseTranslationModel(modelId);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = modelId,
            Status = "completed",
            Usage = new Dictionary<string, object?>(),
            Metadata = new Dictionary<string, object?>
            {
                ["finishReason"] = "stop",
                ["requestId"] = lastResponse?.RequestId,
                ["sourceLanguage"] = lastResponse?.SourceLanguageCode ?? source,
                ["targetLanguage"] = target
            },
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = string.Join("\n", translated) }]
                    }
                ]
            }
        };
    }

    internal async IAsyncEnumerable<AIStreamEvent> StreamTranslationUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteTranslationUnifiedAsync(request, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault() ?? string.Empty;
        var id = Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateTranslationStreamEvent(id, "text-start", new AITextStartEventData(), timestamp, response.Metadata);
        if (!string.IsNullOrEmpty(text))
            yield return CreateTranslationStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);
        yield return CreateTranslationStreamEvent(id, "text-end", new AITextEndEventData(), timestamp, response.Metadata);
        yield return CreateTranslationStreamEvent(id, "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = response.Model,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? string.Empty, timestamp, response.Usage, temperature: request.Temperature)
        }, DateTimeOffset.UtcNow, response.Metadata);
    }

    private AIStreamEvent CreateTranslationStreamEvent(
        string id,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Id = id,
                Type = type,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };
}
