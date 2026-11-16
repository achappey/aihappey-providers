using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.ByteIT;

public sealed partial class ByteITProvider
{
    private const int MaximumUploadBytes = 50 * 1024 * 1024;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var model = request.Model;
        if (model?.StartsWith("byteit/", StringComparison.OrdinalIgnoreCase) == true)
            model = model["byteit/".Length..];
        if (model is not ("parse" or "extract" or "classification" or "custom"))
            throw new ArgumentException($"Unsupported ByteIT model '{request.Model}'.", nameof(request));

        var options = request.Metadata?.TryGetValue(GetIdentifier(), out var native) == true && native is not null
            ? JsonSerializer.SerializeToNode(native, JsonSerializerOptions.Web) as JsonObject
                ?? throw new ArgumentException("ByteIT provider metadata must be an object.", nameof(request))
            : new JsonObject();
        ApplyResponseFormat(request, options);
        if (options["queue_for_batch"]?.GetValue<bool>() == true)
            throw new NotSupportedException("ByteIT queue_for_batch cannot be used with synchronous document processing.");
        if (options.ContainsKey("schema") && options["schema"] is not JsonObject)
            throw new ArgumentException("ByteIT schema must be a JSON object.", nameof(request));
        if (model == "extract" && options["schema"] is not JsonObject)
            throw new ArgumentException("ByteIT extraction requires a native schema or a json_schema response_format.", nameof(request));
        if (model == "classification" && options.ContainsKey("schema"))
            throw new ArgumentException("ByteIT classification does not support a response schema.", nameof(request));

        var latestUser = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latestUser?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        var existingParseId = options["parse_job_id"]?.GetValue<string>();
        var existingParse = model == "extract" && !string.IsNullOrWhiteSpace(existingParseId);
        var s3Input = (model is "parse" or "extract") && options["input_connector"]?.GetValue<string>() == "s3";
        if (s3Input && options["input_connection_data"] is not JsonObject)
            throw new ArgumentException("ByteIT S3 input requires input_connection_data.", nameof(request));
        if ((existingParse || s3Input) && files.Count > 0)
            throw new ArgumentException("ByteIT accepts uploaded files or a remote input, not both.", nameof(request));
        if (files.Count == 0 && !existingParse && !s3Input)
            throw new ArgumentException("ByteIT requires at least one file in the latest user message.", nameof(request));
        if (model == "custom" && files.Count > 25)
            throw new ArgumentException("ByteIT custom jobs accept at most 25 files.", nameof(request));
        if (model == "custom" && !options.ContainsKey("user_prompt"))
        {
            var prompt = string.Join("\n", latestUser?.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []);
            if (!string.IsNullOrWhiteSpace(prompt))
                options["user_prompt"] = prompt;
        }

        var output = new List<AIOutputItem>();
        var groups = model == "custom" ? new[] { files } : files.Select(f => new List<AIFileContentPart> { f }).ToArray();
        if (existingParse || s3Input)
            groups = [new List<AIFileContentPart>()];

