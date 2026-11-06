using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.SippulseAI;

public partial class SippulseAIProvider
{
    private const string SessionTool = "sippulseai_session";
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var (agentId, options, threadId, message) = BuildRequest(request);
        JsonElement? threadResponse = null;
        if (threadId is null)
        {
            threadResponse = await CreateThread(agentId, options, request.Instructions, cancellationToken);
            threadId = RequiredString(threadResponse.Value, "id");
        }

        var raw = await SendJson(HttpMethod.Post, $"threads/{Uri.EscapeDataString(threadId)}/run",
            RunPayload(options, message), cancellationToken);
        var responseText = ResponseText(raw);
        var metadata = Metadata(agentId, threadId, raw, threadResponse: threadResponse);
        var receipt = SessionPart(agentId, threadId, raw);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Usage = Property(raw, "usage") is { ValueKind: JsonValueKind.Object } usage ? usage.Clone() : null,
            Metadata = metadata,
            Output = new AIOutput
            {
                Metadata = metadata,
                Items =
                [
                    new AIOutputItem
                    {
                        Role = "assistant",
                        Metadata = metadata,
                        Content = [receipt, new AITextContentPart { Type = "text", Text = responseText, Metadata = metadata }]
                    }
                ]
            }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (agentId, options, threadId, message) = BuildRequest(request);
        JsonElement? threadResponse = null;
        if (threadId is null)
        {
            threadResponse = await CreateThread(agentId, options, request.Instructions, cancellationToken);
            threadId = RequiredString(threadResponse.Value, "id");
        }

        using var httpRequest = CreateRequest(HttpMethod.Post, $"threads/{Uri.EscapeDataString(threadId)}/stream");
        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = JsonContent(RunPayload(options, message));
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var events = new List<JsonElement>();
        var textId = $"sippulseai-text-{Guid.NewGuid():N}";
        var textStarted = false;
        var messageCompleted = false;
        var completedText = string.Empty;
        JsonElement? final = null;
        JsonElement? usage = null;
        await foreach (var frame in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(frame.Data) || frame.Data == "[DONE]") continue;
            using var document = JsonDocument.Parse(frame.Data);
            var raw = document.RootElement.Clone();
            if (raw.ValueKind != JsonValueKind.Object)
                throw new JsonException("SippulseAI stream event must be an object.");
            events.Add(raw);
            var type = String(raw, "event") ?? frame.EventType;
            var metadata = Metadata(agentId, threadId, raw, events, threadResponse);
            var id = String(raw, "id") ?? request.Id ?? threadId;

            yield return Event("data-sippulseai-event", id,
                new AIDataEventData { Id = id, Data = raw.Clone(), Transient = false }, metadata);

