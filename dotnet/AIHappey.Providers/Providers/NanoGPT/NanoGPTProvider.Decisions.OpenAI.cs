using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NanoGPT;

public partial class NanoGPTProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null)
            throw new NotSupportedException("NanoGPT Decisions does not support safety_identifier.");

        var native = new DecisionRequest
        {
            Model = request.Model,
            State = new DecisionInput(JsonSerializer.SerializeToElement(MapNanoGPTDecisionInput(request.Input))),
            ProviderOptions = request.AdditionalProperties is null ? null : new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(request.AdditionalProperties, NanoGPTDecisionJson)
            }
        };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            native.Questions[NanoGPTDecisionQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    // Generated IDs preserve distinct typed values, including "true" and true.
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create(NanoGPTDecisionOptionId(i),
                        (DecisionInput?)new DecisionInput(DescribeNanoGPTDecisionLevel(
                            NanoGPTJsonText(option.Value.Value), option.Description))))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level =>
                        (DecisionInput?)new DecisionInput(DescribeNanoGPTDecisionLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question type.", nameof(request))
            };
        }

        var result = await DecisionRequestAsync(native, cancellationToken);
        var raw = result.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = NanoGPTDecisionQuestionId(index);
            var question = request.Questions[index];
            var answer = result.Answers[id];
            OpenAIDecisionAnswer mapped;
            if (answer is DecisionRefusalAnswer)
                mapped = new OpenAIDecisionRefusalAnswer();
            else
            {
                var original = raw.GetProperty("answers").GetProperty(id);
                switch (question)
                {
                    case OpenAIDecisionPredicateQuestion:
                        mapped = new OpenAIDecisionPredicateAnswer { Probability = ((DecisionBooleanAnswer)answer).Probability };
                        break;
                    case OpenAIDecisionChoiceQuestion choice:
                        var selected = (DecisionChoiceAnswer)answer;
                        var selectedIndex = Enumerable.Range(0, choice.Choices.Count).Single(i => NanoGPTDecisionOptionId(i) == selected.Choice);
                        mapped = new OpenAIDecisionChoiceAnswer
                        {
                            Choice = choice.Choices[selectedIndex].Value,
                            Confidence = ReadNanoGPTDecisionConfidence(original),
                            Probabilities = choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                            {
                                Value = option.Value, Probability = selected.Probabilities![NanoGPTDecisionOptionId(i)]
                            }).ToArray()
                        };
                        break;
                    case OpenAIDecisionScoreQuestion score:
                        var scored = (DecisionScoreAnswer)answer;
                        var divisor = score.Levels.Count - 1;
                        mapped = new OpenAIDecisionScoreAnswer
                        {
                            Score = scored.Score / divisor,
                            Confidence = ReadNanoGPTDecisionConfidence(original),
                            Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                            {
                                Label = level.Label, Value = i / (double)divisor,
                                Probability = scored.Probabilities![NanoGPTDecisionIndex(i)]
                            }).ToArray()
                        };
                        break;
                    default: throw new InvalidOperationException("Unexpected NanoGPT decision question type.");
                }
            }
            mapped.Name = question.Name;
            answers.Add(mapped);
        }

        var input = result.Usage!.InputTokens!.Value;
        var output = result.Usage.OutputTokens!.Value;
        return new()
        {
            Model = raw.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = checked(input + output) },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(result.ProviderMetadata, NanoGPTDecisionJson),
                ["response"] = JsonSerializer.SerializeToElement(result.Response, NanoGPTDecisionJson),
                ["warnings"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { type = "other", message = "NanoGPT does not report cached, cache-write, or reasoning tokens; detail counters default to zero. Ordered scores use an evenly spaced [0,1] scale." }
                }, NanoGPTDecisionJson)
            }
        };
    }

    private static double ReadNanoGPTDecisionConfidence(JsonElement answer)
    {
        if (!answer.TryGetProperty("confidence", out var confidence))
            throw new InvalidOperationException("NanoGPT returned a decision answer without the required confidence.");
        return ReadNanoGPTDecisionProbability(confidence);
    }

    private static string NanoGPTDecisionQuestionId(int index) => "question-" + NanoGPTDecisionIndex(index);
    private static string NanoGPTDecisionOptionId(int index) => "option-" + NanoGPTDecisionIndex(index);
    private static string DescribeNanoGPTDecisionLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
