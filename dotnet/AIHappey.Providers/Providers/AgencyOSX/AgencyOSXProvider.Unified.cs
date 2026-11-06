using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AgencyOSX;

public sealed partial class AgencyOSXProvider
{
    private const string ChatTool = "agencyosx_chat";

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key();
        var agent = request.Model?.StartsWith("agencyosx/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["agencyosx/".Length..] : request.Model;
        if (string.IsNullOrWhiteSpace(agent) || agent.Contains('/'))
            throw new ArgumentException("AgencyOSX requires an agent ID as the model (agencyosx/{agentId}).");
        var message = request.Input?.Items?.AsEnumerable().Reverse()
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? request.Input?.Text;
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("AgencyOSX requires a latest user text message.");

        var body = new Dictionary<string, JsonElement>();
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var native) == true && native is not null)
        {
            var options = JsonSerializer.SerializeToElement(native, JsonSerializerOptions.Web);
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("AgencyOSX provider metadata must be a JSON object.");
            foreach (var property in options.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }
        if (!body.ContainsKey("chatId") && FindChatId(request, agent) is { } chatId)
            body["chatId"] = JsonSerializer.SerializeToElement(chatId);
        body["agentId"] = JsonSerializer.SerializeToElement(agent);
        body["message"] = JsonSerializer.SerializeToElement(message);

        var raw = await SendAsync(HttpMethod.Post, "chat", body, key, cancellationToken);
        var reply = String(raw, "response") ?? throw new JsonException("AgencyOSX chat response is missing response text.");
        var conversation = String(raw, "chatId");
        if (string.IsNullOrWhiteSpace(conversation))
            throw new JsonException("AgencyOSX chat response is missing chatId.");
        var metadata = new Dictionary<string, object?> { [GetIdentifier()] = raw };
        var model = agent.ToModelId(GetIdentifier());
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = model, Status = "completed", Metadata = metadata,
            Usage = raw.TryGetProperty("usage", out var usage) ? usage.Clone() : null,
            Output = new AIOutput
            {
                Items = [new AIOutputItem
                {
                    Role = "assistant", Metadata = metadata,
                    Content =
                    [
                        new AIToolCallContentPart
                        {
                            Type = "tool-call", ToolCallId = $"agencyosx-{conversation}-{Guid.NewGuid():N}",
                            ToolName = ChatTool, Title = "AgencyOSX conversation", ProviderExecuted = true,
                            Input = new { agentId = agent, message },
                            Output = new CallToolResult { StructuredContent = raw },
                            State = "output-available", Metadata = metadata
                        },
                        new AITextContentPart { Type = "text", Text = reply, Metadata = metadata }
                    ]
                }],
                Metadata = metadata
            }
        };
    }

    private static string? FindChatId(AIRequest request, string agent)
    {
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>().Reverse() ?? [])
            {
                if (tool.ToolName != ChatTool || tool.ProviderExecuted != true || tool.Output is null) continue;
                var output = JsonSerializer.SerializeToElement(tool.Output, JsonSerializerOptions.Web);
                if (output.TryGetProperty("structuredContent", out var structured)) output = structured;
                var chatId = String(output, "chatId");
                if (chatId is null) continue;
                if (output.TryGetProperty("agent", out var identity) && String(identity, "id") is { } previous && previous != agent)
                    throw new ArgumentException("AgencyOSX conversation belongs to a different agent. Start a new conversation when switching agents.");
                return chatId;
            }
        return null;
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The native API returns one JSON reply, not token deltas.
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var providerMetadata = new Dictionary<string, object> { [GetIdentifier()] = response.Metadata![GetIdentifier()]! };
        foreach (var part in response.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [])
        {
            if (part is AIToolCallContentPart tool)
            {
                yield return Event("tool-input-available", tool.ToolCallId,
                    new AIToolInputAvailableEventData { ToolName = ChatTool, Input = tool.Input!, ProviderExecuted = true }, response.Metadata);
                yield return Event("tool-output-available", tool.ToolCallId,
                    new AIToolOutputAvailableEventData { ToolName = ChatTool, Output = tool.Output!, ProviderExecuted = true }, response.Metadata);
            }
            if (part is AITextContentPart text)
            {
                yield return Event("text-start", response.Model, new AITextStartEventData { ProviderMetadata = providerMetadata }, response.Metadata);
                yield return Event("text-delta", response.Model, new AITextDeltaEventData { Delta = text.Text, ProviderMetadata = providerMetadata }, response.Metadata);
                yield return Event("text-end", response.Model, new AITextEndEventData { ProviderMetadata = providerMetadata }, response.Metadata);
            }
        }
        yield return Event("finish", response.Model, new AIFinishEventData
        {
            Model = response.Model, FinishReason = "stop",
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
