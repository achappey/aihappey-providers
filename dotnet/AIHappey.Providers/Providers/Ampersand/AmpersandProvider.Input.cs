using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Ampersand;

public sealed partial class AmpersandProvider
{
    private const string SessionTool = "ampersand_session";

    private static Dictionary<string, JsonElement> Options(AIRequest request)
    {
        var result = new Dictionary<string, JsonElement>();
        if (request.Metadata?.TryGetValue("ampersand", out var native) != true || native is null) return result;
        var raw = Element(native);
        if (raw.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Ampersand provider metadata must be an object.");
        foreach (var property in raw.EnumerateObject()) result[property.Name] = property.Value.Clone();
        if (result.Remove("body", out var body))
        {
            if (body.ValueKind != JsonValueKind.Object) throw new ArgumentException("Ampersand body must be an object.");
            foreach (var property in body.EnumerateObject()) result[property.Name] = property.Value.Clone();
        }
        return result;
    }

    private static string? TakeString(Dictionary<string, JsonElement> options, string name)
    {
        if (!options.Remove(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : throw new ArgumentException($"Ampersand {name} must be a nonempty string.");
    }

    private static JsonElement? Receipt(AIRequest request)
    {
        foreach (var tool in request.Input?.Items?.SelectMany(item => item.Content ?? [])
            .OfType<AIToolCallContentPart>().Reverse() ?? [])
        {
            if (tool.ToolName != SessionTool || tool.ProviderExecuted != true) continue;
            var raw = Element(tool.Output ?? tool.Input);
            if (raw.ValueKind == JsonValueKind.String) raw = JsonSerializer.Deserialize<JsonElement>(raw.GetString()!);
            if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("structuredContent", out var structured)) raw = structured;
            if (String(raw, "session_id") is not null) return raw;
        }
        return null;
    }

    private static string CallId(string session, JsonElement action)
        => "ampersand_" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { session, Required(action, "turn_id"), Required(action, "call_id") }))))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static List<object> Messages(AIRequest request, bool continuing)
    {
        var result = new List<object>();
        var items = request.Input?.Items ?? [];
        var users = continuing ? items.TakeLast(1).Where(item => item.Role == "user") : items.Where(item => item.Role == "user");
        foreach (var item in users)
        {
            var content = new List<object>();
            foreach (var part in item.Content ?? [])
            {
                if (part is AITextContentPart text) content.Add(new { type = "input_text", text = text.Text });
                else if (part is AIToolCallContentPart) continue;
                else throw new NotSupportedException("Ampersand non-text input requires native input provider metadata.");
            }
            if (content.Count > 0) result.Add(new { type = "message", role = "user", content });
        }
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
            result.Add(new { type = "message", role = "user", content = new[] { new { type = "input_text", text = request.Input.Text } } });
        return result;
    }

    private static void ConfigureAgent(AIRequest request, string model, Dictionary<string, JsonElement> body)
    {
        var agent = new Dictionary<string, JsonElement>();
        if (body.TryGetValue("agent", out var raw))
        {
            if (raw.ValueKind != JsonValueKind.Object) throw new ArgumentException("Ampersand agent must be an object.");
            foreach (var property in raw.EnumerateObject()) agent[property.Name] = property.Value.Clone();
        }
        if (agent.TryGetValue("model", out var selected) && selected.GetString() != model)
            throw new ArgumentException("Ampersand agent.model must match the selected model.");
        agent["model"] = Element(model);
        var instructions = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) instructions.Add(request.Instructions);
        foreach (var item in request.Input?.Items?.Where(item => item.Role is "system" or "developer") ?? [])
            foreach (var part in item.Content ?? [])
                if (part is AITextContentPart text) instructions.Add(text.Text);
                else throw new NotSupportedException("Ampersand instructions must be text.");
        if (!agent.ContainsKey("instructions") && instructions.Count > 0) agent["instructions"] = Element(string.Join("\n\n", instructions));
        if (!agent.ContainsKey("tools") && request.Tools is { Count: > 0 })
            agent["tools"] = Element(request.Tools.Select(tool => new
            {
                type = "function", name = tool.Name, description = tool.Description,
                parameters = tool.InputSchema ?? new { type = "object", properties = new { } }
            }));
        if (!agent.ContainsKey("text") && (request.ResponseFormat is not null || request.Verbosity is not null))
        {
            var text = new Dictionary<string, object>();
            if (request.ResponseFormat is not null)
            {
                var format = Element(request.ResponseFormat);
                if (String(format, "type") == "json_schema" && format.TryGetProperty("json_schema", out var schema))
                {
                    var fields = schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                    fields["type"] = Element("json_schema");
                    format = Element(fields);
                }
                text["format"] = format;
            }
            if (request.Verbosity is not null) text["verbosity"] = request.Verbosity;
            agent["text"] = Element(text);
        }
        body["agent"] = Element(agent);
        if (!body.ContainsKey("environment")) body["environment"] = Element(new { type = "none" });
    }

    private static List<JsonElement> InputEvents(AIRequest request, string sessionId, JsonElement session,
        Dictionary<string, JsonElement> body)
    {
        if (body.Remove("events", out var native))
        {
            if (body.ContainsKey("input")) throw new ArgumentException("Ampersand events and input cannot be combined.");
            if (native.ValueKind != JsonValueKind.Array || native.GetArrayLength() is < 1 or > 256)
                throw new ArgumentException("Ampersand events must contain 1-256 events.");
            return native.EnumerateArray().Select(evt => evt.Clone()).ToList();
        }
        var events = new List<JsonElement>();
        var parts = request.Input?.Items?.SelectMany(item => item.Content ?? [])
            .OfType<AIToolCallContentPart>().Where(tool => tool.ProviderExecuted != true && tool.Output is not null).ToList() ?? [];
        foreach (var action in Array(session, "required_actions"))
        {
            if (String(action, "type") != "function_call")
                throw new NotSupportedException($"Unsupported Ampersand required action: {action.GetRawText()}");
            var tool = parts.LastOrDefault(part => part.ToolCallId == CallId(sessionId, action));
            if (tool is null) continue;
            var failed = tool.Output is CallToolResult { IsError: true };
            if (tool.Output is JsonElement output && output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True) failed = true;
            var value = tool.Output is string text ? text : JsonSerializer.Serialize(tool.Output, JsonSerializerOptions.Web);
            var result = new Dictionary<string, object?>
            {
                ["type"] = "agent.session.input.tool_result", ["turn_id"] = Required(action, "turn_id"),
                ["call_id"] = Required(action, "call_id"), ["success"] = !failed
            };
            result[failed ? "error" : "output"] = value;
            events.Add(Element(result));
        }
        if (body.Remove("input", out var input))
        {
            if (input.ValueKind == JsonValueKind.String)
                input = Element(new[] { new { role = "user", content = new[] { new { type = "input_text", text = input.GetString() } } } });
            if (input.ValueKind != JsonValueKind.Array) throw new ArgumentException("Ampersand input must be a string or message array.");
            events.Add(Element(new { type = "agent.session.input.message", input }));
        }
        else
        {
            var messages = Messages(request, continuing: true);
            if (messages.Count > 0) events.Add(Element(new { type = "agent.session.input.message", input = messages }));
        }
        if (events.Count == 0) throw new ArgumentException("Ampersand continuation requires new input, matching tool results or native events.");
        return events;
    }
}
