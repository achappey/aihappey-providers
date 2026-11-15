using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Boundless;
using AIHappey.Decisions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class BoundlessDecisionTests
{
    [Fact]
    public async Task Decision_maps_questions_answers_metadata_and_per_request_authentication()
    {
        string? sent = null;
        var client = new HttpClient(new Handler(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("https://api.inference.boundless.network/v1/systemone", message.RequestUri!.ToString());
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", message.Headers.Authorization.Parameter);
            sent = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var response = Response("""
                {"answers":{
                  "refund":{"type":"noul","noul":0.8932},
                  "topic":{"type":"choice","choice":"billing","confidence":0.7971,"probabilities":{"billing":0.9528,"technical":0.0214,"account":0.0259}},
                  "urgency":{"type":"score","score":1.1619,"confidence":0.0644,"legend":{"0":"low","1":"medium","2":"high"},"probabilities":{"0":0.1796,"1":0.479,"2":0.3414}}
                },"usage":{"input_tokens":161,"output_tokens":0},"extra":{"retained":true}}
                """);
            response.Headers.Add("x-request-id", "req-123");
            return response;
        }));
        var provider = Provider(client);
        var request = new DecisionRequest
        {
            Model = "boundless/convaiinnovations/laya",
            State = JsonInput("""{"ticket":"refund"}"""),
            Questions = new()
            {
                ["refund"] = new DecisionBooleanQuestion { Instructions = new DecisionInput("Refund?"), Criteria = new() { True = new DecisionInput("yes"), False = new DecisionInput("no") } },
                ["topic"] = new DecisionChoiceQuestion { Instructions = new DecisionInput("Topic?"), Criteria = new() { ["billing"] = new DecisionInput("Bills"), ["technical"] = new DecisionInput("Bugs"), ["account"] = new DecisionInput("Account") } },
                ["urgency"] = new DecisionScoreQuestion { Instructions = new DecisionInput("Urgency?"), Criteria = [new DecisionInput("low"), new DecisionInput("medium"), new DecisionInput("high")] }
            },
            ProviderOptions = new() { ["boundless"] = Json("""{"max_len":2048,"extra":{"a":1},"model":"override","state":"override","questions":{}}""") }
        };

        var result = await provider.DecisionRequestAsync(request);
        using var document = JsonDocument.Parse(sent!);
        var body = document.RootElement;
        Assert.Equal("convaiinnovations/laya", body.GetProperty("model").GetString());
        Assert.Equal("refund", body.GetProperty("state").GetProperty("ticket").GetString());
        Assert.Equal(2048, body.GetProperty("max_len").GetInt32());
        Assert.Equal(1, body.GetProperty("extra").GetProperty("a").GetInt32());
        var questions = body.GetProperty("questions");
        Assert.Equal("noul", questions.GetProperty("refund").GetProperty("type").GetString());
        Assert.Equal("yes", questions.GetProperty("refund").GetProperty("criteria").GetProperty("true").GetString());
        Assert.Equal("Bills", questions.GetProperty("topic").GetProperty("criteria").GetProperty("billing").GetString());
        Assert.Equal(3, questions.GetProperty("urgency").GetProperty("criteria").GetArrayLength());
        Assert.Equal(0.8932, Assert.IsType<DecisionBooleanAnswer>(result.Answers["refund"]).Probability);
        var choice = Assert.IsType<DecisionChoiceAnswer>(result.Answers["topic"]);
        Assert.Equal("billing", choice.Choice);
        Assert.Equal(0.0259, choice.Probabilities!["account"]);
        Assert.Equal(1.1619, Assert.IsType<DecisionScoreAnswer>(result.Answers["urgency"]).Score);
        Assert.Equal(161, result.Usage!.InputTokens);
        Assert.Equal(0, result.Usage.OutputTokens);
        Assert.Equal("boundless/convaiinnovations/laya", result.Response!.ModelId);
        Assert.Equal("req-123", result.Response.Id);
        Assert.True(result.ProviderMetadata!["boundless"].GetProperty("extra").GetProperty("retained").GetBoolean());
        Assert.True(JsonElement.DeepEquals(result.ProviderMetadata["boundless"], Assert.IsType<JsonElement>(result.Response.Body)));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Openai_mapping_preserves_choice_values_and_normalizes_score()
    {
        var provider = Provider(new HttpClient(new Handler(message =>
        {
            using var document = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("convaiinnovations/laya", document.RootElement.GetProperty("model").GetString());
            Assert.Equal(2048, document.RootElement.GetProperty("max_len").GetInt32());
            Assert.Equal("noul", document.RootElement.GetProperty("questions").GetProperty("question-0").GetProperty("type").GetString());
            return Response("""
                {"answers":{
                  "question-0":{"type":"noul","noul":0.7},
                  "question-1":{"type":"choice","choice":"option-1","confidence":0.5,"probabilities":{"option-0":0.4,"option-1":0.6}},
                  "question-2":{"type":"score","score":1.4,"confidence":0.8,"probabilities":{"0":0.1,"1":0.4,"2":0.5}}
                },"usage":{"input_tokens":100,"output_tokens":0}}
                """);
        })));
        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "boundless/convaiinnovations/laya",
            Input = new("Ticket"),
            AdditionalProperties = new() { ["max_len"] = Json("2048") },
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "review", Instructions = "Review?" },
                new OpenAIDecisionChoiceQuestion { Name = "route", Instructions = "Choose", Choices = [new() { Value = "true" }, new() { Value = true }] },
                new OpenAIDecisionScoreQuestion { Name = "quality", Instructions = "Quality?", Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }] }
            ]
        });
        Assert.Equal("convaiinnovations/laya", result.Model);
        Assert.Equal(0.7, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.True(choice.Choice.Value.GetBoolean());
        Assert.Equal("route", choice.Name);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(0.7, score.Score);
        Assert.Equal("high", score.Probabilities[2].Label);
        Assert.Equal(100, result.Usage.TotalTokens);
        Assert.Contains("providerMetadata", result.AdditionalProperties!.Keys);
    }

    [Fact]
    public async Task Invalid_options_and_provider_errors_are_reported()
    {
        var provider = Provider(new HttpClient(new Handler(_ => Response("""{"detail":"invalid state"}""", HttpStatusCode.UnprocessableEntity))));
        var request = SimpleRequest();
        request.ProviderOptions = new() { ["boundless"] = Json("42") };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
        request.ProviderOptions = null;
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(request));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.StatusCode);
        Assert.Contains("invalid state", error.Message);
    }

    [Fact]
    public async Task Invalid_answer_and_missing_key_fail()
    {
        var client = new HttpClient(new Handler(_ => Response("""{"answers":{"q":{"type":"noul","noul":1.5}},"usage":{"input_tokens":1,"output_tokens":0}}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client).DecisionRequestAsync(SimpleRequest()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(client, null).DecisionRequestAsync(SimpleRequest()));
    }

    private static BoundlessProvider Provider(HttpClient client, string? key = "test-key")
        => new(new Resolver(key), new Factory(client));
    private static DecisionRequest SimpleRequest() => new()
    {
        Model = "boundless/model", State = new DecisionInput("state"),
        Questions = new() { ["q"] = new DecisionBooleanQuestion { Instructions = new DecisionInput("Question?") } }
    };
    private static DecisionInput JsonInput(string json) => new(Json(json));
    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Resolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
