using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.CoralBricks;

public sealed partial class CoralBricksProvider
{
    private Dictionary<string, string> Headers(Dictionary<string, string>? supplied = null, string? key = null)
    {
        key ??= _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No CoralBricks API key.");

        var headers = ProviderCredentialHeaders.ForModel(supplied, null);
        headers["Authorization"] = new AuthenticationHeaderValue("Bearer", key).ToString();
        return headers;
    }

    private JsonElement BuildPayload<T>(T options, bool stream)
    {
        var serialized = JsonSerializer.SerializeToElement(options, ResponseJson.Default);
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in serialized.EnumerateObject())
            if (property.Name != GetIdentifier())
                body[property.Name] = property.Value.Clone();

        // Flatten native provider options without interpreting, coercing, or filtering their values.
        if (serialized.TryGetProperty(GetIdentifier(), out var native) && native.ValueKind == JsonValueKind.Object)
            foreach (var property in native.EnumerateObject())
                body[property.Name] = property.Value.Clone();

        if (body.TryGetValue("model", out var model) && model.ValueKind == JsonValueKind.String)
        {
            var id = model.GetString()!;
            var prefix = GetIdentifier() + "/";
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                body["model"] = JsonSerializer.SerializeToElement(id[prefix.Length..]);
        }
        body["stream"] = JsonSerializer.SerializeToElement(stream);
        return JsonSerializer.SerializeToElement(body);
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        // The shared Responses transport forces store=false, which would break native background agents.
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "responses");
        foreach (var (name, value) in Headers(options.Headers))
            request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(BuildPayload(options, false).GetRawText(), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var result = JsonSerializer.Deserialize<ResponseResult>(
            await response.Content.ReadAsStringAsync(cancellationToken), ResponseJson.Default)
            ?? throw new JsonException("Empty CoralBricks response.");
        if (!string.IsNullOrEmpty(result.Model))
            result.Model = result.Model.ToModelId(GetIdentifier());
        return result;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in _client.GetChatCompletionSseEvents(BuildPayload(options, true),
            BaseUrl + "responses", Headers(options.Headers), cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(part.Data)) continue;
            var update = JsonSerializer.Deserialize<ResponseStreamPart>(part.Data, ResponseJson.Default)
                ?? throw new JsonException("Empty CoralBricks response event.");
            var response = update switch
            {
                ResponseCreated created => created.Response,
                ResponseInProgress progress => progress.Response,
                ResponseCompleted completed => completed.Response,
                _ => null
            };
            if (!string.IsNullOrEmpty(response?.Model))
                response.Model = response.Model.ToModelId(GetIdentifier());
            yield return update;
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"CoralBricks API error ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
    }
}
