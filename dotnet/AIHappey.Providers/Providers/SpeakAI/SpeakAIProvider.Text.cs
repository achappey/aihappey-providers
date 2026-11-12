using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.SpeakAI;

public sealed partial class SpeakAIProvider
{
    private static bool IsTranscriptionModel(string model)
        => model is "transcription" or "live-transcription";

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = LocalModel(request.Model);
        if (IsTranscriptionModel(model))
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken, TranscriptionOptions(request));

        if (request.Tools?.Count > 0 || request.ToolChoice is not null || request.ResponseFormat is not null)
            throw new NotSupportedException("SpeakAI does not document tool calling or structured output.");

        var payload = UnifiedOptions(request);
        if (!payload.ContainsKey("prompt"))
        {
            var texts = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Instructions))
                texts.Add(request.Instructions);

            var lastUser = request.Input?.Items?.LastOrDefault(item => item.Role == "user");
            if (lastUser?.Content?.Any(part => part is not AITextContentPart) == true)
                throw new NotSupportedException("SpeakAI chat accepts text messages only.");
            if (lastUser is not null)
                texts.Add(string.Join("\n", lastUser.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []));
            if (request.Input?.Text is { } inputText)
                texts.Add(inputText);

            var prompt = string.Join("\n\n", texts.Where(text => !string.IsNullOrWhiteSpace(text)));
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("SpeakAI chat requires a message.");
            payload["prompt"] = prompt;
        }

        var raw = await Send(HttpMethod.Post, "prompt", payload, cancellationToken);
        var data = Unwrap(raw);
        var answer = GetString(data, "answer", "response", "message")
            ?? throw new JsonException("SpeakAI prompt response did not contain an answer.");

        return new AIResponse
        {
            ProviderId = ProviderId, Model = $"{ProviderId}/{model}", Status = "completed",
            Metadata = new Dictionary<string, object?> { [ProviderId] = raw, ["finishReason"] = "stop" },
            Output = new AIOutput
            {
                Items = [new AIOutputItem
                {
                    Role = "assistant",
                    Content = [new AITextContentPart { Type = "text", Text = answer }]
                }]
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsTranscriptionModel(LocalModel(request.Model)))
        {
            await foreach (var part in this.StreamUnifiedTranscriptionAsync(request, cancellationToken,
                TranscriptionOptions(request)).WithCancellation(cancellationToken))
                yield return part;
            yield break;
        }

        // The prompt endpoint is not streamed; adapt the completed answer.
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var text = response.Output!.Items![0].Content!.OfType<AITextContentPart>().Single().Text;
        yield return TextEvent(id, "text-start", new AITextStartEventData());
        yield return TextEvent(id, "text-delta", new AITextDeltaEventData { Delta = text });
        yield return TextEvent(id, "text-end", new AITextEndEventData());
        yield return TextEvent(id, "finish", new AIFinishEventData
        {
            FinishReason = "stop", Model = response.Model, CompletedAt = now.ToUnixTimeSeconds(),
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

    private static Dictionary<string, JsonElement> TranscriptionOptions(AIRequest request)
        => UnifiedOptions(request).ToDictionary(option => option.Key,
            option => JsonSerializer.SerializeToElement(option.Value));
}
