using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Parallel;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class ParallelDecisionTests
{
    [Fact]
    public async Task Decision_request_sends_typed_questions_and_maps_answers_with_raw_metadata()
    {
        HttpRequestMessage? sentRequest = null;
        string? sentBody = null;
        var provider = CreateProvider(request =>
        {
            sentRequest = request;
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(
                """
                {
                  "mode": "fast",
                  "answers": {
                    "likely": { "type": "noul", "noul": 0.8 },
                    "specified": { "type": "noul", "noul": 0.65 },
                    "category": {
                      "type": "choice",
                      "choice": "alpha",
                      "probabilities": { "alpha": 0.75, "beta": 0.25 }
                    },
                    "quality": {
                      "type": "score",
                      "score": 1.25,
                      "legend": { "0": "Low", "1": "Medium", "2": "High" },
                      "probabilities": { "0": 0.25, "1": 0.25, "2": 0.5 }
                    },
                    "declined": { "type": "refusal" }
                  },
                  "usage": { "input_tokens": 123, "output_tokens": 45 },
                  "citations": [
                    { "url": "https://example.com", "title": "Example", "excerpts": ["evidence"] }
                  ],
                  "decision_id": "decisions_123"
                }
                """);
        });
        var request = new DecisionRequest
        {
            Model = "parallel/fast",
            State = State("""{"company":"Example","website":"https://example.com"}"""),
            Questions = new Dictionary<string, DecisionQuestion>
            {
                ["likely"] = new DecisionBooleanQuestion { Instructions = Input("Is this likely?") },
                ["specified"] = new DecisionBooleanQuestion
                {
                    Instructions = Input("Does this meet the criteria?"),
                    Criteria = new DecisionBooleanCriteria
                    {
                        True = Input("It meets the positive criteria."),
                        False = Input("It meets the negative criteria.")
                    }
                },
                ["category"] = new DecisionChoiceQuestion
                {
                    Instructions = Input("Choose a category."),
                    Criteria = new Dictionary<string, DecisionInput?>
                    {
                        ["alpha"] = Input("First category"),
                        ["beta"] = Input("Second category")
                    }
                },
                ["quality"] = new DecisionScoreQuestion
                {
                    Instructions = Input("Score the quality."),
                    Criteria = [Input("Low"), Input("Medium"), Input("High")]
                },
                ["declined"] = new DecisionBooleanQuestion { Instructions = Input("Can this be determined?") }
            },
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["parallel"] = Json("""{"mode":"fast","advanced_settings":{"location":"US","include_excerpts":true}}""")
            }
        };

        var result = await provider.DecisionRequestAsync(request);

        Assert.Equal(HttpMethod.Post, sentRequest!.Method);
        Assert.Equal("https://api.parallel.ai/v1/decisions", sentRequest.RequestUri!.ToString());
        Assert.Equal("test-parallel-key", sentRequest.Headers.GetValues("x-api-key").Single());
        using var requestDocument = JsonDocument.Parse(sentBody!);
        var sent = requestDocument.RootElement;
        Assert.Equal(JsonValueKind.Object, sent.GetProperty("state").ValueKind);
        Assert.Equal("fast", sent.GetProperty("mode").GetString());
        Assert.Equal("US", sent.GetProperty("advanced_settings").GetProperty("location").GetString());
        Assert.True(sent.GetProperty("advanced_settings").GetProperty("include_excerpts").GetBoolean());
        Assert.Equal("noul", sent.GetProperty("questions").GetProperty("likely").GetProperty("type").GetString());
        var specified = sent.GetProperty("questions").GetProperty("specified");
        Assert.Equal("noul", specified.GetProperty("type").GetString());
        Assert.Equal("It meets the positive criteria.", specified.GetProperty("criteria").GetProperty("true").GetString());
        Assert.Equal("It meets the negative criteria.", specified.GetProperty("criteria").GetProperty("false").GetString());
        Assert.Equal("First category", sent.GetProperty("questions").GetProperty("category").GetProperty("criteria").GetProperty("alpha").GetString());
        Assert.Equal(3, sent.GetProperty("questions").GetProperty("quality").GetProperty("criteria").GetArrayLength());

        Assert.Equal(0.8, Assert.IsType<DecisionBooleanAnswer>(result.Answers["likely"]).Probability);
        Assert.Equal(0.65, Assert.IsType<DecisionBooleanAnswer>(result.Answers["specified"]).Probability);
        var choice = Assert.IsType<DecisionChoiceAnswer>(result.Answers["category"]);
        Assert.Equal("alpha", choice.Choice);
        Assert.Equal(0.75, choice.Probabilities!["alpha"]);
        var score = Assert.IsType<DecisionScoreAnswer>(result.Answers["quality"]);
        Assert.Equal(1.25, score.Score);
        Assert.IsType<DecisionRefusalAnswer>(result.Answers["declined"]);
        Assert.NotNull(result.Usage);
        Assert.Equal(123, result.Usage.InputTokens);
        Assert.Equal(45, result.Usage.OutputTokens);
        Assert.Equal("decisions_123", result.Response!.Id);
        var responseBody = Assert.IsType<JsonElement>(result.Response.Body);
        Assert.Equal("https://example.com", responseBody.GetProperty("citations")[0].GetProperty("url").GetString());
        Assert.Equal("decisions_123", responseBody.GetProperty("decision_id").GetString());
        Assert.Contains("citations", JsonSerializer.Serialize(result.ProviderMetadata));
    }

    [Fact]
    public async Task Decision_error_preserves_http_status_and_response_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"detail":"invalid state"}""", Encoding.UTF8, "application/json")
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.DecisionRequestAsync(CreateSimpleRequest()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Contains("invalid state", exception.Message);
    }

    [Fact]
    public async Task Decision_request_rejects_more_than_one_hundred_questions()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new DecisionRequest
        {
            Model = "parallel/fast",
            State = State("""{"company":"Example"}"""),
            Questions = Enumerable.Range(0, 101).ToDictionary(
                index => $"q{index}",
                _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = Input("Question?") })
        };

        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    private static ParallelProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(new TestKeyResolver(), new TestHttpClientFactory(responseFactory));

    private static DecisionRequest CreateSimpleRequest()
        => new()
        {
            Model = "parallel/fast",
            State = State("""{"company":"Example"}"""),
            Questions = new Dictionary<string, DecisionQuestion>
            {
                ["likely"] = new DecisionBooleanQuestion { Instructions = Input("Is this likely?") }
            }
        };

    private static DecisionInput Input(string value)
        => new(JsonSerializer.SerializeToElement(value));

    private static DecisionInput State(string value)
    {
        using var document = JsonDocument.Parse(value);
        return new(document.RootElement.Clone());
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-parallel-key";
    }

    private sealed class TestHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestHttpMessageHandler(responseFactory));
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
