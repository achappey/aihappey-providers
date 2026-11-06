using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AgenticWorkforceME;

public sealed partial class AgenticWorkforceMEProvider
{
    private const string RunTool = "agenticworkforceme_run";

    private sealed class Turn(string agent, string tenant, string key, bool oneOff, Dictionary<string, JsonElement> body)
    {
        public string Agent { get; } = agent;
        public string Tenant { get; } = tenant;
        public string Key { get; } = key;
        public bool OneOff { get; } = oneOff;
        public Dictionary<string, JsonElement> Body { get; } = body;
        public Dictionary<string, object?> Receipt { get; } = new() { ["agent_id"] = agent, ["tenant_id"] = tenant };
        public List<JsonElement> Events { get; } = [];
        public List<AIContentPart> Content { get; } = [];
        public string? ThreadId { get; set; }
        public string? RunId { get; set; }
        public string Status { get; set; } = "incomplete";
        public AIUsage? Usage { get; set; }
        public string Model => $"agenticworkforceme/{Agent}" + (OneOff ? "/run" : "");
        public Dictionary<string, object?> Metadata => new() { ["agenticworkforceme"] = JsonSerializer.SerializeToElement(Receipt) };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = CreateTurn(request, cancellationToken);
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
        var turn = CreateTurn(request, cancellationToken);
        await foreach (var evt in Run(turn, request, cancellationToken)) yield return evt;
    }

