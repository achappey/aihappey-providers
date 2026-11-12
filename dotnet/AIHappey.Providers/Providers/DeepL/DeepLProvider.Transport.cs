using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    private static StringContent JsonContent(object value)
        => new(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

    private static string? TraceId(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-Trace-ID", out var values) ? values.FirstOrDefault() : null;

    private async Task<HttpResponseMessage> SendDeepLAsync(HttpMethod method, string path, Func<HttpContent>? content,
        Dictionary<string, string>? headers, bool retry, CancellationToken cancellationToken, string? documentKey = null)
    {
        const int maxRetries = 3;
        for (var attempt = 0; ; attempt++)
        {
            using var request = CreateApiRequest(method, path);
            // Reporting tags are the only documented caller-supplied DeepL transport header.
            // Never forward inbound Authorization, host, or provider credential headers.
            var reportingTag = headers?.FirstOrDefault(pair => string.Equals(pair.Key, "X-DeepL-Reporting-Tag", StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrEmpty(reportingTag)) request.Headers.Add("X-DeepL-Reporting-Tag", reportingTag);
            request.Content = content?.Invoke();
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var traceId = TraceId(response);
            Trace.TraceInformation("DeepL {0}: HTTP {1}, X-Trace-ID={2}", path, (int)response.StatusCode, traceId ?? "(absent)");
            if (response.IsSuccessStatusCode) return response;

            var status = response.StatusCode;
            // 429 explicitly rejects the request before processing, including uploads/downloads.
            // Server/transport failures of those one-shot operations may be ambiguous.
            var canRetry = attempt < maxRetries
                && (status == HttpStatusCode.TooManyRequests || retry && (int)status >= 500);
            var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt) + Random.Shared.Next(100));
            if (response.Headers.RetryAfter?.Delta is { } delta && delta > delay) delay = delta;
            if (response.Headers.RetryAfter?.Date is { } date && date - DateTimeOffset.UtcNow > delay)
                delay = date - DateTimeOffset.UtcNow;
            // Keep backoff bounded even if an upstream sends an unreasonable Retry-After.
            delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0, 30));
            if (canRetry)
            {
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            string message;
            string? code = null;
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                message = "Request failed.";
                try
                {
                    using var error = JsonDocument.Parse(body);
                    var root = error.RootElement;
                    if (root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object) root = nested;
                    if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String) message = msg.GetString()!;
                    if (root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
                }
                catch (JsonException) { /* Do not expose arbitrary proxy HTML or credential-bearing bodies. */ }
            }
            var details = Redact($"DeepL request failed ({(int)status}){(code is null ? "" : $" [{code}]")}: {message}. X-Trace-ID: {traceId ?? "unavailable"}", documentKey);
            throw new HttpRequestException(details, null, status);
        }
    }

    private string Redact(string message, string? documentKey = null)
    {
        var apiKey = _keyResolver.Resolve(GetIdentifier())?.Trim();
        if (!string.IsNullOrEmpty(apiKey)) message = message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(documentKey)) message = message.Replace(documentKey, "[redacted]", StringComparison.Ordinal);
        return message;
    }
}
