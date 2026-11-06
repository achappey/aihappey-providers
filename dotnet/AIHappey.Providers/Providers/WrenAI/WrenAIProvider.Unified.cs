using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.WrenAI;

public sealed partial class WrenAIProvider
{
    private static readonly string[] Checkpoints = ["user_question", "agent_form", "plan_review"];
    private static readonly string[] AskOptions = ["threadId", "language", "agentModel", "reasoningEffort", "role", "planMode", "memoryNamespace", "files"];

    private static CancellationTokenSource Deadline(JsonObject options, CancellationToken ct)
    {
        var seconds = options["timeoutSeconds"]?.GetValue<int>() ?? 7200;
        if (seconds is < 1 or > 86400) throw new ArgumentException("WrenAI timeoutSeconds must be 1–86400.");
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(seconds));
        return source;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = new StringBuilder();
        var frames = new List<object>();
        Dictionary<string, object?>? metadata = null;
        AIFinishEventData? finish = null;
        string? error = null;
        var paused = false;

        await foreach (var evt in StreamUnifiedAsync(request, cancellationToken))
        {
            metadata = evt.Metadata ?? metadata;
            switch (evt.Event.Data)
            {
                case AITextDeltaEventData delta: text.Append(delta.Delta); break;
                case AIErrorEventData failure: error = failure.ErrorText; break;
                case AIFinishEventData end: finish = end; break;
                case AIDataEventData { Data: WrenFrame frame }:
                    frames.Add(new { @event = frame.Event, data = frame.Data });
                    // The turn waits for a side-channel reply; hand the checkpoint back instead of blocking.
                    paused = Checkpoints.Contains(frame.Event);
                    break;
            }
            if (paused) break;
        }

