using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Boundless;

public partial class BoundlessProvider
{
    private const string DecisionProvider = "boundless";
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var model = request.Model.StartsWith(DecisionProvider + "/", StringComparison.OrdinalIgnoreCase)
            ? request.Model[(DecisionProvider.Length + 1)..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.Questions is null || request.Questions.Count == 0)
            throw new ArgumentException("At least one decision question is required.", nameof(request));
        if (request.Reasoning.HasValue)
            throw new NotSupportedException("Boundless System One does not document a reasoning control.");

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null)
                throw new ArgumentException("Decision questions require an ID and instructions.", nameof(request));
            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = JsonSerializer.SerializeToNode(question.Instructions, DecisionJsonOptions)
            };
            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is not null)
                        mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, DecisionJsonOptions);
                    break;
                case DecisionChoiceQuestion choice when choice.Criteria is not null && choice.Criteria.Count >= 2:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, DecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score when score.Criteria is not null && score.Criteria.Count >= 2
                    && score.Criteria.All(level => level is not null):
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, DecisionJsonOptions);
                    break;
                default:
                    throw new ArgumentException($"Question '{id}' has unsupported criteria.", nameof(request));
            }
            questions[id] = mapped;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(request.State, DecisionJsonOptions),
            ["questions"] = questions
        };
        if (request.ProviderOptions?.TryGetValue(DecisionProvider, out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.boundless must be an object.", nameof(request));
            foreach (var property in options.EnumerateObject())
            {
                if (!property.Name.Equals("model", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Equals("state", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Equals("questions", StringComparison.OrdinalIgnoreCase))
                    payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Boundless API key.");
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = JsonContent.Create(payload, options: DecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(DecisionProvider) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Boundless decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}", null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var answers = MapBoundlessAnswers(body, request);
            var usage = body.GetProperty("usage");
            var input = usage.GetProperty("input_tokens").GetInt32();
            var output = usage.GetProperty("output_tokens").GetInt32();
            if (input < 0 || output < 0)
                throw new JsonException("Invalid decision usage.");
            return new DecisionResponse
            {
                Answers = answers,
                Usage = new() { InputTokens = input, OutputTokens = output },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone()),
                Response = new()
                {
                    ModelId = model.ToModelId(GetIdentifier()), Timestamp = DateTimeOffset.UtcNow,
                    Id = headers.GetValueOrDefault("x-request-id"),
                    Headers = headers.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase),
                    Body = body.Clone()
                },
                Warnings = []
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Boundless returned an invalid decision response.", ex);
        }
    }

    private static Dictionary<string, DecisionAnswer> MapBoundlessAnswers(JsonElement body, DecisionRequest request)
    {
        var native = body.GetProperty("answers");
        if (native.ValueKind != JsonValueKind.Object
            || native.EnumerateObject().Count() != request.Questions.Count
            || native.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = native.GetProperty(id);
            var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
            if (answer.TryGetProperty("type", out var type) && type.GetString() != expectedType)
                throw new JsonException("Answer type does not match question.");
            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer { Probability = BoundlessProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    BoundlessProbability(answer.GetProperty("confidence"));
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    var distribution = BoundlessDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (!distribution.ContainsKey(selected))
                        throw new JsonException("Unknown choice.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution };
                    break;
                case DecisionScoreQuestion score:
                    BoundlessProbability(answer.GetProperty("confidence"));
                    var probabilities = BoundlessDistribution(answer.GetProperty("probabilities"),
                        Enumerable.Range(0, score.Criteria.Count).Select(BoundlessIndex));
                    var value = answer.GetProperty("score").GetDouble();
                    if (!double.IsFinite(value) || value < 0 || value > score.Criteria.Count - 1)
                        throw new JsonException("Invalid score.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = probabilities };
                    break;
                default:
                    throw new JsonException("Unsupported question type.");
            }
        }
        return answers;
    }

    private static double BoundlessProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static Dictionary<string, double> BoundlessDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, BoundlessProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        // Each probability may be rounded independently to four decimal places.
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > expected.Count * 0.00005 + 1e-9)
            throw new JsonException("Incomplete or unnormalized distribution.");
        return result;
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null)
            throw new ArgumentException("Boundless System One does not support safety_identifier.", nameof(request));

        DecisionInput state;
        if (request.Input.Text is not null)
            state = request.Input.Text;
        else
        {
            if (request.Input.Messages!.Any(message => message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true))
                throw new NotSupportedException("Boundless Decisions does not support OpenAI image input parts.");
            state = new DecisionInput(JsonSerializer.SerializeToElement(request.Input, DecisionJsonOptions));
        }
        var universal = new DecisionRequest { Model = request.Model, State = state };
        if (request.AdditionalProperties is { Count: > 0 })
            universal.ProviderOptions = new()
            {
                [DecisionProvider] = JsonSerializer.SerializeToElement(request.AdditionalProperties, DecisionJsonOptions)
            };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            universal.Questions[BoundlessQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create(BoundlessOptionId(i),
                        (DecisionInput?)new DecisionInput(DescribeBoundlessLevel(
                            option.Value.Value.ValueKind == JsonValueKind.String ? option.Value.Value.GetString()! : option.Value.Value.GetRawText(), option.Description))))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(DescribeBoundlessLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question type.", nameof(request))
            };
        }

        var normalized = await DecisionRequestAsync(universal, cancellationToken);
        var raw = normalized.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = BoundlessQuestionId(index);
            var question = request.Questions[index];
            var original = raw.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer mapped = (question, normalized.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) => new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) => new OpenAIDecisionChoiceAnswer
                {
                    Choice = choice.Choices[int.Parse(selected.Choice["option-".Length..], CultureInfo.InvariantCulture)].Value,
                    Confidence = original.GetProperty("confidence").GetDouble(),
                    Probabilities = choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                    { Value = option.Value, Probability = selected.Probabilities![BoundlessOptionId(i)] }).ToArray()
                },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) => new OpenAIDecisionScoreAnswer
                {
                    Score = scored.Score / (score.Levels.Count - 1d),
                    Confidence = original.GetProperty("confidence").GetDouble(),
                    Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                    { Label = level.Label, Value = i / (score.Levels.Count - 1d), Probability = scored.Probabilities![BoundlessIndex(i)] }).ToArray()
                },
                _ => throw new InvalidOperationException("Unexpected Boundless decision answer type.")
            };
            mapped.Name = question.Name;
            answers.Add(mapped);
        }
        var input = normalized.Usage!.InputTokens!.Value;
        var output = normalized.Usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse
        {
            Model = request.Model.StartsWith(DecisionProvider + "/", StringComparison.OrdinalIgnoreCase)
                ? request.Model[(DecisionProvider.Length + 1)..] : request.Model,
            Answers = answers,
            Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = checked(input + output) },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(normalized.ProviderMetadata, DecisionJsonOptions),
                ["warnings"] = JsonSerializer.SerializeToElement(new[] { new
                {
                    type = "other", message = "Boundless does not report cached, cache-write, or reasoning tokens; OpenAI-compatible detail counters default to zero. Score values use an evenly spaced [0,1] scale."
                } }, DecisionJsonOptions)
            }
        };
    }

    private static string BoundlessIndex(int index) => index.ToString(CultureInfo.InvariantCulture);
    private static string BoundlessQuestionId(int index) => "question-" + BoundlessIndex(index);
    private static string BoundlessOptionId(int index) => "option-" + BoundlessIndex(index);
    private static string DescribeBoundlessLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
