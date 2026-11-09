using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.DataForSEO;

public partial class DataForSEOProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (endpoint, model) = await ResolveModelAsync(request.Model, cancellationToken);
        var payload = BuildPayload(request, model);
        using var http = new HttpRequestMessage(HttpMethod.Post, $"v3/ai_optimization/{endpoint}/llm_responses/live");
        ApplyAuthHeader(http);
        http.Content = JsonContent.Create(new[] { payload });
        using var response = await _client.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"DataForSEO live request failed ({(int)response.StatusCode}): {body}", null, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        EnsureSuccess(root, "response");
        if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() != 1)
            throw new InvalidOperationException("DataForSEO live response must contain exactly one task.");
        var task = tasks[0];
        EnsureSuccess(task, "task");
        if (!task.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != 1)
            throw new InvalidOperationException("DataForSEO live task must contain exactly one result.");
        var result = results[0];
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("DataForSEO live result is missing its items array.");
        return MapResponse(request, endpoint, model, root, task, result, items);
    }

    private async Task<(string Endpoint, string Model)> ResolveModelAsync(string? value, CancellationToken ct)
    {
        var model = value?.Trim() ?? "";
        var prefix = GetIdentifier() + "/";
        if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            model = model[prefix.Length..];
        var slash = model.IndexOf('/');
        if (slash > 0 && Endpoints.Contains(model[..slash], StringComparer.OrdinalIgnoreCase))
        {
            var endpoint = Endpoints.Single(e => e.Equals(model[..slash], StringComparison.OrdinalIgnoreCase));
            var name = model[(slash + 1)..];
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("DataForSEO model name is required.");
            return (endpoint, name);
        }
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("DataForSEO model name is required.");
        var match = (await ListModels(ct)).FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
        if (match is null || !Endpoints.Contains(match.OwnedBy, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"DataForSEO model '{model}' was not found in the live model catalog. Use an endpoint-qualified model ID to select a platform explicitly.");
        return (match.OwnedBy!, model);
    }

    private Dictionary<string, object?> BuildPayload(AIRequest request, string model)
    {
        if (request.Tools is { Count: > 0 } || request.ToolChoice is not null || IsStructuredFormat(request.ResponseFormat))
            throw new NotSupportedException("DataForSEO live responses do not support client tools or structured output.");
        if (request.Metadata?.TryGetValue("responses.previous_response_id", out var previous) == true && previous is not null
            || request.Metadata?.TryGetValue("responses.conversation", out var conversation) == true && conversation is not null)
            throw new NotSupportedException("DataForSEO requires the complete conversation history; stored Responses conversations are not supported.");
        var payload = new Dictionary<string, object?>();
        if (request.Metadata?.TryGetValue(GetIdentifier(), out var options) == true && options is not null)
        {
            var raw = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web);
            if (raw.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("DataForSEO provider metadata must be an object.");
            var protocolType = request.Metadata.ContainsKey("messages.request.raw") ? typeof(MessagesRequest)
                : request.Metadata.ContainsKey("chatcompletions.request.raw") ? typeof(ChatCompletionOptions)
                : request.Metadata.ContainsKey("responses.additional_properties") ? typeof(ResponseRequest)
                : null;
            var protocolFields = protocolType?.GetProperties()
                .Select(p => p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).OfType<JsonPropertyNameAttribute>().FirstOrDefault()?.Name)
                .Where(name => name is not null).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var property in raw.EnumerateObject())
                if (!protocolFields.Contains(property.Name) || property.Name == "max_output_tokens")
                    payload[property.Name] = property.Value.Clone();
        }

        var instructions = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            instructions.Add(request.Instructions);
        var messages = new List<(string Role, string Text)>();
        if (request.Input?.Items is { Count: > 0 } inputItems)
        {
            foreach (var item in inputItems)
            {
                if (item.Type != "message" || item.Content?.Any(part => part is not AITextContentPart) == true)
                    throw new NotSupportedException("DataForSEO supports text messages only.");
                var text = string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>().Select(p => p.Text));
                switch (item.Role?.ToLowerInvariant())
                {
                    case "system":
                    case "developer":
                        if (messages.Count > 0)
                            throw new NotSupportedException("DataForSEO system messages must precede conversation turns.");
                        instructions.Add(text);
                        break;
                    case "user":
                        messages.Add(("user", text));
                        break;
                    case "assistant":
                        messages.Add(("ai", text));
                        break;
                    default:
                        throw new NotSupportedException($"DataForSEO does not support message role '{item.Role}'.");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            messages.Add(("user", request.Input.Text));

        if (messages.Count == 0)
        {
            if (!payload.TryGetValue("user_prompt", out var rawPrompt) || rawPrompt is not JsonElement { ValueKind: JsonValueKind.String } prompt
                || string.IsNullOrWhiteSpace(prompt.GetString()))
                throw new ArgumentException("DataForSEO requires a final non-empty user message or native user_prompt.");
        }
        else if (messages[^1].Role != "user" || string.IsNullOrWhiteSpace(messages[^1].Text))
            throw new ArgumentException("DataForSEO requires a final non-empty user message.");
        if (messages.Count - 1 > 10)
            throw new ArgumentException("DataForSEO supports at most 10 history messages.");
        if (messages.Any(m => m.Text.Length > 500) || string.Join("\n", instructions).Length > 500)
            throw new ArgumentException("DataForSEO prompts, history messages, and system messages must not exceed 500 characters.");

        if (messages.Count > 0)
            payload.TryAdd("user_prompt", messages[^1].Text);
        if (messages.Count > 1)
            payload.TryAdd("message_chain", messages.Take(messages.Count - 1).Select(m => new { role = m.Role, message = m.Text }).ToArray());
        if (instructions.Count > 0)
            payload.TryAdd("system_message", string.Join("\n", instructions));
        if (request.MaxOutputTokens is not null) payload.TryAdd("max_output_tokens", request.MaxOutputTokens);
        if (request.Temperature is not null) payload.TryAdd("temperature", request.Temperature);
        if (request.TopP is not null) payload.TryAdd("top_p", request.TopP);
        payload["model_name"] = model;
        return payload;
    }

    private static bool IsStructuredFormat(object? format)
    {
        if (format is null) return false;
        var value = JsonSerializer.SerializeToElement(format, JsonSerializerOptions.Web);
        return value.ValueKind != JsonValueKind.Object || String(value, "type") != "text";
    }

    private static void EnsureSuccess(JsonElement value, string context)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("status_code", out var code)
            || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var status))
            throw new InvalidOperationException($"DataForSEO {context} is missing status_code.");
        if (status != 20000)
            throw new InvalidOperationException($"DataForSEO {context} failed ({status}): {String(value, "status_message")}");
    }

    private AIResponse MapResponse(AIRequest request, string endpoint, string model, JsonElement root, JsonElement task, JsonElement result, JsonElement items)
    {
        var id = String(task, "id") ?? $"dataforseo_{Guid.NewGuid():N}";
        var actualModel = String(result, "model_name") ?? model;
        var scoped = new Dictionary<string, object?> { ["endpoint"] = endpoint, ["raw"] = root.Clone() };
        var metadata = new Dictionary<string, object?>
        {
            [GetIdentifier()] = scoped,
            ["responses.id"] = id,
            ["messages.response.id"] = id,
            ["chatcompletions.response.id"] = id,
            ["chatcompletions.response.raw"] = JsonSerializer.SerializeToElement(new { provider_metadata = new Dictionary<string, object?> { [GetIdentifier()] = scoped } })
        };
        var output = new List<AIOutputItem>();
        foreach (var item in items.EnumerateArray())
        {
            var type = String(item, "type");
            if (type is not ("message" or "reasoning") || !item.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
                continue;
            var content = new List<AIContentPart>();
            foreach (var section in sections.EnumerateArray())
            {
                var text = String(section, "text");
                if (text is null) continue;
                var sectionMetadata = new Dictionary<string, object?> { [GetIdentifier()] = section.Clone() };
                if (type == "message" && section.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Array)
                {
                    var citations = annotations.EnumerateArray()
                        .Where(a => String(a, "url") is { Length: > 0 } && Number(a, "start_index").HasValue && Number(a, "end_index").HasValue)
                        .Select(a => new { type = "url_citation", url = String(a, "url"), title = String(a, "title") ?? String(a, "url"), start_index = Number(a, "start_index"), end_index = Number(a, "end_index") });
                    sectionMetadata["responses.annotations"] = JsonSerializer.SerializeToElement(citations);
                }
                content.Add(type == "reasoning"
                    ? new AIReasoningContentPart { Type = "reasoning", Text = text, Metadata = sectionMetadata }
                    : new AITextContentPart { Type = "text", Text = text, Metadata = sectionMetadata });
            }
            if (content.Count > 0)
                output.Add(new AIOutputItem { Type = type, Role = "assistant", Content = content, Metadata = new() { [GetIdentifier()] = item.Clone() } });
        }
        var input = Number(result, "input_tokens");
        var outputTokens = Number(result, "output_tokens");
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"{GetIdentifier()}/{actualModel}",
            Status = "completed",
            Output = new AIOutput { Items = output },
            Usage = new AIUsage { InputTokens = input, OutputTokens = outputTokens, TotalTokens = input.HasValue && outputTokens.HasValue ? input + outputTokens : null, ReasoningTokens = Number(result, "reasoning_tokens") },
            Metadata = metadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        var id = response.Metadata?["responses.id"]?.ToString() ?? Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var usage = response.NormalizedUsage;
        var streamMetadata = new Dictionary<string, object?>(response.Metadata ?? [])
        {
            ["chatcompletions.stream.id"] = id,
            ["chatcompletions.stream.model"] = response.Model,
            ["chatcompletions.stream.created"] = timestamp.ToUnixTimeSeconds()
        };
        AIStreamEvent Event(string type, string eventId, object data)
        {
            var metadata = new Dictionary<string, object?>(streamMetadata);
            if (data is AIFinishEventData && usage is not null)
                metadata["chatcompletions.stream.usage"] = JsonSerializer.SerializeToElement(new
                {
                    prompt_tokens = usage.InputTokens,
                    completion_tokens = usage.OutputTokens,
                    total_tokens = usage.TotalTokens,
                    completion_tokens_details = new { reasoning_tokens = usage.ReasoningTokens }
                });
            return new AIStreamEvent
            {
                ProviderId = GetIdentifier(),
                Event = new AIEventEnvelope { Type = type, Id = eventId, Timestamp = timestamp, Data = data, Metadata = metadata },
                Metadata = metadata
            };
        }
        var index = 0;
        foreach (var item in response.Output?.Items ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var part in item.Content ?? [])
            {
                var itemId = $"{id}_{index++}";
                if (part is AITextContentPart text)
                {
                    yield return Event("text-start", itemId, new AITextStartEventData());
                    yield return Event("text-delta", itemId, new AITextDeltaEventData { Delta = text.Text });
                    yield return Event("text-end", itemId, new AITextEndEventData());
                    if (text.Metadata?.TryGetValue(GetIdentifier(), out var rawSection) == true
                        && rawSection is JsonElement section && section.TryGetProperty("annotations", out var annotations)
                        && annotations.ValueKind == JsonValueKind.Array)
                        foreach (var annotation in annotations.EnumerateArray())
                            if (String(annotation, "url") is { Length: > 0 } url)
                            {
                                var sourceId = $"{id}_{index++}";
                                yield return Event("source-url", sourceId, new AISourceUrlEventData
                                {
                                    SourceId = sourceId, Url = url, Title = String(annotation, "title"), Type = "url_citation",
                                    StartIndex = Number(annotation, "start_index"), EndIndex = Number(annotation, "end_index"),
                                    ProviderMetadata = new() { [GetIdentifier()] = new() { ["raw"] = annotation.Clone() } }
                                });
                            }
                }
                else if (part is AIReasoningContentPart reasoning && reasoning.Text is not null)
                {
                    yield return Event("reasoning-start", itemId, new AIReasoningStartEventData());
                    yield return Event("reasoning-delta", itemId, new AIReasoningDeltaEventData { Delta = reasoning.Text });
                    yield return Event("reasoning-end", itemId, new AIReasoningEndEventData());
                }
            }
        }
        yield return Event("finish", id, new AIFinishEventData
        {
            FinishReason = "stop",
            Model = response.Model,
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            InputTokens = usage?.InputTokens,
            OutputTokens = usage?.OutputTokens,
            TotalTokens = usage?.TotalTokens,
            MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, timestamp, usage: new
            {
                input_tokens = usage?.InputTokens, output_tokens = usage?.OutputTokens, total_tokens = usage?.TotalTokens,
                output_tokens_details = new { reasoning_tokens = usage?.ReasoningTokens }
            },
                inputTokens: usage?.InputTokens, outputTokens: usage?.OutputTokens, totalTokens: usage?.TotalTokens,
                temperature: request.Temperature, additionalProperties: new() { [GetIdentifier()] = response.Metadata?[GetIdentifier()] })
        });
    }

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static int? Number(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number) ? number : null;
}
