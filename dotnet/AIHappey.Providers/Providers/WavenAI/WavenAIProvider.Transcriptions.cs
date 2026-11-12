using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WavenAI;

public sealed partial class WavenAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        var audio = DecodeAudio(request.Audio);
        var options = NativeOptions(request.ProviderOptions);
        Set(options, "model", LocalModel(request.Model));
        var queued = Flag(options, "queued", remove: true);
        var diarize = Flag(options, "diarize");
        if (!queued) options.Remove("diarize");
        if (options.TryGetValue("format", out var format) && format.GetString() != "json")
            throw new NotSupportedException("Normalized transcription requires format=json. Use OpenAI transcription for text or captions.");
        Set(options, "format", "json");
        if (queued) options.Remove("format");
        var path = queued ? "stt/transcribe/jobs" : diarize ? "stt/transcribe/diarize" : "stt/transcribe";
        using var message = CreateRequest(HttpMethod.Post, path);
        ApplyNativeHeaders(message, options);
        var form = new MultipartFormDataContent();
        message.Content = form;
        AddFile(form, "file", audio, request.MediaType);
        AddNativeFields(form, options);
        var timestamp = DateTime.UtcNow;
        var metadata = await SendJson(message, cancellationToken);
        var result = metadata;
        if (queued)
        {
            metadata = await WaitForJob(metadata, path, cancellationToken);
            var key = diarize ? "diarization" : "result";
            if (!metadata.TryGetProperty(key, out result) || result.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"WavenAI completed without a transcription result: {metadata.GetRawText()}");
        }
        var segments = new List<TranscriptionSegment>();
        var hasSegments = (result.TryGetProperty("segments", out var items) && items.ValueKind == JsonValueKind.Array)
            || (result.TryGetProperty("utterances", out items) && items.ValueKind == JsonValueKind.Array);
        var text = ReadString(result, "text");
        if (text is null && !hasSegments)
            throw new InvalidOperationException($"WavenAI returned no transcript: {result.GetRawText()}");
        if (hasSegments)
            foreach (var item in items.EnumerateArray())
                segments.Add(new()
                {
                    Text = ReadString(item, "text") ?? "",
                    StartSecond = ReadNumber(item, "start") ?? 0,
                    EndSecond = ReadNumber(item, "end") ?? 0
                });
        var warnings = new List<object>();
        foreach (var field in new[] { "language_warning", "correction_denied" })
            if (ReadString(result, field) is { Length: > 0 } warning)
                warnings.Add(new { type = "other", message = $"{field}: {warning}" });
        return new TranscriptionResponse
        {
            Text = text ?? string.Join(" ", segments.Select(s => s.Text)),
            Language = ReadString(result, "detected_language") ?? ReadString(result, "language"),
            DurationInSeconds = ReadNumber(result, "duration_seconds") ?? ReadNumber(result, "duration"),
            Segments = segments,
            Warnings = warnings,
            Request = new() { Body = JsonSerializer.Serialize(options) },
            Response = new() { Timestamp = timestamp, ModelId = request.Model.ToModelId(ProviderId), Body = metadata },
            ProviderMetadata = new() { [ProviderId] = metadata.Clone() }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        // The native shim preserves raw output, caption bytes and its stricter parameter validation.
        var native = new OpenAITranscriptionRequest
        {
            File = options.File,
            Model = LocalModel(options.Model),
            Language = options.Language,
            Prompt = options.Prompt,
            ResponseFormat = options.ResponseFormat,
            Temperature = options.Temperature,
            TimestampGranularities = options.TimestampGranularities,
            Stream = options.Stream,
            Include = options.Include,
            ChunkingStrategy = options.ChunkingStrategy,
            KnownSpeakerNames = options.KnownSpeakerNames,
            KnownSpeakerReferences = options.KnownSpeakerReferences,
            AdditionalProperties = options.AdditionalProperties
        };
        return _client.OpenAICompatibleTranscriptionRequestWithHeadersAsync(
            native, new Dictionary<string, string> { ["Authorization"] = $"Bearer {ResolveKey()}" },
            new Uri(ApiBase, "audio/transcriptions").AbsoluteUri, cancellationToken);
    }
}
