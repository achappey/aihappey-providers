using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.WavenAI;

public sealed partial class WavenAIProvider
{
    private static Dictionary<string, JsonElement> NativeOptions(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue(ProviderId, out var native) != true) return new(StringComparer.Ordinal);
        if (native.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("WavenAI provider options must be a JSON object.");
        return native.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static void Set(Dictionary<string, JsonElement> options, string name, object? value)
    {
        if (value is not null) options[name] = JsonSerializer.SerializeToElement(value);
    }

    private static bool Flag(Dictionary<string, JsonElement> options, string name, bool remove = false)
    {
        if (!options.TryGetValue(name, out var value)) return false;
        if (remove) options.Remove(name);
        return value.GetBoolean();
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ReadNumber(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static byte[] DecodeAudio(object? audio)
    {
        if (audio is byte[] bytes)
            return bytes.Length > 0 ? bytes : throw new ArgumentException("Audio must not be empty.");
        var text = audio is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() : audio?.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = text.IndexOf(',');
            if (comma < 0 || !text[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Audio data URLs must contain base64 data.");
            text = text[(comma + 1)..];
        }
        var decoded = Convert.FromBase64String(text);
        return decoded.Length > 0 ? decoded : throw new ArgumentException("Audio must not be empty.");
    }

    private static void AddFile(MultipartFormDataContent form, string name, byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        var extension = content.Headers.ContentType.MediaType switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/mpeg" => "mp3",
            "audio/flac" => "flac",
            "audio/ogg" => "ogg",
            "audio/mp4" or "audio/x-m4a" => "m4a",
            _ => "bin"
        };
        form.Add(content, name, $"audio.{extension}");
    }

    private static void AddNativeFields(MultipartFormDataContent form, Dictionary<string, JsonElement> options)
    {
        foreach (var (name, value) in options)
        {
            if (name == "headers") continue;
            if (name is "reference_audio" or "prompt_audio")
            {
                var dataUrl = value.GetString();
                if (dataUrl?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) != true || !dataUrl.Contains(';'))
                    throw new ArgumentException($"{name} must be a base64 audio data URL.");
                AddFile(form, name, DecodeAudio(dataUrl), dataUrl[5..dataUrl.IndexOf(';')]);
                continue;
            }
            form.Add(new StringContent(value.ValueKind == JsonValueKind.String
                ? value.GetString()! : value.GetRawText(), Encoding.UTF8), name);
        }
    }

    private static void ApplyNativeHeaders(HttpRequestMessage request, Dictionary<string, JsonElement> options)
    {
        if (!options.TryGetValue("headers", out var headers)) return;
        if (headers.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("WavenAI headers must be a JSON object.");
        foreach (var header in headers.EnumerateObject())
        {
            if (!header.Name.Equals("X-Waven-Retain-Audio", StringComparison.OrdinalIgnoreCase)
                && !header.Name.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Unsupported WavenAI transport header '{header.Name}'.");
            request.Headers.Add(header.Name, header.Value.ValueKind == JsonValueKind.String
                ? header.Value.GetString() : header.Value.GetRawText());
        }
    }

    private async Task<JsonElement> SendJson(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"WavenAI request failed ({(int)response.StatusCode}): {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> WaitForJob(JsonElement initial, string path, CancellationToken cancellationToken)
    {
        var current = initial;
        var deadline = DateTime.UtcNow.AddMinutes(30);
        var id = ReadString(initial, "job_id");
        while (true)
        {
            var status = ReadString(current, "status");
            if (status is "completed") return current;
            if (status is "failed" or "cancelled" or "dead_letter")
                throw new InvalidOperationException($"WavenAI job {status}: {current.GetRawText()}");
            if (status is not ("pending" or "processing" or "retrying"))
                throw new InvalidOperationException($"Unexpected WavenAI job response: {current.GetRawText()}");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("WavenAI job response did not include job_id.");
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"WavenAI job '{id}' did not finish within 30 minutes.");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var poll = CreateRequest(HttpMethod.Get, $"{path}/{Uri.EscapeDataString(id)}");
            current = await SendJson(poll, cancellationToken);
        }
    }
}
