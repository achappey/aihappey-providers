using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.JouleCloud;

public sealed partial class JouleCloudProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
        => (await this.OpenAICompatibleEmbeddingRequestAsync(_client, NativeOptions(request), cancellationToken: cancellationToken)).Response;

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(_client,
            NativeOptions(request.ToOpenAIEmbeddingRequest(GetIdentifier())), cancellationToken: cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Response));
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        request.ValidateOpenAIImageGenerationRequest();
        var native = NativeOptions(request);
        native.Stream = false;
        return _client.OpenAICompatibleImageGenerationRequestAsync(native, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        request.ValidateOpenAIImageGenerationRequest();
        var native = NativeOptions(request);
        native.Stream = false;
        // JouleCloud documents JSON image generation, not an image SSE endpoint.
        await foreach (var evt in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(native, cancellationToken: cancellationToken))
            yield return evt;
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("JouleCloud does not support image edits.");

        var fields = ProviderFields(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        fields["model"] = LocalModel(request.Model);
        fields["prompt"] = request.Prompt;
        fields["n"] = request.N;
        if (!string.IsNullOrWhiteSpace(request.Size))
            fields["size"] = request.Size;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            fields["aspect_ratio"] = request.AspectRatio;
        if (request.Seed.HasValue)
            fields["seed"] = request.Seed.Value;
        fields["response_format"] ??= "b64_json";
        var native = fields.Deserialize<OpenAIImageGenerationRequest>(JsonSerializerOptions.Web)!;
        var response = await OpenAIImageGenerationRequestAsync(native, cancellationToken);
        var format = response.OutputFormat ?? native.OutputFormat ?? "png";
        var mime = format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : $"image/{format}";
        var images = (response.Data ?? [])
            .Where(image => !string.IsNullOrWhiteSpace(image.B64Json))
            .Select(image => $"data:{mime};base64,{image.B64Json}").ToArray();
        if (images.Length == 0)
            throw new InvalidOperationException("JouleCloud returned no inline images. Use response_format 'b64_json'.");

        return new ImageResponse
        {
            Images = images,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(response),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = $"{GetIdentifier()}/{LocalModel(request.Model)}"
            }
        };
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest request,
        CancellationToken cancellationToken = default)
        => _client.OpenAICompatibleSpeechRequestAsync(NativeOptions(request), cancellationToken: cancellationToken);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = ProviderFields(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        fields["model"] = LocalModel(request.Model);
        fields["input"] = request.Text;
        if (!string.IsNullOrWhiteSpace(request.Voice))
            fields["voice"] ??= request.Voice;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            fields["response_format"] ??= request.OutputFormat;
        if (request.Speed.HasValue)
            fields["speed"] ??= JsonSerializer.SerializeToNode(request.Speed.Value);
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            fields["instructions"] ??= request.Instructions;
        var native = fields.Deserialize<AudioSpeechRequest>(JsonSerializerOptions.Web)!;
        var (audio, mimeType) = await OpenAISpeechRequestAsync(native, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = mimeType,
                Format = native.ResponseFormat ?? "mp3"
            },
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = $"{GetIdentifier()}/{LocalModel(request.Model)}"
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var native = NativeOptions(request);
        native.File = request.File;
        native.Stream = false;
        return _client.OpenAICompatibleTranscriptionRequestAsync(native, cancellationToken: cancellationToken);
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audio = request.Audio is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));
        if (MediaContentHelpers.TryParseDataUrl(audio, out _, out var base64))
            audio = base64;
        using var stream = new MemoryStream(Convert.FromBase64String(audio));
        var fields = ProviderFields(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        fields["model"] = LocalModel(request.Model);
        fields["response_format"] ??= "verbose_json";
        var native = fields.Deserialize<OpenAITranscriptionRequest>(JsonSerializerOptions.Web)!;
        native.File = new FormFile(stream, 0, stream.Length, "file", "audio" + request.MediaType.GetAudioExtension())
        {
            Headers = new HeaderDictionary(),
            ContentType = request.MediaType
        };
        var response = await OpenAITranscriptionRequestAsync(native, cancellationToken);
        var body = JsonSerializer.SerializeToElement(response, response.GetType(), JsonSerializerOptions.Web);
        return new TranscriptionResponse
        {
            Text = response.Text,
            Language = ReadString(body, "language"),
            DurationInSeconds = response switch
            {
                OpenAITranscriptionVerboseResponse verbose => verbose.Duration,
                OpenAITranscriptionDiarizedResponse diarized => diarized.Duration,
                _ => null
            },
            Segments = response switch
            {
                OpenAITranscriptionVerboseResponse timed => (timed.Segments ?? []).Select(segment => new TranscriptionSegment
                {
                    Text = segment.Text, StartSecond = segment.Start, EndSecond = segment.End
                }).ToArray(),
                OpenAITranscriptionDiarizedResponse diarized => diarized.Segments.Select(segment => new TranscriptionSegment
                {
                    Text = segment.Text, StartSecond = segment.Start, EndSecond = segment.End
                }).ToArray(),
                _ => []
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = $"{GetIdentifier()}/{LocalModel(request.Model)}",
                Body = body
            }
        };
    }

    private static JsonObject ProviderFields(JsonElement options)
    {
        if (options.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new JsonObject();
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("JouleCloud provider options must be a JSON object.");
        return JsonNode.Parse(options.GetRawText())!.AsObject();
    }
}
