using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Decisions;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests;

public sealed partial class GoogleDecisionTests
{
    [Fact]
    public async Task Vercel_maps_all_question_types_and_retains_unmodified_native_details()
    {
        const string answers = """
            {
              "severity":{"type":"score","score":1.12,"probabilities":{"0":0.03,"1":0.83,"2":0.15},
                "legend":{"0":"Cosmetic","1":"Workaround","2":"Blocked"},"confidence":0.83},
              "route":{"type":"choice","choice":"billing team","probabilities":{"shipping":0.01,"billing team":0.99},"confidence":0.99},
              "damaged":{"type":"noul","noul":0.03,"confidence":0.97,"future_detail":{"kept":true}}
            }
            """;
        var transport = new Transport(_ => Reply(Envelope(answers)));
        var request = SimpleRequest();
        request.Model = "google/models/gemini-decision-1";
        request.State = Json("""{"order":{"id":"8821"},"message":"Cracked mug"}""");
        ((DecisionBooleanQuestion)request.Questions["damaged"]).Criteria = new() { True = "Broken", False = "Preference" };
        request.Questions["route"] = new DecisionChoiceQuestion
        {
            Instructions = "Where should this go?",
            Criteria = new() { ["billing team"] = "Payments", ["shipping"] = null }
        };
        request.Questions["severity"] = new DecisionScoreQuestion
        {
            Instructions = "How severe?", Criteria = ["Cosmetic", "Workaround", "Blocked"]
        };
        request.ProviderOptions = new()
        {
            ["google"] = Json("""{"future_option":{"nested":[1,null,{"enabled":true}]}}"""),
            ["other"] = Json("""{"do_not_forward":true}""")
        };
        request.Headers = new()
        {
            ["x-goog-user-project"] = "project-1", ["x-google-trace"] = "trace-1",
            ["x-goog-api-key"] = "do-not-use", ["Authorization"] = "do-not-use", ["Cookie"] = "do-not-use"
        };

        var response = await Provider(transport).DecisionRequestAsync(request);

        Assert.Equal(HttpMethod.Post, transport.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/decisions", transport.Uri!.ToString());
        Assert.Equal("test-google-key", transport.Headers["x-goog-api-key"]);
        Assert.Equal("project-1", transport.Headers["x-goog-user-project"]);
        Assert.Equal("trace-1", transport.Headers["x-google-trace"]);
        Assert.False(transport.Headers.ContainsKey("Authorization"));
        Assert.False(transport.Headers.ContainsKey("Cookie"));
        var body = transport.Body;
        Assert.Equal("gemini-decision-1", body.GetProperty("model").GetString());
        Assert.Equal(request.State.Value.GetRawText(), body.GetProperty("input").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("future_option").GetProperty("nested")[1].ValueKind);
        Assert.False(body.TryGetProperty("providerOptions", out _));
        Assert.False(body.TryGetProperty("do_not_forward", out _));
        var questions = body.GetProperty("questions");
        Assert.Equal("noul", questions.GetProperty("damaged").GetProperty("type").GetString());
        Assert.Equal("Broken", questions.GetProperty("damaged").GetProperty("criteria").GetProperty("true_description").GetString());
        Assert.Equal("Preference", questions.GetProperty("damaged").GetProperty("criteria").GetProperty("false_description").GetString());
        Assert.Equal("", questions.GetProperty("route").GetProperty("criteria").GetProperty("shipping").GetString());
        Assert.Equal(3, questions.GetProperty("severity").GetProperty("criteria").GetArrayLength());
        Assert.Equal(0.03, Assert.IsType<DecisionBooleanAnswer>(response.Answers["damaged"]).Probability);
        Assert.Equal("billing team", Assert.IsType<DecisionChoiceAnswer>(response.Answers["route"]).Choice);
        var score = Assert.IsType<DecisionScoreAnswer>(response.Answers["severity"]);
        Assert.Equal(1.12, score.Score); // Do not recalculate/renormalize rounded upstream values.
        Assert.Equal(0.15, score.Probabilities!["2"]);
        Assert.Equal(100, response.Usage!.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal("google/gemini-decision-1", response.Response!.ModelId);
        Assert.Equal("request-1", response.Response.Headers!["x-request-id"]);
        var native = response.ProviderMetadata!["google"];
        Assert.Equal(Json(Envelope(answers)).GetRawText(), native.GetRawText());
        Assert.Equal(native.GetRawText(), Assert.IsType<JsonElement>(response.Response.Body).GetRawText());
        Assert.Equal(0.97, native.GetProperty("answers").GetProperty("damaged").GetProperty("confidence").GetDouble());
        Assert.True(native.GetProperty("answers").GetProperty("damaged").GetProperty("future_detail").GetProperty("kept").GetBoolean());
        Assert.Equal("text", native.GetProperty("usage").GetProperty("input_tokens_by_modality")[0].GetProperty("modality").GetString());
    }

    [Fact]
    public async Task OpenAI_preserves_names_order_typed_choices_labels_and_native_metadata()
    {
        var transport = new Transport(_ => Reply(Envelope("""
            {
              "q0002":{"type":"score","score":1.25,"probabilities":{"2":0.5,"0":0.25,"1":0.25},
                "legend":{"0":"Low","1":"Medium","2":"High"},"confidence":0.5},
              "q0001":{"type":"choice","choice":"o001","probabilities":{"o000":0.2,"o001":0.8},"confidence":0.8},
              "q0000":{"type":"noul","noul":0.8,"confidence":0.8}
            }
            """)));
        var request = new OpenAIDecisionRequest
        {
            Model = "google/gemini-decision-1", Input = "Evidence",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "duplicate name!", Instructions = "Is it true?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "duplicate name!", Instructions = "Which value?",
                    Choices = [new() { Value = "true" }, new() { Value = true, Description = "Boolean true" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Instructions = "How severe?",
                    Levels = [new() { Label = "low" }, new() { Label = "medium", Description = "Workaround" }, new() { Label = "high" }]
                }
            ],
            AdditionalProperties = new() { ["future_native_option"] = Json("""{"preserve":[null,true]}""") }
        };

        var response = await Provider(transport).OpenAIDecisionRequestAsync(request);

        var nativeQuestions = transport.Body.GetProperty("questions");
        Assert.Equal(new[] { "q0000", "q0001", "q0002" }, nativeQuestions.EnumerateObject().Select(item => item.Name));
        Assert.Equal("true", nativeQuestions.GetProperty("q0001").GetProperty("criteria").GetProperty("o000").GetString());
        Assert.Equal("true: Boolean true", nativeQuestions.GetProperty("q0001").GetProperty("criteria").GetProperty("o001").GetString());
        Assert.Equal("medium: Workaround", nativeQuestions.GetProperty("q0002").GetProperty("criteria")[1].GetString());
        Assert.True(transport.Body.GetProperty("future_native_option").GetProperty("preserve")[1].GetBoolean());
        Assert.Equal("gemini-decision-1", response.Model);
        Assert.Equal(0.8, Assert.IsType<OpenAIDecisionPredicateAnswer>(response.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]);
        Assert.True(choice.Choice.Value.GetBoolean());
        Assert.Equal(JsonValueKind.String, choice.Probabilities[0].Value.Value.ValueKind);
        Assert.Equal(JsonValueKind.True, choice.Probabilities[1].Value.Value.ValueKind);
        Assert.Equal(new string?[] { "duplicate name!", "duplicate name!", null }, response.Answers.Select(answer => answer.Name));
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers[2]);
        Assert.Equal(new[] { "low", "medium", "high" }, score.Probabilities.Select(item => item.Label));
        Assert.Equal(new double[] { 0, 1, 2 }, score.Probabilities.Select(item => item.Value));
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal(100, response.Usage.TotalTokens);
        Assert.True(response.AdditionalProperties!["providerMetadata"].GetProperty("google").TryGetProperty("answers", out _));
        Assert.Equal("request-1", response.AdditionalProperties["responseHeaders"].GetProperty("x-request-id").GetString());
    }

    [Theory]
    [InlineData("{\"type\":\"text\",\"text\":\"plain data\"}")]
    [InlineData("[{\"type\":\"file\",\"data\":\"plain data\"}]")]
    [InlineData("[{\"role\":\"user\",\"content\":\"history as evidence\"}]")]
    public async Task Legacy_structured_state_is_always_JSON_text_not_inferred_media(string state)
    {
        var transport = new Transport();
        var request = SimpleRequest();
        request.State = Json(state);
        await Provider(transport).DecisionRequestAsync(request);
        Assert.Equal(state, transport.Body.GetProperty("input").GetString());
    }

    [Fact]
    public async Task OpenAI_text_JSON_images_and_multipart_text_instructions_keep_order_and_separators()
    {
        var transport = new Transport(_ => Reply(Envelope("""{"q0000":{"type":"noul","noul":0.8,"confidence":0.8}}""")));
        var request = new OpenAIDecisionRequest
        {
            Model = "gemini-decision-1",
            Input = new OpenAIDecisionInputMessage[]
            {
                new() { Content = new OpenAIDecisionInputPart[]
                {
                    new OpenAIDecisionInputText { Text = "Context\n" },
                    new OpenAIDecisionInputJson { Data = Json("""{"order":8821}""") },
                    new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQID", Detail = "original" },
                    new OpenAIDecisionInputImage { ImageUrl = "https://example.com/photo.jpg?signature=kept", Detail = "low" }
                } }
            },
            Questions = [new OpenAIDecisionPredicateQuestion
            {
                InstructionContent = new OpenAIDecisionInputPart[]
                {
                    new OpenAIDecisionInputText { Text = "Check\n" },
                    new OpenAIDecisionInputJson { Data = Json("""{"policy":"damage"}""") }
                }
            }]
        };
        await Provider(transport).OpenAIDecisionRequestAsync(request);
        var blocks = transport.Body.GetProperty("input");
        Assert.Equal("Context\n", blocks[0].GetProperty("text").GetString());
        Assert.Equal("{\"order\":8821}", blocks[1].GetProperty("text").GetString());
        Assert.Equal("AQID", blocks[2].GetProperty("data").GetString());
        Assert.Equal("image/png", blocks[2].GetProperty("mime_type").GetString());
        Assert.Equal("ultra_high", blocks[2].GetProperty("resolution").GetString());
        Assert.Equal("https://example.com/photo.jpg?signature=kept", blocks[3].GetProperty("uri").GetString());
        Assert.Equal("image/jpeg", blocks[3].GetProperty("mime_type").GetString());
        Assert.Equal("low", blocks[3].GetProperty("resolution").GetString());
        Assert.Equal("Check\n{\"policy\":\"damage\"}", transport.Body.GetProperty("questions").GetProperty("q0000").GetProperty("instructions").GetString());
        Assert.Equal(1, transport.Calls); // No media downloads or additional network calls.
    }

    [Theory]
    [InlineData("image/png", "image")]
    [InlineData("audio/mp3", "audio")]
    [InlineData("video/mp4", "video")]
    [InlineData("application/pdf", "document")]
    public async Task SDK_supports_each_Google_media_kind_with_inline_and_Files_URI_inputs(string mimeType, string nativeType)
    {
        var transport = new Transport();
        var request = SimpleRequest();
        request.StateFormat = "sdk";
        request.State = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "text", text = "Evidence\n" },
            new { type = "json", value = new { order = 8821 } },
            new { type = "file", mediaType = mimeType, data = new { type = "data", data = "AQID" } },
            new { type = "file", mediaType = mimeType, data = new { type = "url", url = "https://generativelanguage.googleapis.com/v1beta/files/file-1" } }
        });
        await Provider(transport).DecisionRequestAsync(request);
        var blocks = transport.Body.GetProperty("input");
        Assert.Equal("Evidence\n", blocks[0].GetProperty("text").GetString());
        Assert.Equal("{\"order\":8821}", blocks[1].GetProperty("text").GetString());
        Assert.Equal(nativeType, blocks[2].GetProperty("type").GetString());
        Assert.Equal(mimeType, blocks[2].GetProperty("mime_type").GetString());
        Assert.Equal("AQID", blocks[2].GetProperty("data").GetString());
        Assert.Equal(nativeType, blocks[3].GetProperty("type").GetString());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/files/file-1", blocks[3].GetProperty("uri").GetString());
    }

    [Fact]
    public async Task SDK_part_options_and_original_URL_are_preserved_and_question_text_blocks_are_concatenated()
    {
        var transport = new Transport();
        var request = SimpleRequest();
        request.StateFormat = "sdk";
        request.State = Json("""
            [
              {"type":"file","mediaType":"image/webp","filename":"photo.webp",
                "data":{"type":"url","url":"https://rewritten.invalid/file","originalUrl":"https://example.com/signed?token=keep"},
                "providerOptions":{"google":{"resolution":"ultra_high","future_visual_option":{"preserved":true}}}},
              {"type":"file","mediaType":"video/mp4","data":{"type":"data","data":"AQID"},
                "providerOptions":{"google":{"resolution":"medium"}}},
              {"type":"file","mediaType":"text/plain","data":{"type":"text","text":"Extra context\n"}}
            ]
            """);
        request.Questions["damaged"].Instructions = Json("""[{"type":"text","text":"Is it "},{"type":"text","text":"damaged?"}]""");
        await Provider(transport).DecisionRequestAsync(request);
        var blocks = transport.Body.GetProperty("input");
        Assert.Equal("File: photo.webp\n", blocks[0].GetProperty("text").GetString());
        Assert.Equal("https://example.com/signed?token=keep", blocks[1].GetProperty("uri").GetString());
        Assert.Equal("ultra_high", blocks[1].GetProperty("resolution").GetString());
        Assert.True(blocks[1].GetProperty("future_visual_option").GetProperty("preserved").GetBoolean());
        Assert.Equal("medium", blocks[2].GetProperty("resolution").GetString());
        Assert.Equal("Extra context\n", blocks[3].GetProperty("text").GetString());
        Assert.Equal("Is it damaged?", transport.Body.GetProperty("questions").GetProperty("damaged").GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task Rounded_choice_probabilities_are_preserved_without_renormalizing()
    {
        var transport = new Transport(_ => Reply(Envelope("""
            {"q0000":{"type":"choice","choice":"billing","probabilities":{"billing":0.99,"shipping":0.004,"other":0.003,"technical":0.002},"confidence":0.99}}
            """)));
        var request = SimpleOpenAIRequest();
        request.Questions = [new OpenAIDecisionChoiceQuestion
        {
            Name = "department", Instructions = "Route this request.",
            Choices = [new() { Value = "technical" }, new() { Value = "shipping" }, new() { Value = "other" }, new() { Value = "billing" }]
        }];
        var response = await Provider(transport).OpenAIDecisionRequestAsync(request);
        var answer = Assert.IsType<OpenAIDecisionChoiceAnswer>(Assert.Single(response.Answers));
        Assert.Equal("billing", answer.Choice.Value.GetString());
        Assert.Equal(0.999, answer.Probabilities.Sum(item => item.Probability), 12);
        Assert.Equal(0.002, answer.Probabilities[0].Probability);
        Assert.Equal(new[] { "technical", "shipping", "other", "billing" },
            transport.Body.GetProperty("questions").GetProperty("q0000").GetProperty("criteria").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task SDK_data_URL_and_text_only_native_instructions_are_supported()
    {
        var transport = new Transport();
        var request = SimpleRequest();
        request.StateFormat = "sdk";
        request.State = Json("""[{"type":"file","mediaType":"image/png","data":{"type":"url","url":"data:image/png;base64,AQID"}}]""");
        request.Questions["damaged"].Instructions = Json("""{"type":"text","text":"Is this damaged?"}""");
        await Provider(transport).DecisionRequestAsync(request);
        Assert.Equal("AQID", transport.Body.GetProperty("input")[0].GetProperty("data").GetString());
        Assert.Equal("Is this damaged?", transport.Body.GetProperty("questions").GetProperty("damaged").GetProperty("instructions").GetString());
    }

    [Fact]
    public async Task Maximum_question_count_is_accepted()
    {
        var transport = new Transport(body =>
        {
            var answers = new JsonObject();
            foreach (var question in body.GetProperty("questions").EnumerateObject())
                answers.Add(question.Name, new JsonObject { ["type"] = "noul", ["noul"] = 0.8, ["confidence"] = 0.8 });
            return Reply(Envelope(answers.ToJsonString()));
        });
        var request = SimpleRequest();
        request.Questions = Enumerable.Range(0, 1000).ToDictionary(index => "q" + index,
            _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "Check?" });
        var response = await Provider(transport).DecisionRequestAsync(request);
        Assert.Equal(1000, response.Answers.Count);
    }

    private static DecisionRequest SimpleRequest() => new()
    {
        Model = "gemini-decision-1", State = "Cracked mug",
        Questions = new() { ["damaged"] = new DecisionBooleanQuestion { Instructions = "Is it damaged?" } }
    };

    private static OpenAIDecisionRequest SimpleOpenAIRequest() => new()
    {
        Model = "gemini-decision-1", Input = "Cracked mug",
        Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it damaged?" }]
    };

    private static GoogleAIProvider Provider(Transport transport)
        => new(new KeyResolver(), new AsyncCacheHelper(), NullLogger<GoogleAIProvider>.Instance, transport);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Envelope(string answers) => $$$"""
        {"answers":{{{answers}}},"usage":{"total_input_tokens":100,"total_output_tokens":0,"total_tokens":100,
          "input_tokens_by_modality":[{"modality":"text","tokens":100}]},"future_response":{"kept":true}}
        """;

    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.Add("x-request-id", "request-1");
        return response;
    }

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "google" ? "test-google-key" : null;
    }

    private sealed class Transport(Func<JsonElement, HttpResponseMessage>? respond = null) : HttpMessageHandler, IHttpClientFactory
    {
        public JsonElement Body { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public int Calls { get; private set; }
        public Dictionary<string, string> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public Func<CancellationToken, Task>? BeforeReply { get; init; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = Json(await request.Content!.ReadAsStringAsync(cancellationToken));
            Uri = request.RequestUri;
            Method = request.Method;
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
            if (BeforeReply is not null) await BeforeReply(cancellationToken);
            return respond?.Invoke(Body) ?? Reply(Envelope("""{"damaged":{"type":"noul","noul":0.8,"confidence":0.8}}"""));
        }
    }
}
