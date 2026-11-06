using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Allternit;

public sealed partial class AllternitProvider
{
    private static async IAsyncEnumerable<(string Type, JsonElement Data)> ReadEvents(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? type = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 && type is "message.delta" or "message.completed" or "tool.started"
                    or "tool.completed" or "tool.failed" or "error")
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    yield return (type, document.RootElement.Clone());
                }
                data.Clear();
                type = null;
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                type = line[6..].TrimStart(' ');
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart(' '));
            }
        }
        throw new IOException("Allternit SSE stream ended before message.completed.");
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var turn = Prepare(request, true);
        var conversationId = await Conversation(turn, cancellationToken);
        using var http = HttpRequest(HttpMethod.Post, $"conversations/{conversationId}/messages", turn.Body);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var textId = $"allternit-text-{Guid.NewGuid():N}";
        var text = new StringBuilder();
        var started = false;
        var tools = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var (type, raw) in ReadEvents(response, cancellationToken))
        {
            if (type == "error")
                throw new InvalidOperationException($"Allternit stream failed: {raw.GetRawText()}");
            if (type.StartsWith("tool.", StringComparison.Ordinal))
            {
                var callId = RequiredString(raw, "tool_call_id");
                var name = RequiredString(raw, "name");
                var scoped = ScopedMetadata(conversationId, raw);
                if (tools.Add(callId))
                    yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
                    {
                        ToolName = name, ProviderExecuted = true, ProviderMetadata = scoped,
                        Input = raw.TryGetProperty("input", out var input) ? input.Clone() : new { }
                    }, conversationId, raw);
                if (type == "tool.completed")
                    yield return Event("tool-output-available", callId, new AIToolOutputAvailableEventData
                    {
                        ToolName = name, ProviderExecuted = true, ProviderMetadata = scoped,
                        Output = raw.TryGetProperty("output", out var output) ? output.Clone() : raw
                    }, conversationId, raw);
                else if (type == "tool.failed")
                    yield return Event("tool-output-error", callId, new AIToolOutputErrorEventData
                    {
                        ToolCallId = callId, ErrorText = raw.TryGetProperty("error", out var error)
                            ? error.ToString() : "Allternit tool failed.",
                        ProviderExecuted = true, ProviderMetadata = scoped
                    }, conversationId, raw);
                continue;
            }

            var completed = type == "message.completed";
            var delta = completed ? CompletedText(raw, conversationId)
                : String(raw, "delta") ?? throw new JsonException("Allternit message.delta is missing delta.");
            if (completed)
            {
                if (!delta.StartsWith(text.ToString(), StringComparison.Ordinal))
                    throw new IOException("Allternit stored reply does not match the streamed text.");
                delta = delta[text.Length..];
            }
            if (!started)
            {
                started = true;
                yield return Event("text-start", textId, new AITextStartEventData
                { ProviderMetadata = LooseMetadata(conversationId, raw) }, conversationId, raw);
            }
            if (delta.Length > 0)
            {
                text.Append(delta);
                yield return Event("text-delta", textId, new AITextDeltaEventData
                { Delta = delta, ProviderMetadata = LooseMetadata(conversationId, raw) }, conversationId, raw);
            }
            if (!completed) continue;

            yield return Event("text-end", textId, new AITextEndEventData
            { ProviderMetadata = LooseMetadata(conversationId, raw) }, conversationId, raw);
            var usage = Usage(raw);
            var model = $"allternit/{turn.AgentId}";
            yield return Event("finish", RequiredString(raw, "id"), new AIFinishEventData
            {
                Model = model, FinishReason = "stop",
                InputTokens = usage?.InputTokens, OutputTokens = usage?.OutputTokens, TotalTokens = usage?.TotalTokens,
                MessageMetadata = AIFinishMessageMetadata.Create(model, DateTimeOffset.UtcNow, usage,
                    additionalProperties: Metadata(conversationId, raw))
            }, conversationId, raw);
            yield break;
        }
    }
}
