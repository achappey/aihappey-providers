using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.NiaVoice;

public sealed partial class NiaVoiceProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = LocalModel(request.Model);
        if (model == "nia-scribe-1")
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken, TranscriptionOptions(request));
        if (model == "speech" || model.StartsWith("speech/", StringComparison.Ordinal))
            return await this.ExecuteUnifiedSpeechAsync(request, cancellationToken, SpeechFormat(request));

        if (request.Tools?.Count > 0 || request.ToolChoice is not null || request.ResponseFormat is not null)
            throw new NotSupportedException("NiaVoice does not document tool calling or structured output.");
        var payload = UnifiedOptions(request);
        var translation = model == "nia-translate-1";
        if (translation)
        {
            var text = request.Input?.Text ?? string.Join("\n", request.Input?.Items?
                .LastOrDefault(item => item.Role == "user")?.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
            if (!payload.ContainsKey("text"))
                payload["text"] = text;
            if (!payload.ContainsKey("target_language"))
                throw new ArgumentException("NiaVoice translation requires target_language in provider options.");
        }
        else
        {
            if (!payload.ContainsKey("messages") && !payload.ContainsKey("message"))
            {
                var messages = new JsonArray();
                if (!string.IsNullOrWhiteSpace(request.Instructions))
                    messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.Instructions });
                foreach (var item in request.Input?.Items ?? [])
                {
                    if (item.Content?.Any(part => part is not AITextContentPart) == true)
                        throw new NotSupportedException("NiaVoice chat accepts text messages only.");
                    messages.Add(new JsonObject
                    {
                        ["role"] = item.Role,
                        ["content"] = string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? [])
                    });
                }
                if (request.Input?.Text is { } text)
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
                if (messages.Count == 0)
                    throw new ArgumentException("NiaVoice chat requires a message.");
                payload["messages"] = messages;
            }
            if (!payload.ContainsKey("temperature") && request.Temperature is { } temperature)
                payload["temperature"] = temperature;
            payload["model"] = model;
            payload["stream"] = false;
        }

        var raw = await SendJson(translation ? "v1/translate" : "v1/chat", payload, cancellationToken);
        var reply = RequiredString(raw, translation ? "translation" : "reply");
        var usage = ReadUsage(raw);
        var metadata = new Dictionary<string, object?> { [ProviderId] = raw, ["finishReason"] = "stop" };
        return new AIResponse
        {
            ProviderId = ProviderId, Model = $"{ProviderId}/{model}", Status = "completed",
            Usage = usage, Metadata = metadata,
            Output = new AIOutput
            {
                Items = [new AIOutputItem
                {
                    Role = "assistant",
                    Content = [new AITextContentPart { Type = "text", Text = reply }]
                }]
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = LocalModel(request.Model);
        if (model == "nia-scribe-1" || model == "speech" || model.StartsWith("speech/", StringComparison.Ordinal))
        {
            var stream = model == "nia-scribe-1"
                ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken, TranscriptionOptions(request))
                : this.StreamUnifiedSpeechAsync(request, cancellationToken, SpeechFormat(request));
            await foreach (var part in stream.WithCancellation(cancellationToken))
                yield return part;
            yield break;
        }

        // Adapt the blocking text result; realtime audio uses Nia's native session handshakes.
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var usage = response.Usage as AIUsage;
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var text = response.Output!.Items![0].Content!.OfType<AITextContentPart>().Single().Text;
        yield return TextEvent(id, "text-start", new AITextStartEventData());
        yield return TextEvent(id, "text-delta", new AITextDeltaEventData { Delta = text });
        yield return TextEvent(id, "text-end", new AITextEndEventData());
        yield return TextEvent(id, "finish", new AIFinishEventData
        {
            FinishReason = "stop", Model = response.Model, CompletedAt = now.ToUnixTimeSeconds(),
            InputTokens = usage?.InputTokens, OutputTokens = usage?.OutputTokens,
            TotalTokens = usage?.TotalTokens,
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, now, response.Usage,
                additionalProperties: response.Metadata)
        });

        AIStreamEvent TextEvent(string eventId, string type, object data) => new()
        {
            ProviderId = ProviderId, Metadata = response.Metadata,
            Event = new AIEventEnvelope
            {
                Id = eventId, Type = type, Data = data, Timestamp = now, Metadata = response.Metadata
            }
        };
    }

    private static JsonObject UnifiedOptions(AIRequest request)
    {
        var native = request.Metadata?.GetValueOrDefault(ProviderId);
        return Options(native is null ? default : JsonSerializer.SerializeToElement(native));
    }

    private static string SpeechFormat(AIRequest request)
        => UnifiedOptions(request)["format"]?.GetValue<string>() ?? "wav";

    private static Dictionary<string, JsonElement> TranscriptionOptions(AIRequest request)
        => UnifiedOptions(request).ToDictionary(option => option.Key,
            option => JsonSerializer.SerializeToElement(option.Value));

    private static AIUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        int? Count(string name) => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;
        var input = Count("input_tokens");
        var output = Count("output_tokens");
        return new AIUsage
        {
            InputTokens = input, OutputTokens = output,
            TotalTokens = input.HasValue && output.HasValue ? input + output : null,
            CachedInputTokens = Count("cached_tokens"),
            AdditionalProperties = new() { [ProviderId] = usage.Clone() }
        };
    }
}
