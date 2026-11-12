using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private static readonly JsonSerializerOptions GoogleDecisionJsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 100 };

    private sealed record GoogleDecisionOption(string Id, OpenAIDecisionChoiceValue Value);

    private sealed record GoogleDecisionQuestion(
        string Id, string? Name, string Type, JsonObject Payload,
        IReadOnlyList<GoogleDecisionOption>? Options = null,
        IReadOnlyList<string>? Labels = null);

    private async Task<DecisionResponse> ExecuteGoogleDecisionAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.MaxOutputTokens is not null || request.Reasoning is not null)
            throw new NotSupportedException("Google Decisions does not support maxOutputTokens or reasoning.");
        if (request.StateFormat is not (null or "sdk"))
            throw new ArgumentException("stateFormat must be omitted (JSON evidence) or 'sdk'.", nameof(request));

        var questions = MapGoogleDecisionQuestions(request.Questions);
        if (request.State.Value.ValueKind == JsonValueKind.String && request.State.Value.GetString()!.Length == 0)
            throw new ArgumentException("Google decision input cannot be empty.", nameof(request));
        JsonNode input = request.StateFormat == "sdk"
            ? MapGoogleDecisionSdkState(request.State.Value)
            : JsonValue.Create(GoogleDecisionEvidence(request.State.Value))!;
        JsonElement? options = request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            ? providerOptions : null;
        var result = await SendGoogleDecisionAsync(request.Model, input, questions, options, request.Headers, cancellationToken);

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        for (var index = 0; index < questions.Count; index++)
        {
            var answer = result.Response.Answers[index];
            answers.Add(questions[index].Id, answer switch
            {
                OpenAIDecisionPredicateAnswer predicate => new DecisionBooleanAnswer { Probability = predicate.Probability },
                OpenAIDecisionChoiceAnswer choice => new DecisionChoiceAnswer
                {
                    Choice = choice.Choice.Value.GetString()!,
                    Probabilities = choice.Probabilities.ToDictionary(item => item.Value.Value.GetString()!, item => item.Probability, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreAnswer score => new DecisionScoreAnswer
                {
                    Score = score.Score,
                    Probabilities = score.Probabilities.ToDictionary(item => item.Label, item => item.Probability, StringComparer.Ordinal)
                },
                _ => throw new InvalidOperationException("Unexpected Google decision answer type.")
            });
        }
        return new DecisionResponse
        {
            Answers = answers,
            Usage = new() { InputTokens = result.Response.Usage.InputTokens, OutputTokens = result.Response.Usage.OutputTokens },
            ProviderMetadata = new() { [GetIdentifier()] = result.Body.Clone() },
            Response = new()
            {
                ModelId = result.Response.Model.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Headers = result.Headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                Body = result.Body.Clone()
            }
        };
    }

    private async Task<OpenAIDecisionResponse> ExecuteGoogleOpenAIDecisionAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.SafetyIdentifier is not null || request.PromptCacheKey is not null || request.PromptCacheOptions is not null)
            throw new NotSupportedException("Google Decisions does not support safety_identifier or prompt-cache controls.");
        var questions = MapGoogleOpenAIDecisionQuestions(request.Questions);
        var input = MapGoogleOpenAIDecisionInput(request.Input);
        JsonElement? options = request.AdditionalProperties is null ? null : JsonSerializer.SerializeToElement(request.AdditionalProperties, GoogleDecisionJsonOptions);
        return (await SendGoogleDecisionAsync(request.Model, input, questions, options, null, cancellationToken)).Response;
    }

    private async Task<OpenAICompatibleDecisionResult> SendGoogleDecisionAsync(
        string model, JsonNode input, IReadOnlyList<GoogleDecisionQuestion> questions, JsonElement? options,
        Dictionary<string, string?>? headers, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.StartsWith(GetIdentifier() + "/", StringComparison.OrdinalIgnoreCase))
            model = model[(GetIdentifier().Length + 1)..];
        if (model.StartsWith("models/", StringComparison.Ordinal))
            model = model["models/".Length..];
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var nativeQuestions = new JsonObject();
        foreach (var question in questions)
            nativeQuestions.Add(question.Id, question.Payload);
        var payload = new JsonObject { ["model"] = model, ["input"] = input, ["questions"] = nativeQuestions };
        if (options.HasValue)
        {
            if (options.Value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Google decision provider options must be an object.");
            foreach (var property in options.Value.EnumerateObject())
            {
                if (new[] { "model", "input", "questions" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"Google decision options cannot override '{property.Name}'.");
                if (new[] { "safety_identifier", "safetyIdentifier", "prompt_cache_key", "prompt_cache_options", "promptCacheKey",
                    "promptCacheOptions", "cached_content", "cachedContent", "generation_config", "generationConfig",
                    "max_output_tokens", "maxOutputTokens", "reasoning", "service_tier", "serviceTier" }
                    .Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    throw new NotSupportedException($"Google Decisions does not support '{property.Name}'.");
                payload.Add(property.Name, ParseGoogleDecisionNode(property.Value));
            }
        }

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Google API key.");
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1beta/decisions")
        {
            Content = new StringContent(payload.ToJsonString(GoogleDecisionJsonOptions), Encoding.UTF8, "application/json")
        };
        // Authentication is request-scoped, not mutable HttpClient default state.
        message.Headers.Add("x-goog-api-key", key);
        foreach (var (name, value) in headers ?? [])
        {
            if (string.IsNullOrWhiteSpace(value) || name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader(name, GetIdentifier())
                || name.StartsWith("x-goog-", StringComparison.OrdinalIgnoreCase))
                message.Headers.TryAddWithoutValidation(name, value);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseHeaders = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Google decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}", null, response.StatusCode);
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 100 });
            var root = document.RootElement;
            var mapped = MapGoogleDecisionResponse(root, model, questions);
            mapped.AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [GetIdentifier()] = root.Clone() }, GoogleDecisionJsonOptions),
                ["responseHeaders"] = JsonSerializer.SerializeToElement(responseHeaders)
            };
            return new(mapped, responseHeaders, root.Clone());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Google returned an invalid Decisions response.", exception);
        }
    }

    private static string GoogleDecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static string GoogleDecisionEvidence(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static JsonNode? ParseGoogleDecisionNode(JsonElement value)
        => JsonNode.Parse(value.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = 100 });
}
