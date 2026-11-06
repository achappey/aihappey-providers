using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AgentCommons;

public partial class AgentCommonsProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = BuildPayload(request);
        var raw = await SendJson(HttpMethod.Post, "v1/agents/run", body, cancellationToken);
        var data = Data(raw);
        EnsureNoError(data);
        var agent = body["agentId"].GetString()!;
        var session = RequiredString(data, "sessionId");
        var metadata = Metadata(agent, session, raw);
        var text = String(data, "content") ?? throw new JsonException("AgentCommons run response requires content.");
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"agentcommons/{agent}", Status = "completed",
            Metadata = metadata, Usage = Usage(data),
            Output = new AIOutput
            {
                Metadata = metadata,
                Items = [new AIOutputItem
                {
                    Role = "assistant", Metadata = metadata,
                    Content = [SessionPart(agent, session, raw), new AITextContentPart { Type = "text", Text = text, Metadata = metadata }]
                }]
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = BuildPayload(request);
        var agent = body["agentId"].GetString()!;
        var session = body.TryGetValue("sessionId", out var existing) ? existing.GetString() : null;
        using var message = CreateRequest(HttpMethod.Post, "v1/agents/run/stream");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var textId = $"agentcommons-text-{Guid.NewGuid():N}";
        var reasoningId = $"agentcommons-reasoning-{Guid.NewGuid():N}";
        var textStarted = false;
        var reasoningStarted = false;
        var tools = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        await foreach (var frame in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(frame.Data)) continue;
            using var document = JsonDocument.Parse(frame.Data);
            var raw = document.RootElement.Clone();
            var data = Data(raw);
            if (data.ValueKind != JsonValueKind.Object)
                throw new JsonException("AgentCommons stream event must be an object.");
            var type = String(data, "type") ?? frame.EventType;
            session = String(data, "sessionId") ?? session;
            var metadata = Metadata(agent, session, raw);
            var id = String(data, "runId");
            // Keep every native event, including future event types and sequence numbers.
            yield return Event("data-agentcommons-event", id,
                new AIDataEventData { Id = id, Data = raw.Clone(), Transient = false }, metadata);
            EnsureNoError(raw);
            EnsureNoError(data);
            if (type is "error" or "failed" or "cancelled")
                throw new InvalidOperationException($"AgentCommons run failed: {raw.GetRawText()}");
            if (type == "token")
            {
                var text = String(data, "content") ?? throw new JsonException("AgentCommons token requires content.");
                if (String(data, "phase") == "commentary")
                {
                    if (!reasoningStarted)
                    {
                        yield return Event("reasoning-start", reasoningId, new AIReasoningStartEventData { ProviderMetadata = Scoped(metadata) }, metadata);
                        reasoningStarted = true;
                    }
                    yield return Event("reasoning-delta", reasoningId,
                        new AIReasoningDeltaEventData { Delta = text, ProviderMetadata = Scoped(metadata) }, metadata);
                }
                else
                {
                    if (!textStarted)
                    {
                        yield return Event("text-start", textId, new AITextStartEventData { ProviderMetadata = Flat(metadata) }, metadata);
                        textStarted = true;
                    }
                    yield return Event("text-delta", textId,
                        new AITextDeltaEventData { Delta = text, ProviderMetadata = Flat(metadata) }, metadata);
                }
            }
            else if (type == "toolStart")
            {
                var name = RequiredString(data, "toolName");
                var callId = String(data, "toolCallId") ?? String(data, "callId") ?? $"agentcommons-tool-{Guid.NewGuid():N}";
                if (!tools.TryGetValue(name, out var queue)) tools[name] = queue = new();
                queue.Enqueue(callId);
                yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
                {
                    ToolName = name, Input = Property(data, "args") is { ValueKind: not JsonValueKind.Undefined } args ? args.Clone() : new { },
                    ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
                }, metadata);
            }
            else if (type == "toolEnd")
            {
                var name = RequiredString(data, "toolName");
                var explicitId = String(data, "toolCallId") ?? String(data, "callId");
                var callId = explicitId;
                if (tools.TryGetValue(name, out var queue) && queue.Count > 0)
                {
                    if (explicitId is null) callId = queue.Dequeue();
                    else tools[name] = new Queue<string>(queue.Where(value => value != explicitId));
                }
                callId ??= $"agentcommons-tool-{Guid.NewGuid():N}";
                yield return Event("tool-output-available", callId, new AIToolOutputAvailableEventData
                {
                    ToolName = name, ProviderExecuted = true, Dynamic = true,
                    Output = new CallToolResult { Content = [], StructuredContent = raw.Clone() },
                    ProviderMetadata = Scoped(metadata)
                }, metadata);
            }
            else if (type == "cli_tool_request")
            {
                throw new NotSupportedException(
                    $"AgentCommons requires caller-executed CLI tools. Submit the native tool result to /v1/agents/cli-tool-result: {raw.GetRawText()}");
            }
            else if (type == "final")
            {
                session = RequiredString(data, "sessionId");
                var text = String(data, "content") ?? throw new JsonException("AgentCommons final event requires content.");
                metadata = Metadata(agent, session, raw);
                if (reasoningStarted)
                    yield return Event("reasoning-end", reasoningId, new AIReasoningEndEventData { ProviderMetadata = Scoped(metadata) }, metadata);
                if (!textStarted && text.Length > 0)
                {
                    yield return Event("text-start", textId, new AITextStartEventData { ProviderMetadata = Flat(metadata) }, metadata);
                    yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = text, ProviderMetadata = Flat(metadata) }, metadata);
                    textStarted = true;
                }
                if (textStarted)
                    yield return Event("text-end", textId, new AITextEndEventData { ProviderMetadata = Flat(metadata) }, metadata);
                var receiptId = $"agentcommons-session-{id ?? Guid.NewGuid().ToString("N")}";
                yield return Event("tool-input-available", receiptId, new AIToolInputAvailableEventData
                {
                    ToolName = SessionTool, Title = "AgentCommons session", Input = new { agentId = agent, sessionId = session },
                    ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
                }, metadata);
                yield return Event("tool-output-available", receiptId, new AIToolOutputAvailableEventData
                {
                    ToolName = SessionTool, Output = SessionResult(agent, session, raw),
                    ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
                }, metadata);
                var usage = Usage(data);
                var now = DateTimeOffset.UtcNow;
                yield return Event("finish", id ?? session, new AIFinishEventData
                {
                    FinishReason = "stop", Model = $"agentcommons/{agent}", CompletedAt = now.ToUnixTimeSeconds(),
                    Response = raw.Clone(), InputTokens = usage?.InputTokens, OutputTokens = usage?.OutputTokens,
                    TotalTokens = usage?.TotalTokens,
                    MessageMetadata = AIFinishMessageMetadata.Create($"agentcommons/{agent}", now, usage,
                        inputTokens: usage?.InputTokens, outputTokens: usage?.OutputTokens,
                        totalTokens: usage?.TotalTokens, additionalProperties: metadata)
                }, metadata);
                yield break;
            }
        }
        throw new EndOfStreamException("AgentCommons stream ended before a final event.");
    }
}