        foreach (var group in groups)
        {
            var jobType = model == "extract" ? "parse" : model;
            string jobId;
            JsonNode? parseJob = null;
            if (existingParse)
            {
                jobId = existingParseId!;
            }
            else
            {
                using var form = await CreateUploadAsync(group, jobType, options, cancellationToken);
                var created = await SendJsonAsync(HttpMethod.Post, $"{jobType}-jobs/", form, cancellationToken);
                jobId = GetJobId(created, jobType);
                parseJob = await WaitForJobAsync(jobId, cancellationToken);
            }

            var extract = (model is "parse" or "extract") && options["schema"] is JsonObject;
            JsonNode? completedJob = parseJob;
            if (extract)
            {
                var extraction = model == "extract" ? (JsonObject)options.DeepClone() : new JsonObject();
                foreach (var name in new[] { "processing_options", "output_format", "include_extras", "file", "files",
                    "input_connector", "input_connection_data", "output_connector", "output_connection_data", "queue_for_batch", "nickname" })
                    extraction.Remove(name);
                extraction["parse_job_id"] = jobId;
                extraction["schema"] = options["schema"]!.DeepClone();
                if (options.ContainsKey("extraction_complexity"))
                    extraction["extraction_complexity"] = options["extraction_complexity"]?.DeepClone();
                var created = await SendJsonAsync(HttpMethod.Post, "extract-jobs/", JsonContent.Create(extraction), cancellationToken);
                jobId = GetJobId(created, "extract");
                completedJob = await WaitForJobAsync(jobId, cancellationToken);
                jobType = "extract";
            }

            var query = new List<string>();
            if (!extract && model == "parse" && options["output_format"] is { } format)
                query.Add($"output_format={Uri.EscapeDataString(format.GetValue<string>())}");
            if (model == "custom" && options["include_extras"] is { } extras)
                query.Add($"include_extras={extras.ToJsonString()}");
            var path = $"{Uri.EscapeDataString(jobId)}/result/" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            using var result = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
            var bytes = await result.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                throw new InvalidOperationException($"ByteIT {jobType} job '{jobId}' returned an empty result.");
            var outputFormat = extract || model == "classification" || (model == "custom" && options["schema"] is JsonObject)
                ? "json" : options["output_format"]?.GetValue<string>() ?? "md";
            var mediaType = result.Content.Headers.ContentType?.MediaType;
            var binary = mediaType is "application/zip" or "application/x-zip-compressed"
                || outputFormat == "zip"
                || (model == "custom" && options["include_extras"]?.GetValue<bool>() == true);
            if (string.IsNullOrWhiteSpace(mediaType) || mediaType == "application/octet-stream")
                mediaType = binary ? "application/zip" : outputFormat switch
                {
                    "json" => "application/json", "html" => "text/html",
                    "md" => "text/markdown", _ => "text/plain"
                };
            var itemMetadata = new Dictionary<string, object?>
            {
                ["byteit.job_id"] = jobId,
                ["byteit.job"] = completedJob,
                ["byteit.parse_job"] = extract ? parseJob : null,
                ["fileIndex"] = output.Count,
                ["filename"] = group.FirstOrDefault()?.Filename
            };
            if (!binary && (outputFormat == "json" || mediaType == "application/json"))
                itemMetadata["byteit.structured_output"] = JsonNode.Parse(bytes)
                    ?? throw new InvalidOperationException($"ByteIT job '{jobId}' returned an empty JSON result.");
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Metadata = itemMetadata,
                Content = binary
                    ? [new AIFileContentPart
                    {
                        Type = "file", MediaType = mediaType,
                        Filename = result.Content.Headers.ContentDisposition?.FileNameStar
                            ?? result.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? $"{jobId}.zip",
                        Data = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}"
                    }]
                    : [new AITextContentPart { Type = "text", Text = Encoding.UTF8.GetString(bytes) }]
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"byteit/{model}", Status = "completed",
            Output = new AIOutput { Items = output },
            Metadata = new Dictionary<string, object?> { ["finishReason"] = "stop" }
        };
    }

    private static void ApplyResponseFormat(AIRequest request, JsonObject options)
    {
        if (request.ResponseFormat is null)
            return;
        var format = JsonSerializer.SerializeToNode(request.ResponseFormat, JsonSerializerOptions.Web) as JsonObject
            ?? throw new ArgumentException("ByteIT response_format must be an object.", nameof(request));
        switch (format["type"]?.GetValue<string>())
        {
            case "text":
                break;
            case "json_object":
                options.TryAdd("output_format", JsonValue.Create("json"));
                break;
            case "json_schema":
                options["schema"] = format["json_schema"]?["schema"] is JsonObject schema
                    ? schema.DeepClone()
                    : throw new ArgumentException("ByteIT json_schema response_format requires json_schema.schema.", nameof(request));
                break;
            default:
                throw new ArgumentException("ByteIT supports text, json_object and json_schema response formats.", nameof(request));
        }
    }

    private async Task<MultipartFormDataContent> CreateUploadAsync(List<AIFileContentPart> files, string jobType,
        JsonObject options, CancellationToken cancellationToken)
    {
        var form = new MultipartFormDataContent();
        try
        {
            foreach (var option in options)
            {
                if (option.Key is "file" or "files" or "parse_job_id" or "include_extras"
                    || (jobType == "parse" && option.Key is "schema" or "extraction_complexity"))
                    continue;
                var value = option.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
                    ? text : option.Value?.ToJsonString() ?? "null";
                form.Add(new StringContent(value), option.Key);
            }
            if (jobType == "parse")
            {
                if (!options.ContainsKey("output_format"))
                    form.Add(new StringContent("md"), "output_format");
                if (!options.ContainsKey("input_connector"))
                    form.Add(new StringContent("localfile"), "input_connector");
            }
            var totalBytes = 0;
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var (bytes, mediaType, filename) = await ReadFileAsync(file, index, cancellationToken);
                totalBytes += bytes.Length;
                if (totalBytes > MaximumUploadBytes)
                    throw new ArgumentException("ByteIT uploads cannot exceed 50 MB.", nameof(files));
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                form.Add(content, jobType == "custom" ? "files" : "file", filename);
            }
            return form;
        }
        catch
        {
            form.Dispose();
            throw;
        }
    }

    private async Task<(byte[] Bytes, string MediaType, string Filename)> ReadFileAsync(
        AIFileContentPart file, int index, CancellationToken cancellationToken)
    {
        var mediaType = file.MediaType;
        var filename = file.Filename;
        byte[] bytes;
        if (file.Data is byte[] data)
            bytes = data;
        else
        {
            var value = file.Data is JsonElement json && json.ValueKind == JsonValueKind.String
                ? json.GetString() : file.Data as string;
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"ByteIT file {index + 1} is empty.", nameof(file));
            if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            {
                using var download = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await EnsureSuccessAsync(download, cancellationToken);
                mediaType = download.Content.Headers.ContentType?.MediaType ?? mediaType;
                filename ??= Path.GetFileName(url.LocalPath);
                if (download.Content.Headers.ContentLength > MaximumUploadBytes)
                    throw new ArgumentException("ByteIT uploads cannot exceed 50 MB.", nameof(file));
                await using var stream = await download.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + read > MaximumUploadBytes)
                        throw new ArgumentException("ByteIT uploads cannot exceed 50 MB.", nameof(file));
                    buffer.Write(chunk, 0, read);
                }
                bytes = buffer.ToArray();
            }
            else
            {
                if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = value.IndexOf(',');
                    if (comma < 0 || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("ByteIT files must use base64 data URLs.", nameof(file));
                    mediaType = value[5..value.IndexOf(';')];
                    value = value[(comma + 1)..];
                }
                if (value.Length > (MaximumUploadBytes + 2L) / 3 * 4)
                    throw new ArgumentException("ByteIT uploads cannot exceed 50 MB.", nameof(file));
                bytes = Convert.FromBase64String(value);
            }
        }
        if (bytes.Length == 0 || bytes.Length > MaximumUploadBytes)
            throw new ArgumentException("ByteIT files must be non-empty and no larger than 50 MB.", nameof(file));
        mediaType = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType;
        if (string.IsNullOrWhiteSpace(filename))
        {
            var extension = mediaType switch
            {
                "application/pdf" => "pdf", "image/jpeg" => "jpg", "image/tiff" => "tiff",
                "application/msword" => "doc", "application/rtf" or "text/rtf" => "rtf",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "pptx",
                "application/vnd.ms-excel" => "xls", "application/vnd.oasis.opendocument.text" => "odt",
                "text/plain" => "txt", "text/markdown" => "md", "text/html" => "html",
                "application/json" => "json", "application/xml" or "text/xml" => "xml",
                "image/png" => "png", "image/bmp" => "bmp",
                _ => throw new ArgumentException("ByteIT requires a filename for this media type.", nameof(file))
            };
            filename = $"document-{index + 1}.{extension}";
        }
        return (bytes, mediaType, filename);
    }

    private static string GetJobId(JsonNode created, string type)
        => created[$"{type}_job"]?["job_id"]?.GetValue<string>() is { Length: > 0 } id
            ? id : throw new InvalidOperationException($"ByteIT {type} response omitted {type}_job.job_id.");

    private async Task<JsonNode> WaitForJobAsync(string id, CancellationToken cancellationToken)
    {
        var escapedId = Uri.EscapeDataString(id);
        var delay = 1;
        while (true)
        {
            var status = await SendJsonAsync(HttpMethod.Get, $"{escapedId}/status/", null, cancellationToken);
            switch (status["processing_status"]?.GetValue<string>())
            {
                case "completed":
                    return await SendJsonAsync(HttpMethod.Get, $"{escapedId}/", null, cancellationToken);
                case "failed":
                    throw new InvalidOperationException($"ByteIT job '{id}' failed: {status.ToJsonString()}");
                case "pending":
                case "queued":
                case "processing":
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                    delay = Math.Min(delay * 2, 10);
                    break;
                default:
                    throw new InvalidOperationException($"ByteIT job '{id}' returned an invalid status: {status.ToJsonString()}");
            }
        }
    }

    private async Task<JsonNode> SendJsonAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, content, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken)
            ?? throw new InvalidOperationException("ByteIT returned an empty JSON response.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No ByteIT API key.");
        using var request = new HttpRequestMessage(method, BaseUrl + path) { Content = content };
        request.Headers.Add("X-API-Key", key);
        var response = await _client.SendAsync(request, cancellationToken);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ByteIT request failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}",
                null, response.StatusCode);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request, cancellationToken);
        foreach (var item in response.Output?.Items ?? [])
        {
            var id = Guid.NewGuid().ToString("n");
            foreach (var part in item.Content ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (part is AITextContentPart text)
                {
                    yield return StreamEvent(id, "text-start", new AITextStartEventData(), item.Metadata);
                    yield return StreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = text.Text }, item.Metadata);
                    yield return StreamEvent(id, "text-end", new AITextEndEventData(), item.Metadata);
                }
                else if (part is AIFileContentPart file)
                    yield return StreamEvent(id, "file", new { mediaType = file.MediaType, filename = file.Filename, url = file.Data }, item.Metadata);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var completed = DateTimeOffset.UtcNow;
        yield return StreamEvent(request.Id ?? Guid.NewGuid().ToString("n"), "finish",
            new AIFinishEventData
            {
                FinishReason = "stop", Model = response.Model, CompletedAt = completed.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(response.Model!, completed, response.Usage)
            }, response.Metadata);
    }

    private AIStreamEvent StreamEvent(string id, string type, object data, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope { Id = id, Type = type, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = metadata }
        };
}