    private Turn CreateTurn(AIRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var model = request.Model?.StartsWith("agenticworkforceme/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["agenticworkforceme/".Length..] : request.Model;
        var route = model?.Split('/') ?? [];
        if (route.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(route[0])
            || route.Length == 2 && route[1] != "run")
            throw new ArgumentException("AgenticWorkforceME requires model agenticworkforceme/{agentId} or agenticworkforceme/{agentId}/run.");
        if (request.Tools?.Count > 0 || !string.IsNullOrWhiteSpace(request.Instructions)
            || request.Input?.Items?.Any(item => item.Role is "system" or "developer") == true
            || request.Temperature is not (null or 1) || request.TopP is not null
            || request.ToolChoice is not null || request.ResponseFormat is not null)
            throw new NotSupportedException("AgenticWorkforceME uses the published agent's instructions, tools and generation settings.");
        if (request.Input?.Items?.Where(item => item.Role == "user")
            .SelectMany(item => item.Content ?? []).Any(part => part is not AITextContentPart) == true)
            throw new NotSupportedException("Use native attachment_ids for AgenticWorkforceME attachments.");

        var body = new Dictionary<string, JsonElement>();
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var native) == true && native is not null)
        {
            var value = JsonSerializer.SerializeToElement(native, JsonSerializerOptions.Web);
            if (value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("AgenticWorkforceME provider metadata must be a JSON object.");
            foreach (var property in value.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        if (body.Remove("body", out var nativeBody))
        {
            if (nativeBody.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("AgenticWorkforceME body must be an object.");
            foreach (var property in nativeBody.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        var tenant = TakeString(body, "tenant_id") ?? _options.TenantId;
        return new Turn(route[0], Tenant(tenant), Key(), route.Length == 2, body);
    }

    private async IAsyncEnumerable<AIStreamEvent> Run(Turn turn, AIRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = turn.Body;
        turn.ThreadId = TakeString(body, "thread_id");
        turn.RunId = TakeString(body, "run_id");
        var idempotencyKey = TakeString(body, "idempotency_key") ?? Guid.NewGuid().ToString("N");
        Dictionary<string, JsonElement>? threadBody = null;
        if (body.Remove("thread", out var thread))
        {
            if (thread.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("AgenticWorkforceME thread must be an object.");
            threadBody = thread.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        }
        if (turn.OneOff && (turn.ThreadId is not null || threadBody is not null))
            throw new ArgumentException("AgenticWorkforceME one-off runs cannot use a thread.");

        if (!turn.OneOff && turn.RunId is null && turn.ThreadId is null && FindReceipt(request) is { } previous)
        {
            if (String(previous, "agent_id") != turn.Agent || String(previous, "tenant_id") != turn.Tenant)
                throw new ArgumentException("AgenticWorkforceME history belongs to a different agent or tenant. Start a new conversation.");
            turn.ThreadId = String(previous, "thread_id");
        }
        if (turn.ThreadId is not null && threadBody is not null)
            throw new ArgumentException("AgenticWorkforceME thread creation options cannot continue an existing thread.");

        if (turn.RunId is null)
        {
            var prompt = request.Input?.Items?.LastOrDefault(item => item.Role == "user")?.Content?
                .OfType<AITextContentPart>().Select(part => part.Text);
            var text = prompt is null ? request.Input?.Text : string.Join("\n", prompt);
            if (turn.OneOff)
            {
                if (!body.ContainsKey("input"))
                {
                    if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("AgenticWorkforceME requires input or a user prompt.");
                    body["input"] = JsonSerializer.SerializeToElement(new { text });
                }
                turn.Receipt["submit"] = await Send(HttpMethod.Post, $"agents/{Uri.EscapeDataString(turn.Agent)}/runs",
                    turn.Key, turn.Tenant, body, ct);
            }
            else
            {
                if (!body.ContainsKey("content") && !string.IsNullOrWhiteSpace(text))
                    body["content"] = JsonSerializer.SerializeToElement(text);
                if (!body.ContainsKey("content") && !body.ContainsKey("attachment_ids"))
                    throw new ArgumentException("AgenticWorkforceME requires content, attachment_ids or a user prompt.");
                if (turn.ThreadId is null)
                {
                    if (request.Input?.Items?.Any(item => item.Role == "assistant") == true)
                        throw new ArgumentException("AgenticWorkforceME history requires a thread receipt or thread_id provider metadata.");
                    threadBody ??= [];
                    threadBody["agent_id"] = JsonSerializer.SerializeToElement(turn.Agent);
                    var created = await Send(HttpMethod.Post, "threads", turn.Key, turn.Tenant, threadBody, ct);
                    turn.Receipt["thread"] = created;
                    turn.ThreadId = Required(created, "id");
                }
                turn.Receipt["submit"] = await Send(HttpMethod.Post, $"threads/{Uri.EscapeDataString(turn.ThreadId)}/messages",
                    turn.Key, turn.Tenant, body, ct, idempotencyKey);
            }
            turn.RunId = Required((JsonElement)turn.Receipt["submit"]!, "run_id");
        }
        var detail = await Send(HttpMethod.Get, $"runs/{Uri.EscapeDataString(turn.RunId)}", turn.Key, turn.Tenant, null, ct);
        var status = RunStatus(detail, turn);
        turn.Receipt["thread_id"] = turn.ThreadId;
        turn.Receipt["run_id"] = turn.RunId;
        var toolId = "agenticworkforceme-" + turn.RunId;
        yield return Event("tool-input-available", toolId, new AIToolInputAvailableEventData
        {
            ToolName = RunTool, Input = JsonSerializer.SerializeToElement(turn.Receipt), ProviderExecuted = true,
            ProviderMetadata = Scoped(turn.Metadata)
        }, turn.Metadata);
        yield return Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
        {
            ToolName = RunTool, Output = new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(turn.Receipt) },
            ProviderExecuted = true, Preliminary = true, ProviderMetadata = Scoped(turn.Metadata)
        }, turn.Metadata);

        var textOutput = new StringBuilder();
        var reasoning = new StringBuilder();
        var textStarted = false;
        if (status is "queued" or "running")
        {
            using var http = Http(HttpMethod.Get, $"runs/{Uri.EscapeDataString(turn.RunId)}/events?since=-1", turn.Key, turn.Tenant);
            http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccess(response, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var frame in SseParser.Create(stream).EnumerateAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(frame.Data)) continue;
                using var document = JsonDocument.Parse(frame.Data);
                var raw = document.RootElement.Clone();
                if (raw.ValueKind != JsonValueKind.Object)
                    throw new JsonException("AgenticWorkforceME event must be an object.");
                var type = String(raw, "type") ?? frame.EventType;
                if (type == "heartbeat") continue;
                turn.Events.Add(raw);
                var metadata = new Dictionary<string, object?> { [GetIdentifier()] = raw };
                yield return Event("data-agenticworkforceme-event", turn.RunId, raw, metadata);
                if (type == "llm.delta" && String(raw, "text") is { } delta)
                {
                    if (!textStarted)
                    {
                        yield return Event("text-start", turn.RunId, new AITextStartEventData { ProviderMetadata = Flat(metadata) }, metadata);
                        textStarted = true;
                    }
                    textOutput.Append(delta);
                    yield return Event("text-delta", turn.RunId, new AITextDeltaEventData { Delta = delta, ProviderMetadata = Flat(metadata) }, metadata);
                }
                if (type == "llm.thinking" && String(raw, "text") is { } thinking)
                {
                    if (raw.TryGetProperty("replace", out var replace) && replace.ValueKind == JsonValueKind.True)
                        reasoning.Clear();
                    reasoning.Append(thinking);
                }
                if (type is "run.completed" or "run.failed" or "run.waiting") break;
            }
            detail = await Send(HttpMethod.Get, $"runs/{Uri.EscapeDataString(turn.RunId)}", turn.Key, turn.Tenant, null, ct);
            status = RunStatus(detail, turn);
        }
        if (status is "queued" or "running")
            throw new EndOfStreamException("AgenticWorkforceME event stream ended before the run reached a terminal or waiting state.");
        turn.Receipt["result"] = detail;
        turn.Receipt["events"] = turn.Events;
        turn.Receipt["thread_id"] = turn.ThreadId;
        turn.Status = status == "succeeded" ? "completed" : status is "failed" or "cancelled" or "expired" ? "failed" : "incomplete";
        var run = detail.GetProperty("run");
        var inputTokens = Tokens(run, "tokens_in");
        var outputTokens = Tokens(run, "tokens_out");
        turn.Usage = inputTokens is null && outputTokens is null ? null : new AIUsage
        {
            InputTokens = inputTokens, OutputTokens = outputTokens,
            TotalTokens = inputTokens is not null && outputTokens is not null ? checked(inputTokens + outputTokens) : null
        };
        var finalMetadata = turn.Metadata;
        var finalText = run.TryGetProperty("output", out var output)
            ? output.ValueKind == JsonValueKind.String ? output.GetString() : String(output, "text") : null;
        if (!textStarted)
        {
            if (!string.IsNullOrEmpty(finalText))
            {
                textOutput.Append(finalText);
                textStarted = true;
                yield return Event("text-start", turn.RunId, new AITextStartEventData { ProviderMetadata = Flat(finalMetadata) }, finalMetadata);
                yield return Event("text-delta", turn.RunId, new AITextDeltaEventData { Delta = finalText, ProviderMetadata = Flat(finalMetadata) }, finalMetadata);
            }
        }
        if (textStarted)
        {
            yield return Event("text-end", turn.RunId, new AITextEndEventData { ProviderMetadata = Flat(finalMetadata) }, finalMetadata);
            turn.Content.Add(new AITextContentPart { Type = "text", Text = finalText ?? textOutput.ToString(), Metadata = finalMetadata });
        }
        if (reasoning.Length > 0)
        {
            // Native thinking can replace earlier snapshots; normalize only the final value.
            yield return Event("reasoning-start", toolId, new AIReasoningStartEventData { ProviderMetadata = Scoped(finalMetadata) }, finalMetadata);
            yield return Event("reasoning-delta", toolId, new AIReasoningDeltaEventData { Delta = reasoning.ToString(), ProviderMetadata = Scoped(finalMetadata) }, finalMetadata);
            yield return Event("reasoning-end", toolId, new AIReasoningEndEventData { ProviderMetadata = Scoped(finalMetadata) }, finalMetadata);
            turn.Content.Add(new AIReasoningContentPart { Type = "reasoning", Text = reasoning.ToString(), Metadata = finalMetadata });
        }
        var receipt = JsonSerializer.SerializeToElement(turn.Receipt);
        var tool = new AIToolCallContentPart
        {
            Type = "tool-call", ToolCallId = toolId, ToolName = RunTool, ProviderExecuted = true,
            Input = receipt, Output = new CallToolResult { StructuredContent = receipt }, State = "output-available",
            Metadata = new Dictionary<string, object?>(finalMetadata)
            {
                ["messages.provider.call.metadata"] = new Dictionary<string, object>
                {
                    [GetIdentifier()] = new { type = "server_tool_use" }
                }
            }
        };
        turn.Content.Insert(0, tool);
        yield return Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
        {
            ToolName = RunTool, Output = tool.Output, ProviderExecuted = true, ProviderMetadata = Scoped(finalMetadata)
        }, finalMetadata);
        if (turn.Status == "failed")
            yield return Event("error", turn.RunId, new AIErrorEventData
            {
                ErrorText = String(run, "error") ?? $"AgenticWorkforceME run {status}; inspect provider metadata."
            }, finalMetadata);
        yield return Event("finish", turn.RunId, new AIFinishEventData
        {
            Model = turn.Model, FinishReason = turn.Status == "completed" ? "stop" : turn.Status == "failed" ? "error" : "other",
            InputTokens = turn.Usage?.InputTokens, OutputTokens = turn.Usage?.OutputTokens, TotalTokens = turn.Usage?.TotalTokens,
            MessageMetadata = AIFinishMessageMetadata.Create(turn.Model, DateTimeOffset.UtcNow, turn.Usage, additionalProperties: finalMetadata)
        }, finalMetadata);
    }

    private static string RunStatus(JsonElement detail, Turn turn)
    {
        var status = detail.TryGetProperty("run", out var run) ? String(run, "status") : null;
        if (status is not ("queued" or "running" or "waiting_approval" or "succeeded" or "failed" or "cancelled" or "expired"))
            throw new JsonException("AgenticWorkforceME run has an unknown or missing status.");
        if (String(run, "agent_id") is { } agent && agent != turn.Agent)
            throw new ArgumentException("AgenticWorkforceME run belongs to a different agent.");
        if (String(run, "thread_id") is { } thread)
        {
            if (turn.OneOff || turn.ThreadId is not null && turn.ThreadId != thread)
                throw new ArgumentException("AgenticWorkforceME run belongs to a different thread.");
            turn.ThreadId = thread;
        }
        return status;
    }

    private static int? Tokens(JsonElement run, string name)
    {
        if (!run.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
            throw new JsonException($"AgenticWorkforceME {name} must be a non-negative integer.");
        return count;
    }

    private static string? TakeString(Dictionary<string, JsonElement> body, string name)
    {
        if (!body.Remove(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"AgenticWorkforceME {name} must be a non-empty string.");
        return value.GetString();
    }

    private static JsonElement? FindReceipt(AIRequest request)
    {
        foreach (var tool in request.Input?.Items?.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>().Reverse() ?? [])
        {
            if (tool.ToolName != RunTool || tool.ProviderExecuted != true) continue;
            var raw = JsonSerializer.SerializeToElement(tool.Output ?? tool.Input, JsonSerializerOptions.Web);
            if (raw.ValueKind == JsonValueKind.String) raw = JsonSerializer.Deserialize<JsonElement>(raw.GetString()!);
            if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("structuredContent", out var structured)) raw = structured;
            if (String(raw, "thread_id") is not null) return raw;
        }
        return null;
    }

    private Dictionary<string, object> Flat(Dictionary<string, object?> metadata)
        => new() { [GetIdentifier()] = metadata[GetIdentifier()]! };

    private Dictionary<string, Dictionary<string, object>> Scoped(Dictionary<string, object?> metadata)
        => new()
        {
            [GetIdentifier()] = ((JsonElement)metadata[GetIdentifier()]!).EnumerateObject()
                .ToDictionary(property => property.Name, property => (object)property.Value.Clone())
        };

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata }
    };
}
