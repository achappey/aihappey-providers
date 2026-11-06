using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Ampersand;

public sealed partial class AmpersandProvider
{
    private sealed class Turn(string model)
    {
        public string Model { get; } = model;
        public string? SessionId { get; set; }
        public string Status { get; set; } = "incomplete";
        public AIUsage? Usage { get; set; }
        public List<AIContentPart> Content { get; } = [];
        public Dictionary<string, object?> Receipt { get; } = new() { ["model"] = model };
        public Dictionary<string, object?> Metadata => new() { ["ampersand"] = Element(Receipt) };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = CreateTurn(request);
        await foreach (var _ in Run(turn, request, cancellationToken)) { }
        var metadata = turn.Metadata;
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = turn.Model, Status = turn.Status, Metadata = metadata, Usage = turn.Usage,
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
        var turn = CreateTurn(request);
        await foreach (var evt in Run(turn, request, cancellationToken)) yield return evt;
    }

    private static Turn CreateTurn(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Model?.StartsWith("ampersand/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["ampersand/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Ampersand requires a model.");
        if (request.Temperature is not (null or 1) || request.TopP is not null
            || request.MaxToolCalls is not null || request.ToolChoice is not null || request.ParallelToolCalls is not null)
            throw new NotSupportedException("Ampersand generation settings must use native agent provider metadata.");
        return new Turn("ampersand/" + model);
    }

    private async IAsyncEnumerable<AIStreamEvent> Run(Turn turn, AIRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = Key();
        var body = Options(request);
        var receipt = Receipt(request);
        turn.SessionId = TakeString(body, "session_id");
        if (receipt is { } saved)
        {
            if (Required(saved, "model") != turn.Model)
                throw new ArgumentException("Ampersand history belongs to a different model.");
            var previous = Required(saved, "session_id");
            if (turn.SessionId is not null && turn.SessionId != previous)
                throw new ArgumentException("Ampersand session_id conflicts with the history receipt.");
            turn.SessionId = previous;
        }
        var idempotencyKey = TakeString(body, "idempotency_key");
        body.Remove("stream");
        var pollOnly = false;
        if (body.Remove("pollOnly", out var poll))
        {
            if (poll.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("Ampersand pollOnly must be a boolean.");
            pollOnly = poll.GetBoolean();
        }
        if (pollOnly && (turn.SessionId is null || body.Count > 0 || idempotencyKey is not null))
            throw new ArgumentException("Ampersand pollOnly requires an existing session without input, events or update options.");
        string? after = null;
        var emittedText = new HashSet<string>(StringComparer.Ordinal);
        HttpRequestMessage http;
        if (turn.SessionId is null)
        {
            if (idempotencyKey is not null || body.ContainsKey("events"))
                throw new ArgumentException("Ampersand native events and idempotency_key require an existing session.");
            if (request.Input?.Items?.Any(item => item.Role == "assistant") == true)
                throw new ArgumentException("Ampersand assistant history requires a session receipt or session_id.");
            ConfigureAgent(request, turn.Model["ampersand/".Length..], body);
            if (!body.ContainsKey("input"))
            {
                var messages = Messages(request, continuing: false);
                if (messages.Count == 0) throw new ArgumentException("Ampersand requires nonempty input.");
                body["input"] = Element(messages);
            }
            body["stream"] = Element(true);
            http = Http(HttpMethod.Post, "agents/sessions", key, body);
        }
        else
        {
            var path = "agents/sessions/" + Uri.EscapeDataString(turn.SessionId);
            var session = await Send(HttpMethod.Get, path, key, null, ct);
            if (session.TryGetProperty("agent", out var agent) && String(agent, "model") is { } model
                && "ampersand/" + model != turn.Model)
                throw new ArgumentException("Ampersand session belongs to a different model.");
            if (!pollOnly)
            {
                var latest = await Send(HttpMethod.Get, path + "/items?limit=1&order=desc", key, null, ct);
                after = Array(latest, "data").Select(item => Required(item, "id")).FirstOrDefault();
                var events = InputEvents(request, turn.SessionId, session, body);
                if (body.Count > 0) await Send(HttpMethod.Post, path, key, body, ct);
                using var submit = Http(HttpMethod.Post, path + "/events", key, new { events });
                submit.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
                using var accepted = await _client.SendAsync(submit, ct);
                await EnsureSuccess(accepted, ct);
                if (accepted.StatusCode != HttpStatusCode.Accepted)
                    throw new HttpRequestException("Ampersand event submission did not return 202.");
            }
            http = Http(HttpMethod.Get, path + "/events", key);
        }
        using (http)
        {
            http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccess(response, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var events = new List<JsonElement>();
            var ended = false;
            var announced = false;
            await foreach (var frame in SseParser.Create(stream).EnumerateAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(frame.Data)) continue;
                var raw = JsonSerializer.Deserialize<JsonElement>(frame.Data);
                if (raw.ValueKind != JsonValueKind.Object) throw new JsonException("Ampersand event must be an object.");
                var type = String(raw, "type") ?? frame.EventType;
                events.Add(raw);
                turn.Receipt["events"] = events;
                yield return Event("data-ampersand-event", String(raw, "event_id"), new AIDataEventData { Data = raw },
                    new() { ["ampersand"] = raw });
                if (type == "error") throw new InvalidOperationException($"Ampersand stream error: {raw.GetRawText()}");
                if (raw.TryGetProperty("session", out var snapshot))
                {
                    var id = Required(snapshot, "id");
                    if (turn.SessionId is not null && turn.SessionId != id)
                        throw new JsonException("Ampersand event belongs to a different session.");
                    turn.SessionId = id;
                    turn.Receipt["session_id"] = id;
                    turn.Receipt["session"] = snapshot.Clone();
                    if (!announced)
                    {
                        foreach (var evt in ReceiptEvents(turn, preliminary: true)) yield return evt;
                        announced = true;
                    }
                }
                if (type == "agent.session.turn.output_text.done")
                {
                    var id = $"{Required(raw, "session_id")}-{Required(raw, "item_id")}-{raw.GetProperty("content_index").GetInt32()}";
                    var text = String(raw, "text") ?? throw new JsonException("Ampersand output_text.done requires text.");
                    if (emittedText.Add(id))
                        foreach (var evt in TextEvents(id, text, new() { ["ampersand"] = raw })) yield return evt;
                }
                if (type is "agent.session.idle" or "agent.session.failed" or "agent.session.requires_action")
                {
                    ended = true;
                    break;
                }
            }
            if (!ended) throw new EndOfStreamException("Ampersand stream ended before the session became idle, failed or required action.");
        }
        if (turn.SessionId is null) throw new JsonException("Ampersand stream omitted the session ID.");
        var sessionPath = "agents/sessions/" + Uri.EscapeDataString(turn.SessionId);
        var final = await Send(HttpMethod.Get, sessionPath, key, null, ct);
        turn.Receipt["session_id"] = turn.SessionId;
        turn.Receipt["session"] = final;
        var turns = await Send(HttpMethod.Get, sessionPath + "/turns?limit=1&order=desc", key, null, ct);
        turn.Receipt["turns"] = turns;
        var lastTurn = Array(turns, "data").FirstOrDefault();
        var sessionStatus = Required(final, "status");
        var turnStatus = String(lastTurn, "status");
        turn.Status = sessionStatus == "failed" || turnStatus is "failed" or "cancelled" ? "failed"
            : sessionStatus == "idle" && turnStatus == "completed" ? "completed" : "incomplete";
        if (sessionStatus is not ("idle" or "requires_action" or "failed"))
            throw new InvalidOperationException("Ampersand session resumed work before its result could be read.");
        var items = await Page(sessionPath + "/items", key, after, ct);
        turn.Receipt["items"] = items;
        if (final.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
            turn.Usage = Usage(usage);
        var metadata = turn.Metadata;
        foreach (var item in items)
        {
            if (String(item, "type") != "message" || String(item, "role") != "assistant") continue;
            var index = 0;
            foreach (var part in Array(item, "content"))
            {
                var id = $"{turn.SessionId}-{Required(item, "id")}-{index++}";
                if (String(part, "type") != "output_text") continue;
                var text = String(part, "text") ?? throw new JsonException("Ampersand output_text requires text.");
                turn.Content.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
                if (emittedText.Add(id))
                    foreach (var evt in TextEvents(id, text, metadata)) yield return evt;
            }
        }
        foreach (var action in Array(final, "required_actions"))
        {
            if (String(action, "type") != "function_call")
                throw new NotSupportedException($"Unsupported Ampersand required action: {action.GetRawText()}");
            var call = new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = CallId(turn.SessionId, action), ToolName = Required(action, "name"),
                Input = action.GetProperty("arguments").Clone(), State = "input-available", ProviderExecuted = false,
                Metadata = new() { ["ampersand"] = action.Clone() }
            };
            turn.Content.Add(call);
            yield return Event("tool-input-available", call.ToolCallId, new AIToolInputAvailableEventData
            {
                ToolName = call.ToolName, Input = call.Input, ProviderExecuted = false,
                ProviderMetadata = new() { ["ampersand"] = new() { ["raw"] = action.Clone() } }
            }, metadata);
        }
        foreach (var evt in ReceiptEvents(turn, preliminary: false)) yield return evt;
        if (turn.Status == "failed")
            yield return Event("error", turn.SessionId, new AIErrorEventData
            {
                ErrorText = $"Ampersand session or turn failed: {final.GetRawText()}; turn: {lastTurn}"
            }, metadata);
        yield return Event("finish", turn.SessionId, new AIFinishEventData
        {
            Model = turn.Model, FinishReason = turn.Status == "failed" ? "error"
                : sessionStatus == "requires_action" ? "tool-calls" : turn.Status == "completed" ? "stop" : "other",
            InputTokens = turn.Usage?.InputTokens, OutputTokens = turn.Usage?.OutputTokens, TotalTokens = turn.Usage?.TotalTokens,
            Response = final,
            MessageMetadata = AIFinishMessageMetadata.Create(turn.Model, DateTimeOffset.UtcNow, turn.Usage,
                inputTokens: turn.Usage?.InputTokens, outputTokens: turn.Usage?.OutputTokens, totalTokens: turn.Usage?.TotalTokens,
                additionalProperties: turn.Metadata)
        }, turn.Metadata);
    }

    private static AIUsage Usage(JsonElement raw)
    {
        static int? Count(JsonElement value, string name)
        {
            if (!value.TryGetProperty(name, out var count) || count.ValueKind == JsonValueKind.Null) return null;
            return count.TryGetInt32(out var number) && number >= 0 ? number
                : throw new JsonException($"Ampersand {name} must be a nonnegative integer.");
        }

        return new AIUsage
        {
            InputTokens = Count(raw, "input_tokens"), OutputTokens = Count(raw, "output_tokens"), TotalTokens = Count(raw, "total_tokens"),
            AdditionalProperties = new() { ["ampersand"] = raw.Clone() }
        };
    }

    private IEnumerable<AIStreamEvent> TextEvents(string id, string text, Dictionary<string, object?> metadata)
    {
        // Ampersand's done text replaces provisional deltas; keep those deltas in native events only.
        yield return Event("text-start", id, new AITextStartEventData { ProviderMetadata = Flat(metadata) }, metadata);
        yield return Event("text-delta", id, new AITextDeltaEventData { Delta = text, ProviderMetadata = Flat(metadata) }, metadata);
        yield return Event("text-end", id, new AITextEndEventData { ProviderMetadata = Flat(metadata) }, metadata);
    }

    private IEnumerable<AIStreamEvent> ReceiptEvents(Turn turn, bool preliminary)
    {
        var metadata = turn.Metadata;
        var receipt = Element(turn.Receipt);
        var id = "ampersand-session-" + turn.SessionId;
        yield return Event("tool-input-available", id, new AIToolInputAvailableEventData
        {
            ToolName = SessionTool, Input = receipt, ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
        }, metadata);
        var output = new CallToolResult { StructuredContent = receipt };
        if (!preliminary)
            turn.Content.Insert(0, new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = id, ToolName = SessionTool, Input = receipt, Output = output,
                ProviderExecuted = true, State = "output-available",
                Metadata = new(metadata)
                {
                    ["messages.provider.call.metadata"] = new Dictionary<string, object> { ["ampersand"] = new { type = "server_tool_use" } }
                }
            });
        yield return Event("tool-output-available", id, new AIToolOutputAvailableEventData
        {
            ToolName = SessionTool, Output = output, ProviderExecuted = true, Preliminary = preliminary, ProviderMetadata = Scoped(metadata)
        }, metadata);
    }

    private static Dictionary<string, object> Flat(Dictionary<string, object?> metadata) => new() { ["ampersand"] = metadata["ampersand"]! };
    private static Dictionary<string, Dictionary<string, object>> Scoped(Dictionary<string, object?> metadata)
        => new() { ["ampersand"] = ((JsonElement)metadata["ampersand"]!).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone()) };
    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata }
    };
}
