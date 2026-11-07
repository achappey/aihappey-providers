using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NanoGPT;

public partial class NanoGPTProvider
{
    private static readonly JsonSerializerOptions NanoGPTDecisionJson = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var sdkState = request.StateFormat == "sdk";
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var prefix = GetIdentifier() + "/";
        var model = request.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? request.Model[prefix.Length..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            var native = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = JsonSerializer.SerializeToNode(question.Instructions, NanoGPTDecisionJson)
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    native["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, NanoGPTDecisionJson);
                    break;
                case DecisionChoiceQuestion choice:
                    native["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, NanoGPTDecisionJson);
                    break;
                case DecisionScoreQuestion score when score.Criteria.Count is >= 2 and <= 10:
                    native["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, NanoGPTDecisionJson);
                    break;
                case DecisionScoreQuestion:
                    throw new ArgumentException("NanoGPT score questions require 2 to 10 ordered criteria.", nameof(request));
                case DecisionBooleanQuestion:
                    break;
                default:
                    throw new NotSupportedException($"NanoGPT Decisions does not support question type '{question.Type}'.");
            }
            questions.Add(id, native);
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = sdkState
                ? MapNanoGPTDecisionInput(request.ToOpenAIDecisionRequest(GetIdentifier()).Input)
                : JsonSerializer.SerializeToNode(request.State, NanoGPTDecisionJson),
            ["questions"] = questions
        };
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.nanogpt must be an object.", nameof(request));
            foreach (var property in options.EnumerateObject())
            {
                if (new[] { "model", "state", "questions", "input" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"NanoGPT decision options cannot override '{property.Name}'.", nameof(request));
                payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = JsonContent.Create(payload, options: NanoGPTDecisionJson)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        ApplyAuthHeader(message);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"NanoGPT decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var answers = MapNanoGPTDecisionAnswers(body.GetProperty("answers"), request);
            var input = body.GetProperty("usage").GetProperty("input_tokens").GetInt32();
            var output = body.GetProperty("usage").GetProperty("output_tokens").GetInt32();
            var id = body.GetProperty("id").GetString();
            if (input < 0 || output < 0 || string.IsNullOrWhiteSpace(body.GetProperty("model").GetString())
                || string.IsNullOrWhiteSpace(id))
                throw new JsonException("A decision response requires an ID, a model and nonnegative token counts.");
            var headers = response.GetHeaders();
            return new()
            {
                Answers = answers,
                Usage = new() { InputTokens = input, OutputTokens = output },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone()),
                Response = new()
                {
                    Id = id,
                    ModelId = body.GetProperty("model").GetString()!.ToModelId(GetIdentifier()),
                    Timestamp = DateTimeOffset.UtcNow,
                    Headers = headers.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase),
                    Body = body.Clone()
                },
                Warnings = []
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("NanoGPT returned an invalid decision response.", exception);
        }
    }

    private static JsonNode MapNanoGPTDecisionInput(OpenAIDecisionInput input)
    {
        if (input.Text is not null)
            return JsonValue.Create(input.Text)!;
        var state = new JsonArray();
        foreach (var message in input.Messages!)
        {
            if (message.Content.Text is not null)
                state.Add(message.Content.Text);
            else
                foreach (var part in message.Content.Parts!)
                    switch (part)
                    {
                        case OpenAIDecisionInputText text:
                            state.Add(text.Text);
                            break;
                        case OpenAIDecisionInputImage image:
                            var imageUrl = new JsonObject { ["url"] = image.ImageUrl };
                            if (image.Detail is not null)
                                imageUrl["detail"] = image.Detail;
                            state.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = imageUrl
                            });
                            break;
                        default:
                            throw new NotSupportedException($"NanoGPT Decisions does not support input part '{part.Type}'.");
                    }
        }
        return state;
    }

    private static Dictionary<string, DecisionAnswer> MapNanoGPTDecisionAnswers(JsonElement native, DecisionRequest request)
    {
        if (native.ValueKind != JsonValueKind.Object
            || native.EnumerateObject().Count() != request.Questions.Count
            || native.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");
        var result = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = native.GetProperty(id);
            var type = answer.GetProperty("type").GetString();
            if (type == "refusal")
            {
                result[id] = new DecisionRefusalAnswer();
                continue;
            }
            if (type != (question is DecisionBooleanQuestion ? "noul" : question.Type))
                throw new JsonException($"Answer type does not match question '{id}'.");
            if (answer.TryGetProperty("confidence", out var confidence))
                ReadNanoGPTDecisionProbability(confidence);
            switch (question)
            {
                case DecisionBooleanQuestion:
                    result[id] = new DecisionBooleanAnswer { Probability = ReadNanoGPTDecisionProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing selected choice.");
                    if (!choice.Criteria.ContainsKey(selected))
                        throw new JsonException("Selected choice is not one of the supplied criteria.");
                    var distribution = ReadNanoGPTDecisionDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (distribution[selected] + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Selected choice is not a maximal option.");
                    result[id] = new DecisionChoiceAnswer
                    {
                        Choice = selected,
                        Probabilities = distribution
                    };
                    break;
                case DecisionScoreQuestion score:
                    var value = answer.GetProperty("score").GetDouble();
                    if (!double.IsFinite(value) || value < 0 || value > score.Criteria.Count - 1)
                        throw new JsonException("Score is outside the supplied rubric.");
                    var probabilities = ReadNanoGPTDecisionDistribution(answer.GetProperty("probabilities"),
                        Enumerable.Range(0, score.Criteria.Count).Select(NanoGPTDecisionIndex));
                    if (Math.Abs(value - probabilities.Sum(p => int.Parse(p.Key, CultureInfo.InvariantCulture) * p.Value)) > 1e-6)
                        throw new JsonException("Score does not match its probability distribution.");
                    result[id] = new DecisionScoreAnswer
                    {
                        Score = value,
                        Probabilities = probabilities
                    };
                    break;
                default:
                    throw new JsonException($"Unsupported NanoGPT decision question type '{question.Type}'.");
            }
        }
        return result;
    }

    private static string NanoGPTDecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static double ReadNanoGPTDecisionProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var probability)
            || !double.IsFinite(probability) || probability is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return probability;
    }

    private static Dictionary<string, double> ReadNanoGPTDecisionDistribution(JsonElement value, IEnumerable<string> keys)
    {
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadNanoGPTDecisionProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Probability map must cover every option and sum to one.");
        return result;
    }
}
