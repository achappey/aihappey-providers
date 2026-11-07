using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Upivia;

public partial class UpiviaProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = CreateTurn(request, cancellationToken);
        await foreach (var _ in Run(turn, cancellationToken)) { }
        var metadata = turn.Metadata;
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = turn.Model,
            Status = turn.Waiting ? "incomplete" : "completed", Metadata = metadata, Usage = turn.Usage,
            Output = new AIOutput
            {
                Metadata = metadata,
                Items = [new AIOutputItem { Role = "assistant", Content = turn.Content, Metadata = metadata }]
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = CreateTurn(request, cancellationToken);
        await foreach (var evt in Run(turn, cancellationToken)) yield return evt;
    }

    private async IAsyncEnumerable<AIStreamEvent> Run(Turn turn,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = turn.Body;
        Dictionary<string, JsonElement>? sessionBody = null;
        if (body.Remove("session", out var sessionOptions))
        {
            if (sessionOptions.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Upivia session creation metadata must be an object.");
            sessionBody = sessionOptions.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        }
        JsonElement session;
        if (body.TryGetValue("sessionId", out var existing))
        {
            if (sessionBody is not null)
                throw new ArgumentException("Upivia session creation options cannot continue an existing session.");
            turn.SessionId = existing.GetString()!;
            session = await SendJson(HttpMethod.Get, $"chat/sessions/{Uri.EscapeDataString(turn.SessionId)}", null, ct);
            if (RequiredString(session, "id") != turn.SessionId)
                throw new InvalidOperationException("Upivia returned a different session identity.");
        }
        else
        {
            sessionBody ??= [];
            if (sessionBody.TryGetValue("agent_id", out var selected)
                && (selected.ValueKind != JsonValueKind.String || selected.GetString() != turn.Agent))
                throw new ArgumentException("Upivia session agent_id must match the selected model.");
            sessionBody["agent_id"] = JsonSerializer.SerializeToElement(turn.Agent);
            session = await SendJson(HttpMethod.Post, "chat/sessions", sessionBody, ct);
            turn.SessionId = RequiredString(session, "id");
            body["sessionId"] = JsonSerializer.SerializeToElement(turn.SessionId);
        }
        if (RequiredString(session, "agent_id") != turn.Agent)
            throw new ArgumentException("Upivia session belongs to a different agent.");
        if (Property(session, "closed_at").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            throw new InvalidOperationException("Upivia session is closed.");
        turn.Receipt["agent_id"] = turn.Agent;
        turn.Receipt["sessionId"] = turn.SessionId;
        turn.Receipt["session"] = session;
        var receiptId = turn.ReceiptId;
        var metadata = turn.Metadata;
        yield return Event("tool-input-available", receiptId, new AIToolInputAvailableEventData
        {
            ToolName = SessionTool, Title = "Upivia session",
            Input = new { agent_id = turn.Agent, sessionId = turn.SessionId },
            ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
        }, metadata);
        yield return Event("tool-output-available", receiptId, new AIToolOutputAvailableEventData
        {
            ToolName = SessionTool, Output = SessionResult(turn), Preliminary = true,
            ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
        }, metadata);

        using var message = CreateRequest(HttpMethod.Post, "chat/turn");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        // A chat turn can execute paid actions; never replay a disconnected POST.
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var textId = $"upivia-text-{Guid.NewGuid():N}";
        var reasoningId = $"upivia-reasoning-{Guid.NewGuid():N}";
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var textStarted = false;
        var reasoningStarted = false;
        var toolInputs = new Dictionary<string, (string Name, JsonElement Input)>(StringComparer.Ordinal);
        await foreach (var frame in SseParser.Create(stream).EnumerateAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(frame.Data)) continue;
            using var document = JsonDocument.Parse(frame.Data);
            var raw = document.RootElement.Clone();
            if (raw.ValueKind != JsonValueKind.Object)
                throw new JsonException("Upivia SSE data must be an object.");
            var type = frame.EventType == "message" ? String(raw, "type") ?? frame.EventType : frame.EventType;
            var nativeEvent = JsonSerializer.SerializeToElement(new { @event = frame.EventType, data = raw }, JsonOptions);
            turn.Events.Add(nativeEvent);
            turn.Receipt["event"] = nativeEvent;
            metadata = turn.Metadata;
            yield return Event("data-upivia-event", turn.SessionId,
                new AIDataEventData { Id = turn.SessionId, Data = nativeEvent, Transient = false }, metadata);
            if (type is "error" or "failed" or "cancelled")
                throw new InvalidOperationException($"Upivia chat failed: {raw.GetRawText()}");
            if (type is not ("tool_done" or "tool_pending"))
                EnsureNoError(raw);
            if (String(raw, "sessionId") is { } eventSession && eventSession != turn.SessionId)
                throw new InvalidOperationException("Upivia SSE session identity does not match the requested session.");

            if (type == "thinking")
            {
                var delta = String(raw, "delta") ?? String(raw, "text") ?? String(raw, "content");
                if (delta is not null)
                {
                    if (!reasoningStarted)
                    {
                        yield return Event("reasoning-start", reasoningId, new AIReasoningStartEventData { ProviderMetadata = Scoped(metadata) }, metadata);
                        reasoningStarted = true;
                    }
                    reasoning.Append(delta);
                    yield return Event("reasoning-delta", reasoningId,
                        new AIReasoningDeltaEventData { Delta = delta, ProviderMetadata = Scoped(metadata) }, metadata);
                }
            }
            else if (type == "message_delta")
            {
                var delta = String(raw, "delta") ?? throw new JsonException("Upivia message_delta requires delta.");
                if (!textStarted)
                {
                    yield return Event("text-start", textId, new AITextStartEventData { ProviderMetadata = Flat(metadata) }, metadata);
                    textStarted = true;
                }
                text.Append(delta);
                yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = delta, ProviderMetadata = Flat(metadata) }, metadata);
            }
            else if (type == "message" && !textStarted && String(raw, "content") is { } content)
            {
                text.Append(content);
                textStarted = true;
                yield return Event("text-start", textId, new AITextStartEventData { ProviderMetadata = Flat(metadata) }, metadata);
                yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = content, ProviderMetadata = Flat(metadata) }, metadata);
            }
            else if (type is "tool_planned" or "tool_executing" or "tool_done" or "tool_pending" or "local_tools_pending")
            {
                if (type is "tool_pending" or "local_tools_pending") turn.Waiting = true;
                // Unknown tool shapes remain available verbatim via data-upivia-event.
                var name = String(raw, "tool_name");
                var callId = String(raw, "tool_call_id") ?? String(raw, "request_id") ?? String(raw, "id");
                if (name is not null && callId is not null && type == "tool_planned")
                {
                    var input = Property(raw, "arguments");
                    if (input.ValueKind == JsonValueKind.Undefined) input = Property(raw, "payload");
                    if (input.ValueKind != JsonValueKind.Undefined)
                    {
                        if (!toolInputs.TryAdd(callId, (name, input.Clone())))
                            throw new InvalidOperationException("Upivia emitted a duplicate tool call identity.");
                        yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
                        {
                            ToolName = name, Input = input.Clone(), ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
                        }, metadata);
                    }
                }
                else if (callId is not null && type == "tool_done" && toolInputs.Remove(callId, out var planned))
                {
                    if (name is not null && name != planned.Name)
                        throw new InvalidOperationException("Upivia tool result does not match the planned tool.");
                    var failed = String(raw, "status") is "failed" or "blocked"
                        || Property(raw, "error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
                    var output = new CallToolResult { Content = [], StructuredContent = raw.Clone(), IsError = failed };
                    turn.Content.Add(new AIToolCallContentPart
                    {
                        Type = "tool-call", ToolName = planned.Name, ToolCallId = callId, ProviderExecuted = true,
                        State = failed ? "output-error" : "output-available", Input = planned.Input, Output = output, Metadata = metadata
                    });
                    if (failed)
                        yield return Event("tool-output-error", callId, new AIToolOutputErrorEventData
                        {
                            ToolCallId = callId, ErrorText = raw.GetRawText(),
                            ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
                        }, metadata);
                    else
                        yield return Event("tool-output-available", callId, new AIToolOutputAvailableEventData
                        {
                            ToolName = planned.Name, Output = output, ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
                        }, metadata);
                }
            }
            else if (type == "usage")
                turn.Usage = Usage(raw) ?? turn.Usage;
            else if (type == "done")
            {
                if (String(raw, "status") is "blocked" or "approval_required"
                    || Property(raw, "approval_id").ValueKind == JsonValueKind.String)
                    turn.Waiting = true;
                turn.Usage = Usage(raw) ?? turn.Usage;
                turn.Receipt["events"] = turn.Events;
                turn.Receipt["done"] = raw;
                metadata = turn.Metadata;
                if (reasoningStarted)
                {
                    turn.Content.Add(new AIReasoningContentPart { Type = "reasoning", Text = reasoning.ToString(), Metadata = metadata });
                    yield return Event("reasoning-end", reasoningId, new AIReasoningEndEventData { ProviderMetadata = Scoped(metadata) }, metadata);
                }
                if (textStarted)
                {
                    turn.Content.Add(new AITextContentPart { Type = "text", Text = text.ToString(), Metadata = metadata });
                    yield return Event("text-end", textId, new AITextEndEventData { ProviderMetadata = Flat(metadata) }, metadata);
                }
                turn.Content.Add(SessionPart(turn));
                yield return Event("tool-output-available", receiptId, new AIToolOutputAvailableEventData
                {
                    ToolName = SessionTool, Output = SessionResult(turn),
                    ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
                }, metadata);
                var now = DateTimeOffset.UtcNow;
                yield return Event("finish", turn.SessionId, new AIFinishEventData
                {
                    FinishReason = turn.Waiting ? "tool-calls" : "stop", Model = turn.Model,
                    CompletedAt = now.ToUnixTimeSeconds(), Response = raw.Clone(),
                    InputTokens = turn.Usage?.InputTokens, OutputTokens = turn.Usage?.OutputTokens, TotalTokens = turn.Usage?.TotalTokens,
                    MessageMetadata = AIFinishMessageMetadata.Create(turn.Model, now, turn.Usage,
                        inputTokens: turn.Usage?.InputTokens, outputTokens: turn.Usage?.OutputTokens,
                        totalTokens: turn.Usage?.TotalTokens, additionalProperties: metadata)
                }, metadata);
                yield break;
            }
        }
        throw new EndOfStreamException("Upivia stream ended before a done event.");
    }
}
