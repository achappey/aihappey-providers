using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Allternit;

public sealed partial class AllternitProvider
{
    private sealed record Turn(string AgentId, string? ConversationId, Dictionary<string, JsonElement> Body, JsonElement? Metadata);

    private static Turn Prepare(AIRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tools?.Count > 0)
            throw new NotSupportedException("Allternit agents execute their configured tools, not client tools.");
        var agentId = request.Model?.StartsWith("allternit/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["allternit/".Length..] : request.Model;
        Segment(agentId ?? "");
        var body = new Dictionary<string, JsonElement>();
        if (request.Metadata?.TryGetValue("allternit", out var native) == true && native is not null)
        {
            var options = JsonSerializer.SerializeToElement(native, JsonSerializerOptions.Web);
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Allternit provider metadata must be a JSON object.");
            foreach (var property in options.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        string? conversationId = null;
        if (body.Remove("conversation_id", out var conversation))
        {
            if (conversation.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Allternit conversation_id must be a string.");
            conversationId = Segment(conversation.GetString()!);
        }
        JsonElement? metadata = body.Remove("metadata", out var value) ? value : null;
        if (metadata is { ValueKind: not JsonValueKind.Object })
            throw new ArgumentException("Allternit conversation metadata must be a JSON object.");

        if (!body.ContainsKey("content"))
        {
            var latest = request.Input?.Items?.LastOrDefault();
            if (latest is not null && !string.Equals(latest.Role, "user", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Allternit requires a new user message as the latest input.");
            if (latest?.Content?.Any(part => part is not AITextContentPart) == true)
                throw new NotSupportedException("Allternit conversations accept text only.");
            body["content"] = JsonSerializer.SerializeToElement(latest is null ? request.Input?.Text
                : string.Join("\n", latest.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []));
        }
        var content = body["content"];
        if (content.ValueKind != JsonValueKind.String || content.GetString() is not { Length: >= 1 and <= 16000 })
            throw new ArgumentException("Allternit content must be a string of 1-16,000 characters.");
        body["stream"] = JsonSerializer.SerializeToElement(stream);
        return new Turn(agentId!, conversationId, body, metadata);
    }

    private async Task<string> Conversation(Turn turn, CancellationToken ct)
    {
        if (turn.ConversationId is not null)
        {
            var existing = await SendJson(HttpMethod.Get, $"conversations/{turn.ConversationId}", null, ct);
            if (RequiredString(existing, "id") != turn.ConversationId || RequiredString(existing, "agent_id") != turn.AgentId)
                throw new ArgumentException("Allternit conversation does not belong to the selected agent.");
            return turn.ConversationId;
        }
        var body = new Dictionary<string, JsonElement>();
        if (turn.Metadata is { } metadata) body["metadata"] = metadata;
        var created = await SendJson(HttpMethod.Post, $"agents/{turn.AgentId}/conversations", body, ct);
        return Segment(RequiredString(created, "id"));
    }

    private static string CompletedText(JsonElement raw, string conversationId)
    {
        if (RequiredString(raw, "conversation_id") != conversationId)
            throw new JsonException("Allternit reply has a different conversation_id.");
        var status = RequiredString(raw, "status");
        if (status == "failed")
            throw new InvalidOperationException($"Allternit turn failed: {raw.GetRawText()}");
        if (status != "completed")
            throw new JsonException($"Allternit reply has unexpected status: {status}.");
        RequiredString(raw, "id");
        if (!raw.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new JsonException("Allternit reply is missing content.");
        return content.GetString()!;
    }

    private static AIUsage? Usage(JsonElement raw)
    {
        int? TokenCount(string name)
        {
            if (!raw.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
                throw new JsonException($"Allternit {name} must be a non-negative integer.");
            return count;
        }
        var input = TokenCount("tokens_in");
        var output = TokenCount("tokens_out");
        return input is null && output is null ? null : new AIUsage
        {
            InputTokens = input, OutputTokens = output,
            TotalTokens = input is { } i && output is { } o ? checked(i + o) : null
        };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var turn = Prepare(request, false);
        var conversationId = await Conversation(turn, cancellationToken);
        var raw = await SendJson(HttpMethod.Post, $"conversations/{conversationId}/messages", turn.Body, cancellationToken);
        var text = CompletedText(raw, conversationId);
        var metadata = Metadata(conversationId, raw);
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"allternit/{turn.AgentId}", Status = "completed",
            Metadata = metadata, Usage = Usage(raw),
            Output = new AIOutput
            {
                Metadata = metadata,
                Items = [new AIOutputItem
                {
                    Role = "assistant", Metadata = metadata,
                    Content = [new AITextContentPart { Type = "text", Text = text, Metadata = metadata }]
                }]
            }
        };
    }
}