        metadata ??= Metadata(ProjectId(request.Model));
        var details = (Dictionary<string, object?>)metadata["wrenai"]!;
        details["events"] = frames;
        var status = error is not null ? "failed" : paused ? "in_progress" : "completed";
        if (paused) metadata["messages.response.stop_reason"] = "pause_turn";
        if (error is not null) metadata["responses.error"] = new { code = "wrenai_error", message = error };
        var content = new List<AIContentPart>();
        if (text.Length > 0) content.Add(new AITextContentPart { Type = "text", Text = text.ToString(), Metadata = metadata });
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = status,
            Metadata = metadata,
            Usage = finish is null ? null : new AIUsage
            {
                InputTokens = finish.InputTokens,
                OutputTokens = finish.OutputTokens,
                TotalTokens = finish.TotalTokens
            },
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }], Metadata = metadata }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = ProjectId(request.Model);
        var options = Options(request);
        using var deadline = Deadline(options, cancellationToken);
        var ct = deadline.Token;
        var metadata = Metadata(project);
        var details = (Dictionary<string, object?>)metadata["wrenai"]!;
        var operation = Text(options, "operation") ?? "ask";
        var model = request.Model ?? $"wrenai/projects/{project}";

        if (operation != "ask")
        {
            var result = await SendOperation(operation, project, options, ct);
            yield return Event("data-wrenai-result", null, new AIDataEventData { Data = result }, metadata);
            yield return Finish(model, "stop", null, metadata);
            yield break;
        }

        ValidateAsk(request);
        using var http = BuildAsk(project, request, options);
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);

        var texts = new HashSet<string>();
        var reasoning = new HashSet<string>();
        var failed = false;
        var finished = false;
        JsonElement? usage = null;

        await foreach (var frame in ReadFrames(response, ct))
        {
            var raw = frame.Data;
            yield return Event("data-wrenai-event", frame.Event, new AIDataEventData { Data = frame }, metadata);
            var block = Property(raw, "block_id")?.ToString() ?? "0";
            switch (frame.Event)
            {
                case "init":
                    foreach (var key in new[] { "threadId", "threadResponseId", "answerId", "version" })
                        if (Property(raw, key) is { } value) details[key] = value;
                    break;
                case "thinking":
                    if (reasoning.Add(block))
                        yield return Event("reasoning-start", $"wrenai-thinking-{block}", new AIReasoningStartEventData(), metadata);
                    yield return Event("reasoning-delta", $"wrenai-thinking-{block}", new AIReasoningDeltaEventData
                    {
                        Delta = String(raw, "content") ?? "",
                        ProviderMetadata = Scoped(raw)
                    }, metadata);
                    break;
                case "thinking_done":
                    foreach (var open in reasoning)
                        yield return Event("reasoning-end", $"wrenai-thinking-{open}", new AIReasoningEndEventData(), metadata);
                    reasoning.Clear();
                    break;
                case "answer":
                    if (texts.Add(block))
                        yield return Event("text-start", $"wrenai-answer-{block}", new AITextStartEventData(), metadata);
                    yield return Event("text-delta", $"wrenai-answer-{block}", new AITextDeltaEventData
                    {
                        Delta = String(raw, "content") ?? "",
                        ProviderMetadata = new() { ["wrenai"] = raw }
                    }, metadata);
                    break;
                case "tool_call":
                    if (String(raw, "id") is { } callId)
                        yield return Event("tool-input-available", callId, new AIToolInputAvailableEventData
                        {
                            ToolName = String(raw, "name") ?? "tool",
                            Input = Property(raw, "input") ?? Serialize(new { sql = String(raw, "sql") }),
                            ProviderExecuted = true,
                            ProviderMetadata = Scoped(raw)
                        }, metadata);
                    break;
                case "tool_result":
                    if (String(raw, "id") is not { } resultId) break;
                    if (Property(raw, "error") is { ValueKind: JsonValueKind.String } toolError)
                        yield return Event("tool-output-error", resultId, new AIToolOutputErrorEventData
                        {
                            ToolCallId = resultId,
                            ErrorText = toolError.GetString() ?? "WrenAI tool failed.",
                            ProviderExecuted = true,
                            ProviderMetadata = Scoped(raw)
                        }, metadata);
                    else
                        yield return Event("tool-output-available", resultId, new AIToolOutputAvailableEventData
                        {
                            ToolName = String(raw, "name"),
                            Output = raw,
                            ProviderExecuted = true,
                            ProviderMetadata = Scoped(raw)
                        }, metadata);
                    break;
                case "error":
                    failed = true;
                    yield return Event("error", null, new AIErrorEventData { ErrorText = String(raw, "message") ?? raw.GetRawText() }, metadata);
                    break;
                case "done":
                    foreach (var key in new[] { "session_id", "trace_id" })
                        if (Property(raw, key) is { } value) details[key] = value;
                    usage = Property(raw, "usage");
                    finished = true;
                    break;
            }
            if (finished) break;
        }

        foreach (var open in reasoning)
            yield return Event("reasoning-end", $"wrenai-thinking-{open}", new AIReasoningEndEventData(), metadata);
        foreach (var open in texts)
            yield return Event("text-end", $"wrenai-answer-{open}", new AITextEndEventData(), metadata);
        yield return Finish(model, failed ? "error" : "stop", usage, metadata);
    }

    private static AIStreamEvent Finish(string model, string reason, JsonElement? usage, Dictionary<string, object?> metadata)
    {
        int? Number(params string[] names)
        {
            foreach (var name in names)
                if (usage is { } value && Property(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var count))
                    return count;
            return null;
        }
        var input = Number("input_tokens", "inputTokens", "prompt_tokens", "promptTokens");
        var output = Number("output_tokens", "outputTokens", "completion_tokens", "completionTokens");
        var total = Number("total_tokens", "totalTokens") ?? (input.HasValue && output.HasValue ? input + output : null);
        if (usage is { } raw) ((Dictionary<string, object?>)metadata["wrenai"]!)["usage"] = raw;
        return Event("finish", null, new AIFinishEventData
        {
            FinishReason = reason,
            Model = model,
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = total,
            MessageMetadata = AIFinishMessageMetadata.Create(model, DateTimeOffset.UtcNow, null,
                inputTokens: input, outputTokens: output, totalTokens: total, additionalProperties: metadata)
        }, metadata);
    }

    private static void ValidateAsk(AIRequest request)
    {
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("WrenAI agent turns cannot serve gateway client tools.");
        if (!string.IsNullOrWhiteSpace(request.Instructions) || request.Input?.Items?.Any(item => item.Role is "system" or "developer") == true)
            throw new NotSupportedException("A WrenAI project owns its agent instructions; gateway instructions do not reconfigure it.");
        if (request.Temperature is not null || request.TopP is not null || request.ToolChoice is not null
            || request.MaxToolCalls is not null || request.ParallelToolCalls is not null)
            throw new NotSupportedException("WrenAI agent turns do not support gateway sampling or tool-choice settings.");
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        if ((latest?.Content ?? []).Any(part => part is not AITextContentPart))
            throw new NotSupportedException("WrenAI questions support text only; upload files through the Uploads API and pass them as wrenai.files.");
    }

    private HttpRequestMessage BuildAsk(string project, AIRequest request, JsonObject options)
    {
        // Raw request options take precedence over values derived from the gateway request.
        var body = options["request"] is null ? new JsonObject() : Object(options["request"], "WrenAI request options");
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var question = latest is null ? request.Input?.Text
            : string.Join("\n", (latest.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text));
        if (!body.ContainsKey("projectId")) body["projectId"] = ProjectValue(project);
        if (!body.ContainsKey("question")) body["question"] = question ?? "";
        foreach (var key in AskOptions)
            if (!body.ContainsKey(key) && options[key] is { } value) body[key] = value.DeepClone();

        var http = CreateRequest(HttpMethod.Post, "stream/agent_ask");
        http.Headers.Accept.ParseAdd("text/event-stream");
        http.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json");
        if (Text(options, "idempotencyKey") is { } key2) http.Headers.Add("Idempotency-Key", key2);
        var properties = options["sessionProperties"] switch
        {
            JsonObject map => string.Join(",", map.Select(pair => $"{pair.Key}={pair.Value}")),
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(properties)) http.Headers.Add("X-Wren-Session-Properties", properties);
        return http;
    }

    // Side-channel calls address an already running turn by its threadResponseId.
    private async Task<JsonElement> SendOperation(string operation, string project, JsonObject options, CancellationToken ct)
    {
        var turn = Uri.EscapeDataString(Text(options, "threadResponseId")
            ?? options["threadResponseId"]?.ToString()
            ?? throw new ArgumentException("WrenAI threadResponseId is required."));
        var body = options["request"] is null ? new JsonObject() : Object(options["request"], "WrenAI request options");
        if (!body.ContainsKey("projectId")) body["projectId"] = ProjectValue(project);
        switch (operation)
        {
            case "answer":
                foreach (var key in new[] { "questionId", "answers", "freeText" })
                    if (!body.ContainsKey(key) && options[key] is { } value) body[key] = value.DeepClone();
                return await SendJson(HttpMethod.Post, $"stream/agent_ask/{turn}/user_input", body, ct);
            case "cancel":
                return await SendJson(HttpMethod.Post, $"stream/agent_ask/{turn}/cancellation", body, ct);
            case "status":
            case "result":
                return await SendJson(HttpMethod.Get, $"stream/agent_ask/{turn}/{operation}?projectId={Uri.EscapeDataString(project)}", null, ct);
            default:
                throw new ArgumentException($"Unknown WrenAI operation '{operation}'.");
        }
    }

    private static async IAsyncEnumerable<WrenFrame> ReadFrames(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        string? name = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (name is not null || data.Length > 0)
                    yield return new WrenFrame(name ?? "message", ParseData(data.ToString()));
                name = null;
                data.Clear();
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
        if (name is not null || data.Length > 0)
            yield return new WrenFrame(name ?? "message", ParseData(data.ToString()));
    }

    private static JsonElement ParseData(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(data) ? "{}" : data);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return Serialize(data); }
    }
}
