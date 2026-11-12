using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Decisions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed partial class GoogleDecisionTests
{
    [Theory]
    [InlineData("model")]
    [InlineData("INPUT")]
    [InlineData("questions")]
    [InlineData("cached_content")]
    [InlineData("prompt_cache_key")]
    [InlineData("service_tier")]
    [InlineData("reasoning")]
    public async Task Canonical_overrides_and_unsupported_controls_fail_before_transport(string property)
    {
        var transport = new Transport();
        var provider = Provider(transport);
        var vercel = SimpleRequest();
        vercel.ProviderOptions = new() { ["google"] = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [property] = "unsupported" }) };
        var vercelError = await Record.ExceptionAsync(() => provider.DecisionRequestAsync(vercel));
        Assert.True(vercelError is ArgumentException or NotSupportedException);
        var openAI = SimpleOpenAIRequest();
        openAI.AdditionalProperties = new() { [property] = JsonSerializer.SerializeToElement("unsupported") };
        var openAIError = await Record.ExceptionAsync(() => provider.OpenAIDecisionRequestAsync(openAI));
        Assert.True(openAIError is ArgumentException or NotSupportedException);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("question-count")]
    [InlineData("question-key")]
    [InlineData("choice-too-few")]
    [InlineData("choice-too-many")]
    [InlineData("score-too-few")]
    [InlineData("score-too-many")]
    [InlineData("empty-level")]
    [InlineData("empty-input")]
    public async Task Invalid_request_limits_are_rejected_before_sending(string scenario)
    {
        var transport = new Transport();
        var request = SimpleRequest();
        switch (scenario)
        {
            case "question-count":
                request.Questions = Enumerable.Range(0, 1001).ToDictionary(index => "q" + index,
                    _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "Check?" });
                break;
            case "question-key":
                request.Questions = new() { ["bad key"] = new DecisionBooleanQuestion { Instructions = "Check?" } };
                break;
            case "choice-too-few":
            case "choice-too-many":
                request.Questions["damaged"] = new DecisionChoiceQuestion
                {
                    Instructions = "Choose?", Criteria = Enumerable.Range(0, scenario == "choice-too-few" ? 1 : 256)
                        .ToDictionary(index => "option" + index, _ => (DecisionInput?)null)
                };
                break;
            case "score-too-few":
            case "score-too-many":
                request.Questions["damaged"] = new DecisionScoreQuestion
                {
                    Instructions = "Score?", Criteria = Enumerable.Range(0, scenario == "score-too-few" ? 1 : 256)
                        .Select(index => (DecisionInput?)new DecisionInput("Level " + index)).ToArray()
                };
                break;
            case "empty-level":
                request.Questions["damaged"] = new DecisionScoreQuestion { Instructions = "Score?", Criteria = ["", "High"] };
                break;
            case "empty-input": request.State = ""; break;
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Provider(transport).DecisionRequestAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("non\u00a0breaking")]
    [InlineData("control\tcharacter")]
    public async Task Google_option_key_rules_are_validated(string key)
    {
        var transport = new Transport();
        var request = SimpleRequest();
        request.Questions["damaged"] = new DecisionChoiceQuestion
        {
            Instructions = "Choose?", Criteria = new() { [key] = null, ["other"] = null }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(transport).DecisionRequestAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("{\"type\":\"file\",\"mediaType\":\"application/octet-stream\",\"data\":{\"type\":\"data\",\"data\":\"AQID\"}}")]
    [InlineData("{\"type\":\"file\",\"mediaType\":\"image/png\",\"data\":{\"type\":\"url\",\"url\":\"gs://bucket/photo.png\"}}")]
    [InlineData("{\"type\":\"file\",\"mediaType\":\"video/mp4\",\"data\":{\"type\":\"url\",\"url\":\"https://www.youtube.com/watch?v=123\"}}")]
    [InlineData("{\"type\":\"file\",\"mediaType\":\"image/png\",\"data\":{\"type\":\"url\",\"url\":\"data:image/jpeg;base64,AQID\"}}")]
    [InlineData("{\"type\":\"file\",\"mediaType\":\"image/png\",\"data\":{\"type\":\"data\",\"data\":\"not base64!\"}}")]
    [InlineData("{\"type\":\"file\",\"mediaType\":\"audio/mp3\",\"data\":{\"type\":\"data\",\"data\":\"AQID\"},\"providerOptions\":{\"google\":{\"resolution\":\"low\"}}}")]
    [InlineData("{\"type\":\"file\",\"mediaType\":\"video/mp4\",\"data\":{\"type\":\"data\",\"data\":\"AQID\"},\"providerOptions\":{\"google\":{\"resolution\":\"ultra_high\"}}}")]
    [InlineData("{\"type\":\"text\",\"text\":\"Evidence\",\"providerOptions\":{\"google\":{\"type\":\"image\"}}}")]
    public async Task Unsupported_or_invalid_SDK_media_is_not_silently_discarded(string part)
    {
        var transport = new Transport();
        var request = SimpleRequest();
        request.StateFormat = "sdk";
        request.State = Json("[" + part + "]");
        var exception = await Record.ExceptionAsync(() => Provider(transport).DecisionRequestAsync(request));
        Assert.True(exception is ArgumentException or NotSupportedException);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("cache-key")]
    [InlineData("cache-options")]
    [InlineData("safety")]
    [InlineData("part-breakpoint")]
    [InlineData("tool-history")]
    [InlineData("assistant-history")]
    [InlineData("question-image")]
    public async Task Unsupported_OpenAI_semantics_are_rejected_instead_of_flattened(string scenario)
    {
        var transport = new Transport();
        var request = SimpleOpenAIRequest();
        switch (scenario)
        {
            case "cache-key": request.PromptCacheKey = "cache"; break;
            case "cache-options": request.PromptCacheOptions = new() { Mode = "explicit" }; break;
            case "safety": request.SafetyIdentifier = "user"; break;
            case "part-breakpoint":
                request.Input = new OpenAIDecisionInputMessage[]
                {
                    new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputText { Text = "Evidence", PromptCacheBreakpoint = new() } } }
                };
                break;
            case "tool-history": request.Input = new OpenAIDecisionInputItem[] { new OpenAIDecisionFunctionCall { CallId = "1", Name = "test", Arguments = "{}" } }; break;
            case "assistant-history": request.Input = new OpenAIDecisionInputMessage[] { new() { Role = "assistant", Content = "History" } }; break;
            case "question-image":
                request.Questions[0].InstructionContent = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQID" } };
                break;
        }
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(transport).OpenAIDecisionRequestAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("missing-answer")]
    [InlineData("extra-answer")]
    [InlineData("wrong-type")]
    [InlineData("missing-confidence")]
    [InlineData("bad-confidence")]
    [InlineData("out-of-range")]
    [InlineData("negative-tokens")]
    [InlineData("output-tokens")]
    [InlineData("wrong-total")]
    [InlineData("missing-usage")]
    public async Task Malformed_responses_do_not_turn_into_default_answers(string scenario)
    {
        var root = JsonNode.Parse(Envelope("""{"damaged":{"type":"noul","noul":0.8,"confidence":0.8}}"""))!.AsObject();
        var answer = root["answers"]!["damaged"]!.AsObject();
        switch (scenario)
        {
            case "missing-answer": root["answers"] = new JsonObject(); break;
            case "extra-answer": root["answers"]!["extra"] = answer.DeepClone(); break;
            case "wrong-type": answer["type"] = "refusal"; break;
            case "missing-confidence": answer.Remove("confidence"); break;
            case "bad-confidence": answer["confidence"] = 0.2; break;
            case "out-of-range": answer["noul"] = 1.1; break;
            case "negative-tokens": root["usage"]!["total_input_tokens"] = -1; break;
            case "output-tokens": root["usage"]!["total_output_tokens"] = 1; break;
            case "wrong-total": root["usage"]!["total_tokens"] = 101; break;
            case "missing-usage": root.Remove("usage"); break;
        }
        var transport = new Transport(_ => Reply(root.ToJsonString()));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(transport).DecisionRequestAsync(SimpleRequest()));
        Assert.Contains("invalid Decisions response", exception.Message);
    }

    [Theory]
    [InlineData("missing-outcome")]
    [InlineData("wrong-distribution")]
    [InlineData("unknown-choice")]
    [InlineData("nonmaximal-choice")]
    [InlineData("score-missing-level")]
    [InlineData("score-missing-legend")]
    [InlineData("score-out-of-range")]
    [InlineData("score-wrong-mean")]
    public async Task Invalid_choice_and_score_distributions_are_rejected(string scenario)
    {
        var request = SimpleRequest();
        JsonObject answer;
        if (scenario.StartsWith("score-", StringComparison.Ordinal))
        {
            request.Questions["damaged"] = new DecisionScoreQuestion { Instructions = "Score?", Criteria = ["Low", "Medium", "High"] };
            answer = JsonNode.Parse("""{"type":"score","score":1.25,"probabilities":{"0":0.25,"1":0.25,"2":0.5},"confidence":0.5,"legend":{"0":"Low","1":"Medium","2":"High"}}""")!.AsObject();
        }
        else
        {
            request.Questions["damaged"] = new DecisionChoiceQuestion { Instructions = "Choose?", Criteria = new() { ["a"] = null, ["b"] = null } };
            answer = JsonNode.Parse("""{"type":"choice","choice":"a","probabilities":{"a":0.8,"b":0.2},"confidence":0.8}""")!.AsObject();
        }
        switch (scenario)
        {
            case "missing-outcome": answer["probabilities"]!.AsObject().Remove("b"); break;
            case "wrong-distribution": answer["probabilities"]!["b"] = 0.8; break;
            case "unknown-choice": answer["choice"] = "unknown"; break;
            case "nonmaximal-choice": answer["choice"] = "b"; break;
            case "score-missing-level": answer["probabilities"]!.AsObject().Remove("1"); break;
            case "score-missing-legend": answer.Remove("legend"); break;
            case "score-out-of-range": answer["score"] = 3; break;
            case "score-wrong-mean": answer["score"] = 0.5; break;
        }
        var transport = new Transport(_ => Reply(Envelope(new JsonObject { ["damaged"] = answer }.ToJsonString())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(transport).DecisionRequestAsync(request));
    }

    [Theory]
    [InlineData("max-output")]
    [InlineData("reasoning")]
    [InlineData("box-question")]
    [InlineData("sdk-unknown-field")]
    [InlineData("sdk-tool-part")]
    [InlineData("media-in-question")]
    public async Task Unsupported_Vercel_features_fail_before_transport(string scenario)
    {
        var transport = new Transport();
        var request = SimpleRequest();
        switch (scenario)
        {
            case "max-output": request.MaxOutputTokens = 1; break;
            case "reasoning": request.Reasoning = Json("{}"); break;
            case "box-question": request.Questions["damaged"] = new DecisionBoxQuestion { Instructions = "Locate damage." }; break;
            case "sdk-unknown-field": request.StateFormat = "sdk"; request.State = Json("""[{"type":"text","text":"Evidence","unmapped":true}]"""); break;
            case "sdk-tool-part": request.StateFormat = "sdk"; request.State = Json("""[{"type":"tool-call","toolName":"test"}]"""); break;
            case "media-in-question": request.Questions["damaged"].Instructions = Json("""{"type":"image","data":"AQID","mime_type":"image/png"}"""); break;
        }
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(transport).DecisionRequestAsync(request));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("https://example.com/opaque?signature=123")]
    [InlineData("http://example.com/photo.png")]
    public async Task OpenAI_images_without_a_representable_Google_MIME_or_HTTPS_URI_fail_explicitly(string url)
    {
        var transport = new Transport();
        var request = SimpleOpenAIRequest();
        request.Input = new OpenAIDecisionInputMessage[]
        {
            new() { Content = new OpenAIDecisionInputPart[] { new OpenAIDecisionInputImage { ImageUrl = url } } }
        };
        var exception = await Record.ExceptionAsync(() => Provider(transport).OpenAIDecisionRequestAsync(request));
        Assert.True(exception is ArgumentException or NotSupportedException);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Safety_errors_preserve_HTTP_status_code_and_provider_body(bool openAI)
    {
        const string body = """{"error":{"code":"safety","message":"Request blocked due to safety violations."}}""";
        var transport = new Transport(_ => Reply(body, HttpStatusCode.BadRequest));
        var provider = Provider(transport);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            if (openAI) await provider.OpenAIDecisionRequestAsync(SimpleOpenAIRequest());
            else await provider.DecisionRequestAsync(SimpleRequest());
        });
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains(body, exception.Message);
    }

    [Fact]
    public async Task Precancelled_and_inflight_requests_propagate_cancellation()
    {
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        var transport = new Transport();
        var provider = Provider(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DecisionRequestAsync(SimpleRequest(), preCancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAIDecisionRequestAsync(SimpleOpenAIRequest(), preCancelled.Token));
        Assert.Equal(0, transport.Calls);

        using var cancellation = new CancellationTokenSource();
        var inflight = new Transport
        {
            BeforeReply = token =>
            {
                Assert.True(token.CanBeCanceled);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(inflight).DecisionRequestAsync(SimpleRequest(), cancellation.Token));
    }
}
