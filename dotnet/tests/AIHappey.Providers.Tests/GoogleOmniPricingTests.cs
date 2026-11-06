using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests;

public sealed class GoogleOmniPricingTests
{
    private const string Model = "gemini-omni-1.2-flash";
    private const string Pricing = """
        {
          "google/gemini-omni-1.2-flash": {"pricing":{"input":"0.0000015","output":"0.000009"}},
          "google/gemini-other": {"pricing":{"input":"0.0000015","output":"0.000009"}}
        }
        """;

    private static ProviderCatalogScope Catalog(string pricing = Pricing)
        => ProviderCatalogScope.Begin(new TestCatalogSource("google", "{}", pricing));

    private static object? Call(string name, object? instance, params object?[] arguments)
        => typeof(GoogleAIProvider).GetMethod(name,
            BindingFlags.NonPublic | (instance is null ? BindingFlags.Static : BindingFlags.Instance))!
            .Invoke(instance, arguments);

    private static Dictionary<string, object?> Enrich(object? usage,
        Dictionary<string, object?>? metadata = null, string model = Model)
        => (Dictionary<string, object?>)Call("EnrichGoogleUsageWithGatewayCost", null,
            usage, metadata, model, null, null)!;

    private static decimal Cost(object? metadata)
        => JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
            .GetProperty("gateway").GetProperty("cost").GetDecimal();

    private static InteractionUsage Usage(int input = 100, int output = 58020, int video = 57920)
        => new()
        {
            TotalInputTokens = input,
            TotalOutputTokens = output,
            TotalTokens = input + output,
            OutputTokensByModality =
            [
                new() { Modality = "text", Tokens = output - video },
                new() { Modality = "video", Tokens = video }
            ]
        };

    private static JsonElement InteractionJson(string status = "completed", InteractionUsage? usage = null,
        string content = """{"type":"video","mime_type":"video/mp4","data":"dmlkZW8="}""")
        => JsonSerializer.SerializeToElement(new
        {
            id = "v1_omni_test",
            @object = "interaction",
            model = Model,
            status,
            steps = new[] { new { type = "model_output", content = new[] { JsonSerializer.Deserialize<JsonElement>(content) } } },
            usage
        }, JsonSerializerOptions.Web);