            if (type == "message_chunk")
            {
                var delta = String(raw, "content") ?? string.Empty;
                if (!textStarted)
                {
                    yield return Event("text-start", textId, new AITextStartEventData
                    {
                        ProviderMetadata = new Dictionary<string, object> { ["raw"] = raw.Clone() }
                    }, metadata);
                    textStarted = true;
                }
                if (delta.Length > 0)
                    yield return Event("text-delta", textId, new AITextDeltaEventData
                    {
                        Delta = delta,
                        ProviderMetadata = new Dictionary<string, object> { ["raw"] = raw.Clone() }
                    }, metadata);
                completedText += delta;
            }
            else if (type == "message_complete")
            {
                messageCompleted = true;
                completedText = String(raw, "content") ?? completedText;
                final = raw;
            }
            else if (type == "message_report")
            {
                usage = Property(raw, "usage") is { ValueKind: JsonValueKind.Object } reportUsage
                    ? reportUsage.Clone() : null;
                final = raw;
            }
            else if (type is "abort" or "error")
                throw new InvalidOperationException($"SippulseAI stream failed: {String(raw, "reason") ?? raw.GetRawText()}");
        }

        if (!messageCompleted)
            throw new EndOfStreamException("SippulseAI stream ended before message_complete.");

        var allMetadata = Metadata(agentId, threadId, final ?? JsonSerializer.SerializeToElement(new { }),
            events, threadResponse);
        if (!textStarted && completedText.Length > 0)
        {
            yield return Event("text-start", textId, new AITextStartEventData(), allMetadata);
            yield return Event("text-delta", textId, new AITextDeltaEventData { Delta = completedText }, allMetadata);
            textStarted = true;
        }
        if (textStarted)
            yield return Event("text-end", textId, new AITextEndEventData(), allMetadata);

        var receiptId = $"sippulseai-session-{Guid.NewGuid():N}";
        yield return Event("tool-input-available", receiptId, new AIToolInputAvailableEventData
        {
            ToolName = SessionTool,
            Title = "SippulseAI session",
            Input = new { agentId, threadId },
            ProviderExecuted = true,
            ProviderMetadata = Scoped(allMetadata)
        }, allMetadata);
        yield return Event("tool-output-available", receiptId, new AIToolOutputAvailableEventData
        {
            ToolName = SessionTool,
            Output = SessionResult(agentId, threadId, final ?? JsonSerializer.SerializeToElement(new { })),
            ProviderExecuted = true,
            ProviderMetadata = Scoped(allMetadata)
        }, allMetadata);

        var timestamp = DateTimeOffset.UtcNow;
        yield return Event("finish", String(final ?? default, "id") ?? threadId, new AIFinishEventData
        {
            FinishReason = "stop",
            Model = request.Model,
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            Response = final?.Clone(),
            TotalTokens = UsageCount(usage, "total_tokens", "totalTokens"),
            InputTokens = UsageCount(usage, "prompt_tokens", "input_tokens", "inputTokens"),
            OutputTokens = UsageCount(usage, "completion_tokens", "output_tokens", "outputTokens"),
            MessageMetadata = AIFinishMessageMetadata.Create(request.Model ?? threadId, timestamp,
                additionalProperties: new Dictionary<string, object?> { ["sippulseai"] = allMetadata["sippulseai"] })
        }, allMetadata);
    }

    private async Task<JsonElement> CreateThread(string agentId, JsonElement options, string? instructions,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in options.EnumerateObject())
            if (property.Name is not ("thread_id" or "run"))
                body[property.Name] = property.Value.Clone();
        body["agent_id"] = JsonSerializer.SerializeToElement(agentId);
        if (!body.ContainsKey("additional_instructions") && !string.IsNullOrWhiteSpace(instructions))
            body["additional_instructions"] = JsonSerializer.SerializeToElement(instructions);
        return await SendJson(HttpMethod.Post, "threads", body, cancellationToken);
    }

    private static object RunPayload(JsonElement options, JsonElement message)
    {
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var run = Property(options, "run");
        if (run.ValueKind == JsonValueKind.Object)
            foreach (var property in run.EnumerateObject())
                body[property.Name] = property.Value.Clone();
        body["role"] = Property(message, "role").Clone();
        body["content"] = Property(message, "content").Clone();
        foreach (var name in new[] { "name", "tool_call_id" })
            if (Property(message, name) is { ValueKind: not JsonValueKind.Undefined } value)
                body[name] = value.Clone();
        return body;
    }

    private (string AgentId, JsonElement Options, string? ThreadId, JsonElement Message) BuildRequest(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agentId = request.Model?.StartsWith("sippulseai/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["sippulseai/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(agentId) || !agentId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new ArgumentException("Expected SippulseAI model 'sippulseai/{agentId}'.", nameof(request));
        object? native = null;
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var configured) == true)
            native = configured;
        var options = native is null
            ? JsonSerializer.SerializeToElement(new { }, JsonOptions)
            : native is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(native, JsonOptions);
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SippulseAI provider metadata must be a JSON object.", nameof(request));
        var configuredAgent = Property(options, "agent_id");
        if (configuredAgent.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String)
            || configuredAgent.ValueKind == JsonValueKind.String && configuredAgent.GetString() != agentId)
            throw new ArgumentException("SippulseAI metadata agent_id must match the selected model.", nameof(request));
        if (Property(options, "run") is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Object) })
            throw new ArgumentException("SippulseAI run metadata must be a JSON object.", nameof(request));

        var configuredThread = Property(options, "thread_id");
        if (configuredThread.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String)
            || configuredThread.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(configuredThread.GetString()))
            throw new ArgumentException("SippulseAI thread_id must be a non-empty string.", nameof(request));
        var threadId = String(options, "thread_id") ?? FindThreadId(request, agentId);
        var text = LatestUserText(request);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("SippulseAI requires a non-empty user message.", nameof(request));
        var message = JsonSerializer.SerializeToElement(new { role = "user", content = text }, JsonOptions);
        return (agentId, options, threadId, message);
    }

    private async Task<JsonElement> SendJson(HttpMethod method, string path, object payload, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(method, path);
        message.Content = JsonContent(payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static StringContent JsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"SippulseAI request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
    }

    private static string ResponseText(JsonElement raw)
    {
        var choices = Property(raw, "choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new JsonException("SippulseAI run response requires choices[0].message.content.");
        var content = Property(Property(choices[0], "message"), "content");
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
            return string.Join("", content.EnumerateArray().Select(part => String(part, "text")).Where(text => text is not null));
        throw new JsonException("SippulseAI run response requires choices[0].message.content.");
    }

    private static string LatestUserText(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text)) return request.Input.Text!;
        var user = (request.Input?.Items ?? [])
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", user?.Content?.OfType<AITextContentPart>().Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);
    }

    private static string? FindThreadId(AIRequest request, string agentId)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
            foreach (var tool in (item.Content?.OfType<AIToolCallContentPart>() ?? []).Reverse())
            {
                if (tool.ProviderExecuted != true || tool.ToolName != SessionTool) continue;
                var value = JsonSerializer.SerializeToElement(tool.Output ?? tool.Input, JsonOptions);
                var structured = Property(value, "structuredContent");
                var receipt = structured.ValueKind == JsonValueKind.Object ? structured : value;
                var receiptAgent = String(receipt, "agentId");
                if (receiptAgent != agentId)
                    throw new ArgumentException("SippulseAI session belongs to a different agent. Start a new conversation.", nameof(request));
                return RequiredString(receipt, "threadId");
            }
        return null;
    }

    private static Dictionary<string, object?> Metadata(string agentId, string threadId, JsonElement raw,
        IEnumerable<JsonElement>? events = null, JsonElement? threadResponse = null)
        => new()
        {
            ["sippulseai"] = JsonSerializer.SerializeToElement(new
            {
                agent_id = agentId,
                thread_id = threadId,
                thread = threadResponse?.Clone(),
                raw = raw.Clone(),
                events = events?.Select(evt => evt.Clone()).ToArray()
            }, JsonOptions)
        };

    private static AIToolCallContentPart SessionPart(string agentId, string threadId, JsonElement raw)
        => new()
        {
            Type = "tool-call",
            ToolName = SessionTool,
            ToolCallId = $"sippulseai-session-{Guid.NewGuid():N}",
            Title = "SippulseAI session",
            ProviderExecuted = true,
            State = "output-available",
            Input = new { agentId, threadId },
            Output = SessionResult(agentId, threadId, raw)
        };

    private static CallToolResult SessionResult(string agentId, string threadId, JsonElement raw)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                type = SessionTool, agentId, threadId, raw = raw.Clone()
            }, JsonOptions)
        };

    private static Dictionary<string, Dictionary<string, object>> Scoped(Dictionary<string, object?> metadata)
        => new()
        {
            ["sippulseai"] = new Dictionary<string, object> { ["session"] = metadata["sippulseai"]! }
        };

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow }
        };

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static string? String(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static string RequiredString(JsonElement value, string name)
        => !string.IsNullOrWhiteSpace(String(value, name)) ? String(value, name)!
            : throw new JsonException($"SippulseAI response requires '{name}'.");

    private static int? UsageCount(JsonElement? usage, params string[] names)
    {
        if (usage is not { ValueKind: JsonValueKind.Object } value) return null;
        foreach (var name in names)
            if (Property(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var count))
                return count;
        return null;
    }
}
