using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Runtype;

public sealed partial class RuntypeProvider
{
    private static readonly string[] RunQueryParameters =
    [
        "agentId", "flowId", "conversationId", "surfaceId", "surfaceType", "recordId",
        "batchExecutionId", "parentExecutionId", "rootExecutionId", "includeChildren",
        "executionId", "source", "status", "traffic", "signal", "from", "to", "view",
        "overlay", "limit", "cursor", "includeCount"
    ];

    private async Task<JsonReply> ListAgentsPage(JsonObject options, CancellationToken ct)
    {
        var query = new List<string>();
        foreach (var name in new[] { "limit", "cursor", "includeCount", "search", "agentType", "managedInCode", "model", "view" })
            if (options[name] is JsonValue value)
                query.Add($"{name}={Uri.EscapeDataString(value.ToString())}");
        return await SendJson(HttpMethod.Get, "v1/agents" + (query.Count > 0 ? "?" + string.Join("&", query) : ""), null, ct);
    }

    private async Task<JsonReply> ListRunsPage(Route route, JsonObject options, CancellationToken ct)
    {
        var query = new List<string>();
        foreach (var name in RunQueryParameters)
        {
            if (name == "agentId" && route.AgentId is not null)
            {
                query.Add($"agentId={Uri.EscapeDataString(route.AgentId)}");
                continue;
            }

            if (options[name] is JsonArray values)
            {
                foreach (var value in values)
                    AddRunQueryValue(query, name, value);
            }
            else
            {
                AddRunQueryValue(query, name, options[name]);
            }
        }

        return await SendJson(HttpMethod.Get, "v1/runs" + (query.Count > 0 ? "?" + string.Join("&", query) : ""), null, ct);
    }

    private static void AddRunQueryValue(List<string> query, string name, JsonNode? value)
    {
        if (value is not JsonValue) return;
        query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value.ToString())}");
    }

    private async Task<JsonReply> ListConversationEvents(Route route, AIRequest request, JsonObject options, CancellationToken ct)
    {
        var conversationId = RequiredId(options, request, "conversationId");
        var query = new List<string>();
        foreach (var name in new[] { "limit", "after" })
            if (options[name] is JsonValue value)
                query.Add($"{name}={Uri.EscapeDataString(value.ToString())}");

        var path = $"v1/agents/{Uri.EscapeDataString(route.AgentId!)}/conversations/{Uri.EscapeDataString(conversationId)}/events";
        return await SendJson(HttpMethod.Get, path + (query.Count > 0 ? "?" + string.Join("&", query) : ""), null, ct);
    }

    private async Task<AIResponse> CancelExecution(Route route, AIRequest request, JsonObject options, CancellationToken ct)
    {
        var executionId = RequiredId(options, request, "executionId");
        var conversationId = Text(options, "conversationId") ?? FindIdentity(request, "conversationId");
        var path = $"v1/agents/{Uri.EscapeDataString(route.AgentId!)}/executions/{Uri.EscapeDataString(executionId)}/cancel";
        if (conversationId is not null)
            path += "?conversationId=" + Uri.EscapeDataString(conversationId);

        return CreateOperationResponse(route, "cancel_runtype_execution",
            await SendJson(HttpMethod.Post, path, null, ct), executionId);
    }

    private async Task<AIResponse> ReadRunAsync(Route route, AIRequest request, JsonObject options, CancellationToken ct)
    {
        var id = RequiredId(options, request, "executionId");
        var path = route.Kind == RouteKind.Status
            ? StatusPath(options, request, id)
            : $"v1/agents/{Uri.EscapeDataString(route.AgentId!)}/executions/{Uri.EscapeDataString(id)}";
        return CreateOperationResponse(route, route.Kind == RouteKind.Status ? "get_runtype_execution_status" : "get_runtype_execution",
            await SendJson(HttpMethod.Get, path, null, ct), id);
    }

    private static string StatusPath(JsonObject options, AIRequest request, string executionId)
    {
        var statusUrl = Text(options, "statusUrl") ?? FindIdentity(request, "statusUrl");
        if (string.IsNullOrWhiteSpace(statusUrl))
            return $"v1/executions/{Uri.EscapeDataString(executionId)}/status";

        var apiRoot = new Uri("https://api.runtype.com/");
        if (!Uri.TryCreate(apiRoot, statusUrl, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(apiRoot.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != apiRoot.Port
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Runtype statusUrl must resolve to an HTTPS URL on api.runtype.com.", nameof(options));

        return uri.PathAndQuery.TrimStart('/');
    }

    private AIResponse CreateOperationResponse(Route route, string toolName, JsonReply reply, string? executionId = null)
    {
        var raw = reply.Body;
        var metadata = Metadata(raw, headers: reply.Headers);
        metadata["runtype.httpStatus"] = reply.HttpStatus;
        if (executionId is not null) metadata["runtype.executionId"] = executionId;
        var tool = new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = $"runtype-{toolName}-{executionId ?? Guid.NewGuid().ToString("N")}",
            ToolName = toolName, Title = toolName, Input = executionId is null ? new { } : new { executionId },
            Output = new CallToolResult { StructuredContent = raw.Clone() }, ProviderExecuted = true,
            State = "output-available", Metadata = metadata
        };
        var content = new List<AIContentPart> { tool };
        var final = Property(raw, "finalOutput") ?? Property(raw, "result");
        if (final is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } value)
            content.Add(new AITextContentPart { Type = "text", Text = value, Metadata = metadata });
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = route.Model, Status = String(raw, "status") ?? "completed",
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }], Metadata = metadata },
            Usage = Usage(raw), Metadata = metadata
        };
    }
}
