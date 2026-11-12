using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using Microsoft.AspNetCore.StaticFiles;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    private sealed record DocumentInput(byte[] Bytes, string Filename, string MediaType);
    private static readonly FileExtensionContentTypeProvider DocumentContentTypes = new();
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".pptx", ".xlsx", ".xlsm", ".pdf", ".htm", ".html", ".txt", ".xlf", ".xliff",
        ".srt", ".vtt", ".idml", ".xml", ".json", ".yaml", ".yml", ".properties", ".strings", ".md",
        ".markdown", ".dita", ".mif", ".zip", ".odt", ".rtf", ".resx", ".jpeg", ".jpg", ".png"
    };

    private static readonly Dictionary<string, string> ExtraDocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".xlf"] = "application/xliff+xml", [".xliff"] = "application/xliff+xml", [".srt"] = "application/x-subrip",
        [".vtt"] = "text/vtt", [".idml"] = "application/vnd.adobe.indesign-idml-package", [".md"] = "text/markdown",
        [".markdown"] = "text/markdown", [".yaml"] = "application/yaml", [".yml"] = "application/yaml",
        [".properties"] = "text/plain", [".strings"] = "text/plain", [".dita"] = "application/xml",
        [".mif"] = "application/vnd.mif", [".resx"] = "application/xml"
    };

    private static string DocumentMediaType(string filename)
        => ExtraDocumentTypes.TryGetValue(Path.GetExtension(filename), out var extra) ? extra
            : DocumentContentTypes.TryGetContentType(filename, out var type) ? type : "application/octet-stream";

    private static DocumentInput DecodeDocument(AIFileContentPart file, int index, Dictionary<string, JsonElement> options)
    {
        var mediaType = file.MediaType;
        byte[] bytes;
        if (file.Data is byte[] data) bytes = data;
        else if (file.Data is ReadOnlyMemory<byte> memory) bytes = memory.ToArray();
        else
        {
            var raw = file.Data is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : file.Data as string;
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException($"DeepL document {index + 1} has no inline content.");
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                throw new NotSupportedException("DeepL document inputs must contain inline bytes/base64, not remote URLs or file IDs.");

            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = raw.IndexOf(',');
                if (comma < 0) throw new ArgumentException("Invalid document data URI.");
                var descriptor = raw[5..comma];
                mediaType ??= descriptor.Split(';')[0];
                bytes = descriptor.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    ? DecodeBase64(raw[(comma + 1)..]) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(raw[(comma + 1)..]));
            }
            else if (IsPlainTextDocument(file)) bytes = Encoding.UTF8.GetBytes(raw);
            else bytes = DecodeBase64(raw);
        }
        if (bytes.Length == 0) throw new ArgumentException("DeepL cannot translate an empty document.");
        var filename = options.TryGetValue("filename", out var supplied) && supplied.ValueKind == JsonValueKind.String
            ? supplied.GetString() : file.Filename;
        if (string.IsNullOrWhiteSpace(filename))
        {
            var extension = DocumentContentTypes.Mappings.Concat(ExtraDocumentTypes)
                .FirstOrDefault(pair => string.Equals(pair.Value, mediaType, StringComparison.OrdinalIgnoreCase)).Key;
            if (extension is null) throw new ArgumentException("DeepL document input requires a filename with its original extension.");
            filename = $"document-{index + 1}{extension}";
        }
        filename = Path.GetFileName(filename.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrEmpty(Path.GetExtension(filename)) || filename.Any(char.IsControl))
            throw new ArgumentException("DeepL document input requires a valid filename with its original extension.");
        if (!DocumentExtensions.Contains(Path.GetExtension(filename)))
            throw new NotSupportedException($"DeepL does not support document extension '{Path.GetExtension(filename)}'.");
        mediaType = string.IsNullOrWhiteSpace(mediaType) ? DocumentMediaType(filename) : mediaType;
        if (!MediaTypeHeaderValue.TryParse(mediaType, out _))
            throw new ArgumentException("DeepL document media type is invalid.");
        return new DocumentInput(bytes, filename, mediaType);
    }

    private static bool IsPlainTextDocument(AIFileContentPart file)
    {
        // Anthropic document source.type=text contains UTF-8 text, not base64.
        if (file.Metadata?.TryGetValue("messages.block.raw", out var raw) != true || raw is null) return false;
        var block = JsonSerializer.SerializeToElement(raw);
        return block.TryGetProperty("source", out var source) && source.TryGetProperty("type", out var type) && type.GetString() == "text";
    }

    private static byte[] DecodeBase64(string value)
    {
        try { return Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new ArgumentException("DeepL document input is not valid base64.", ex); }
    }

    private async Task<AIContentPart> TranslateDocumentAsync(DocumentInput input, string target,
        Dictionary<string, JsonElement> options, Dictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10), _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = timeout.Token;
        try
        {
            // An accepted upload may create a billable job. Only explicit throttling is safe to retry.
            using var upload = await SendDeepLAsync(HttpMethod.Post, "v2/document", () => DocumentForm(input, target, options), headers, retry: false, ct);
            using var uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync(ct));
            var root = uploaded.RootElement;
            var documentId = RequiredString(root, "document_id");
            var documentKey = RequiredString(root, "document_key");
            var path = $"v2/document/{Uri.EscapeDataString(documentId)}";
            var keyPayload = new { document_key = documentKey };
            JsonElement status;
            while (true)
            {
                using var response = await SendDeepLAsync(HttpMethod.Post, path, () => JsonContent(keyPayload), headers, retry: true, ct, documentKey);
                using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                status = state.RootElement.Clone();
                var value = RequiredString(status, "status");
                if (value == "done") break;
                if (value == "error")
                {
                    var message = status.TryGetProperty("error_message", out var error) ? error.ToString()
                        : status.TryGetProperty("message", out error) ? error.ToString() : "Document translation failed.";
                    throw new InvalidOperationException(Redact($"DeepL document translation failed: {message}. X-Trace-ID: {TraceId(response) ?? "unavailable"}", documentKey));
                }
                if (value is not ("queued" or "translating"))
                    throw new InvalidOperationException("DeepL returned an unknown document translation status.");
                var seconds = status.TryGetProperty("seconds_remaining", out var remaining) && remaining.TryGetInt32(out var estimate)
                    ? Math.Clamp(estimate / 2, 1, 5) : 1;
                await Task.Delay(TimeSpan.FromSeconds(seconds), _timeProvider, ct);
            }

            // Results can be downloaded exactly once. Never retry an ambiguous download or body read.
            using var download = await SendDeepLAsync(HttpMethod.Post, path + "/result", () => JsonContent(keyPayload), headers, retry: false, ct, documentKey);
            var bytes = await download.Content.ReadAsByteArrayAsync(ct);
            var filename = options.TryGetValue("output_format", out var format) && format.ValueKind == JsonValueKind.String
                ? Path.ChangeExtension(input.Filename, format.GetString()) : input.Filename;
            var downloadedName = download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            if (!string.IsNullOrWhiteSpace(downloadedName)) filename = Path.GetFileName(downloadedName.Replace('\\', '/'));
            var mediaType = download.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || mediaType == "application/octet-stream") mediaType = DocumentMediaType(filename);
            var metadata = new Dictionary<string, object?>
            {
                ["document_id"] = documentId, ["status"] = "done", ["trace_id"] = TraceId(download)
            };
            if (status.TryGetProperty("billed_characters", out var billed)) metadata["billed_characters"] = billed.Clone();
            if (root.TryGetProperty("quality_evaluation_job_id", out var quality)) metadata["quality_evaluation_job_id"] = quality.Clone();
            return new AIFileContentPart
            {
                Type = "file", Filename = filename, MediaType = mediaType,
                Data = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}", Metadata = new() { ["deepl"] = metadata }
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("DeepL document translation did not finish within ten minutes.");
        }
    }

    private static string RequiredString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidOperationException($"DeepL returned a response without '{name}'.");

    private static MultipartFormDataContent DocumentForm(DocumentInput input, string target, Dictionary<string, JsonElement> options)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(input.Bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(input.MediaType);
        form.Add(content, "file", input.Filename);
        form.Add(new StringContent(target), "target_lang");
        foreach (var (name, value) in options)
        {
            if (name is "target_lang" or "filename" || TextOnlyOptions.Contains(name)) continue;
            if (name == "glossary_ids" && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray()) form.Add(new StringContent(item.GetString()!), name);
            }
            else form.Add(new StringContent(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()), name);
        }
        return form;
    }
}
