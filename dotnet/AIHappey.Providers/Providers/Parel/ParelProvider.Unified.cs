using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Parel;

public sealed partial class ParelProvider
{
    private const string Api = "https://api.parel.sh/";
    private const string ToolName = "parel_session";

    private string Key() => _keys.Resolve(GetIdentifier()) is { } key && !string.IsNullOrWhiteSpace(key)
        ? key : throw new InvalidOperationException("No PAREL API key.");

    private async Task<JsonElement> Send(HttpMethod method, string path, string key, object? body, CancellationToken ct, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"PAREL HTTP {(int)response.StatusCode}: {text}", null, response.StatusCode);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_keys.Resolve(GetIdentifier()))) return [];
        var agents = await Send(HttpMethod.Get, "agents", Key(), null, cancellationToken);
        if (agents.ValueKind != JsonValueKind.Array) throw new JsonException("PAREL agents must be an array.");
        return agents.EnumerateArray().Select(agent =>
        {
            var name = Required(agent, "name");
            return new Model
            {
                Id = $"parel/{name}", Name = name, Description = String(agent, "description"),
                OwnedBy = "PAREL", Type = "chat", Tags = ["agent"]
            };
        }).ToArray();
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = new Turn();
        await foreach (var _ in Run(request, turn, cancellationToken)) { }
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = turn.Model, Status = turn.Status, Metadata = turn.Metadata,
            Output = new AIOutput
            {
                Metadata = turn.Metadata,
                Items = [new AIOutputItem { Role = "assistant", Content = turn.Content, Metadata = turn.Metadata }]
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in Run(request, new Turn(), cancellationToken)) yield return evt;
    }

    private sealed class Turn
    {
        public string Model { get; set; } = "";
        public string Status { get; set; } = "incomplete";
        public Dictionary<string, object?> Receipt { get; } = [];
        public Dictionary<string, object?> Metadata => new() { ["parel"] = JsonSerializer.SerializeToElement(Receipt) };
        public List<AIContentPart> Content { get; } = [];
    }

    private async IAsyncEnumerable<AIStreamEvent> Run(AIRequest request, Turn turn,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var agent = request.Model?.StartsWith("parel/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["parel/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(agent) || agent.Contains('/'))
            throw new ArgumentException("PAREL requires model parel/{agent name or id}.");
        if (request.Tools?.Count > 0 || !string.IsNullOrWhiteSpace(request.Instructions)
            || request.Input?.Items?.Any(item => item.Role is "system" or "developer") == true
            || request.Temperature is not (null or 1) || request.TopP is not null || request.MaxOutputTokens is not null
            || request.ToolChoice is not null || request.ResponseFormat is not null)
            throw new NotSupportedException("PAREL uses the deployed agent's instructions, tools and model settings.");

        var options = Object(request.Metadata?.GetValueOrDefault("parel"), "provider metadata");
        var sessionId = TakeString(options, "session_id");
        var idempotencyKey = TakeString(options, "idempotency_key");
        var sessionBody = TakeObject(options, "session");
        var messageBody = TakeObject(options, "message");
        foreach (var (name, value) in options) messageBody[name] = value;
        if (messageBody.TryGetValue("idempotencyKey", out var bodyKey))
        {
            if (bodyKey.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(bodyKey.GetString()))
                throw new ArgumentException("PAREL idempotencyKey must be a non-empty string.");
            if (idempotencyKey is not null && idempotencyKey != bodyKey.GetString())
                throw new ArgumentException("PAREL idempotency keys in metadata and message must match.");
            idempotencyKey ??= bodyKey.GetString();
        }
        idempotencyKey ??= Guid.NewGuid().ToString("N");
        var previous = FindReceipt(request);
        if (sessionId is null && previous is not null)
        {
            if (String(previous.Value, "agent") != agent)
                throw new ArgumentException("PAREL history belongs to a different agent.");
            sessionId = String(previous.Value, "session_id");
        }
        if (sessionId is not null && sessionBody.Count > 0)
            throw new ArgumentException("PAREL session creation options cannot be used with an existing session.");
        if (sessionId is null && (request.Input?.Items?.Any(item => item.Role == "assistant") == true
            || request.Input?.Items?.Count(item => item.Role == "user") > 1))
            throw new ArgumentException("PAREL history requires a session receipt or session_id.");

        var user = request.Input?.Items?.LastOrDefault(item => item.Role == "user");
        if (user?.Content?.Any(part => part is not AITextContentPart) == true)
            throw new NotSupportedException("Use PAREL message.content provider metadata for native media parts.");
        var prompt = user?.Content is null ? request.Input?.Text : string.Join("\n", user.Content.OfType<AITextContentPart>().Select(p => p.Text));
        if (!messageBody.ContainsKey("content") && !string.IsNullOrWhiteSpace(prompt))
            messageBody["content"] = JsonSerializer.SerializeToElement(prompt);
        if (!messageBody.ContainsKey("content")) throw new ArgumentException("PAREL requires message content.");
        if (messageBody.TryGetValue("injectInFlight", out var inject) && inject.ValueKind == JsonValueKind.True)
            throw new NotSupportedException("PAREL injectInFlight does not create a separately identifiable turn.");

        var key = Key();
        turn.Model = $"parel/{agent}";
        turn.Receipt["agent"] = agent;
        if (sessionId is null)
        {
            sessionBody["agent"] = JsonSerializer.SerializeToElement(agent);
            var created = await Send(HttpMethod.Post, "sessions", key, sessionBody, ct);
            sessionId = Required(created, "id");
            turn.Receipt["session"] = created;
        }
        else
        {
            var session = await Send(HttpMethod.Get, $"sessions/{Uri.EscapeDataString(sessionId)}", key, null, ct);
            var agentInfo = await Send(HttpMethod.Get, $"agents/{Uri.EscapeDataString(agent)}", key, null, ct);
            if (Required(session, "agentId") != Required(agentInfo, "id"))
                throw new ArgumentException("PAREL session belongs to a different agent.");
            turn.Receipt["session"] = session;
        }
        turn.Receipt["session_id"] = sessionId;
        var path = $"sessions/{Uri.EscapeDataString(sessionId)}";
        var before = await Send(HttpMethod.Get, $"{path}/messages?view=chat", key, null, ct);
        if (before.ValueKind != JsonValueKind.Array) throw new JsonException("PAREL messages must be an array.");
        var submitted = await Send(HttpMethod.Post, $"{path}/messages", key, messageBody, ct, idempotencyKey);
        turn.Receipt["submit"] = submitted;
        turn.Receipt["idempotency_key"] = idempotencyKey;
        var status = Required(submitted, "status");
        var toolId = "parel-" + sessionId;
        var preliminary = turn.Metadata;
        var preliminaryReceipt = JsonSerializer.SerializeToElement(turn.Receipt);
        var preliminaryScoped = new Dictionary<string, Dictionary<string, object>>
        {
            [GetIdentifier()] = preliminaryReceipt.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone())
        };
        yield return Event("tool-input-available", toolId, new AIToolInputAvailableEventData
        {
            ToolName = ToolName, Input = preliminaryReceipt, ProviderExecuted = true, ProviderMetadata = preliminaryScoped
        }, preliminary);
        yield return Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
        {
            ToolName = ToolName, Output = new CallToolResult { StructuredContent = preliminaryReceipt },
            ProviderExecuted = true, Preliminary = true, ProviderMetadata = preliminaryScoped
        }, preliminary);
        string? text;
        if (status == "executed")
        {
            if (!submitted.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                throw new JsonException("PAREL command receipt is missing its result.");
            if (result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException(String(result, "error") ?? "PAREL command failed.");
            text = String(result, "reply");
        }
        else
        {
            if (status is not ("accepted" or "queued")) throw new JsonException("PAREL returned an unknown message status.");
            var receipt = submitted;
            string? turnId = String(submitted, "turnId");
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if (turnId is null)
                {
                    try
                    {
                        receipt = await Send(HttpMethod.Get, $"{path}/turns?by_idempotency_key={Uri.EscapeDataString(idempotencyKey)}", key, null, ct);
                        turnId = String(receipt, "turnId") ?? String(receipt, "id");
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
                }
                if (turnId is not null)
                {
                    receipt = await Send(HttpMethod.Get, $"turns/{Uri.EscapeDataString(turnId)}", key, null, ct);
                    var state = Required(receipt, "status");
                    if (state is "completed" or "error" or "cancelled")
                    {
                        turn.Receipt["turn"] = receipt;
                        turn.Receipt["turn_id"] = turnId;
                        if (state != "completed")
                            throw new InvalidOperationException($"PAREL turn {state}: {String(receipt, "errorMessage") ?? String(receipt, "errorCode") ?? "unknown error"}");
                        break;
                    }
                    if (state is not ("queued" or "running" or "paused"))
                        throw new JsonException("PAREL returned an unknown turn status.");
                }
                if (attempt == 119) throw new TimeoutException("Timed out waiting for the PAREL turn.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            var history = await Send(HttpMethod.Get, $"{path}/messages?view=chat", key, null, ct);
            turn.Receipt["messages"] = history;
            text = ExtractReply(before, history, String(submitted, "inputId"), turnId!, status == "queued");
        }
        var metadata = turn.Metadata;
        var receiptJson = JsonSerializer.SerializeToElement(turn.Receipt);
        var tool = new AIToolCallContentPart
        {
            Type = "tool-call", ToolCallId = toolId, ToolName = ToolName, ProviderExecuted = true, Input = receiptJson,
            Output = new CallToolResult { StructuredContent = receiptJson }, State = "output-available", Metadata = metadata
        };
        turn.Content.Add(tool);
        var scoped = new Dictionary<string, Dictionary<string, object>>
        {
            [GetIdentifier()] = receiptJson.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone())
        };
        yield return Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
        {
            ToolName = ToolName, Output = tool.Output, ProviderExecuted = true, ProviderMetadata = scoped
        }, metadata);
        if (!string.IsNullOrEmpty(text))
        {
            turn.Content.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
            yield return Event("text-start", toolId, new AITextStartEventData(), metadata);
            yield return Event("text-delta", toolId, new AITextDeltaEventData { Delta = text }, metadata);
            yield return Event("text-end", toolId, new AITextEndEventData(), metadata);
        }
        turn.Status = "completed";
        yield return Event("finish", toolId, new AIFinishEventData
        {
            Model = turn.Model, FinishReason = "stop",
            MessageMetadata = AIFinishMessageMetadata.Create(turn.Model, DateTimeOffset.UtcNow, null, additionalProperties: metadata)
        }, metadata);
    }

    private static string? ExtractReply(JsonElement before, JsonElement history, string? inputId, string turnId, bool queued)
    {
        if (history.ValueKind != JsonValueKind.Array) throw new JsonException("PAREL messages must be an array.");
        var items = history.EnumerateArray().ToArray();
        var matched = items.Where(item => String(item, "role") == "assistant" && String(item, "turnId") == turnId)
            .Select(MessageText).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        if (matched.Length > 0) return string.Join("\n", matched);
        if (queued || items.Any(item => String(item, "role") == "assistant" && String(item, "turnId") is not null))
            throw new InvalidOperationException("PAREL could not associate an assistant reply with the completed turn.");
        var start = inputId is null ? -1 : Array.FindIndex(items, item => String(item, "id") == inputId);
        if (start < 0)
        {
            start = before.GetArrayLength();
            if (items.Length < start || items.Skip(start).Count(item => String(item, "role") == "user") > 1)
                throw new InvalidOperationException("PAREL could not safely associate the reply with the submitted input.");
        }
        var replies = items.Skip(start).SkipWhile(item => String(item, "role") != "user")
            .Skip(1).TakeWhile(item => String(item, "role") != "user")
            .Where(item => String(item, "role") == "assistant").Select(MessageText).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        if (replies.Length == 0) throw new InvalidOperationException("PAREL completed the turn without an identifiable assistant reply.");
        return string.Join("\n", replies);
    }

    private static string MessageText(JsonElement message)
    {
        if (String(message, "content") is { } text) return text;
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(parts.EnumerateArray().Where(part => String(part, "type") == "text").Select(part => String(part, "text")));
    }

    private static Dictionary<string, JsonElement> Object(object? value, string name)
    {
        if (value is null) return [];
        var json = JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException($"PAREL {name} must be an object.");
        return json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static Dictionary<string, JsonElement> TakeObject(Dictionary<string, JsonElement> value, string name)
        => value.Remove(name, out var item) ? Object(item, name) : [];

    private static string? TakeString(Dictionary<string, JsonElement> value, string name)
    {
        if (!value.Remove(name, out var item)) return null;
        if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            throw new ArgumentException($"PAREL {name} must be a non-empty string.");
        return item.GetString();
    }

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() : null;

    private static string Required(JsonElement value, string name)
        => String(value, name) is { Length: > 0 } text ? text : throw new JsonException($"PAREL response is missing {name}.");

    private static JsonElement? FindReceipt(AIRequest request)
    {
        foreach (var tool in request.Input?.Items?.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>().Reverse() ?? [])
        {
            if (tool.ToolName != ToolName || tool.ProviderExecuted != true) continue;
            var value = JsonSerializer.SerializeToElement(tool.Output ?? tool.Input, JsonSerializerOptions.Web);
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("structuredContent", out var structured)) value = structured;
            if (String(value, "session_id") is not null) return value;
        }
        return null;
    }

    private AIStreamEvent Event(string type, string id, object data, Dictionary<string, object?> metadata) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata }
    };
}
