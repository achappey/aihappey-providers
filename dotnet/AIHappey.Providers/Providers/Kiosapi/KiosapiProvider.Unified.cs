using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Kiosapi;

public sealed partial class KiosapiProvider
{
    private const string ReceiptTool = "kiosapi_thread";

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = Key();
        var model = request.Model?.StartsWith("kiosapi/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["kiosapi/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(model) || model.Contains('/'))
            throw new ArgumentException("Kiosapi requires an agent ID as the model (kiosapi/{agentId}).");

        var options = Options(request);
        var receipt = FindReceipt(request, model);
        var threadId = String(options, "thread_id") ?? (receipt.HasValue ? String(receipt.Value, "thread_id") : null);
        var runId = String(options, "run_id") ?? (receipt.HasValue ? String(receipt.Value, "id") : null);
        if (String(options, "thread_id") is { } explicitThread && receipt.HasValue
            && String(receipt.Value, "thread_id") != explicitThread)
        {
            runId = String(options, "run_id");
            receipt = null;
        }

        JsonElement? thread = null;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            if (!string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("Kiosapi run_id requires thread_id.");
            thread = await SendAsync(HttpMethod.Post, $"agents/{PathId(model)}/threads",
                ObjectOption(options, "thread"), key, cancellationToken);
            threadId = RequiredString(thread.Value, "id");
            if (String(thread.Value, "agent_id") is { } threadAgent && threadAgent != model)
                throw new JsonException("Kiosapi thread belongs to a different agent.");
        }

