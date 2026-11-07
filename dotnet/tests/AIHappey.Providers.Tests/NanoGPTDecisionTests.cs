using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.NanoGPT;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class NanoGPTDecisionTests
{
    [Fact]
    public async Task Native_decisions_preserve_rich_input_options_and_raw_response()
    {
        string? sent = null;
        var client = new HttpClient(new Handler(message =>
        {
            Assert.Equal("https://nano-gpt.com/api/v1/decisions", message.RequestUri!.ToString());
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", message.Headers.Authorization.Parameter);
            Assert.Equal("trace-123", message.Headers.GetValues("x-nanogpt-trace").Single());
            Assert.False(message.Headers.Contains("x-other-trace"));
            sent = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var response = Response(
                """
                {"id":"decision_123","model":"perplexity/pplx-decider-v1.2","answers":{
                  "review":{"type":"noul","noul":0.84},
                  "route":{"type":"choice","choice":"billing","confidence":0.97,"probabilities":{"billing":0.97,"technical":0.03}},
                  "urgency":{"type":"score","score":1.6,"confidence":0.8,"legend":{"0":"Low","1":"Medium","2":"High"},"probabilities":{"0":0.1,"1":0.2,"2":0.7}},
                  "declined":{"type":"refusal"}
                },"usage":{"input_tokens":126,"output_tokens":8,"cost":0.000005027},"future":{"retained":true}}
                """);
            response.Headers.Add("x-request-id", "req-123");
            return response;
        }));
        var provider = Provider(client);
        var request = SimpleRequest();
        request.State = Input("""["Return claim",{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID","detail":"low"}}]""");
        request.Questions["review"] = new DecisionBooleanQuestion
        {
            Instructions = Input("""{"task":"Review?"}"""),
            Criteria = new() { True = Input("""["Needs review"]"""), False = new DecisionInput("Safe to automate") }
        };
        request.Questions["route"] = new DecisionChoiceQuestion
        {
            Instructions = new DecisionInput("Choose a team"),
            Criteria = new() { ["billing"] = null, ["technical"] = Input("""{"scope":"Bugs"}""") }
        };
        request.Questions["urgency"] = new DecisionScoreQuestion
        {
            Instructions = new DecisionInput("Urgency?"),
            Criteria = [new DecisionInput("Low"), new DecisionInput("Medium"), new DecisionInput("High")]
        };
        request.Questions["declined"] = new DecisionBooleanQuestion { Instructions = new DecisionInput("Unknown?") };
        request.ProviderOptions = new()
        {
            ["nanogpt"] = Json("""{"user":"customer_123","session_id":"session","trace":{"request":"abc"},"provider":{"zdr":true},"include_usage":true}""")
        };
        request.Headers = new() { ["x-nanogpt-trace"] = "trace-123", ["x-other-trace"] = "ignored" };

        var result = await provider.DecisionRequestAsync(request);

        using var document = JsonDocument.Parse(sent!);
        var body = document.RootElement;
        Assert.Equal("perplexity/pplx-decider-v1.2", body.GetProperty("model").GetString());
        Assert.Equal(request.State.Value.GetRawText(), body.GetProperty("state").GetRawText());
        var review = body.GetProperty("questions").GetProperty("review");
        Assert.Equal("noul", review.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, review.GetProperty("instructions").ValueKind);
        Assert.Equal(JsonValueKind.Array, review.GetProperty("criteria").GetProperty("true").ValueKind);
        Assert.True(body.GetProperty("include_usage").GetBoolean());
        Assert.Equal("customer_123", body.GetProperty("user").GetString());
        Assert.Equal("session", body.GetProperty("session_id").GetString());
        Assert.True(body.GetProperty("provider").GetProperty("zdr").GetBoolean());
        Assert.Equal("abc", body.GetProperty("trace").GetProperty("request").GetString());
        Assert.Equal(0.84, Assert.IsType<DecisionBooleanAnswer>(result.Answers["review"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["route"]).Choice);
        Assert.Equal(1.6, Assert.IsType<DecisionScoreAnswer>(result.Answers["urgency"]).Score);
        Assert.IsType<DecisionRefusalAnswer>(result.Answers["declined"]);
        Assert.Equal(126, result.Usage!.InputTokens);
        Assert.Equal(8, result.Usage.OutputTokens);
        Assert.Equal("decision_123", result.Response!.Id);
        Assert.Equal("nanogpt/perplexity/pplx-decider-v1.2", result.Response.ModelId);
        Assert.Equal("req-123", result.Response.Headers!["x-request-id"]);
        Assert.True(result.ProviderMetadata!["nanogpt"].GetProperty("future").GetProperty("retained").GetBoolean());
        Assert.Equal(0.000005027, result.ProviderMetadata["nanogpt"].GetProperty("usage").GetProperty("cost").GetDouble());
        Assert.True(JsonElement.DeepEquals(result.ProviderMetadata["nanogpt"], Assert.IsType<JsonElement>(result.Response.Body)));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Sdk_state_is_normalized_to_native_text_and_inline_image_parts()
    {
        var provider = Provider(new HttpClient(new Handler(message =>
        {
            using var document = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var state = document.RootElement.GetProperty("state");
            Assert.Equal("Photo", state[0].GetString());
            Assert.Equal("image_url", state[1].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,AQID", state[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.False(state[1].GetProperty("image_url").TryGetProperty("detail", out _));
            return Response(SimpleResponse);
        })));
        var request = SimpleRequest();
        request.StateFormat = "sdk";
        request.State = Input("""[{"type":"text","text":"Photo"},{"type":"file","mediaType":"image/png","data":{"type":"data","data":"AQID"}}]""");

        await provider.DecisionRequestAsync(request);
    }

    [Fact]
    public async Task Openai_decisions_preserve_typed_choices_names_scores_and_refusals()
    {
        var provider = Provider(new HttpClient(new Handler(message =>
        {
            using var document = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var questions = document.RootElement.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("question-0").GetProperty("type").GetString());
            Assert.Equal(3, questions.GetProperty("question-1").GetProperty("criteria").EnumerateObject().Count());
            return Response(
                """
                {"id":"decision_456","model":"typesafe/jev-1.13","answers":{
                  "question-0":{"type":"noul","noul":0.7},
                  "question-1":{"type":"choice","choice":"option-1","confidence":0.6,"probabilities":{"option-0":0.3,"option-1":0.6,"option-2":0.1}},
                  "question-2":{"type":"score","score":1.4,"confidence":0.8,"probabilities":{"0":0.1,"1":0.4,"2":0.5}},
                  "question-3":{"type":"refusal"}
                },"usage":{"input_tokens":100,"output_tokens":5,"estimated_cost":0.002}}
                """);
        })));
        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "nanogpt/typesafe/jev-1.13",
            Input = new("Ticket"),
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "review", Instructions = "Review?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route", Instructions = "Choose",
                    Choices = [new() { Value = "true" }, new() { Value = true }, new() { Value = false }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "quality", Instructions = "Quality?",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }]
                },
                new OpenAIDecisionPredicateQuestion { Name = "declined", Instructions = "Unknown?" }
            ]
        });

        Assert.Equal(0.7, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.True(choice.Choice.Value.GetBoolean());
        Assert.Equal(JsonValueKind.String, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal(0.6, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(0.7, score.Score);
        Assert.Equal("high", score.Probabilities[2].Label);
        Assert.Equal(1, score.Probabilities[2].Value);
        Assert.Equal("declined", Assert.IsType<OpenAIDecisionRefusalAnswer>(result.Answers[3]).Name);
        Assert.Equal(105, result.Usage.TotalTokens);
        Assert.Contains("estimated_cost", result.AdditionalProperties!["providerMetadata"].GetRawText());
    }

    [Fact]
    public async Task Openai_message_input_preserves_every_text_and_inline_image_part()
    {
        var provider = Provider(new HttpClient(new Handler(message =>
        {
            using var document = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var state = document.RootElement.GetProperty("state");
            Assert.Equal(3, state.GetArrayLength());
            Assert.Equal("First", state[0].GetString());
            Assert.Equal("Second", state[1].GetString());
            Assert.Equal("low", state[2].GetProperty("image_url").GetProperty("detail").GetString());
            Assert.Equal("data:image/png;base64,AQID", state[2].GetProperty("image_url").GetProperty("url").GetString());
            Assert.True(document.RootElement.GetProperty("include_usage").GetBoolean());
            return Response("""{"id":"d","model":"cloudflare/clef","answers":{"question-0":{"type":"noul","noul":0.8}},"usage":{"input_tokens":1,"output_tokens":0}}""");
        })));

        await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "nanogpt/cloudflare/clef",
            Input = new(new OpenAIDecisionInputMessage[]
            {
                new() { Content = "First" },
                new()
                {
                    Content = new OpenAIDecisionInputPart[]
                    {
                        new OpenAIDecisionInputText { Text = "Second" },
                        new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQID", Detail = "low" }
                    }
                }
            }),
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Review?" }],
            AdditionalProperties = new() { ["include_usage"] = JsonSerializer.SerializeToElement(true) }
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Errors_preserve_http_status_and_provider_body(HttpStatusCode status)
    {
        var provider = Provider(new HttpClient(new Handler(_ => Response("""{"error":{"code":"content_policy_violation"}}""", status))));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(SimpleRequest()));
        Assert.Equal(status, exception.StatusCode);
        Assert.Contains("content_policy_violation", exception.Message);
    }

    [Theory]
    [InlineData("""{"review":{"type":"noul","noul":1.2}}""")]
    [InlineData("""{"review":{"type":"choice","choice":"unknown"}}""")]
    [InlineData("{}")]
    [InlineData("""{"review":{"type":"noul","noul":0.5},"extra":{"type":"refusal"}}""")]
    public async Task Malformed_answers_fail_explicitly(string answers)
    {
        var provider = Provider(new HttpClient(new Handler(_ => Response(
            $$$"""{"id":"d","model":"m","answers":{{{answers}}},"usage":{"input_tokens":1,"output_tokens":0}}"""))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(SimpleRequest()));
    }

    [Fact]
    public async Task Cancellation_missing_keys_and_canonical_overrides_do_not_send_requests()
    {
        var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not send")));
        var provider = Provider(client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.DecisionRequestAsync(SimpleRequest(), new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(client, null).DecisionRequestAsync(SimpleRequest()));
        var request = SimpleRequest();
        request.ProviderOptions = new() { ["nanogpt"] = Json("""{"state":"override"}""") };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public async Task Score_requests_outside_native_limits_are_rejected_before_sending(int levels)
    {
        var provider = Provider(new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not send"))));
        var request = SimpleRequest();
        request.Questions["review"] = new DecisionScoreQuestion
        {
            Instructions = "Score?",
            Criteria = Enumerable.Range(0, levels).Select(i => (DecisionInput?)new DecisionInput($"Level {i}")).ToArray()
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData("""{"choice":"a","probabilities":{"a":0.4,"b":0.6}}""")]
    [InlineData("""{"choice":"a","probabilities":{"a":0.8}}""")]
    [InlineData("""{"choice":"a","probabilities":{"a":0.8,"b":0.8}}""")]
    public async Task Invalid_choice_distributions_are_not_returned_as_success(string fields)
    {
        var answer = Json(fields);
        var provider = Provider(new HttpClient(new Handler(_ => Response(JsonSerializer.Serialize(new
        {
            id = "d", model = "m",
            answers = new { review = new { type = "choice", choice = answer.GetProperty("choice"), probabilities = answer.GetProperty("probabilities") } },
            usage = new { input_tokens = 1, output_tokens = 0 }
        })))));
        var request = SimpleRequest();
        request.Questions["review"] = new DecisionChoiceQuestion
        {
            Instructions = "Choose", Criteria = new() { ["a"] = null, ["b"] = null }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(0.1)]
    public async Task Invalid_scores_are_not_returned_as_success(double score)
    {
        var provider = Provider(new HttpClient(new Handler(_ => Response(JsonSerializer.Serialize(new
        {
            id = "d", model = "m",
            answers = new { review = new { type = "score", score, probabilities = new Dictionary<string, double> { ["0"] = 0.1, ["1"] = 0.9 } } },
            usage = new { input_tokens = 1, output_tokens = 0 }
        })))));
        var request = SimpleRequest();
        request.Questions["review"] = new DecisionScoreQuestion { Instructions = "Score?", Criteria = ["Low", "High"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Decision_authentication_overrides_stale_client_credentials_without_mutating_them()
    {
        var client = new HttpClient(new Handler(message =>
        {
            Assert.Equal("test-key", message.Headers.Authorization!.Parameter);
            return Response(SimpleResponse);
        }));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "old-key");

        await Provider(client).DecisionRequestAsync(SimpleRequest());

        Assert.Equal("old-key", client.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public async Task Model_discovery_adds_decisions_without_reclassifying_existing_models()
    {
        var paths = new List<string>();
        var provider = Provider(new HttpClient(new Handler(message =>
        {
            var path = message.RequestUri!.PathAndQuery;
            paths.Add(path);
            return Response(path switch
            {
                "/api/v1/decision-models" => """{"data":[{"id":"cloudflare/clef","name":"Clef"}]}""",
                "/api/v1/models?detailed=true" => """{"data":[{"id":"chat-model"}]}""",
                "/api/v1/images/models" => """{"data":[{"id":"image-model"}]}""",
                "/api/v1/audio-models?detailed=true&type=all" => """{"data":[{"id":"voice-model","capabilities":{"text_to_speech":true}},{"id":"asr-model","capabilities":{"speech_to_text":true}}]}""",
                "/api/v1/video-models?detailed=true" => """{"data":[{"id":"video-model"}]}""",
                _ => """{"data":[]}"""
            });
        })));

        var models = (await provider.ListModels()).ToArray();
        Assert.Equal("decisions", models.Single(m => m.Id == "nanogpt/cloudflare/clef").Type);
        Assert.Equal("language", models.Single(m => m.Id == "nanogpt/chat-model").Type);
        Assert.Equal("image", models.Single(m => m.Id == "nanogpt/image-model").Type);
        Assert.Equal("speech", models.Single(m => m.Id == "nanogpt/voice-model").Type);
        Assert.Equal("transcription", models.Single(m => m.Id == "nanogpt/asr-model").Type);
        Assert.Equal("video", models.Single(m => m.Id == "nanogpt/video-model").Type);
        Assert.Equal(5, paths.Count);
        Assert.Contains("/api/v1/images/models", paths);
        Assert.Contains("/api/v1/audio-models?detailed=true&type=all", paths);
        Assert.Contains("/api/v1/video-models?detailed=true", paths);
    }

    private const string SimpleResponse = """{"id":"decision_1","model":"perplexity/pplx-decider-v1.2","answers":{"review":{"type":"noul","noul":0.8}},"usage":{"input_tokens":1,"output_tokens":0}}""";

    private static DecisionRequest SimpleRequest() => new()
    {
        Model = "nanogpt/perplexity/pplx-decider-v1.2", State = new DecisionInput("Ticket"),
        Questions = new() { ["review"] = new DecisionBooleanQuestion { Instructions = new DecisionInput("Review?") } }
    };

    private static DecisionInput Input(string json) => new(Json(json));
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static NanoGPTProvider Provider(HttpClient client, string? key = "test-key")
        => new(new Keys(key), new AsyncCacheHelper(), new Factory(client));
    private sealed class Keys(string? key) : IApiKeyResolver { public string? Resolve(string provider) => key; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
