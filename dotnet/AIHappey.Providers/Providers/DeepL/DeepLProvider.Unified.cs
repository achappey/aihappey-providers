using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (operation, target) = ParseModel(request.Model);
        if (request.Tools?.Count > 0)
            throw new NotSupportedException("DeepL does not support tool calls.");

        var parts = GetInputParts(request);
        if (parts.Count == 0)
            throw new ArgumentException("DeepL requires text or a document in the latest user message.", nameof(request));
        if (parts.Any(part => part is not AITextContentPart and not AIFileContentPart))
            throw new NotSupportedException("DeepL accepts text and inline document files only.");

        var files = parts.OfType<AIFileContentPart>().ToList();
        var texts = parts.OfType<AITextContentPart>().Select(part => part.Text).ToList();
        if (operation != "translate" && files.Count > 0)
            throw new NotSupportedException($"DeepL {operation} accepts plain text only; use a translation model for documents.");

        var options = ReadOptions(request);
        ValidateOptions(options, operation, target, texts.Count > 0, files.Count > 0);
        if (options.TryGetValue("output_format", out var format)
            && (format.ValueKind != JsonValueKind.String || !Regex.IsMatch(format.GetString()!, "^[A-Za-z0-9]+$")))
            throw new ArgumentException("DeepL output_format must be a file extension such as docx.");
        // Preflight every file before incurring any translation charges.
        var documents = files.Select((file, index) => DecodeDocument(file, index, options)).ToList();
        var output = new List<AIContentPart>();
        var translated = texts.Count == 0 ? [] : await ProcessTextsAsync(texts, operation, target, options, request.Headers, cancellationToken);
        var textIndex = 0;
        var fileIndex = 0;
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(part is AITextContentPart ? translated[textIndex++]
                : await TranslateDocumentAsync(documents[fileIndex++], target!, options, request.Headers, cancellationToken));
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = "completed",
            Output = new AIOutput { Items = [new AIOutputItem { Type = "message", Role = "assistant", Content = output }] }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // DeepL has no text SSE endpoint. Emit the completed result through shared stream mappings.
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        foreach (var part in response.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            if (part is AITextContentPart text)
            {
                var metadata = new Dictionary<string, object> { [GetIdentifier()] = text.Metadata ?? [] };
                yield return StreamEvent(id, "text-start", new AITextStartEventData { ProviderMetadata = metadata });
                yield return StreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text, ProviderMetadata = metadata });
                yield return StreamEvent(id, "text-end", new AITextEndEventData { ProviderMetadata = metadata });
            }
            else if (part is AIFileContentPart file)
                yield return StreamEvent(id, "file", new AIFileEventData
                {
                    Filename = file.Filename, MediaType = file.MediaType!, Url = (string)file.Data!,
                    ProviderMetadata = new() { [GetIdentifier()] = new() { ["document"] = file.Metadata ?? [] } }
                });
        }
        yield return StreamEvent(request.Id ?? Guid.NewGuid().ToString("N"), "finish",
            new AIFinishEventData { Model = request.Model, FinishReason = "stop" });
    }

    private AIStreamEvent StreamEvent(string id, string type, object data)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Id = id, Type = type, Timestamp = DateTimeOffset.UtcNow, Data = data }
        };

    private static List<AIContentPart> GetInputParts(AIRequest request)
    {
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (latest is not null)
            return latest.Content?.Where(part => part is not AITextContentPart text || !string.IsNullOrWhiteSpace(text.Text)).ToList() ?? [];
        return string.IsNullOrWhiteSpace(request.Input?.Text) ? [] : [new AITextContentPart { Type = "text", Text = request.Input.Text }];
    }

    private static (string Operation, string? Target) ParseModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        model = model.Trim();
        if (model.StartsWith("deepl/", StringComparison.OrdinalIgnoreCase))
            model = model[6..];
        if (model.Equals("rephrase", StringComparison.OrdinalIgnoreCase)) return ("rephrase", null);
        if (model.Equals("correct", StringComparison.OrdinalIgnoreCase)) return ("correct", null);
        const string prefix = "translate-to/";
        if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(model[prefix.Length..], "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant))
            throw new ArgumentException("DeepL model must be translate-to/{language}, rephrase, or correct.", nameof(model));
        return ("translate", model[prefix.Length..]);
    }

    private static Dictionary<string, JsonElement> ReadOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("deepl", out var raw) != true || raw is null) return [];
        var element = raw is JsonElement json ? json : JsonSerializer.SerializeToElement(raw);
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("DeepL provider options must be a JSON object.");
        return element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private static readonly HashSet<string> TextOnlyOptions = new(StringComparer.Ordinal)
    {
        "context", "show_billed_characters", "split_sentences", "preserve_formatting", "model_type",
        "tag_handling", "tag_handling_version", "outline_detection", "non_splitting_tags", "splitting_tags", "ignore_tags"
    };
    private static readonly HashSet<string> DocumentOnlyOptions = new(StringComparer.Ordinal)
    {
        "filename", "output_format", "input_conversion_options", "enable_watermark", "enable_quality_evaluation"
    };

    private static void ValidateOptions(Dictionary<string, JsonElement> options, string operation, string? target, bool hasText, bool hasFiles)
    {
        foreach (var name in new[] { "text", "file", "document_id", "document_key" })
            if (options.ContainsKey(name)) throw new ArgumentException($"DeepL '{name}' is supplied by the message, not provider options.");
        if (operation == "translate" && options.TryGetValue("target_lang", out var lang)
            && (lang.ValueKind != JsonValueKind.String || !string.Equals(lang.GetString(), target, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("DeepL target_lang conflicts with the translation model.");
        if (operation == "correct" && (options.ContainsKey("writing_style") || options.ContainsKey("tone")))
            throw new ArgumentException("DeepL correct does not accept writing_style or tone; use rephrase instead.");
        if (operation == "translate" && (options.ContainsKey("writing_style") || options.ContainsKey("tone")))
            throw new ArgumentException("DeepL translation does not accept writing_style or tone; use rephrase instead.");
        foreach (var name in options.Keys)
        {
            if (TextOnlyOptions.Contains(name) && (!hasText || operation != "translate"))
                throw new ArgumentException($"DeepL '{name}' requires text translation input.");
            if (DocumentOnlyOptions.Contains(name) && !hasFiles)
                throw new ArgumentException($"DeepL '{name}' requires a document attachment.");
        }
    }

    private async Task<List<AIContentPart>> ProcessTextsAsync(List<string> texts, string operation, string? target,
        Dictionary<string, JsonElement> options, Dictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        var payload = options.Where(pair => !DocumentOnlyOptions.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        payload["text"] = texts;
        if (target is not null) payload["target_lang"] = target;
        using var response = await SendDeepLAsync(HttpMethod.Post, operation == "translate" ? "v2/translate" : $"v2/write/{operation}",
            () => JsonContent(payload), headers, retry: true, cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var key = operation == "translate" ? "translations" : "improvements";
        if (!body.RootElement.TryGetProperty(key, out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != texts.Count)
            throw new InvalidOperationException($"DeepL returned an invalid {key} response; expected one result per input text.");
        return results.EnumerateArray().Select(result =>
        {
            if (!result.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("DeepL returned a result without text.");
            return (AIContentPart)new AITextContentPart
            {
                Type = "text", Text = text.GetString()!,
                Metadata = new() { ["deepl"] = result.Clone(), ["deepl.trace_id"] = TraceId(response) }
            };
        }).ToList();
    }
}