        var resuming = String(options, "run_id") is not null
            || (receipt.HasValue && String(receipt.Value, "status") == "requires_action");
        var results = resuming ? ClientResults(request) : new Dictionary<string, string>(StringComparer.Ordinal);
        JsonElement run;
        if (!string.IsNullOrWhiteSpace(runId) && resuming)
        {
            run = await SendAsync(HttpMethod.Get, $"runs/{PathId(runId)}", null, key, cancellationToken);
            ValidateRun(run, model, threadId, runId);
            if (String(run, "status") == "requires_action")
            {
                var pending = PendingCalls(run);
                var pendingIds = pending.Select(call => RequiredString(call, "id")).ToHashSet(StringComparer.Ordinal);
                var matchingResults = pendingIds.Count(results.ContainsKey);
                if (matchingResults > 0 && matchingResults != pendingIds.Count)
                    throw new InvalidOperationException("Kiosapi requires results for every pending tool call in one request.");
                if (matchingResults > 0)
                {
                    var payload = new { outputs = pendingIds.Select(id => new { tool_call_id = id, output = results[id] }).ToArray() };
                    run = await SendAsync(HttpMethod.Post, $"runs/{PathId(runId)}/tool_outputs", payload, key, cancellationToken);
                    ValidateRun(run, model, threadId, runId);
                }
            }
            else if (results.Count > 0 && String(options, "run_id") is not null)
                throw new InvalidOperationException("Kiosapi run is not waiting for tool outputs.");
        }
        else
        {
            if (results.Count > 0) throw new InvalidOperationException("Kiosapi tool results require a run ID.");
            var payload = ObjectOption(options, "run");
            foreach (var prop in options.EnumerateObject())
                if (prop.Name is not ("thread" or "run" or "thread_id" or "run_id" or "idempotency_key"))
                    payload[prop.Name] = prop.Value.Clone();
            if (!payload.ContainsKey("input")) payload["input"] = LatestUserText(request);
            run = await SendAsync(HttpMethod.Post, $"threads/{PathId(threadId)}/runs", payload, key,
                cancellationToken, String(options, "idempotency_key"));
            runId = RequiredString(run, "id");
            ValidateRun(run, model, threadId, runId);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(11));
        while (String(run, "status") is "queued" or "running")
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException($"Timed out polling Kiosapi run '{runId}'."); }
            try { run = await SendAsync(HttpMethod.Get, $"runs/{PathId(runId!)}", null, key, timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException($"Timed out polling Kiosapi run '{runId}'."); }
            ValidateRun(run, model, threadId, runId!);
        }

        var status = RequiredString(run, "status");
        if (status is "failed" or "expired" or "cancelled")
            throw new InvalidOperationException($"Kiosapi run {status}: {String(run, "error") ?? run.GetRawText()}");
        if (status is not ("succeeded" or "requires_action"))
            throw new JsonException($"Unknown Kiosapi run status '{status}'.");

        var metadata = new Dictionary<string, object?> { ["kiosapi"] = run, ["kiosapi.thread"] = thread };
        var parts = new List<AIContentPart>
        {
            new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = $"kiosapi-{runId}", ToolName = ReceiptTool,
                Title = "Kiosapi thread", ProviderExecuted = true, State = "output-available",
                Input = new { agent_id = model }, Output = new CallToolResult { StructuredContent = run }, Metadata = metadata
            }
        };
        if (status == "succeeded")
            parts.Add(new AITextContentPart { Type = "text", Text = String(run, "output")
                ?? throw new JsonException("Kiosapi succeeded run is missing output."), Metadata = metadata });
        else
            foreach (var call in PendingCalls(run))
            {
                var arguments = RequiredString(call, "arguments");
                using var document = JsonDocument.Parse(arguments);
                parts.Add(new AIToolCallContentPart
                {
                    Type = "tool-call", ToolCallId = RequiredString(call, "id"), ToolName = RequiredString(call, "name"),
                    Input = document.RootElement.Clone(), ProviderExecuted = false, State = "input-available",
                    Metadata = new Dictionary<string, object?> { ["kiosapi"] = call.Clone() }
                });
            }

        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = model.ToModelId(GetIdentifier()), Status = status, Metadata = metadata,
            Output = new AIOutput
            {
                Items = [new AIOutputItem { Type = "message", Role = "assistant", Content = parts, Metadata = metadata }],
                Metadata = metadata
            }
        };
    }

    private static void ValidateRun(JsonElement run, string agent, string thread, string id)
    {
        if (RequiredString(run, "id") != id || RequiredString(run, "thread_id") != thread
            || RequiredString(run, "agent_id") != agent)
            throw new JsonException("Kiosapi run identity does not match the requested agent and thread.");
    }

    private static List<JsonElement> PendingCalls(JsonElement run)
    {
        if (!run.TryGetProperty("pending_tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array
            || calls.GetArrayLength() == 0)
            throw new JsonException("Kiosapi requires_action run has no pending tool calls.");
        var result = calls.EnumerateArray().Select(call => call.Clone()).ToList();
        if (result.Select(call => RequiredString(call, "id")).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new JsonException("Kiosapi returned duplicate pending tool call IDs.");
        return result;
    }

    private static string LatestUserText(AIRequest request)
    {
        var text = request.Input?.Items?.AsEnumerable().Reverse()
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? request.Input?.Text;
        return !string.IsNullOrWhiteSpace(text) ? text : throw new ArgumentException("Kiosapi requires a user message or native run input.");
    }

    private static JsonElement? FindReceipt(AIRequest request, string agent)
    {
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>().Reverse() ?? [])
            {
                if (tool.ToolName != ReceiptTool || tool.ProviderExecuted != true || tool.Output is null) continue;
                var output = JsonSerializer.SerializeToElement(tool.Output, Json);
                if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("structuredContent", out var content))
                    output = content;
                if (String(output, "agent_id") is { } previous && previous != agent)
                    throw new ArgumentException("Kiosapi thread belongs to a different agent.");
                if (String(output, "thread_id") is not null && String(output, "id") is not null) return output.Clone();
            }
        return null;
    }

    private static Dictionary<string, string> ClientResults(AIRequest request)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in (request.Input?.Items ?? []).SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>())
        {
            if (!tool.IsClientToolCall || tool.Output is null) continue;
            var value = JsonSerializer.SerializeToElement(tool.Output, Json);
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (results.TryGetValue(tool.ToolCallId, out var previous) && previous != text)
                throw new ArgumentException($"Conflicting results for Kiosapi tool call '{tool.ToolCallId}'.");
            results[tool.ToolCallId] = text;
        }
        return results;
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var providerMetadata = new Dictionary<string, object> { ["kiosapi"] = response.Metadata!["kiosapi"]! };
        foreach (var part in response.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [])
        {
            if (part is AIToolCallContentPart tool)
            {
                yield return Event("tool-input-available", tool.ToolCallId,
                    new AIToolInputAvailableEventData { ToolName = tool.ToolName ?? throw new JsonException("Kiosapi tool name is missing."), Input = tool.Input!, ProviderExecuted = tool.ProviderExecuted }, response.Metadata);
                if (tool.ProviderExecuted == true)
                    yield return Event("tool-output-available", tool.ToolCallId,
                        new AIToolOutputAvailableEventData { ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true }, response.Metadata);
            }
            else if (part is AITextContentPart text)
            {
                yield return Event("text-start", response.Model, new AITextStartEventData { ProviderMetadata = providerMetadata }, response.Metadata);
                yield return Event("text-delta", response.Model, new AITextDeltaEventData { Delta = text.Text, ProviderMetadata = providerMetadata }, response.Metadata);
                yield return Event("text-end", response.Model, new AITextEndEventData { ProviderMetadata = providerMetadata }, response.Metadata);
            }
        }
        yield return Event("finish", response.Model, new AIFinishEventData
        {
            Model = response.Model, FinishReason = response.Status == "requires_action" ? "tool-calls" : "stop",
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? GetIdentifier(), DateTimeOffset.UtcNow,
                response.Usage, additionalProperties: response.Metadata)
        }, response.Metadata);
    }

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow }
        };
}
