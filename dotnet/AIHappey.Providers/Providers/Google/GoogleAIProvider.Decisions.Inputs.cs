using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private static JsonArray MapGoogleDecisionSdkState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array || state.GetArrayLength() == 0)
            throw new ArgumentException("SDK decision state requires a nonempty array of parts.");
        var result = new JsonArray();
        long inlineBytes = 0, pdfBytes = 0;
        foreach (var part in state.EnumerateArray())
        {
            var type = RequiredGoogleDecisionString(part, "type");
            JsonObject content;
            switch (type)
            {
                case "text":
                    RequireGoogleDecisionFields(part, "type", "text", "providerOptions");
                    content = new() { ["type"] = "text", ["text"] = RequiredGoogleDecisionString(part, "text") };
                    break;
                case "json":
                    RequireGoogleDecisionFields(part, "type", "value", "providerOptions");
                    if (!part.TryGetProperty("value", out var value))
                        throw new ArgumentException("SDK JSON parts require a value.");
                    content = new() { ["type"] = "text", ["text"] = value.GetRawText() };
                    break;
                case "file":
                    RequireGoogleDecisionFields(part, "type", "mediaType", "data", "filename", "providerOptions");
                    var mimeType = RequiredGoogleDecisionString(part, "mediaType");
                    if (!part.TryGetProperty("data", out var data))
                        throw new ArgumentException("SDK file parts require tagged data.");
                    var kind = RequiredGoogleDecisionString(data, "type");
                    if (kind == "text")
                    {
                        RequireGoogleDecisionFields(data, "type", "text");
                        if (!(mimeType == "text" || mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mimeType == "application/json"))
                            throw new NotSupportedException("Inline text files require a text or JSON media type.");
                        content = new() { ["type"] = "text", ["text"] = RequiredGoogleDecisionString(data, "text") };
                    }
                    else if (kind == "data")
                    {
                        RequireGoogleDecisionFields(data, "type", "data");
                        content = CreateGoogleDecisionMedia(mimeType, RequiredGoogleDecisionString(data, "data"), inline: true, ref inlineBytes, ref pdfBytes);
                    }
                    else if (kind == "url")
                    {
                        RequireGoogleDecisionFields(data, "type", "url", "originalUrl");
                        var url = data.TryGetProperty("originalUrl", out _) ? RequiredGoogleDecisionString(data, "originalUrl")
                            : RequiredGoogleDecisionString(data, "url");
                        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            var (actualType, base64) = ParseGoogleDecisionDataUrl(url);
                            if (!actualType.Equals(mimeType, StringComparison.OrdinalIgnoreCase))
                                throw new ArgumentException("SDK file mediaType must match its data URL MIME type.");
                            content = CreateGoogleDecisionMedia(mimeType, base64, inline: true, ref inlineBytes, ref pdfBytes);
                        }
                        else
                            content = CreateGoogleDecisionMedia(mimeType, url, inline: false, ref inlineBytes, ref pdfBytes);
                    }
                    else
                        throw new NotSupportedException($"Google Decisions does not support SDK file data '{kind}'.");
                    if (part.TryGetProperty("filename", out _))
                        result.Add(new JsonObject { ["type"] = "text", ["text"] = "File: " + RequiredGoogleDecisionString(part, "filename") + "\n" });
                    break;
                default:
                    throw new NotSupportedException($"Google Decisions does not support SDK state part '{type}'.");
            }
            ApplyGoogleDecisionPartOptions(content, part);
            result.Add(content);
        }
        ValidateGoogleDecisionInputNotEmpty(result);
        return result;
    }

    private static JsonNode MapGoogleOpenAIDecisionInput(OpenAIDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Text is not null)
        {
            if (input.Text.Length == 0)
                throw new ArgumentException("Google decision input cannot be empty.");
            return JsonValue.Create(input.Text)!;
        }
        if (input.Items is null || input.Items.Count == 0)
            throw new ArgumentException("Google decision input requires text or user input messages.");
        var result = new JsonArray();
        long inlineBytes = 0, pdfBytes = 0;
        foreach (var item in input.Items)
        {
            if (item is not OpenAIDecisionInputMessage message || message.Role != "user"
                || message.Type is not (null or "message") || message.Phase is not null)
                throw new NotSupportedException("Google Decisions does not accept conversation or tool history. Serialize history as text evidence explicitly.");
            ArgumentNullException.ThrowIfNull(message.Content);
            if (message.Content.Text is not null)
            {
                result.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content.Text });
                continue;
            }
            if (message.Content.Parts is null || message.Content.Parts.Count == 0)
                throw new ArgumentException("Decision messages require nonempty content.");
            foreach (var part in message.Content.Parts)
            {
                if (part is not OpenAIDecisionInputImage image)
                {
                    result.Add(new JsonObject { ["type"] = "text", ["text"] = RenderGoogleDecisionOpenAITextPart(part) });
                    continue;
                }
                if (image.PromptCacheBreakpoint is not null)
                    throw new NotSupportedException("Google Decisions does not support prompt-cache breakpoints.");
                ArgumentException.ThrowIfNullOrWhiteSpace(image.ImageUrl);
                JsonObject content;
                if (image.ImageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var (mimeType, base64) = ParseGoogleDecisionDataUrl(image.ImageUrl);
                    if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("OpenAI image inputs require an image MIME type.");
                    content = CreateGoogleDecisionMedia(mimeType, base64, inline: true, ref inlineBytes, ref pdfBytes);
                }
                else
                {
                    // This contract has no MIME field. Only map URLs whose image format
                    // is representable; opaque URLs need an SDK file part with mediaType.
                    if (!Uri.TryCreate(image.ImageUrl, UriKind.Absolute, out var uri))
                        throw new ArgumentException("Decision images require a data URL or HTTPS URL.");
                    var mimeType = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
                    {
                        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
                        ".heic" => "image/heic", ".heif" => "image/heif", ".gif" => "image/gif",
                        ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff",
                        _ => throw new NotSupportedException("An opaque image URL has no MIME type in the OpenAI contract. Use inline image data or an SDK file with mediaType.")
                    };
                    content = CreateGoogleDecisionMedia(mimeType, image.ImageUrl, inline: false, ref inlineBytes, ref pdfBytes);
                }
                var resolution = image.Detail switch
                {
                    null or "auto" => null, "low" => "low", "high" => "high", "original" => "ultra_high",
                    _ => throw new NotSupportedException($"Unsupported Google decision image detail '{image.Detail}'.")
                };
                if (resolution is not null)
                    content["resolution"] = resolution;
                result.Add(content);
            }
        }
        ValidateGoogleDecisionInputNotEmpty(result);
        return result;
    }

    private static JsonObject CreateGoogleDecisionMedia(string mimeType, string value, bool inline, ref long inlineBytes, ref long pdfBytes)
    {
        mimeType = mimeType.ToLowerInvariant();
        var type = mimeType switch
        {
            "image/png" or "image/jpeg" or "image/webp" or "image/heic" or "image/heif" or "image/gif" or "image/bmp" or "image/tiff" => "image",
            "audio/wav" or "audio/mp3" or "audio/mpeg" or "audio/aiff" or "audio/aac" or "audio/ogg" or "audio/flac" or "audio/m4a"
                or "audio/opus" or "audio/webm" or "audio/l16" or "audio/alaw" or "audio/mulaw" => "audio",
            "video/mp4" or "video/mpeg" or "video/mov" or "video/avi" or "video/webm" or "video/wmv" or "video/x-flv" or "video/3gpp" => "video",
            "application/pdf" => "document",
            _ => throw new NotSupportedException($"Google Decisions does not support media type '{mimeType}'.")
        };
        if (inline)
        {
            // Check encoded size before allocating decoded bytes, then validate base64.
            if (value.Length > 140_000_000)
                throw new ArgumentException("Google decision inline media exceeds the 100 MB request limit.");
            int bytes;
            try { bytes = Convert.FromBase64String(value).Length; }
            catch (FormatException exception) { throw new ArgumentException("Google decision media data must be base64.", exception); }
            inlineBytes += bytes;
            if (type == "document") pdfBytes += bytes;
            if (bytes == 0 || inlineBytes > 100_000_000 || pdfBytes > 50_000_000)
                throw new ArgumentException("Google Decisions requires nonempty inline media, at most 100 MB total and 50 MB of PDFs.");
        }
        else
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Google decision media requires a Files API URI or public/signed HTTPS URL, not YouTube or gs:// URIs.");
        }
        return new JsonObject { ["type"] = type, ["mime_type"] = mimeType, [inline ? "data" : "uri"] = value };
    }

    private static (string MimeType, string Data) ParseGoogleDecisionDataUrl(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0 || !url.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Decision inline media URLs require a MIME type and base64 encoding.");
        return (url[5..(comma - ";base64".Length)], url[(comma + 1)..]);
    }

    private static void ApplyGoogleDecisionPartOptions(JsonObject content, JsonElement part)
    {
        if (!part.TryGetProperty("providerOptions", out var allOptions)) return;
        if (allOptions.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SDK part providerOptions must be an object.");
        if (!allOptions.TryGetProperty("google", out var options)) return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SDK part providerOptions.google must be an object.");
        foreach (var property in options.EnumerateObject())
        {
            if (new[] { "type", "text", "data", "uri", "mime_type" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Google state-part options cannot override '{property.Name}'.");
            if (property.Name == "resolution")
            {
                var type = content["type"]!.GetValue<string>();
                if (property.Value.ValueKind != JsonValueKind.String || type is not ("image" or "video")
                    || property.Value.GetString() is not ("low" or "medium" or "high" or "ultra_high")
                    || (type == "video" && property.Value.GetString() == "ultra_high"))
                    throw new ArgumentException("Google resolution applies to images/videos only; ultra_high applies to images only.");
            }
            content.Add(property.Name, ParseGoogleDecisionNode(property.Value));
        }
    }

    private static void ValidateGoogleDecisionInputNotEmpty(JsonArray content)
    {
        if (!content.OfType<JsonObject>().Any(part => part["type"]!.GetValue<string>() != "text" || part["text"]!.GetValue<string>().Length > 0))
            throw new ArgumentException("Google decision input cannot be empty.");
    }

    private static string RequiredGoogleDecisionString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new ArgumentException($"Google decision content requires string '{name}'.");

    private static void RequireGoogleDecisionFields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
            throw new NotSupportedException("Unrecognized decision content fields cannot be mapped without loss.");
    }
}
