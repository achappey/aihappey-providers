using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Allternit;

public sealed partial class AllternitProvider : IModelProvider
{
    private readonly IApiKeyResolver _keys;
    private readonly HttpClient _client;

    public AllternitProvider(IApiKeyResolver keys, IHttpClientFactory clients)
    {
        _keys = keys;
        _client = clients.CreateClient();
    }

    public string GetIdentifier() => "allternit";

    private static AIRequest NativeRequest(AIRequest request, Dictionary<string, JsonElement>? additional)
    {
        var metadata = request.Metadata is null ? new Dictionary<string, object?>() : new(request.Metadata);
        metadata["allternit"] = additional?.TryGetValue("allternit", out var native) == true
            ? native.Clone() : JsonSerializer.SerializeToElement(new { });
        return new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Input = request.Input, Tools = request.Tools,
            Metadata = metadata
        };
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(NativeRequest(options.ToUnifiedRequest(GetIdentifier()), options.AdditionalProperties), cancellationToken);
        var result = response.ToChatCompletion();
        result.AdditionalProperties ??= new();
        result.AdditionalProperties["allternit"] = JsonSerializer.SerializeToElement(response.Metadata!["allternit"]);
        return result;
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = new ChatCompletionsUnifiedMapper.ChatCompletionsReverseStreamMappingState();
        var id = $"chatcmpl-allternit-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await foreach (var evt in StreamUnifiedAsync(NativeRequest(options.ToUnifiedRequest(GetIdentifier()), options.AdditionalProperties), cancellationToken))
        {
            var update = evt.ToChatCompletionUpdate(state);
            update.Id = id;
            update.Created = created;
            update.Model = options.Model.StartsWith("allternit/", StringComparison.OrdinalIgnoreCase)
                ? options.Model : $"allternit/{options.Model}";
            update.AdditionalProperties ??= new();
            update.AdditionalProperties["allternit"] = JsonSerializer.SerializeToElement(evt.Metadata!["allternit"]);
            if (evt.Event.Data is AIFinishEventData finish && (finish.InputTokens is not null || finish.OutputTokens is not null))
                update.Usage = new AIUsage
                {
                    InputTokens = finish.InputTokens, OutputTokens = finish.OutputTokens, TotalTokens = finish.TotalTokens
                }.ToChatCompletionUsage();
            yield return update;
        }
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(NativeRequest(options.ToUnifiedRequest(GetIdentifier()), options.AdditionalProperties), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(NativeRequest(options.ToUnifiedRequest(GetIdentifier()), options.AdditionalProperties), cancellationToken)
            .ToResponseStreamParts(cancellationToken)) yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(NativeRequest(request.ToUnifiedRequest(GetIdentifier()), request.AdditionalProperties), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(NativeRequest(request.ToUnifiedRequest(GetIdentifier()), request.AdditionalProperties), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken)) yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var part in evt.Event.ToUIMessagePart(GetIdentifier())) yield return part;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
