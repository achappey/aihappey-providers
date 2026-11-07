using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Athena;

public partial class AthenaProvider
{
    private static readonly JsonSerializerOptions JsonWeb = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = BuildPayload(request);
        var raw = await SendAsync(payload, cancellationToken);
        return MapResponse(request.Model ?? "general".ToModelId(GetIdentifier()), raw);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var id = request.Id ?? $"athena-{Guid.NewGuid():N}";
        var text = response.Output?.Items?.SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>().Select(part => part.Text).FirstOrDefault() ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        yield return StreamEvent(id + ":text", "text-start", new AITextStartEventData(), now);
        if (text.Length > 0)
            yield return StreamEvent(id + ":text", "text-delta", new AITextDeltaEventData { Delta = text }, now);
        yield return StreamEvent(id + ":text", "text-end", new AITextEndEventData(), now);

        var raw = response.Metadata?["athena.raw"];
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Metadata = response.Metadata,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = id,
                Timestamp = now,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = response.Model,
                    Response = raw is JsonElement json ? json : null,
                    CompletedAt = now.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(response.Model ?? GetIdentifier(), now,
                        additionalProperties: response.Metadata)
                }
            }
        };
    }

    private JsonElement BuildPayload(AIRequest request)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var providerOptions) == true && providerOptions is not null)
        {
            var options = providerOptions is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(providerOptions, JsonWeb);
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Athena provider metadata must be a JSON object.", nameof(request));
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        if (!payload.ContainsKey("config"))
            payload["config"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>(), JsonWeb);

        var messages = new List<JsonElement>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            messages.Add(JsonSerializer.SerializeToElement(new { role = "system", content = request.Instructions }, JsonWeb));

        foreach (var item in request.Input?.Items ?? [])
        {
            var parts = item.Content ?? [];
            object content = parts.All(part => part is AITextContentPart)
                ? string.Concat(parts.OfType<AITextContentPart>().Select(part => part.Text))
                : JsonSerializer.SerializeToElement(parts, JsonWeb);
            messages.Add(JsonSerializer.SerializeToElement(new
            {
                role = string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role,
                content
            }, JsonWeb));
        }

        if (messages.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text))
            messages.Add(JsonSerializer.SerializeToElement(new { role = "user", content = request.Input.Text }, JsonWeb));

        if (messages.Count > 0)
            payload["messages"] = JsonSerializer.SerializeToElement(messages, JsonWeb);
        else if (!payload.TryGetValue("messages", out var rawMessages) || rawMessages.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Athena requires messages in the request or provider metadata.", nameof(request));

        return JsonSerializer.SerializeToElement(payload, JsonWeb);
    }

    private async Task<JsonElement> SendAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Athena API key.");

        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v0/agents/general/invoke")
        {
            Content = new StringContent(payload.GetRawText(), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        message.Headers.Add("X-API-KEY", key);

        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Athena POST api/v0/agents/general/invoke failed ({(int)response.StatusCode}).",
                null, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private AIResponse MapResponse(string model, JsonElement raw)
    {
        var metadata = new Dictionary<string, object?> { ["athena.raw"] = raw.Clone() };
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("structured_output", out var structured)
            && structured.ValueKind != JsonValueKind.Null)
            metadata["athena.structured_output"] = structured.Clone();

        var content = new List<AIContentPart>();
        if (raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (!IsAssistantMessage(message) || !message.TryGetProperty("content", out var value))
                    continue;

                var text = ExtractText(value);
                if (text.Length > 0)
                    content.Add(new AITextContentPart { Type = "text", Text = text });
            }
        }

        if (content.Count == 0 && raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("structured_output", out var structuredOutput)
            && structuredOutput.ValueKind != JsonValueKind.Null)
        {
            content.Add(new AITextContentPart { Type = "text", Text = structuredOutput.GetRawText() });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = "completed",
            Output = content.Count == 0 ? null : new AIOutput
            {
                Items = [new AIOutputItem { Type = "message", Role = "assistant", Content = content, Metadata = metadata }],
                Metadata = metadata
            },
            Metadata = metadata
        };
    }

    private static bool IsAssistantMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return false;
        var role = message.TryGetProperty("role", out var roleValue) && roleValue.ValueKind == JsonValueKind.String
            ? roleValue.GetString()
            : message.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString()
                : null;
        return string.Equals(role, "ai", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        return string.Concat(content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            .Select(part => part.GetProperty("text").GetString()));
    }

    private AIStreamEvent StreamEvent(string id, string type, object data, DateTimeOffset timestamp)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data }
        };
}
