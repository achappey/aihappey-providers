using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private static OpenAIDecisionResponse MapGoogleDecisionResponse(
        JsonElement root, string requestedModel, IReadOnlyList<GoogleDecisionQuestion> questions)
    {
        var answers = root.GetProperty("answers");
        if (answers.ValueKind != JsonValueKind.Object || answers.EnumerateObject().Count() != questions.Count
            || answers.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != questions.Count)
            throw new JsonException("Google decision answers must cover every question exactly once.");
        var result = new List<OpenAIDecisionAnswer>(questions.Count);
        foreach (var question in questions)
        {
            var answer = answers.GetProperty(question.Id);
            if (answer.GetProperty("type").GetString() != question.Type)
                throw new JsonException("Google decision answer type does not match its question.");
            var confidence = ReadGoogleDecisionProbability(answer.GetProperty("confidence"));
            OpenAIDecisionAnswer mapped;
            if (question.Type == "noul")
            {
                var probability = ReadGoogleDecisionProbability(answer.GetProperty("noul"));
                if (probability is < 0.01 or > 0.99)
                    throw new JsonException("Google yes/no probabilities must be in [0.01, 0.99].");
                if (Math.Abs(confidence - Math.Max(probability, 1 - probability))
                    > GoogleDecisionRoundingError(answer.GetProperty("confidence")) + GoogleDecisionRoundingError(answer.GetProperty("noul")) + 1e-9)
                    throw new JsonException("Google yes/no confidence does not match its probability.");
                mapped = new OpenAIDecisionPredicateAnswer { Probability = probability };
            }
            else
            {
                var keys = question.Type == "choice" ? question.Options!.Select(option => option.Id).ToArray()
                    : Enumerable.Range(0, question.Labels!.Count).Select(GoogleDecisionIndex).ToArray();
                var probabilities = ReadGoogleDecisionDistribution(answer.GetProperty("probabilities"), keys);
                var max = probabilities.Values.Max(item => item.Value);
                var largestError = probabilities.Values.Max(item => item.Error);
                var tolerance = largestError + GoogleDecisionRoundingError(answer.GetProperty("confidence")) + 1e-9;
                if (Math.Abs(confidence - max) > tolerance)
                    throw new JsonException("Google decision confidence must match the most likely outcome.");
                if (question.Type == "choice")
                {
                    var selected = answer.GetProperty("choice").GetString();
                    var option = question.Options!.SingleOrDefault(item => item.Id == selected);
                    if (option is null || max - probabilities[option.Id].Value > largestError + probabilities[option.Id].Error + 1e-9)
                        throw new JsonException("Google decision choice must be a supplied option with maximal probability.");
                    mapped = new OpenAIDecisionChoiceAnswer
                    {
                        Choice = option.Value,
                        Confidence = confidence,
                        Probabilities = question.Options.Select(item => new OpenAIDecisionChoiceProbability
                        {
                            Value = item.Value, Probability = probabilities[item.Id].Value
                        }).ToArray()
                    };
                }
                else
                {
                    var score = ReadGoogleDecisionNumber(answer.GetProperty("score"));
                    if (score < 0 || score > keys.Length - 1)
                        throw new JsonException("Google decision score is outside its supplied rubric.");
                    var expected = keys.Select((key, index) => index * probabilities[key].Value).Sum();
                    var scoreTolerance = keys.Select((key, index) => index * probabilities[key].Error).Sum()
                        + GoogleDecisionRoundingError(answer.GetProperty("score")) + 1e-9;
                    if (Math.Abs(score - expected) > scoreTolerance)
                        throw new JsonException("Google decision score does not match its level distribution.");
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var description) || description.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(description.GetString())))
                        throw new JsonException("Google decision score legend must describe every level.");
                    mapped = new OpenAIDecisionScoreAnswer
                    {
                        Score = score, Confidence = confidence,
                        Probabilities = keys.Select((key, index) => new OpenAIDecisionScoreProbability
                        {
                            Label = question.Labels![index], Value = index, Probability = probabilities[key].Value
                        }).ToArray()
                    };
                }
            }
            mapped.Name = question.Name;
            result.Add(mapped);
        }

        var usage = root.GetProperty("usage");
        var inputTokens = ReadGoogleDecisionTokens(usage.GetProperty("total_input_tokens"));
        var outputTokens = ReadGoogleDecisionTokens(usage.GetProperty("total_output_tokens"));
        var totalTokens = ReadGoogleDecisionTokens(usage.GetProperty("total_tokens"));
        if (outputTokens != 0 || totalTokens != inputTokens)
            throw new JsonException("Google Decisions generates zero output tokens; total tokens must equal input tokens.");
        if (usage.TryGetProperty("input_tokens_by_modality", out var modalities))
        {
            if (modalities.ValueKind != JsonValueKind.Array)
                throw new JsonException("Google decision modality usage must be an array.");
            foreach (var modality in modalities.EnumerateArray())
            {
                if (modality.GetProperty("modality").ValueKind != JsonValueKind.String)
                    throw new JsonException("Google decision token modalities must be strings.");
                _ = ReadGoogleDecisionTokens(modality.GetProperty("tokens"));
            }
        }
        var model = requestedModel;
        if (root.TryGetProperty("model", out var nativeModel))
        {
            if (nativeModel.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nativeModel.GetString()))
                throw new JsonException("Google decision model must be a nonempty string when present.");
            model = nativeModel.GetString()!;
        }
        return new OpenAIDecisionResponse
        {
            Model = model, Answers = result,
            Usage = new() { InputTokens = inputTokens, OutputTokens = outputTokens, TotalTokens = totalTokens }
        };
    }

    private static Dictionary<string, (double Value, double Error)> ReadGoogleDecisionDistribution(JsonElement value, string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != keys.Length)
            throw new JsonException("Google decision probabilities must cover every supplied outcome.");
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, (double Value, double Error)>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name,
                (ReadGoogleDecisionProbability(property.Value), GoogleDecisionRoundingError(property.Value))))
                throw new JsonException("Google decision distribution contains unknown or duplicate outcomes.");
        }
        // Allow half a unit in the last reported decimal place, bounded to two
        // decimals. Integral 0/1 values are exact. Never renormalize upstream data.
        if (Math.Abs(result.Values.Sum(item => item.Value) - 1) > result.Values.Sum(item => item.Error) + 1e-9)
            throw new JsonException("Google decision probabilities must sum to one within their reported rounding precision.");
        return result;
    }

    private static double GoogleDecisionRoundingError(JsonElement value)
    {
        var number = ReadGoogleDecisionNumber(value);
        if (number == Math.Truncate(number)) return 0;
        var raw = value.GetRawText();
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var exponent = exponentIndex < 0 ? 0 : int.Parse(raw[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var decimalIndex = mantissa.IndexOf('.');
        var decimals = decimalIndex < 0 ? 0 : mantissa.Length - decimalIndex - 1;
        return Math.Min(0.005, 0.5 * Math.Pow(10, exponent - decimals));
    }

    private static double ReadGoogleDecisionNumber(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : throw new JsonException("Google decision values must be finite numbers.");

    private static double ReadGoogleDecisionProbability(JsonElement value)
    {
        var number = ReadGoogleDecisionNumber(value);
        return number is >= 0 and <= 1 ? number : throw new JsonException("Google decision probabilities must be in [0, 1].");
    }

    private static int ReadGoogleDecisionTokens(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var tokens) && tokens >= 0
            ? tokens : throw new JsonException("Google decision token counts must be nonnegative integers.");
}
