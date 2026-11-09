using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.JouleCloud;

public sealed partial class JouleCloudProvider : IModelProvider, IDisposable
{
    private readonly HttpClient _client;

    public JouleCloudProvider(IApiKeyResolver keys, IHttpMessageHandlerFactory handlers)
    {
        // Keep the resolver outside the factory's pooled handler scope. Authentication
        // belongs to each outgoing request, not to the shared transport or its defaults.
        _client = new HttpClient(new AuthenticationHandler(keys)
        {
            InnerHandler = handlers.CreateHandler("joulecloud")
        }, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.greenjoules.cloud/")
        };
    }

    public string GetIdentifier() => "joulecloud";

    public void Dispose() => _client.Dispose();

    private sealed class AuthenticationHandler(IApiKeyResolver keys) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = keys.Resolve("joulecloud");
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("No JouleCloud API key.");

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static string LocalModel(string model)
        => model.StartsWith("joulecloud/", StringComparison.OrdinalIgnoreCase) ? model["joulecloud/".Length..] : model;

    private static T NativeOptions<T>(T options)
    {
        var root = JsonSerializer.SerializeToNode(options, JsonSerializerOptions.Web)!.AsObject();
        if (root.Remove("joulecloud", out var native) && native is JsonObject fields)
            foreach (var field in fields)
                root[field.Key] = field.Value?.DeepClone();
        if (root["model"] is JsonValue model && model.TryGetValue<string>(out var id))
            root["model"] = LocalModel(id);
        return root.Deserialize<T>(JsonSerializerOptions.Web)!;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var native = NativeOptions(options);
        native.Stream = false;
        return this.GetChatCompletion(_client, native, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => this.GetChatCompletions(_client, NativeOptions(options), cancellationToken: cancellationToken);

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var part in evt.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        return await ModelTypeAsync(request.Model, cancellationToken) switch
        {
            "image" => await this.ExecuteUnifiedImageAsync(request, cancellationToken),
            "speech" => await this.ExecuteUnifiedSpeechAsync(request, cancellationToken),
            "transcription" => await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken, TranscriptionOptions(request)),
            _ => await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var type = await ModelTypeAsync(request.Model, cancellationToken);
        var stream = type switch
        {
            "image" or "speech" or "transcription" => StreamBufferedMediaAsync(request, type, cancellationToken),
            _ => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
        };
        await foreach (var evt in stream.WithCancellation(cancellationToken))
            yield return evt;
    }

    private static Dictionary<string, JsonElement>? TranscriptionOptions(AIRequest request)
        => request.Metadata?.TryGetValue("joulecloud", out var options) == true
            ? JsonSerializer.SerializeToElement(options).EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone())
            : null;

    private async IAsyncEnumerable<AIStreamEvent> StreamBufferedMediaAsync(AIRequest request, string type,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Media endpoints are non-streaming. Reuse the complete-response adapters
        // to retain provider-scoped options instead of synthesized stream defaults.
        var response = type switch
        {
            "image" => await this.ExecuteUnifiedImageAsync(request, cancellationToken),
            "speech" => await this.ExecuteUnifiedSpeechAsync(request, cancellationToken),
            _ => await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken, TranscriptionOptions(request))
        };
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        var finishReason = "stop";
        foreach (var part in response.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [])
        {
            if (part is AIToolCallContentPart tool)
            {
                finishReason = "tool-calls";
                yield return MediaEvent("tool-input-available", tool.ToolCallId, new AIToolInputAvailableEventData
                {
                    ToolName = tool.ToolName!, Title = tool.Title, Input = tool.Input!, ProviderExecuted = true
                });
                yield return MediaEvent("tool-output-available", tool.ToolCallId, new AIToolOutputAvailableEventData
                {
                    ToolName = tool.ToolName!, Output = tool.Output!, ProviderExecuted = true, Preliminary = false
                });
            }
            else if (part is AITextContentPart text)
            {
                yield return MediaEvent("text-start", id, new AITextStartEventData());
                yield return MediaEvent("text-delta", id, new AITextDeltaEventData { Delta = text.Text });
                yield return MediaEvent("text-end", id, new AITextEndEventData());
            }
        }
        yield return MediaEvent("finish", id, new AIFinishEventData { Model = request.Model, FinishReason = finishReason });
    }

    private AIStreamEvent MediaEvent(string type, string id, object data) => new()
    {
        ProviderId = GetIdentifier(),
        Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data }
    };

    private async Task<string> ModelTypeAsync(string model, CancellationToken cancellationToken)
    {
        var id = LocalModel(model);
        if (id == "auto")
            return "language";
        var models = await ListModels(cancellationToken);
        return models.FirstOrDefault(m => LocalModel(m.Id) == id)?.Type ?? id.GuessModelType();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
