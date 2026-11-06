using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Buildprint;

public sealed partial class BuildprintProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Buildprint API key.");
        if (request.Tools?.Count > 0) throw new NotSupportedException("Buildprint does not execute local function tools.");
        var route = request.Model?.StartsWith("buildprint/", StringComparison.OrdinalIgnoreCase) == true
            ? request.Model["buildprint/".Length..] : request.Model;
        if (route is not ("agents/start" or "agents/list"))
            throw new ArgumentException("Buildprint requires model buildprint/agents/start or buildprint/agents/list.");

        var body = new Dictionary<string, JsonElement>();
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var native) == true && native is not null)
        {
            var options = JsonSerializer.SerializeToElement(native, JsonSerializerOptions.Web);
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Buildprint provider metadata must be a JSON object.");
            foreach (var property in options.EnumerateObject()) body[property.Name] = property.Value.Clone();
        }

        var start = route == "agents/start";
        if (start)
        {
            if (!body.TryGetValue("appId", out var appId) || appId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(appId.GetString()))
                throw new ArgumentException("Buildprint start requires provider metadata appId.");
            if (!body.ContainsKey("prompt"))
            {
                var prompt = request.Input?.Items?.AsEnumerable().Reverse()
                    .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                    .Select(item => string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []))
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? request.Input?.Text;
                if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Buildprint start requires a user prompt.");
                body["prompt"] = JsonSerializer.SerializeToElement(prompt);
            }
        }
        var query = start ? "" : string.Join("&", body.Where(property => property.Value.ValueKind != JsonValueKind.Null).Select(property =>
            Uri.EscapeDataString(property.Key) + "=" + Uri.EscapeDataString(QueryValue(property.Value))));
        using var http = new HttpRequestMessage(start ? HttpMethod.Post : HttpMethod.Get,
            "https://api.buildprint.ai/api/v1/agents" + (query.Length > 0 ? "?" + query : ""));
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (start) http.Content = JsonContent.Create(body);
        using var response = await _client.SendAsync(http, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Buildprint HTTP {(int)response.StatusCode}: {text}", null, response.StatusCode);
        using var document = JsonDocument.Parse(text);
        var raw = document.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
            throw new JsonException("Buildprint response must be a JSON object.");
        if (start && (!raw.TryGetProperty("agentId", out var agentId) || agentId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(agentId.GetString())))
            throw new JsonException("Buildprint start response is missing agentId.");
        var metadata = new Dictionary<string, object?> { [GetIdentifier()] = raw };
        var toolName = start ? "buildprint_start_agent" : "buildprint_list_agents";
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"buildprint/{route}", Status = "completed", Metadata = metadata,
            Output = new AIOutput
            {
                Metadata = metadata,
                Items = [new AIOutputItem
                {
                    Role = "assistant", Metadata = metadata,
                    Content =
                    [
                        new AIToolCallContentPart
                        {
                            Type = "tool-call", ToolCallId = $"{toolName}-{Guid.NewGuid():N}", ToolName = toolName,
                            ProviderExecuted = true, Input = body, Output = new CallToolResult { StructuredContent = raw },
                            State = "output-available", Metadata = metadata
                        },
                        new AITextContentPart { Type = "text", Text = raw.GetRawText(), Metadata = metadata }
                    ]
                }]
            }
        };
    }

    private static string QueryValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => value.GetRawText(),
        _ => throw new ArgumentException("Buildprint list query options must be scalar JSON values.")
    };

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Buildprint returns a JSON operation result, not a token stream.
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var metadata = response.Metadata!;
        var loose = new Dictionary<string, object> { [GetIdentifier()] = metadata[GetIdentifier()]! };
        var scoped = new Dictionary<string, Dictionary<string, object>>
        {
            [GetIdentifier()] = ((JsonElement)metadata[GetIdentifier()]!).EnumerateObject()
                .ToDictionary(property => property.Name, property => (object)property.Value.Clone())
        };
        foreach (var part in response.Output!.Items![0].Content!)
        {
            if (part is AIToolCallContentPart tool)
            {
                yield return Event("tool-input-available", tool.ToolCallId,
                    new AIToolInputAvailableEventData { ToolName = tool.ToolName, Input = tool.Input!, ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
                yield return Event("tool-output-available", tool.ToolCallId,
                    new AIToolOutputAvailableEventData { ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
            }
            if (part is AITextContentPart text)
            {
                yield return Event("text-start", response.Model, new AITextStartEventData { ProviderMetadata = loose }, metadata);
                yield return Event("text-delta", response.Model, new AITextDeltaEventData { Delta = text.Text, ProviderMetadata = loose }, metadata);
                yield return Event("text-end", response.Model, new AITextEndEventData { ProviderMetadata = loose }, metadata);
            }
        }
        yield return Event("finish", response.Model, new AIFinishEventData
        {
            Model = response.Model, FinishReason = "stop",
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, DateTimeOffset.UtcNow, null, additionalProperties: metadata)
        }, metadata);
    }

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata }
    };
}
