using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private static List<GoogleDecisionQuestion> MapGoogleDecisionQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ValidateGoogleDecisionCount(questions.Count, 1000, "questions");
        var result = new List<GoogleDecisionQuestion>(questions.Count);
        foreach (var (id, question) in questions)
        {
            if (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                throw new ArgumentException("Google decision question keys require 1 to 64 ASCII letters, digits, underscores, or hyphens.");
            ArgumentNullException.ThrowIfNull(question);
            ArgumentNullException.ThrowIfNull(question.Instructions);
            var instructions = RenderGoogleDecisionText(question.Instructions.Value);
            var payload = new JsonObject { ["instructions"] = instructions };
            GoogleDecisionQuestion mapping;
            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    payload["type"] = "noul";
                    if (boolean.Criteria is not null)
                    {
                        var criteria = new JsonObject();
                        if (boolean.Criteria.True is not null)
                            criteria["true_description"] = RenderGoogleDecisionText(boolean.Criteria.True.Value, allowEmpty: true);
                        if (boolean.Criteria.False is not null)
                            criteria["false_description"] = RenderGoogleDecisionText(boolean.Criteria.False.Value, allowEmpty: true);
                        payload["criteria"] = criteria;
                    }
                    mapping = new(id, id, "noul", payload);
                    break;
                case DecisionChoiceQuestion choice:
                    ArgumentNullException.ThrowIfNull(choice.Criteria);
                    ValidateGoogleDecisionCount(choice.Criteria.Count, 255, "choice options", 2);
                    var options = new List<GoogleDecisionOption>();
                    var choiceCriteria = new JsonObject();
                    foreach (var (key, description) in choice.Criteria)
                    {
                        if (!IsGoogleDecisionOptionId(key))
                            throw new ArgumentException($"Invalid Google decision option key '{key}'.");
                        choiceCriteria.Add(key, description is null ? "" : RenderGoogleDecisionText(description.Value, allowEmpty: true));
                        options.Add(new(key, new(key)));
                    }
                    payload["type"] = "choice";
                    payload["criteria"] = choiceCriteria;
                    mapping = new(id, id, "choice", payload, options);
                    break;
                case DecisionScoreQuestion score:
                    ArgumentNullException.ThrowIfNull(score.Criteria);
                    ValidateGoogleDecisionCount(score.Criteria.Count, 255, "score levels", 2);
                    var levels = new JsonArray();
                    foreach (var criterion in score.Criteria)
                    {
                        if (criterion is null)
                            throw new ArgumentException("Google score levels require nonempty descriptions.");
                        levels.Add(RenderGoogleDecisionText(criterion.Value));
                    }
                    payload["type"] = "score";
                    payload["criteria"] = levels;
                    mapping = new(id, id, "score", payload, Labels: Enumerable.Range(0, score.Criteria.Count).Select(GoogleDecisionIndex).ToArray());
                    break;
                default:
                    throw new NotSupportedException($"Google Decisions does not support question type '{question.Type}'.");
            }
            result.Add(mapping);
        }
        return result;
    }

    private static List<GoogleDecisionQuestion> MapGoogleOpenAIDecisionQuestions(IReadOnlyList<OpenAIDecisionQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ValidateGoogleDecisionCount(questions.Count, 1000, "questions");
        var result = new List<GoogleDecisionQuestion>(questions.Count);
        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            ArgumentNullException.ThrowIfNull(question);
            ArgumentNullException.ThrowIfNull(question.InstructionContent);
            var instructions = question.InstructionContent.Text
                ?? string.Concat(question.InstructionContent.Parts!.Select(RenderGoogleDecisionOpenAITextPart));
            if (string.IsNullOrWhiteSpace(instructions))
                throw new ArgumentException("Google decision questions require nonempty text instructions.");
            // OpenAI names may be absent, duplicate, or invalid as Google keys.
            var id = "q" + index.ToString("D4", CultureInfo.InvariantCulture);
            var payload = new JsonObject { ["instructions"] = instructions };
            GoogleDecisionQuestion mapping;
            switch (question)
            {
                case OpenAIDecisionPredicateQuestion:
                    payload["type"] = "noul";
                    mapping = new(id, question.Name, "noul", payload);
                    break;
                case OpenAIDecisionChoiceQuestion choice:
                    ArgumentNullException.ThrowIfNull(choice.Choices);
                    ValidateGoogleDecisionCount(choice.Choices.Count, 255, "choice options", 2);
                    var typedValues = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var option in choice.Choices)
                    {
                        if (option is null || option.Value.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False)
                            || !typedValues.Add(option.Value.Value.GetRawText()))
                            throw new ArgumentException("Decision choices require distinct string or boolean values.");
                    }
                    // Keep valid string IDs (including alphabetical tie behavior). Remap
                    // the entire list when typed/invalid keys need reversible wire IDs.
                    var keepIds = choice.Choices.All(option => option.Value.Value.ValueKind == JsonValueKind.String
                        && IsGoogleDecisionOptionId(option.Value.Value.GetString()!));
                    var options = new List<GoogleDecisionOption>();
                    var criteria = new JsonObject();
                    for (var optionIndex = 0; optionIndex < choice.Choices.Count; optionIndex++)
                    {
                        var option = choice.Choices[optionIndex];
                        var value = GoogleDecisionEvidence(option.Value.Value);
                        var optionId = keepIds ? value : "o" + optionIndex.ToString("D3", CultureInfo.InvariantCulture);
                        // Google never sees the original value after remapping; carry its
                        // meaning into the description instead of sending an empty criterion.
                        var typedDescription = option.Value.Value.ValueKind == JsonValueKind.String
                            ? "String value " + option.Value.Value.GetRawText() : "Boolean value " + value;
                        var description = keepIds ? option.Description ?? ""
                            : string.IsNullOrEmpty(option.Description) ? typedDescription : typedDescription + ": " + option.Description;
                        criteria.Add(optionId, description);
                        options.Add(new(optionId, option.Value));
                    }
                    payload["type"] = "choice";
                    payload["criteria"] = criteria;
                    mapping = new(id, question.Name, "choice", payload, options);
                    break;
                case OpenAIDecisionScoreQuestion score:
                    ArgumentNullException.ThrowIfNull(score.Levels);
                    ValidateGoogleDecisionCount(score.Levels.Count, 255, "score levels", 2);
                    if (score.Levels.Any(level => level is null || string.IsNullOrWhiteSpace(level.Label))
                        || score.Levels.Select(level => level.Label).Distinct(StringComparer.Ordinal).Count() != score.Levels.Count)
                        throw new ArgumentException("Score levels require distinct nonempty labels.");
                    payload["type"] = "score";
                    payload["criteria"] = new JsonArray(score.Levels.Select(level => (JsonNode?)JsonValue.Create(
                        string.IsNullOrWhiteSpace(level.Description) ? level.Label : level.Label + ": " + level.Description)).ToArray());
                    mapping = new(id, question.Name, "score", payload, Labels: score.Levels.Select(level => level.Label).ToArray());
                    break;
                default:
                    throw new NotSupportedException($"Google Decisions does not support question type '{question.Type}'.");
            }
            result.Add(mapping);
        }
        return result;
    }

    private static string RenderGoogleDecisionText(JsonElement value, bool allowEmpty = false)
    {
        string text;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out _))
        {
            RequireGoogleDecisionFields(value, "type", "text");
            if (RequiredGoogleDecisionString(value, "type") != "text")
                throw new NotSupportedException("Google decision question instructions and criteria accept text only.");
            text = RequiredGoogleDecisionString(value, "text");
        }
        else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out _)))
            text = string.Concat(value.EnumerateArray().Select(item => RenderGoogleDecisionText(item, allowEmpty: true)));
        else
            text = GoogleDecisionEvidence(value);
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Google decision instructions and score levels must be nonempty.");
        return text;
    }

    private static string RenderGoogleDecisionOpenAITextPart(OpenAIDecisionInputPart part)
        => part switch
        {
            OpenAIDecisionInputText text when text.PromptCacheBreakpoint is null && text.Text is not null => text.Text,
            OpenAIDecisionInputJson json when json.Data.ValueKind is JsonValueKind.Object or JsonValueKind.Array => json.Data.GetRawText(),
            _ => throw new NotSupportedException("Google Decisions supports only text/JSON evidence here, without media, tools, or cache breakpoints.")
        };

    private static bool IsGoogleDecisionOptionId(string value)
        => value.Length > 0 && !value.StartsWith(' ') && !value.EndsWith(' ')
            && value.EnumerateRunes().Count() <= 128 && Encoding.UTF8.GetByteCount(value) <= 512
            && value.EnumerateRunes().All(rune => Rune.GetUnicodeCategory(rune) != UnicodeCategory.Control
                && (!Rune.IsWhiteSpace(rune) || rune.Value == ' '));

    private static void ValidateGoogleDecisionCount(int count, int maximum, string name, int minimum = 1)
    {
        if (count < minimum || count > maximum)
            throw new ArgumentException($"Google Decisions requires {minimum} to {maximum} {name}.");
    }
}
