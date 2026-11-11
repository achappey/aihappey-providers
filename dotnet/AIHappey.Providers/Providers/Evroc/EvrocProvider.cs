using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Evroc;

public partial class EvrocProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;
    private readonly AsyncCacheHelper _memoryCache;

    public EvrocProvider(
        IApiKeyResolver keyResolver,
        AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://models.think.evroc.com/");
    }

    public string GetIdentifier() => "evroc";

    private IReadOnlyDictionary<string, string> GetAuthHeaders()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Evroc API key.");

        return new Dictionary<string, string>
        {
            ["Authorization"] = new AuthenticationHeaderValue("Bearer", key).ToString()
        };
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => this.GetChatCompletion(_client, options, GetAuthHeaders(), cancellationToken: cancellationToken);

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in this.GetChatCompletions(
            _client, options, GetAuthHeaders(), cancellationToken: cancellationToken))
            yield return update;
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(
        Responses.ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedViaChatCompletionsAsync(
            options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = options.ToUnifiedRequest(GetIdentifier());
        await foreach (var part in StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await _client.SendAsync(request, ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Evroc model listing failed ({(int)response.StatusCode}): {raw}");

                using var document = JsonDocument.Parse(raw);
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Evroc model listing did not contain a data array.");

                return data.EnumerateArray()
                    .Where(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(item =>
                    {
                        var nativeId = item.GetProperty("id").GetString()!;
                        var model = new Model
                        {
                            Id = nativeId.ToModelId(GetIdentifier()),
                            Name = nativeId,
                            OwnedBy = item.TryGetProperty("owned_by", out var owner) ? owner.GetString() ?? string.Empty : string.Empty,
                            Description = item.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty,
                            Type = GuessModelType(nativeId)
                        };
                        return model;
                    })
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static string GuessModelType(string modelId)
    {
        if (modelId.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            return "transcription";
        if (modelId.Contains("embed", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("e5-", StringComparison.OrdinalIgnoreCase))
            return "embedding";
        return "language";
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedViaChatCompletionsAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedViaChatCompletionsAsync(
            request.ToUnifiedRequest(GetIdentifier()), cancellationToken: cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
            chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, request, GetAuthHeaders(), cancellationToken: cancellationToken)).Response;

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client, openAIRequest, GetAuthHeaders(), cancellationToken: cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => SendEvrocTranscriptionAsync(request, cancellationToken);

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var metadata = request.ProviderOptions?.TryGetValue(GetIdentifier(), out var existing) == true
            && existing.ValueKind == JsonValueKind.Object
                ? existing.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.ResponseFormat))
            metadata["response_format"] = JsonSerializer.SerializeToElement(options.ResponseFormat);
        foreach (var (name, value) in options.AdditionalProperties ?? [])
            metadata[name] = value.Clone();
        var nativeRequest = new TranscriptionRequest
        {
            Model = request.Model,
            Audio = request.Audio,
            MediaType = request.MediaType,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata)
            }
        };
        var response = await TranscriptionRequest(nativeRequest, cancellationToken);
        return response.ToOpenAITranscriptionResponse(options.ResolveOpenAITranscriptionResponseFormat());
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<TranscriptionResponse> SendEvrocTranscriptionAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);

        var audio = request.Audio is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = audio.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Audio data URL is invalid.", nameof(request));
            audio = audio[(comma + 1)..];
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(audio); }
        catch (FormatException exception) { throw new ArgumentException("Audio must be valid base64.", nameof(request), exception); }

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio" + GetAudioExtension(request.MediaType));
        form.Add(new StringContent(request.Model), "model");
        var metadata = request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            && providerOptions.ValueKind == JsonValueKind.Object
                ? providerOptions
                : default;
        var hasResponseFormat = false;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Name.Equals("file", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("model", StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;
                hasResponseFormat |= property.Name.Equals("response_format", StringComparison.OrdinalIgnoreCase);
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.GetRawText();
                form.Add(new StringContent(value ?? string.Empty), property.Name);
            }
        }
        if (!hasResponseFormat)
            form.Add(new StringContent("verbose_json"), "response_format");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = form };
        httpRequest.Headers.Authorization = AuthenticationHeaderValue.Parse(GetAuthHeaders()["Authorization"]);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Evroc transcription failed ({(int)response.StatusCode}): {raw}");

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(raw);
            root = document.RootElement.Clone();
        }
        catch (JsonException) when (response.Content.Headers.ContentType?.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true)
        {
            root = JsonSerializer.SerializeToElement(new { text = raw });
        }
        var segments = root.TryGetProperty("segments", out var segmentArray) && segmentArray.ValueKind == JsonValueKind.Array
            ? segmentArray.EnumerateArray().Select(segment => new TranscriptionSegment
            {
                Text = GetString(segment, "text") ?? string.Empty,
                StartSecond = GetFloat(segment, "start"),
                EndSecond = GetFloat(segment, "end")
            }).ToList()
            : [];
        return new TranscriptionResponse
        {
            Text = GetString(root, "text") ?? string.Join(" ", segments.Select(segment => segment.Text)),
            Language = GetString(root, "language"),
            DurationInSeconds = GetNullableFloat(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static string GetAudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" => ".mp4",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/webm" => ".webm",
        "audio/flac" => ".flac",
        _ => ".audio"
    };

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float GetFloat(JsonElement element, string name) => GetNullableFloat(element, name) ?? 0;

    private static float? GetNullableFloat(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetSingle(out var number) ? number : null;

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.InputAudioFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputAudioFormat.Type);

        yield return new TranscriptionStreamStartPart();
        var response = await TranscriptionRequest(new TranscriptionRequest
        {
            Model = request.Model,
            Audio = request.Audio,
            MediaType = request.InputAudioFormat.Type,
            ProviderOptions = request.ProviderOptions
        }, cancellationToken);

        if (request.IncludeRawChunks == true)
            yield return new TranscriptionRawPart { RawValue = response.Response.Body };
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new TranscriptionDeltaPart
            {
                Delta = response.Text,
                ProviderMetadata = response.ProviderMetadata
            };
        yield return new TranscriptionFinishPart
        {
            Text = response.Text,
            Language = response.Language,
            DurationInSeconds = response.DurationInSeconds,
            Segments = response.Segments,
            ProviderMetadata = response.ProviderMetadata
        };
    }
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
