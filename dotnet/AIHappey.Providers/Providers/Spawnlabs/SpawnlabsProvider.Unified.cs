using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Spawnlabs;

public sealed partial class SpawnlabsProvider
{
    private const string SessionTool = "spawnlabs_session";
    private const string FormPrefix = "spawnlabs-answer-";

    private sealed class Turn(string definition, string key, Dictionary<string, JsonElement> body)
    {
        public string Definition { get; } = definition;
        public string Key { get; } = key;
        public string ToolCallId { get; } = "spawnlabs-session-" + Guid.NewGuid().ToString("N");
        public Dictionary<string, JsonElement> Body { get; } = body;
        public Dictionary<string, object?> Receipt { get; } = new() { ["definition"] = definition };
        public string? SessionId { get; set; }
        public JsonElement Poll { get; set; }
        public List<AIContentPart> Content { get; } = [];
        public Dictionary<string, object?> Metadata => new() { ["spawnlabs"] = JsonSerializer.SerializeToElement(Receipt) };
        public string Status => String(Poll, "status") switch
        {
            "completed" => "completed",
            "error" => "failed",
            _ => "incomplete"
        };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = CreateTurn(request, cancellationToken);
        await foreach (var _ in Run(turn, request, cancellationToken)) { }
        var metadata = turn.Metadata;
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"spawnlabs/{turn.Definition}", Status = turn.Status,
            Metadata = metadata,
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
        if (request.Tools?.Count > 0) throw new NotSupportedException("Spawnlabs uses its own tools and skills, not caller function tools.");
        if (request.Input?.Items?.Where(item => item.Role is "user" or "system" or "developer")
            .SelectMany(item => item.Content ?? []).Any(part => part is not AITextContentPart && part is not AIToolCallContentPart) == true)
            throw new NotSupportedException("Spawnlabs session prompts support text input only.");
        var definition = request.Model?.StartsWith("spawnlabs/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["spawnlabs/".Length..] : request.Model;
        if (definition is null || !Definitions.Contains(definition))
            throw new ArgumentException("Spawnlabs model must select base, pitch_deck, presentation, researcher or outreach.");
        var body = new Dictionary<string, JsonElement>();
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var native) == true && native is not null)
        {
            var options = JsonSerializer.SerializeToElement(native, JsonSerializerOptions.Web);
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Spawnlabs provider metadata must be a JSON object.");
            foreach (var property in options.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        if (body.Remove("body", out var nativeBody))
        {
            if (nativeBody.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Spawnlabs body must be a JSON object.");
            foreach (var property in nativeBody.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        if (body.TryGetValue("definition", out var selected)
            && (selected.ValueKind != JsonValueKind.String || selected.GetString() != definition))
            throw new ArgumentException("Spawnlabs definition conflicts with the selected model.");
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Spawnlabs API key.");
        return new Turn(definition, key, body);
    }

    private async IAsyncEnumerable<AIStreamEvent> Run(Turn turn, AIRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = turn.Body;
        var pollOnly = TakeBoolean(body, "pollOnly");
        var maxPolls = TakeInteger(body, "maxPolls", 300, 1, 1800);
        var interval = TakeInteger(body, "pollIntervalMs", 2000, 1, 30000);
        turn.SessionId = TakeString(body, "session_id");
        var toolUseId = TakeString(body, "tool_use_id");
        var hasAnswers = body.Remove("answers", out var answers);
        var answerTool = FindAnswer(request);
        if (hasAnswers && answers.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Spawnlabs answers must be a JSON object.");
        if (hasAnswers != (toolUseId is not null))
            throw new ArgumentException("Spawnlabs answers require both tool_use_id and answers.");
        if (hasAnswers && answerTool is not null)
            throw new ArgumentException("Submit Spawnlabs answers using either provider metadata or elicitation, not both.");
        if (pollOnly && (hasAnswers || answerTool is not null))
            throw new ArgumentException("Spawnlabs pollOnly cannot submit answers.");

        var previous = FindReceipt(request);
        if (previous is { } receipt)
        {
            if (String(receipt, "definition") != turn.Definition)
                throw new ArgumentException("Spawnlabs session belongs to a different agent definition.");
            var previousId = String(receipt, "session_id");
            if (turn.SessionId is not null && turn.SessionId != previousId)
                throw new ArgumentException("Conflicting Spawnlabs session IDs.");
            turn.SessionId ??= previousId;
        }
        if (turn.SessionId is null && (pollOnly || hasAnswers || answerTool is not null))
            throw new ArgumentException("Spawnlabs resume requires a session receipt or session_id.");

        if (turn.SessionId is null)
        {
            body["definition"] = JsonSerializer.SerializeToElement(turn.Definition);
            body["prompt"] = JsonSerializer.SerializeToElement(Prompt(request, body));
            if (!body.ContainsKey("system_prompt"))
            {
                var instructions = new List<string>();
                if (!string.IsNullOrWhiteSpace(request.Instructions)) instructions.Add(request.Instructions);
                instructions.AddRange(request.Input?.Items?.Where(item => item.Role is "system" or "developer")
                    .SelectMany(item => item.Content?.OfType<AITextContentPart>() ?? [])
                    .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
                if (instructions.Count > 0)
                    body["system_prompt"] = JsonSerializer.SerializeToElement(string.Join("\n\n", instructions));
            }
            var created = await Send(HttpMethod.Post, "", body, turn.Key, ct);
            turn.SessionId = String(created, "session_id");
            if (string.IsNullOrWhiteSpace(turn.SessionId))
                throw new JsonException("Spawnlabs create response is missing session_id.");
            turn.Receipt["create"] = created;
        }
        else if (!pollOnly)
        {
            var current = await Poll(turn, ct);
            var status = String(current, "status");
            if (status is not ("completed" or "waiting_for_answer"))
                throw new InvalidOperationException($"Spawnlabs session cannot accept input while {status}.");
            if (hasAnswers || answerTool is not null)
            {
                var waiting = Property(current, "waiting_for_answer");
                var pendingId = waiting is { } value ? String(value, "tool_use_id") : null;
                if (pendingId is null || status != "waiting_for_answer")
                    throw new InvalidOperationException("Spawnlabs session is not waiting for an answer.");
                if (answerTool is not null)
                {
                    if (answerTool.ToolCallId != FormId(turn.SessionId, pendingId))
                        throw new InvalidOperationException("Spawnlabs elicitation is stale or belongs to another session.");
                    answers = ReadAnswer(answerTool);
                    toolUseId = pendingId;
                }
                if (toolUseId != pendingId)
                    throw new InvalidOperationException("Spawnlabs tool_use_id does not match the pending question.");
                turn.Receipt["answer"] = await Send(HttpMethod.Post, SessionPath(turn) + "/answer",
                    new { tool_use_id = toolUseId, answers }, turn.Key, ct);
            }
            else
            {
                turn.Receipt["message"] = await Send(HttpMethod.Post, SessionPath(turn) + "/message",
                    new { prompt = Prompt(request, body) }, turn.Key, ct);
            }
        }
        turn.Receipt["session_id"] = turn.SessionId;
        foreach (var evt in ReceiptEvents(turn, final: false)) yield return evt;

        for (var poll = 0; poll < maxPolls; poll++)
        {
            turn.Poll = await Poll(turn, ct);
            turn.Receipt["poll"] = turn.Poll;
            if (String(turn.Poll, "status") != "running") break;
            if (poll + 1 < maxPolls) await Task.Delay(interval, ct);
        }
        var metadata = turn.Metadata;
        if (String(turn.Poll, "last_assistant_message") is { Length: > 0 } text)
        {
            turn.Content.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
            var providerMetadata = new Dictionary<string, object> { [GetIdentifier()] = metadata[GetIdentifier()]! };
            yield return Event("text-start", turn.SessionId, new AITextStartEventData { ProviderMetadata = providerMetadata }, metadata);
            yield return Event("text-delta", turn.SessionId, new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, metadata);
            yield return Event("text-end", turn.SessionId, new AITextEndEventData { ProviderMetadata = providerMetadata }, metadata);
        }
        if (String(turn.Poll, "status") == "waiting_for_answer")
        {
            var form = WaitingForm(turn);
            turn.Content.Add(form);
            yield return Event("tool-input-available", form.ToolCallId, new AIToolInputAvailableEventData
            {
                ToolName = "ai_input_required", Input = form.Input!, ProviderExecuted = false, ProviderMetadata = Scoped(metadata)
            }, metadata);
        }
        foreach (var evt in ReceiptEvents(turn, final: true)) yield return evt;
        if (turn.Status == "failed")
            yield return Event("error", turn.SessionId,
                new AIErrorEventData { ErrorText = "Spawnlabs session failed; inspect provider metadata." }, metadata);
        yield return Event("finish", turn.SessionId, new AIFinishEventData
        {
            Model = $"spawnlabs/{turn.Definition}",
            FinishReason = turn.Status == "failed" ? "error" : String(turn.Poll, "status") == "waiting_for_answer"
                ? "tool-calls" : turn.Status == "completed" ? "stop" : "other",
            MessageMetadata = AIFinishMessageMetadata.Create($"spawnlabs/{turn.Definition}", DateTimeOffset.UtcNow,
                additionalProperties: metadata)
        }, metadata);
    }

    private async Task<JsonElement> Send(HttpMethod method, string path, object? body, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "https://spawnapi.spawnlabs.ai/api/v1/sessions" + path);
        request.Headers.Add("X-API-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Spawnlabs HTTP {(int)response.StatusCode}: {text}", null, response.StatusCode);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Spawnlabs response must be a JSON object.");
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> Poll(Turn turn, CancellationToken ct)
    {
        var raw = await Send(HttpMethod.Get, SessionPath(turn) + "/poll", null, turn.Key, ct);
        if (String(raw, "status") is not ("running" or "completed" or "waiting_for_answer" or "stopped" or "error"))
            throw new JsonException("Spawnlabs poll returned an unknown or missing status.");
        return raw;
    }

    private static string SessionPath(Turn turn) => "/" + Uri.EscapeDataString(turn.SessionId!);
    private static JsonElement? Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : null;
    private static string? String(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } property ? property.GetString() : null;

    private static string? TakeString(Dictionary<string, JsonElement> body, string name)
    {
        if (!body.Remove(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"Spawnlabs {name} must be a non-empty string.");
        return value.GetString();
    }

    private static bool TakeBoolean(Dictionary<string, JsonElement> body, string name)
    {
        if (!body.Remove(name, out var value)) return false;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"Spawnlabs {name} must be a boolean.");
        return value.GetBoolean();
    }

    private static int TakeInteger(Dictionary<string, JsonElement> body, string name, int fallback, int min, int max)
    {
        if (!body.Remove(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new ArgumentException($"Spawnlabs {name} must be an integer between {min} and {max}.");
        return number;
    }

    private static string Prompt(AIRequest request, Dictionary<string, JsonElement> body)
    {
        if (body.TryGetValue("prompt", out var native))
        {
            if (native.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(native.GetString()))
                throw new ArgumentException("Spawnlabs prompt must be a non-empty string.");
            return native.GetString()!;
        }
        var text = request.Input?.Items?.AsEnumerable().Reverse()
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? request.Input?.Text;
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Spawnlabs requires a user prompt.");
        return text;
    }

    private static JsonElement Unwrap(object? output)
    {
        var value = JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web);
        if (value.ValueKind == JsonValueKind.String)
            value = JsonSerializer.Deserialize<JsonElement>(value.GetString()!);
        return Property(value, "structuredContent") ?? value;
    }

    private static JsonElement? FindReceipt(AIRequest request)
    {
        foreach (var tool in request.Input?.Items?.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>().Reverse() ?? [])
            if (tool.ToolName == SessionTool && tool.ProviderExecuted == true)
            {
                var receipt = Unwrap(tool.Output ?? tool.Input);
                if (String(receipt, "session_id") is not null) return receipt;
            }
        return null;
    }

    private static AIToolCallContentPart? FindAnswer(AIRequest request)
    {
        var items = request.Input?.Items ?? [];
        var lastUser = items.FindLastIndex(item => item.Role == "user");
        return items.SelectMany((item, index) => (item.Content ?? []).OfType<AIToolCallContentPart>().Select(tool => (tool, index)))
            .LastOrDefault(value => value.index >= lastUser && value.tool.ToolName == "ai_input_required"
                && value.tool.ToolCallId.StartsWith(FormPrefix, StringComparison.Ordinal) && value.tool.Output is not null).tool;
    }

    private static string FormId(string sessionId, string toolUseId) => FormPrefix + sessionId + "-" + toolUseId;

    private static AIToolCallContentPart WaitingForm(Turn turn)
    {
        var waiting = Property(turn.Poll, "waiting_for_answer") ?? throw new JsonException("Spawnlabs waiting state omitted its question.");
        var id = String(waiting, "tool_use_id") ?? throw new JsonException("Spawnlabs waiting state omitted tool_use_id.");
        var question = Property(waiting, "question");
        var message = question is { } value ? String(value, "question") ?? value.ToString() : "Spawnlabs needs your input.";
        return new AIToolCallContentPart
        {
            Type = "tool-call", ToolCallId = FormId(turn.SessionId!, id), ToolName = "ai_input_required",
            Title = "Input required", ProviderExecuted = false, State = "input-available", Metadata = turn.Metadata,
            Input = InputRequest.ForElicitation(new ElicitRequestParams
            {
                Mode = "form", Message = message,
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        ["answers_json"] = new ElicitRequestParams.StringSchema
                        {
                            Title = "Answers (JSON)", Description = "A JSON object of key-value answers to the question: " + question,
                            MinLength = 2
                        }
                    },
                    Required = ["answers_json"]
                }
            })
        };
    }

    private static JsonElement ReadAnswer(AIToolCallContentPart tool)
    {
        var result = Unwrap(tool.Output);
        if (String(result, "action") != "accept" || Property(result, "content") is not { } content
            || String(content, "answers_json") is not { } json)
            throw new ArgumentException("Spawnlabs requires an accepted elicitation result with answers_json.");
        var answers = JsonSerializer.Deserialize<JsonElement>(json);
        if (answers.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Spawnlabs answers_json must contain a JSON object.");
        return answers;
    }

    private IEnumerable<AIStreamEvent> ReceiptEvents(Turn turn, bool final)
    {
        var metadata = turn.Metadata;
        var id = turn.ToolCallId;
        var raw = (JsonElement)metadata[GetIdentifier()]!;
        var output = new CallToolResult { StructuredContent = raw };
        if (!final)
            yield return Event("tool-input-available", id, new AIToolInputAvailableEventData
            {
                ToolName = SessionTool, Input = raw,
                ProviderExecuted = true, ProviderMetadata = Scoped(metadata)
            }, metadata);
        yield return Event("tool-output-available", id, new AIToolOutputAvailableEventData
        {
            ToolName = SessionTool, Output = output, ProviderExecuted = true, Preliminary = !final, ProviderMetadata = Scoped(metadata)
        }, metadata);
        if (final)
        {
            // Messages replays provider tool inputs, so persist the full receipt there too.
            var toolMetadata = new Dictionary<string, object?>(metadata)
            {
                ["messages.provider.call.metadata"] = new Dictionary<string, object>
                {
                    [GetIdentifier()] = new { type = "server_tool_use" }
                }
            };
            turn.Content.Insert(0, new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = id, ToolName = SessionTool, ProviderExecuted = true,
                State = "output-available", Input = raw, Output = output, Metadata = toolMetadata
            });
        }
    }

    private static Dictionary<string, Dictionary<string, object>> Scoped(Dictionary<string, object?> metadata)
        => new()
        {
            ["spawnlabs"] = ((JsonElement)metadata["spawnlabs"]!).EnumerateObject()
                .ToDictionary(property => property.Name, property => (object)property.Value.Clone())
        };

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata }
    };
}
