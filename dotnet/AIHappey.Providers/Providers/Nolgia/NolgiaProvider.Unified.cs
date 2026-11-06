using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Nolgia;

public sealed partial class NolgiaProvider
{
    private const string SessionTool = "nolgia_session";

    private sealed class Turn(string key, Dictionary<string, JsonElement> body)
    {
        public string Key { get; } = key;
        public Dictionary<string, JsonElement> Body { get; } = body;
        public string ToolCallId { get; } = "nolgia-session-" + Guid.NewGuid().ToString("N");
        public Dictionary<string, object?> Receipt { get; } = [];
        public List<AIContentPart> Content { get; } = [];
        public string? SessionId { get; set; }
        public string? MessageId { get; set; }
        public string Status { get; set; } = "incomplete";
        public Dictionary<string, object?> Metadata => new() { ["nolgia"] = JsonSerializer.SerializeToElement(Receipt) };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = CreateTurn(request, cancellationToken);
        await foreach (var _ in Run(turn, request, cancellationToken)) { }
        var metadata = turn.Metadata;
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = "nolgia/agent", Status = turn.Status, Metadata = metadata,
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
        await foreach (var evt in Run(turn, request, cancellationToken)) yield return evt;
    }

    private Turn CreateTurn(AIRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var model = request.Model?.StartsWith("nolgia/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["nolgia/".Length..] : request.Model;
        if (model != "agent") throw new ArgumentException("Nolgia model must be 'nolgia/agent'.");
        // Vercel supplies temperature=1 by default, including for agent-only requests.
        if (request.Tools?.Count > 0 || !string.IsNullOrWhiteSpace(request.Instructions)
            || request.Input?.Items?.Any(item => item.Role is "system" or "developer") == true
            || request.Temperature is not (null or 1) || request.TopP is not null
            || request.ToolChoice is not null || request.ResponseFormat is not null)
            throw new NotSupportedException("Nolgia uses agent-configured instructions, tools and generation settings.");
        if (request.Input?.Items?.Where(item => item.Role == "user")
            .SelectMany(item => item.Content ?? []).Any(part => part is not AITextContentPart) == true)
            throw new NotSupportedException("Nolgia message input supports text only; use native provider metadata for asset references.");
        var body = new Dictionary<string, JsonElement>();
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var native) == true && native is not null)
        {
            var options = JsonSerializer.SerializeToElement(native, JsonSerializerOptions.Web);
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Nolgia provider metadata must be a JSON object.");
            foreach (var property in options.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        if (body.Remove("body", out var nativeBody))
        {
            if (nativeBody.ValueKind != JsonValueKind.Object) throw new ArgumentException("Nolgia body must be a JSON object.");
            foreach (var property in nativeBody.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Nolgia API key.");
        return new Turn(key, body);
    }

    private async IAsyncEnumerable<AIStreamEvent> Run(Turn turn, AIRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = turn.Body;
        var timeoutMs = TakeInteger(body, "timeoutMs", 600_000, 1, 3_600_000);
        var interval = TakeInteger(body, "pollIntervalMs", 1000, 1, 10_000);
        var maxInterval = TakeInteger(body, "maxPollIntervalMs", 10_000, interval, 10_000);
        turn.SessionId = ReadSessionId(body);
        var receipt = FindReceipt(request);
        var previousId = receipt is { } previous ? String(previous, "session_id") : null;
        if (turn.SessionId is not null && previousId is not null && turn.SessionId != previousId)
            throw new ArgumentException("Conflicting Nolgia session IDs.");
        turn.SessionId ??= previousId;
        if (body.TryGetValue("new_session", out var newSession) && newSession.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Nolgia new_session must be a JSON object.");
        if (turn.SessionId is not null && body.ContainsKey("new_session"))
            throw new ArgumentException("Nolgia new_session cannot continue an existing session.");
        if (turn.SessionId is null && request.Input?.Items?.Any(item => item.Role == "assistant") == true)
            throw new ArgumentException("Nolgia chat history requires a session receipt or nolgia.session_id.");
        if (!body.TryGetValue("content", out var content))
        {
            var text = request.Input?.Items?.LastOrDefault(item => item.Role == "user")?.Content?
                .OfType<AITextContentPart>().Select(part => part.Text);
            content = JsonSerializer.SerializeToElement(text is null ? request.Input?.Text : string.Join("\n", text));
        }
        if (content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()))
            throw new ArgumentException("Nolgia requires a non-empty user message.");
        body["content"] = content;
        if (turn.SessionId is not null) body["session_id"] = JsonSerializer.SerializeToElement(turn.SessionId);
        else if (!body.ContainsKey("new_session")) body["new_session"] = JsonSerializer.SerializeToElement(new { });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        // A local timeout/cancellation does not interrupt the provider's running turn.
        var token = timeout.Token;
        var accepted = await Send(HttpMethod.Post, "/agent/messages", body, turn.Key, token);
        turn.Receipt["submit"] = accepted;
        var returnedSession = String(accepted, "session_id");
        if (turn.SessionId is not null && returnedSession is not null && returnedSession != turn.SessionId)
            throw new JsonException("Nolgia accepted response returned a different session_id.");
        turn.SessionId ??= returnedSession;
        if (string.IsNullOrWhiteSpace(turn.SessionId))
            throw new JsonException("Nolgia accepted response is missing session_id.");
        turn.MessageId = String(accepted, "agent_message_id");
        turn.Receipt["session_id"] = turn.SessionId;
        turn.Receipt["agent_message_id"] = turn.MessageId;
        foreach (var evt in ReceiptEvents(turn, final: false)) yield return evt;

        JsonElement? reply = null;
        if (string.IsNullOrWhiteSpace(turn.MessageId))
        {
            if (!body.TryGetValue("steering", out var steering) || steering.ValueKind != JsonValueKind.True
                || string.IsNullOrWhiteSpace(String(accepted, "user_message_id")))
                throw new JsonException("Nolgia accepted response is missing agent_message_id.");
        }
        else
        {
            while (true)
            {
                reply = await Send(HttpMethod.Get, "/agent/messages/" + Uri.EscapeDataString(turn.MessageId), null, turn.Key, token);
                var status = String(reply.Value, "status");
                if (status is not ("pending" or "complete" or "failed" or "interrupted"))
                    throw new JsonException("Nolgia reply has an unknown or missing status.");
                turn.Receipt["message"] = reply.Value;
                yield return Event("data-nolgia-message", turn.MessageId, reply.Value, turn.Metadata);
                if (status != "pending") break;
                await Task.Delay(interval, token);
                interval = Math.Min(maxInterval, (int)Math.Ceiling(interval * 1.5));
            }
            turn.Receipt["assets"] = await Send(HttpMethod.Get,
                "/agent/sessions/" + Uri.EscapeDataString(turn.SessionId) + "/assets", null, turn.Key, token);
            turn.Status = String(reply.Value, "status") == "complete" ? "completed"
                : String(reply.Value, "status") == "failed" ? "failed" : "incomplete";
        }
        var metadata = turn.Metadata;
        var replyStatus = reply is { } value ? String(value, "status") : null;
        var textOutput = reply is { } message
            ? String(message, replyStatus is "failed" or "interrupted" ? "error" : "content") : null;
        if (replyStatus is "failed" or "interrupted" && string.IsNullOrWhiteSpace(textOutput))
            textOutput = $"Nolgia turn {replyStatus}; inspect provider metadata.";
        if (!string.IsNullOrEmpty(textOutput))
        {
            turn.Content.Add(new AITextContentPart { Type = "text", Text = textOutput, Metadata = metadata });
            var providerMetadata = new Dictionary<string, object> { ["nolgia"] = metadata["nolgia"]! };
            yield return Event("text-start", turn.MessageId, new AITextStartEventData { ProviderMetadata = providerMetadata }, metadata);
            yield return Event("text-delta", turn.MessageId, new AITextDeltaEventData { Delta = textOutput, ProviderMetadata = providerMetadata }, metadata);
            yield return Event("text-end", turn.MessageId, new AITextEndEventData { ProviderMetadata = providerMetadata }, metadata);
        }
        foreach (var evt in ReceiptEvents(turn, final: true)) yield return evt;
        if (replyStatus is "failed" or "interrupted")
            yield return Event("error", turn.MessageId, new AIErrorEventData { ErrorText = textOutput! }, metadata);
        yield return Event("finish", turn.MessageId ?? turn.SessionId, new AIFinishEventData
        {
            Model = "nolgia/agent", FinishReason = replyStatus is "failed" or "interrupted" ? "error"
                : turn.Status == "completed" ? "stop" : "other",
            MessageMetadata = AIFinishMessageMetadata.Create("nolgia/agent", DateTimeOffset.UtcNow, additionalProperties: metadata)
        }, metadata);
    }

    private async Task<JsonElement> Send(HttpMethod method, string path, object? body, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "https://api.nolgia.ai/v1" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Nolgia HTTP {(int)response.StatusCode}: {text}", null, response.StatusCode);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)
            || (path == "/agent/messages" || path.StartsWith("/agent/messages/", StringComparison.Ordinal))
                && document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Nolgia returned an invalid response shape.");
        return document.RootElement.Clone();
    }

    private static string? ReadSessionId(Dictionary<string, JsonElement> body)
    {
        if (!body.TryGetValue("session_id", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException("Nolgia session_id must be a non-empty string.");
        return value.GetString();
    }

    private static int TakeInteger(Dictionary<string, JsonElement> body, string name, int fallback, int min, int max)
    {
        if (!body.Remove(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new ArgumentException($"Nolgia {name} must be an integer between {min} and {max}.");
        return number;
    }

    private static JsonElement? Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : null;
    private static string? String(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

    private static JsonElement? FindReceipt(AIRequest request)
    {
        foreach (var tool in request.Input?.Items?.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>().Reverse() ?? [])
            if (tool.ToolName == SessionTool && tool.ProviderExecuted == true)
            {
                var value = JsonSerializer.SerializeToElement(tool.Output ?? tool.Input, JsonSerializerOptions.Web);
                if (value.ValueKind == JsonValueKind.String) value = JsonSerializer.Deserialize<JsonElement>(value.GetString()!);
                var receipt = Property(value, "structuredContent") ?? value;
                if (!string.IsNullOrWhiteSpace(String(receipt, "session_id"))) return receipt;
            }
        return null;
    }

    private IEnumerable<AIStreamEvent> ReceiptEvents(Turn turn, bool final)
    {
        var metadata = turn.Metadata;
        var raw = (JsonElement)metadata["nolgia"]!;
        var output = new CallToolResult { StructuredContent = raw };
        var scoped = new Dictionary<string, Dictionary<string, object>>
        {
            ["nolgia"] = raw.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone())
        };
        if (!final)
            yield return Event("tool-input-available", turn.ToolCallId, new AIToolInputAvailableEventData
            {
                ToolName = SessionTool, Input = raw, ProviderExecuted = true, ProviderMetadata = scoped
            }, metadata);
        yield return Event("tool-output-available", turn.ToolCallId, new AIToolOutputAvailableEventData
        {
            ToolName = SessionTool, Output = output, ProviderExecuted = true, Preliminary = !final, ProviderMetadata = scoped
        }, metadata);
        if (final)
        {
            // Messages replays provider tool inputs, so persist the full receipt there too.
            var toolMetadata = new Dictionary<string, object?>(metadata)
            {
                ["messages.provider.call.metadata"] = new Dictionary<string, object>
                {
                    ["nolgia"] = new { type = "server_tool_use" }
                }
            };
            turn.Content.Insert(0, new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = turn.ToolCallId, ToolName = SessionTool, ProviderExecuted = true,
                State = "output-available", Input = raw, Output = output, Metadata = toolMetadata
            });
        }
    }

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata }
    };
}
