using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{
    private const int MaxDecisionQuestions = 100;
    private const string DefaultDecisionMode = "fast";

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());

        if (request.Questions.Count is < 1 or > MaxDecisionQuestions)
            throw new ArgumentException($"Parallel supports 1 to {MaxDecisionQuestions} decision questions.", nameof(request));

        var (mode, advancedSettings) = GetDecisionOptions(request);
        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (!IsValidQuestionId(id))
                throw new ArgumentException($"Question ID '{id}' must contain only ASCII letters, numbers, underscores, and hyphens.", nameof(request));

            questions[id] = MapDecisionQuestion(question);
        }

        var payload = new JsonObject
        {
            ["state"] = JsonNode.Parse(request.State.Value.GetRawText()),
            ["questions"] = questions,
            ["mode"] = mode
        };
        if (advancedSettings is not null)
            payload["advanced_settings"] = JsonNode.Parse(advancedSettings.Value.GetRawText());

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Parallel)} API key.");

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = new StringContent(payload.ToJsonString(Json), Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("x-api-key", key);

        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Parallel decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var answers = MapDecisionAnswers(body, request);
            var usage = body.GetProperty("usage");
            var inputTokens = ReadTokenCount(usage, "input_tokens");
            var outputTokens = ReadTokenCount(usage, "output_tokens");
            if (!body.TryGetProperty("citations", out var citations) || citations.ValueKind != JsonValueKind.Array)
                throw new JsonException("Response citations must be an array.");
            if (!body.TryGetProperty("decision_id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString()))
                throw new JsonException("Response requires a decision_id.");
            var decisionId = idElement.GetString();

            return new DecisionResponse
            {
                Answers = answers,
                Usage = new() { InputTokens = inputTokens, OutputTokens = outputTokens },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone()),
                Response = new()
                {
                    Id = decisionId,
                    Timestamp = DateTimeOffset.UtcNow,
                    Headers = headers.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase),
                    Body = body.Clone()
                },
                Warnings = []
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidOperationException("Parallel returned an invalid decision response.", exception);
        }
    }

    private static (string Mode, JsonElement? AdvancedSettings) GetDecisionOptions(DecisionRequest request)
    {
        var mode = DefaultDecisionMode;
        JsonElement? advancedSettings = null;
        if (request.ProviderOptions?.TryGetValue("parallel", out var options) != true)
            return (mode, advancedSettings);

        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.parallel must be an object.", nameof(request));

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("mode"))
            {
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != DefaultDecisionMode)
                    throw new ArgumentException("Parallel Decisions only supports mode 'fast'.", nameof(request));
                mode = property.Value.GetString()!;
            }
            else if (property.NameEquals("advanced_settings"))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                    continue;
                if (property.Value.ValueKind != JsonValueKind.Object
                    || !property.Value.EnumerateObject().Any(item => item.NameEquals("location") || item.NameEquals("include_excerpts")))
                    throw new ArgumentException(
                        "Parallel advanced_settings must be an object containing location or include_excerpts.",
                        nameof(request));
                if (property.Value.TryGetProperty("location", out var location) && location.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Parallel advanced_settings.location must be a string.", nameof(request));
                if (property.Value.TryGetProperty("include_excerpts", out var includeExcerpts)
                    && includeExcerpts.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Parallel advanced_settings.include_excerpts must be a boolean.", nameof(request));
                advancedSettings = property.Value.Clone();
            }
            else
            {
                throw new ArgumentException($"Unsupported Parallel decision option '{property.Name}'.", nameof(request));
            }
        }

        return (mode, advancedSettings);
    }

    private static JsonObject MapDecisionQuestion(DecisionQuestion question)
    {
        var instructions = RenderDecisionInput(question.Instructions!);
        var mapped = new JsonObject
        {
            ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
            ["instructions"] = instructions
        };

        switch (question)
        {
            case DecisionBooleanQuestion { Criteria: not null } boolean:
                mapped["criteria"] = new JsonObject
                {
                    ["true"] = boolean.Criteria.True is null ? null : RenderDecisionInput(boolean.Criteria.True),
                    ["false"] = boolean.Criteria.False is null ? null : RenderDecisionInput(boolean.Criteria.False)
                };
                break;
            case DecisionBooleanQuestion:
                break;
            case DecisionChoiceQuestion { Criteria.Count: >= 2 and <= 255 } choice:
                var choices = new JsonObject();
                foreach (var (value, description) in choice.Criteria)
                    choices[value] = description is null ? null : RenderDecisionInput(description);
                mapped["criteria"] = choices;
                break;
            case DecisionScoreQuestion { Criteria.Count: >= 2 and <= 10 } score:
                if (score.Criteria.Any(level => level is null))
                    throw new ArgumentException("Parallel score criteria cannot be null.");
                var levels = new JsonArray();
                for (var index = 0; index < score.Criteria.Count; index++)
                    levels.Add(RenderDecisionInput(score.Criteria[index]!));
                mapped["criteria"] = levels;
                break;
            case DecisionChoiceQuestion:
                throw new ArgumentException("Parallel choice questions require 2 to 255 criteria.");
            case DecisionScoreQuestion:
                throw new ArgumentException("Parallel score questions require 2 to 10 criteria.");
            default:
                throw new ArgumentException("Parallel supports noul, choice, and score questions only.");
        }

        return mapped;
    }

    private static string RenderDecisionInput(DecisionInput input)
        => input.Value.ValueKind == JsonValueKind.String
            ? input.Value.GetString()!
            : input.Value.GetRawText();

    private static bool IsValidQuestionId(string id)
        => !string.IsNullOrEmpty(id)
            && id.All(character => character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-');

    private static Dictionary<string, DecisionAnswer> MapDecisionAnswers(JsonElement body, DecisionRequest request)
    {
        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count)
            throw new JsonException("Answers must contain exactly one entry per question.");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            if (!nativeAnswers.TryGetProperty(id, out var answer))
                throw new JsonException($"Missing answer for question '{id}'.");

            var type = answer.GetProperty("type").GetString();
            if (type == "refusal")
            {
                answers[id] = new DecisionRefusalAnswer();
                continue;
            }

            switch (question, type)
            {
                case (DecisionBooleanQuestion, "noul"):
                    answers[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadProbability(answer.GetProperty("noul"))
                    };
                    break;
                case (DecisionChoiceQuestion choice, "choice"):
                    var selected = answer.GetProperty("choice").GetString()
                        ?? throw new JsonException("Missing selected choice.");
                    if (!choice.Criteria.ContainsKey(selected))
                        throw new JsonException("Selected choice is not one of the submitted choices.");
                    answers[id] = new DecisionChoiceAnswer
                    {
                        Choice = selected,
                        Probabilities = answer.TryGetProperty("probabilities", out var choiceProbabilities)
                            ? ReadProbabilityMap(choiceProbabilities, choice.Criteria.Keys)
                            : null
                    };
                    break;
                case (DecisionScoreQuestion score, "score"):
                    var scoreValue = answer.GetProperty("score").GetDouble();
                    if (!double.IsFinite(scoreValue) || scoreValue < 0 || scoreValue > score.Criteria.Count - 1)
                        throw new JsonException("Score must be a finite value within the submitted levels.");
                    answers[id] = new DecisionScoreAnswer
                    {
                        Score = scoreValue,
                        Probabilities = answer.TryGetProperty("probabilities", out var scoreProbabilities)
                            ? ReadProbabilityMap(
                                scoreProbabilities,
                                Enumerable.Range(0, score.Criteria.Count).Select(index => index.ToString(CultureInfo.InvariantCulture)))
                            : null
                    };
                    break;
                default:
                    throw new JsonException($"Answer type does not match question '{id}'.");
            }
        }

        return answers;
    }

    private static Dictionary<string, double> ReadProbabilityMap(JsonElement value, IEnumerable<string> expectedKeys)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");

        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadProbability(property.Value)))
                throw new JsonException("Probability map contains an unknown or duplicate key.");
        }

        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 0.000001)
            throw new JsonException("Probability map must cover all options and sum to one.");
        return result;
    }

    private static double ReadProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var probability)
            || !double.IsFinite(probability) || probability is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return probability;
    }

    private static int ReadTokenCount(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var value) || !value.TryGetInt32(out var count) || count < 0)
            throw new JsonException($"Usage requires a nonnegative integer '{name}'.");
        return count;
    }
}
