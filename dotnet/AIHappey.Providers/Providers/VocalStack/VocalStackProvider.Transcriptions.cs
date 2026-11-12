using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.VocalStack;

public sealed partial class VocalStackProvider
{
    private static readonly JsonSerializerOptions VocalStackJson = new(JsonSerializerDefaults.Web);

    private async Task<TranscriptionResponse> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => request.Audio?.ToString()
        };
        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));
        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("VocalStack provider options must be a JSON object.");

        var options = CreateOptions(metadata, request.Language);
        var requestBody = options?.ToJsonString(VocalStackJson);
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        if (requestBody is not null)
            form.Add(new StringContent(requestBody, Encoding.UTF8, "application/json"), "options");

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/transcriptions")
        {
            Content = form
        };
        ApplyAuthHeader(createRequest);
        createRequest.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"VocalStack create transcription failed ({(int)createResponse.StatusCode}): {createBody}");

        using var createDocument = JsonDocument.Parse(createBody);
        var recordingId = ReadString(createDocument.RootElement, "job", "recordingId");
        if (string.IsNullOrWhiteSpace(recordingId))
            throw new InvalidOperationException($"VocalStack create response did not contain job.recordingId: {createBody}");

        var jobBody = await PollJobUntilDoneAsync(recordingId, cancellationToken);
        using var jobDocument = JsonDocument.Parse(jobBody);
        var state = ReadString(jobDocument.RootElement, "state");
        if (!string.Equals(state, "done", StringComparison.OrdinalIgnoreCase))
        {
            var error = ReadString(jobDocument.RootElement, "error")
                        ?? ReadString(jobDocument.RootElement, "errorCode")
                        ?? "transcription did not complete";
            throw new InvalidOperationException($"VocalStack transcription failed: {error}. Body: {jobBody}");
        }

        using var transcriptRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/recordings/{Uri.EscapeDataString(recordingId)}/transcript");
        ApplyAuthHeader(transcriptRequest);
        using var transcriptResponse = await _client.SendAsync(transcriptRequest, cancellationToken);
        var transcriptBody = await transcriptResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!transcriptResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"VocalStack get transcript failed ({(int)transcriptResponse.StatusCode}): {transcriptBody}");

        using var transcriptDocument = JsonDocument.Parse(transcriptBody);
        var transcript = transcriptDocument.RootElement;
        var text = ReadString(transcript, "text");
        var segments = new List<TranscriptionSegment>();
        if (TryGetProperty(transcript, "sentences", out var sentences) && sentences.ValueKind == JsonValueKind.Array)
        {
            foreach (var sentence in sentences.EnumerateArray())
            {
                var sentenceText = ReadString(sentence, "text");
                if (string.IsNullOrWhiteSpace(sentenceText))
                    continue;

                segments.Add(new TranscriptionSegment
                {
                    Text = sentenceText,
                    StartSecond = ReadNumber(sentence, "start") ?? 0f,
                    EndSecond = ReadNumber(sentence, "end") ?? ReadNumber(sentence, "start") ?? 0f
                });
            }

            if (string.IsNullOrWhiteSpace(text))
                text = string.Join(" ", segments.Select(segment => segment.Text));
        }

        return new TranscriptionResponse
        {
            Text = text ?? string.Empty,
            Language = ReadString(transcript, "spokenLanguage") ?? ReadString(transcript, "language"),
            DurationInSeconds = ReadNumber(transcript, "duration"),
            Segments = segments,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    recordingId,
                    job = jobDocument.RootElement.Clone(),
                    transcript = transcript.Clone()
                }, VocalStackJson)
            },
            Request = new TranscriptionRequestItem { Body = requestBody },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = transcript.Clone()
            }
        };
    }

    private async Task<string> PollJobUntilDoneAsync(string recordingId, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow.AddMinutes(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var jobRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"v1/recordings/{Uri.EscapeDataString(recordingId)}/job");
            ApplyAuthHeader(jobRequest);
            using var response = await _client.SendAsync(jobRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"VocalStack get job failed ({(int)response.StatusCode}): {body}");

            using var document = JsonDocument.Parse(body);
            var state = ReadString(document.RootElement, "state");
            if (string.Equals(state, "done", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
                return body;

            if (DateTime.UtcNow >= timeoutAt)
                throw new TimeoutException($"VocalStack transcription {recordingId} timed out.");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static JsonObject? CreateOptions(JsonElement metadata, string? language)
    {
        JsonObject? options = metadata.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(metadata.GetRawText())?.AsObject()
            : null;

        if (string.IsNullOrWhiteSpace(language))
            return options;

        options ??= new JsonObject();
        if (options["settings"] is null)
            options["settings"] = new JsonObject();

        if (options["settings"] is not JsonObject settings)
            throw new JsonException("VocalStack provider options settings must be an object.");
        settings.TryAdd("spokenLanguage", JsonValue.Create(language));
        return options;
    }

    private async Task<IOpenAITranscriptionResponse> TranscribeOpenAIAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        return (await TranscribeAsync(request, cancellationToken)).ToOpenAITranscriptionResponse(responseFormat);
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadString(JsonElement element, string parentProperty, string propertyName)
        => TryGetProperty(element, parentProperty, out var parent)
            ? ReadString(parent, propertyName)
            : null;

    private static float? ReadNumber(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? (float)value.GetDouble()
            : null;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
