using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.SundayPyjamas;

public partial class SundayPyjamasProvider
{
    private const string SessionTool = "sundaypyjamas_session";
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
            if (part.Event.Type == "finish")
                return new AIResponse
                {
                    ProviderId = GetIdentifier(), Model = request.Model, Status = "completed",
                    Output = part.Event.Output, Metadata = part.Metadata
                };
        throw new InvalidOperationException("SundayPyjamas did not complete the response.");
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var model = request.Model?.StartsWith("sundaypyjamas/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["sundaypyjamas/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(model) || model.Contains('/') || model is "." or "..")
            throw new ArgumentException("SundayPyjamas requires a model or managed agent ID.");
        var options = Options(request);
        var textId = $"sundaypyjamas-{Guid.NewGuid():N}";
        var metadata = new Dictionary<string, object?>();
        var output = new List<AIContentPart>();
        var reply = new StringBuilder();

        if (model == ChatModel)
        {
            options["model"] = JsonSerializer.SerializeToElement(model);
            if (!options.ContainsKey("messages"))
            {
                var messages = request.Input?.Items?.Select(item => new
                {
                    role = item.Role ?? "",
                    content = string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? [])
                }).Where(item => item.role is "user" or "assistant" or "system" && !string.IsNullOrWhiteSpace(item.content))
                    .ToList();
                if (messages is not { Count: > 0 } && !string.IsNullOrWhiteSpace(request.Input?.Text))
                    messages = [new { role = "user", content = request.Input.Text }];
                if (messages is not { Count: > 0 })
                    throw new ArgumentException("SundayPyjamas chat requires non-empty messages.");
                if (!string.IsNullOrWhiteSpace(request.Instructions))
                    messages.Insert(0, new { role = "system", content = request.Instructions });
                options["messages"] = JsonSerializer.SerializeToElement(messages, JsonOptions);
            }
            using var message = CreateRequest(HttpMethod.Post, "chat");
            message.Content = Content(options);
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
            yield return Event("text-start", textId, new AITextStartEventData(), metadata);
            var buffer = new char[2048];
            int read;
            // /chat streams raw text, despite advertising text/event-stream.
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                var delta = new string(buffer, 0, read);
                reply.Append(delta);
                yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = delta }, metadata);
            }
            metadata[GetIdentifier()] = new { response = reply.ToString() };
            yield return Event("text-end", textId, new AITextEndEventData(), metadata);
        }
        else
        {
            var mode = TakeString(options, "mode") ?? "assist";
            if (mode is not ("assist" or "autonomous"))
                throw new ArgumentException("SundayPyjamas mode must be assist or autonomous.");
            var sessionId = TakeString(options, "sessionId") ?? FindSession(request, model);
            var sessionOptions = TakeObject(options, "session");
            if (sessionOptions.Count > 0 && (sessionId is not null || mode == "autonomous"))
                throw new ArgumentException("SundayPyjamas session options require a new assist-mode session.");
            var agentPath = $"managed-agents/agents/{Uri.EscapeDataString(model)}";
            var prompt = LatestUserText(request);
            var field = mode == "autonomous" ? "task" : "message";
            if (!options.ContainsKey(field))
            {
                if (string.IsNullOrWhiteSpace(prompt))
                    throw new ArgumentException("SundayPyjamas requires a latest user message or native task/message.");
                options[field] = JsonSerializer.SerializeToElement(prompt);
            }

            JsonElement? created = null;
            if (sessionId is null && mode == "assist")
            {
                created = await SendJsonAsync(HttpMethod.Post, $"{agentPath}/sessions", sessionOptions, cancellationToken);
                sessionId = RequiredString(created.Value.GetProperty("session"), "id");
            }

            var native = new Dictionary<string, object?> { ["agentId"] = model, ["sessionId"] = sessionId };
            if (created.HasValue) native["session"] = created.Value.Clone();
            metadata[GetIdentifier()] = native;

            if (mode == "autonomous")
            {
                if (sessionId is not null) options["sessionId"] = JsonSerializer.SerializeToElement(sessionId);
                var start = await SendJsonAsync(HttpMethod.Post, $"{agentPath}/runs", options, cancellationToken);
                sessionId = RequiredString(start, "sessionId");
                var runId = RequiredString(start, "runId");
                native["sessionId"] = sessionId;
                native["runId"] = runId;
                native["created"] = start.Clone();
                yield return Event("data-sundaypyjamas-run", runId, start.Clone(), metadata);
                while (true)
                {
                    var run = await SendJsonAsync(HttpMethod.Get,
                        $"{agentPath}/runs/{Uri.EscapeDataString(runId)}", null, cancellationToken);
                    native["run"] = run.Clone();
                    yield return Event("data-sundaypyjamas-run", runId, run.Clone(), metadata);
                    var status = RequiredString(run.GetProperty("run"), "status");
                    if (status == "failed")
                        throw new InvalidOperationException($"SundayPyjamas run failed: {run.GetRawText()}");
                    if (status == "completed") break;
                    if (status is not ("pending" or "running"))
                        throw new JsonException($"Unknown SundayPyjamas run status '{status}'.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
            else
            {
                var sessionPath = $"managed-agents/sessions/{Uri.EscapeDataString(sessionId!)}";
                var baseline = created.HasValue ? new HashSet<string>() :
                    Timeline(await SendJsonAsync(HttpMethod.Get, $"{sessionPath}/events", null, cancellationToken))
                        .Select(item => RequiredString(item, "id")).ToHashSet();
                var events = new List<object>();
                native["events"] = events;
                using var message = CreateRequest(HttpMethod.Post, $"{sessionPath}/messages");
                message.Content = Content(options);
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
                await foreach (var frame in ReadSseAsync(reader, cancellationToken))
                {
                    events.Add(frame);
                    yield return Event("data-sundaypyjamas-event", textId, frame, metadata);
                    if (frame.Event == "error")
                        throw new InvalidOperationException($"SundayPyjamas stream failed: {frame.Data}");
                }
                var timeline = await SendJsonAsync(HttpMethod.Get, $"{sessionPath}/events", null, cancellationToken);
                native["timeline"] = timeline.Clone();
                foreach (var item in Timeline(timeline))
                {
                    if (baseline.Contains(RequiredString(item, "id"))) continue;
                    if (String(item, "type") == "turn_run" && String(item, "status") == "failed")
                        throw new InvalidOperationException($"SundayPyjamas turn failed: {item.GetRawText()}");
                    if (String(item, "type") == "message" && String(item, "role") == "assistant")
                        reply.Append(String(item, "content"));
                }
                if (reply.Length > 0)
                {
                    yield return Event("text-start", textId, new AITextStartEventData(), metadata);
                    yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = reply.ToString() }, metadata);
                    yield return Event("text-end", textId, new AITextEndEventData(), metadata);
                }
            }

            var receipt = JsonSerializer.SerializeToElement(native, JsonOptions);
            var toolId = $"{textId}-session";
            var toolInput = new { agentId = model, sessionId };
            var toolOutput = new CallToolResult { StructuredContent = receipt };
            output.Add(new AIToolCallContentPart
            {
                Type = "tool-call", ToolName = SessionTool, ToolCallId = toolId,
                Input = toolInput, Output = toolOutput, ProviderExecuted = true,
                State = "output-available", Metadata = metadata
            });
            yield return Event("tool-input-available", toolId,
                new AIToolInputAvailableEventData { ToolName = SessionTool, Input = toolInput, ProviderExecuted = true }, metadata);
            yield return Event("tool-output-available", toolId,
                new AIToolOutputAvailableEventData { ToolName = SessionTool, Output = toolOutput, ProviderExecuted = true }, metadata);
        }

        if (reply.Length > 0) output.Add(new AITextContentPart { Type = "text", Text = reply.ToString(), Metadata = metadata });
        var final = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = output, Metadata = metadata }], Metadata = metadata };
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = "finish", Id = textId, Timestamp = DateTimeOffset.UtcNow, Output = final,
                Data = new AIFinishEventData
                {
                    Model = request.Model, FinishReason = "stop",
                    MessageMetadata = AIFinishMessageMetadata.Create(request.Model ?? GetIdentifier(), DateTimeOffset.UtcNow,
                        additionalProperties: metadata)
                }
            }
        };
    }

    private static string? LatestUserText(AIRequest request)
        => request.Input?.Items?.AsEnumerable().Reverse()
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? request.Input?.Text;

    private Dictionary<string, JsonElement> Options(AIRequest request)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var native) == true && native is not null)
        {
            var options = JsonSerializer.SerializeToElement(native, JsonOptions);
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("SundayPyjamas metadata must be a JSON object.");
            foreach (var property in options.EnumerateObject()) result[property.Name] = property.Value.Clone();
        }
        return result;
    }

    private static string? TakeString(Dictionary<string, JsonElement> options, string name)
    {
        if (!options.Remove(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"SundayPyjamas {name} must be a non-empty string.");
        return value.GetString();
    }

    private static Dictionary<string, JsonElement> TakeObject(Dictionary<string, JsonElement> options, string name)
    {
        if (!options.Remove(name, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"SundayPyjamas {name} must be a JSON object.");
        return value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private static string? FindSession(AIRequest request, string agent)
    {
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>().Reverse() ?? [])
            {
                if (tool.ToolName != SessionTool || tool.ProviderExecuted != true || tool.Output is null) continue;
                var raw = JsonSerializer.SerializeToElement(tool.Output, JsonOptions);
                if (raw.TryGetProperty("structuredContent", out var structured)) raw = structured;
                if (String(raw, "agentId") != agent)
                    throw new ArgumentException("SundayPyjamas session belongs to a different agent.");
                return RequiredString(raw, "sessionId");
            }
        return null;
    }

    private static IEnumerable<JsonElement> Timeline(JsonElement raw)
    {
        if (!raw.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            throw new JsonException("SundayPyjamas timeline is missing an events array.");
        return events.EnumerateArray();
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var message = CreateRequest(method, path);
        if (body is not null) message.Content = Content(body);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("SundayPyjamas response must be a JSON object.");
        return document.RootElement.Clone();
    }

    private static StringContent Content(object body)
        => new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"SundayPyjamas request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
    }

    private static string? String(JsonElement raw, string name)
        => raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string RequiredString(JsonElement raw, string name)
        => !string.IsNullOrWhiteSpace(String(raw, name)) ? String(raw, name)! :
            throw new JsonException($"SundayPyjamas response is missing '{name}'.");

    private sealed record SseFrame(string? Event, string? Id, string Data);

    private static async IAsyncEnumerable<SseFrame> ReadSseAsync(StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? type = null, id = null;
        var data = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Count > 0) yield return new SseFrame(type, id, string.Join("\n", data));
                type = null; id = null; data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "event": type = value; break;
                case "id": id = value; break;
                case "data": data.Add(value); break;
            }
        }
        if (data.Count > 0) yield return new SseFrame(type, id, string.Join("\n", data));
    }

    private AIStreamEvent Event(string type, string id, object data, Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow }
        };
}
