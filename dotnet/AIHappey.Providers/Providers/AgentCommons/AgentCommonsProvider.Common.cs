using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AgentCommons;

public partial class AgentCommonsProvider
{
    private const string SessionTool = "agentcommons_session";
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static string? String(JsonElement value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    private static string RequiredString(JsonElement value, string name)
        => !string.IsNullOrWhiteSpace(String(value, name)) ? String(value, name)!
            : throw new JsonException($"AgentCommons response requires '{name}'.");

    private static JsonElement Data(JsonElement raw)
        => Property(raw, "data") is { ValueKind: not JsonValueKind.Undefined } data ? data : raw;

    private static void EnsureNoError(JsonElement raw)
    {
        if (Property(raw, "error").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            || String(raw, "type") is "error" or "failed" or "cancelled")
            throw new InvalidOperationException($"AgentCommons run failed: {raw.GetRawText()}");
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"AgentCommons request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}",
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

    private Dictionary<string, JsonElement> BuildPayload(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = request.Model?.StartsWith("agentcommons/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["agentcommons/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(agent) || !agent.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new ArgumentException("Expected AgentCommons model 'agentcommons/{agentId}'.", nameof(request));
        if (request.Tools?.Count > 0 || request.ToolChoice is not null || request.ResponseFormat is not null
            || request.TopP is not null || request.Temperature is not (null or 1))
            throw new NotSupportedException("AgentCommons uses the configured agent's tools and generation settings. Use native provider metadata for run options.");

        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var options) == true && options is not null)
        {
            var native = JsonSerializer.SerializeToElement(options, JsonOptions);
            if (native.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("AgentCommons provider metadata must be a JSON object.", nameof(request));
            foreach (var property in native.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        if (body.TryGetValue("agentId", out var selected)
            && (selected.ValueKind != JsonValueKind.String || selected.GetString() != agent))
            throw new ArgumentException("AgentCommons metadata agentId must match the selected model.", nameof(request));
        body["agentId"] = JsonSerializer.SerializeToElement(agent);
        if (body.TryGetValue("sessionId", out var session))
        {
            if (session.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(session.GetString()))
                throw new ArgumentException("AgentCommons sessionId must be a non-empty string.", nameof(request));
        }
        else if (FindSession(request) is { } receipt)
        {
            if (RequiredString(receipt, "agentId") != agent)
                throw new ArgumentException("AgentCommons session belongs to a different agent. Start a new conversation.", nameof(request));
            body["sessionId"] = JsonSerializer.SerializeToElement(RequiredString(receipt, "sessionId"));
        }

        if (!body.ContainsKey("cliContext") && !string.IsNullOrWhiteSpace(request.Instructions))
            body["cliContext"] = JsonSerializer.SerializeToElement(request.Instructions);
        if (!body.ContainsKey("messages"))
        {
            var items = request.Input?.Items ?? [];
            // A continued session already owns the transcript; only submit the new user turn.
            if (body.ContainsKey("sessionId"))
                items = items.LastOrDefault(item => item.Role == "user") is { } latest ? [latest] : [];
            var messages = new List<object>();
            foreach (var item in items)
            {
                if (item.Role is not ("user" or "assistant" or "system" or "developer")) continue;
                var content = item.Content ?? [];
                if (content.Any(part => part is not (AITextContentPart or AIToolCallContentPart)))
                    throw new NotSupportedException("AgentCommons file inputs require uploaded file IDs in native attachments metadata.");
                var text = string.Join("\n", content.OfType<AITextContentPart>().Select(part => part.Text));
                if (!string.IsNullOrWhiteSpace(text)) messages.Add(new { role = item.Role, content = text });
            }
            if (messages.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
                messages.Add(new { role = "user", content = request.Input.Text });
            body["messages"] = JsonSerializer.SerializeToElement(messages, JsonOptions);
        }
        if (body["messages"].ValueKind != JsonValueKind.Array || body["messages"].GetArrayLength() == 0)
            throw new ArgumentException("AgentCommons requires non-empty messages or a user prompt.", nameof(request));
        return body;
    }

    private static JsonElement? FindSession(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
            foreach (var tool in (item.Content?.OfType<AIToolCallContentPart>() ?? []).Reverse())
            {
                if (tool.ProviderExecuted != true || tool.Output is null && tool.Input is null
                    || !(tool.ToolName == SessionTool || tool.Type == $"tool-{SessionTool}"
                        || tool.ToolCallId.StartsWith("agentcommons-session-", StringComparison.Ordinal))) continue;
                var output = JsonSerializer.SerializeToElement(tool.Output ?? tool.Input, JsonOptions);
                if (output.ValueKind == JsonValueKind.String)
                    output = JsonSerializer.Deserialize<JsonElement>(output.GetString()!);
                var receipt = Property(output, "structuredContent") is { ValueKind: JsonValueKind.Object } structured
                    ? structured : output;
                RequiredString(receipt, "agentId");
                RequiredString(receipt, "sessionId");
                return receipt;
            }
        return null;
    }

    private static Dictionary<string, object?> Metadata(string agent, string? session, JsonElement raw)
        => new()
        {
            ["agentcommons"] = JsonSerializer.SerializeToElement(new { agentId = agent, sessionId = session, raw }, JsonOptions)
        };

    private static Dictionary<string, object> Flat(Dictionary<string, object?> metadata)
        => new() { ["agentcommons"] = metadata["agentcommons"]! };

    private static Dictionary<string, Dictionary<string, object>> Scoped(Dictionary<string, object?> metadata)
        => new()
        {
            ["agentcommons"] = JsonSerializer.SerializeToElement(metadata["agentcommons"], JsonOptions)
                .EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone())
        };

    private static CallToolResult SessionResult(string agent, string session, JsonElement raw)
        => new()
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new { type = SessionTool, agentId = agent, sessionId = session, raw }, JsonOptions)
        };

    private static AIToolCallContentPart SessionPart(string agent, string session, JsonElement raw)
        => new()
        {
            Type = "tool-call", ToolName = SessionTool, ToolCallId = $"agentcommons-session-{Guid.NewGuid():N}",
            Title = "AgentCommons session", ProviderExecuted = true, State = "output-available",
            Input = new { agentId = agent, sessionId = session }, Output = SessionResult(agent, session, raw),
            Metadata = new Dictionary<string, object?>(Metadata(agent, session, raw))
            {
                ["messages.provider.call.metadata"] = new Dictionary<string, object>
                {
                    ["agentcommons"] = new { type = "server_tool_use" }
                }
            }
        };

    private static AIUsage? Usage(JsonElement data)
    {
        var usage = Property(data, "usage");
        if (usage.ValueKind != JsonValueKind.Object) return null;
        int? Count(params string[] names)
        {
            foreach (var name in names)
                if (Property(usage, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var count))
                    return count;
            return null;
        }
        var input = Count("inputTokens", "input_tokens", "promptTokens", "prompt_tokens");
        var output = Count("outputTokens", "output_tokens", "completionTokens", "completion_tokens");
        return new AIUsage
        {
            InputTokens = input, OutputTokens = output,
            TotalTokens = Count("totalTokens", "total_tokens") ?? (input.HasValue && output.HasValue ? input + output : null),
            AdditionalProperties = new() { ["agentcommons"] = usage.Clone() }
        };
    }

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow }
        };
}
