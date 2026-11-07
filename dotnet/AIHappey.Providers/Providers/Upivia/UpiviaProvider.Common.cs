using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Upivia;

public partial class UpiviaProvider
{
    private const string SessionTool = "upivia_session";
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private sealed class Turn(string agent, Dictionary<string, JsonElement> body)
    {
        public string Agent { get; } = agent;
        public string Model => $"upivia/{Agent}";
        public string ReceiptId { get; } = $"upivia-session-{Guid.NewGuid():N}";
        public Dictionary<string, JsonElement> Body { get; } = body;
        public string SessionId { get; set; } = "";
        public Dictionary<string, object?> Receipt { get; } = [];
        public List<JsonElement> Events { get; } = [];
        public List<AIContentPart> Content { get; } = [];
        public AIUsage? Usage { get; set; }
        public bool Waiting { get; set; }
        public Dictionary<string, object?> Metadata => new()
        {
            ["upivia"] = JsonSerializer.SerializeToElement(Receipt, JsonOptions)
        };
    }

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static string? String(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static string RequiredString(JsonElement value, string name)
        => !string.IsNullOrWhiteSpace(String(value, name)) ? String(value, name)!
            : throw new JsonException($"Upivia response requires '{name}'.");

    private static void EnsureNoError(JsonElement raw)
    {
        if (Property(raw, "error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            || String(raw, "status") is "failed" or "cancelled")
            throw new InvalidOperationException($"Upivia request failed: {raw.GetRawText()}");
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Upivia request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);
    }

    private async Task<JsonElement> SendJson(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var message = CreateRequest(method, path);
        if (payload is not null)
            message.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var raw = document.RootElement.Clone();
        EnsureNoError(raw);
        return raw;
    }

    private Turn CreateTurn(AIRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var agent = request.Model?.StartsWith("upivia/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["upivia/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(agent) || !agent.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new ArgumentException("Expected Upivia model 'upivia/{agentId}'.", nameof(request));
        if (request.Tools?.Count > 0 || request.ToolChoice is not null || request.ResponseFormat is not null
            || request.TopP is not null || request.Temperature is not (null or 1))
            throw new NotSupportedException("Upivia uses agent-configured tools and generation settings. Use native provider metadata for chat options.");

        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var options) == true && options is not null)
        {
            var native = JsonSerializer.SerializeToElement(options, JsonOptions);
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Upivia provider metadata must be a JSON object.", nameof(request));
            foreach (var property in native.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        var previous = FindSession(request);
        if (previous is { } receipt)
        {
            if (RequiredString(receipt, "agent_id") != agent)
                throw new ArgumentException("Upivia history belongs to a different agent. Start a new conversation.");
            var previousId = RequiredString(receipt, "sessionId");
            if (body.TryGetValue("sessionId", out var selected)
                && (selected.ValueKind != JsonValueKind.String || selected.GetString() != previousId))
                throw new ArgumentException("Conflicting Upivia session identities.");
            body["sessionId"] = JsonSerializer.SerializeToElement(previousId);
        }
        if (body.TryGetValue("sessionId", out var session)
            && (session.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(session.GetString())))
            throw new ArgumentException("Upivia sessionId must be a non-empty string.");
        if (!body.ContainsKey("sessionId") && request.Input?.Items?.Any(item => item.Role == "assistant") == true)
            throw new ArgumentException("Upivia history requires a session receipt or sessionId provider metadata.");
        if (body.ContainsKey("continue_from") && !body.ContainsKey("sessionId"))
            throw new ArgumentException("Upivia continue_from requires an existing sessionId.");
        if (!body.ContainsKey("instructions") && !string.IsNullOrWhiteSpace(request.Instructions))
            body["instructions"] = JsonSerializer.SerializeToElement(request.Instructions);
        if (!body.ContainsKey("message") && !body.ContainsKey("content") && !body.ContainsKey("continue_from"))
        {
            var latest = request.Input?.Items?.LastOrDefault();
            if (latest is not null && latest.Role != "user")
                throw new ArgumentException("Upivia requires a new user message or native continue_from metadata.");
            if (latest?.Content?.Any(part => part is not AITextContentPart) == true)
                throw new NotSupportedException("Upivia multimodal inputs require native content provider metadata.");
            // Upivia owns the transcript; historical user turns must not be replayed.
            var text = latest is null ? request.Input?.Text
                : string.Join("\n", latest.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Upivia requires a user message, native content or continue_from.");
            body["message"] = JsonSerializer.SerializeToElement(text);
        }
        return new Turn(agent, body);
    }

    private static JsonElement? FindSession(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
            foreach (var tool in (item.Content?.OfType<AIToolCallContentPart>() ?? []).Reverse())
            {
                if (tool.ProviderExecuted != true || tool.Output is null && tool.Input is null
                    || !(tool.ToolName == SessionTool || tool.Type == $"tool-{SessionTool}")) continue;
                var output = JsonSerializer.SerializeToElement(tool.Output ?? tool.Input, JsonOptions);
                if (output.ValueKind == JsonValueKind.String)
                    output = JsonSerializer.Deserialize<JsonElement>(output.GetString()!);
                return Property(output, "structuredContent") is { ValueKind: JsonValueKind.Object } structured
                    ? structured : output;
            }
        return null;
    }

    private static Dictionary<string, object> Flat(Dictionary<string, object?> metadata)
        => new() { ["upivia"] = metadata["upivia"]! };

    private static Dictionary<string, Dictionary<string, object>> Scoped(Dictionary<string, object?> metadata)
        => new()
        {
            ["upivia"] = JsonSerializer.SerializeToElement(metadata["upivia"], JsonOptions)
                .EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone())
        };

    private static CallToolResult SessionResult(Turn turn)
        => new() { Content = [], StructuredContent = JsonSerializer.SerializeToElement(turn.Receipt, JsonOptions) };

    private static AIToolCallContentPart SessionPart(Turn turn)
        => new()
        {
            Type = "tool-call", ToolName = SessionTool, ToolCallId = turn.ReceiptId,
            Title = "Upivia session", ProviderExecuted = true, State = "output-available",
            Input = new { agent_id = turn.Agent, sessionId = turn.SessionId }, Output = SessionResult(turn),
            Metadata = new Dictionary<string, object?>(turn.Metadata)
            {
                ["messages.provider.call.metadata"] = new Dictionary<string, object>
                {
                    ["upivia"] = new { type = "server_tool_use" }
                }
            }
        };

    private static AIUsage? Usage(JsonElement raw)
    {
        var usage = Property(raw, "usage") is { ValueKind: JsonValueKind.Object } nested ? nested : raw;
        int? Count(params string[] names)
        {
            foreach (var name in names)
                if (Property(usage, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var count))
                    return count;
            return null;
        }
        var input = Count("input_tokens", "prompt_tokens", "inputTokens", "promptTokens");
        var output = Count("output_tokens", "completion_tokens", "outputTokens", "completionTokens");
        var total = Count("total_tokens", "totalTokens") ?? (input.HasValue && output.HasValue ? input + output : null);
        if (input is null && output is null && total is null) return null;
        return new AIUsage
        {
            InputTokens = input, OutputTokens = output, TotalTokens = total,
            AdditionalProperties = new() { ["upivia"] = usage.Clone() }
        };
    }

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow }
        };
}