    [Theory]
    [InlineData(100, 58020, 57920, "0.69609")]
    [InlineData(0, 57920, 57920, "0.69504")]
    [InlineData(100, 100, 0, "0.00105")]
    [InlineData(100, 86980, 86880, "1.04361")]
    public void Bills_reported_video_tokens_at_video_rate_without_adding_them_to_total_output(
        int input, int output, int video, string expected)
    {
        using var catalog = Catalog();
        var usage = Usage(input, output, video);
        var before = JsonSerializer.Serialize(usage);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), Cost(Enrich(usage)));
        Assert.Equal(before, JsonSerializer.Serialize(usage));
    }

    [Theory]
    [InlineData(Model)]
    [InlineData("google/" + Model)]
    [InlineData("models/" + Model)]
    [InlineData("google/models/" + Model)]
    [InlineData("GEMINI-OMNI-1.2-FLASH")]
    public void Accepts_native_and_provider_prefixed_model_ids(string model)
    {
        using var catalog = Catalog();
        Assert.Equal(0.69609m, Cost(Enrich(Usage(), model: model)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"modality\":\"text\",\"tokens\":58020}]")]
    [InlineData("[{\"modality\":\"video\",\"tokens\":-1}]")]
    [InlineData("[{\"modality\":\"video\",\"tokens\":null}]")]
    [InlineData("[{\"modality\":\"video\",\"tokens\":\"invalid\"}]")]
    [InlineData("[{\"modality\":\"video\",\"tokens\":58021}]")]
    [InlineData("[{\"modality\":\"video\",\"tokens\":2147483647},{\"modality\":\"video\",\"tokens\":2147483647}]")]
    public void Missing_or_invalid_modality_breakdown_keeps_catalog_cost(string modalities)
    {
        using var catalog = Catalog();
        var usage = JsonSerializer.Deserialize<JsonElement>($$"""
            {"total_input_tokens":100,"total_output_tokens":58020,"total_tokens":58120,
             "output_tokens_by_modality":{{modalities}}}
            """);

        Assert.Equal(0.52233m, Cost(Enrich(usage)));
    }

    [Fact]
    public void Json_usage_sums_video_rows_case_insensitively_and_accepts_numeric_strings()
    {
        using var catalog = Catalog();
        var usage = JsonSerializer.Deserialize<JsonElement>("""
            {"total_input_tokens":100,"total_output_tokens":58020,"total_tokens":58120,
             "output_tokens_by_modality":[{"modality":"text","tokens":100},
                {"modality":"VIDEO","tokens":"28960"},{"modality":"video","tokens":28960}]}
            """);

        Assert.Equal(0.69609m, Cost(Enrich(usage)));
    }

    [Fact]
    public void Uses_catalog_input_and_non_video_output_prices_not_hardcoded_text_prices()
    {
        using var catalog = Catalog("""
            {"google/gemini-omni-1.2-flash":{"pricing":{"input":"0.000002","output":"0.000004"}}}
            """);

        Assert.Equal(0.69564m, Cost(Enrich(Usage())));
    }

    [Fact]
    public void Other_models_do_not_receive_the_video_rate_override()
    {
        using var catalog = Catalog();
        Assert.Equal(0.52233m, Cost(Enrich(Usage(), model: "gemini-other")));
    }

    [Fact]
    public void Preserves_json_gateway_metadata_and_repeated_enrichment_does_not_accumulate_cost()
    {
        using var catalog = Catalog();
        var metadata = new Dictionary<string, object?>
        {
            ["custom"] = "retained",
            ["gateway"] = JsonSerializer.Deserialize<JsonElement>("""{"cost":99,"requestId":"keep"}""")
        };

        var once = Enrich(Usage(), metadata);
        var twice = Enrich(Usage(), once);

        Assert.Equal(0.69609m, Cost(once));
        Assert.Equal(Cost(once), Cost(twice));
        Assert.Equal(99m, Cost(metadata));
        Assert.Equal("retained", twice["custom"]);
        Assert.Equal("keep", JsonSerializer.SerializeToElement(twice).GetProperty("gateway").GetProperty("requestId").GetString());
    }

    [Fact]
    public void No_usage_or_no_catalog_does_not_invent_a_cost()
    {
        using var catalog = Catalog();
        Assert.False(Enrich(null).ContainsKey("gateway"));
        Assert.False(Enrich(JsonSerializer.Deserialize<JsonElement>("{}")).ContainsKey("gateway"));
        Assert.False(Enrich(new Dictionary<string, object?>
        {
            ["total_output_tokens"] = 57920,
            ["output_tokens_by_modality"] = Usage().OutputTokensByModality
        }).ContainsKey("gateway"));
        using var empty = ProviderCatalogScope.Begin(EmptyProviderCatalogSource.Instance);
        Assert.False(Enrich(Usage()).ContainsKey("gateway"));
    }

    [Fact]
    public async Task Unified_and_non_streaming_conversational_mappings_preserve_corrected_cost()
    {
        using var catalog = Catalog();
        var provider = Provider(_ => JsonResponse(InteractionJson(usage: Usage())));
        var response = await provider.ExecuteUnifiedAsync(new AIRequest { ProviderId = "google", Model = Model });

        Assert.Equal(0.69609m, Cost(response.Metadata));
        Assert.Equal(58020, Assert.IsType<InteractionUsage>(response.Usage).TotalOutputTokens);
        Assert.Equal(0.69609m, Cost(response.ToResponseResult().Metadata));
        Assert.Contains("0.69609", JsonSerializer.Serialize(response.ToMessagesResponse()));

        var chat = await provider.CompleteChatAsync(new ChatCompletionOptions { Model = Model });
        Assert.Equal(0.69609m, Cost(chat.AdditionalProperties!["metadata"]));

        var endpointResponse = await provider.ResponsesAsync(new ResponseRequest { Model = Model });
        Assert.Equal(0.69609m, Cost(endpointResponse.Metadata));

        var messages = await provider.MessagesAsync(
            new MessagesRequest { Model = Model, MaxTokens = 16, Messages = [] }, []);
        Assert.Equal(0.69609m, Cost(messages.Metadata));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Streaming_finish_metadata_uses_original_typed_or_json_modality_usage(bool json)
    {
        using var catalog = Catalog();
        var provider = Provider(_ => throw new InvalidOperationException("No HTTP expected."));
        var interaction = InteractionJson(usage: Usage()).Deserialize<Interaction>(JsonSerializerOptions.Web)!;
        var original = new AIFinishEventData
        {
            Model = Model,
            InputTokens = 100,
            OutputTokens = 58020,
            TotalTokens = 58120,
            Response = json ? InteractionJson(usage: Usage()) : interaction,
            MessageMetadata = AIFinishMessageMetadata.FromDictionary(new Dictionary<string, object?> { ["custom"] = "retained" })
        };
        var streamEvent = new AIStreamEvent
        {
            ProviderId = "google",
            Event = new AIEventEnvelope { Type = "finish", Data = original }
        };

        var enriched = (AIStreamEvent)Call("EnrichUnifiedFinishEventWithGatewayCost", provider, streamEvent)!;
        var finish = Assert.IsType<AIFinishEventData>(enriched.Event.Data);
        Assert.Equal(0.69609m, Cost(finish.MessageMetadata!.ToDictionary()));
        Assert.Equal(58020, finish.OutputTokens);
        Assert.Same(original.Response, finish.Response);
        Assert.Equal("retained", finish.MessageMetadata.ToDictionary()["custom"]?.ToString());

        var flattened = interaction.ToUnifiedResponse("google").ToResponseResult();
        flattened.Metadata = finish.MessageMetadata.ToDictionary();
        var mapped = (ResponseResult)Call("EnrichResponseWithGatewayCost", null, flattened, Model, null, null)!;
        Assert.Equal(0.69609m, Cost(mapped.Metadata));
    }

    [Fact]
    public async Task Conversational_streaming_endpoints_keep_the_video_rate_after_native_mapping()
    {
        using var catalog = Catalog();
        var interaction = InteractionJson(usage: Usage());
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"event: interaction.completed\ndata: {{\"event_type\":\"interaction.completed\",\"interaction\":{interaction.GetRawText()}}}\n\n",
                Encoding.UTF8, "text/event-stream")
        });

        var unifiedFinishes = new List<AIFinishEventData>();
        await foreach (var item in provider.StreamUnifiedAsync(new AIRequest { ProviderId = "google", Model = Model }))
            if (item.Event.Data is AIFinishEventData finish) unifiedFinishes.Add(finish);
        Assert.Equal(0.69609m, Cost(Assert.Single(unifiedFinishes).MessageMetadata!.ToDictionary()));

        var chatCosts = new List<decimal>();
        await foreach (var item in provider.CompleteChatStreamingAsync(new ChatCompletionOptions { Model = Model }, default))
            if (item.AdditionalProperties?.TryGetValue("metadata", out var metadata) == true)
                chatCosts.Add(Cost(metadata));
        Assert.Equal(0.69609m, Assert.Single(chatCosts));

        var responses = new List<ResponseResult>();
        await foreach (var item in provider.ResponsesStreamingAsync(new ResponseRequest { Model = Model }))
            if (item is AIHappey.Responses.Streaming.ResponseCompleted completed) responses.Add(completed.Response);
        Assert.Equal(0.69609m, Cost(Assert.Single(responses).Metadata));

        var messageCosts = new List<decimal>();
        await foreach (var item in provider.MessagesStreamingAsync(
            new MessagesRequest { Model = Model, MaxTokens = 16, Messages = [] }, []))
        {
            var json = JsonSerializer.SerializeToElement(item);
            if (json.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("gateway", out _))
                messageCosts.Add(Cost(metadata));
        }
        Assert.Equal(0.69609m, Assert.Single(messageCosts));
    }

    [Fact]
    public async Task Video_poll_without_modality_breakdown_keeps_catalog_based_cost()
    {
        using var catalog = Catalog();
        var usage = Usage();
        usage.OutputTokensByModality = null;
        var provider = Provider(_ => JsonResponse(InteractionJson(usage: usage)));

        var result = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus("v1_omni_test"));
        Assert.Equal(0.52233m, Cost(result.ProviderMetadata));
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("in_progress", true)]
    [InlineData("failed", true)]
    [InlineData("completed", false)]
    public async Task Video_poll_metadata_preserves_interaction_and_adds_cost_only_when_usage_is_available(
        string status, bool includeUsage)
    {
        using var catalog = Catalog();
        var interaction = InteractionJson(status, includeUsage ? Usage() : null);
        var provider = Provider(_ => JsonResponse(interaction));

        var result = await provider.GetVideoOperationStatus("v1_omni_test");
        var metadata = JsonSerializer.SerializeToElement(result).GetProperty("providerMetadata");
        Assert.Equal("v1_omni_test", metadata.GetProperty("google").GetProperty("interactionId").GetString());
        Assert.Equal(interaction.GetRawText(), metadata.GetProperty("google").GetProperty("interaction").GetRawText());
        if (includeUsage)
            Assert.Equal(0.69609m, metadata.GetProperty("gateway").GetProperty("cost").GetDecimal());
        else
            Assert.False(metadata.TryGetProperty("gateway", out _));

        if (status == "completed") Assert.IsType<VideoOperationCompletedResult>(result);
        else if (status == "failed") Assert.IsType<VideoOperationErrorResult>(result);
        else Assert.IsType<VideoOperationPendingResult>(result);
    }

    [Fact]
    public async Task Video_start_also_emits_cost_if_google_reports_usage_immediately()
    {
        using var catalog = Catalog();
        var provider = Provider(_ => JsonResponse(InteractionJson(usage: Usage())));
        var result = await provider.StartVideoOperation(new VideoRequest { Model = Model, Prompt = "Make a video" });

        Assert.Equal(0.69609m, Cost(result.ProviderMetadata));
        Assert.True(result.ProviderMetadata!.ContainsKey("google"));
    }

    private static HttpResponseMessage JsonResponse(JsonElement json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json.GetRawText(), Encoding.UTF8, "application/json")
    };

    private static GoogleAIProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> response)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), NullLogger<GoogleAIProvider>.Instance, new TestClientFactory(response));

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestHandler(response));
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
