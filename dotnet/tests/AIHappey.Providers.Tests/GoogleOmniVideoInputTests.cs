using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests;

public sealed class GoogleOmniVideoInputTests
{
    private const string Model = "gemini-omni-1.2-flash";
    private const string Prompt = "A smooth cinematic transition from a green forest to a snowy forest.";

    [Theory]
    [InlineData("first_frame", "last_frame", false)]
    [InlineData("first_frame", "last_frame", true)]
    [InlineData("firstFrame", "lastFrame", true)]
    [InlineData("first", "last", true)]
    [InlineData("FIRST_FRAME", "LAST_FRAME", true)]
    public async Task Sends_both_frames_in_role_order_without_unsupported_warnings_or_control_tags(
        string firstType, string lastType, bool reversed)
    {
        using var transport = new RecordingTransport();
        var request = Request();
        var first = Frame(firstType, "first");
        var last = Frame(lastType, "last");
        request.FrameImages = reversed ? [last, first] : [first, last];

        var result = await Provider(transport).StartVideoOperation(request);

        var sent = Assert.Single(transport.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/v1beta/interactions", sent.Path);
        Assert.Equal(Model, sent.Body.GetProperty("model").GetString());
        var input = sent.Body.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(3, input.Length);
        AssertImage(input[0], "first");
        AssertImage(input[1], "last");
        var prompt = input[2].GetProperty("text").GetString()!;
        Assert.StartsWith(Prompt, prompt);
        Assert.Contains("first image as the starting frame", prompt);
        Assert.Contains("second image as the ending frame", prompt);
        Assert.DoesNotContain("<FIRST_FRAME>", prompt);
        Assert.DoesNotContain("<LAST_FRAME>", prompt);
        Assert.Empty(result.Warnings);
        Assert.Equal("v1_omni_video_test", result.Operation);
        Assert.True(sent.Body.GetProperty("background").GetBoolean());
        Assert.True(sent.Body.GetProperty("store").GetBoolean());
        Assert.False(sent.Body.GetProperty("stream").GetBoolean());
        Assert.False(sent.Body.TryGetProperty("generation_config", out _));
    }

    [Fact]
    public async Task Normalizes_both_data_url_frames_and_preserves_all_reference_images_after_them()
    {
        using var transport = new RecordingTransport();
        var request = Request();
        var first = Frame("first_frame", "first");
        var last = Frame("last_frame", "last");
        foreach (var frame in new[] { first, last })
        {
            frame.Image.Data = "data:image/jpeg;base64," + frame.Image.Data;
            frame.Image.MediaType = "image/webp";
        }
        request.FrameImages = [last, Frame("reference", "frame-reference"), first];
        request.Image = Image("top-level-reference");
        request.InputReferences = [Image("reference")];

        await Provider(transport).StartVideoOperation(request);

        var input = Assert.Single(transport.Requests).Body.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(6, input.Length);
        AssertImage(input[0], "first", "image/jpeg");
        AssertImage(input[1], "last", "image/jpeg");
        AssertImage(input[2], "frame-reference");
        AssertImage(input[3], "top-level-reference");
        AssertImage(input[4], "reference");
        Assert.Equal("text", input[5].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Top_level_image_can_supply_the_starting_frame_when_only_last_frame_is_explicit()
    {
        using var transport = new RecordingTransport();
        var request = Request();
        request.Image = Image("first");
        request.FrameImages = [Frame("last_frame", "last")];

        await Provider(transport).StartVideoOperation(request);

        var input = Assert.Single(transport.Requests).Body.GetProperty("input");
        Assert.Equal(3, input.GetArrayLength());
        AssertImage(input[0], "first");
        AssertImage(input[1], "last");
        Assert.Contains("ending frame", input[2].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("first_frame", "first")]
    [InlineData("last_frame", "last")]
    public async Task Rejects_duplicate_frame_roles_before_sending(string role, string alias)
    {
        using var transport = new RecordingTransport();
        var request = Request();
        request.FrameImages = [Frame(role, "one"), Frame(alias, "two")];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(transport).StartVideoOperation(request));

        Assert.Contains("only one " + role, error.Message);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Rejects_an_ending_frame_without_a_start_even_when_reference_images_are_present()
    {
        using var transport = new RecordingTransport();
        var request = Request();
        request.FrameImages = [Frame("last_frame", "last")];
        request.InputReferences = [Image("reference")];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(transport).StartVideoOperation(request));

        Assert.Contains("requires a first_frame image or top-level image", error.Message);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(10)]
    public async Task Maps_interpolation_duration_to_native_whole_second_string(double duration)
    {
        using var transport = new RecordingTransport();
        var request = InterpolationRequest();
        request.Duration = duration;

        var result = await Provider(transport).StartVideoOperation(request);

        Assert.Equal(duration.ToString("0", CultureInfo.InvariantCulture) + "s",
            Assert.Single(transport.Requests).Body.GetProperty("response_format").GetProperty("duration").GetString());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Omitting_duration_leaves_the_provider_default_unchanged()
    {
        using var transport = new RecordingTransport();

        await Provider(transport).StartVideoOperation(InterpolationRequest());

        Assert.False(Assert.Single(transport.Requests).Body.GetProperty("response_format").TryGetProperty("duration", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3.5)]
    [InlineData(10.5)]
    [InlineData(11)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Rejects_invalid_or_too_long_interpolation_duration_before_sending(double duration)
    {
        using var transport = new RecordingTransport();
        var request = InterpolationRequest();
        request.Duration = duration;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(transport).StartVideoOperation(request));

        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("response_format", "3s")]
    [InlineData("response_format", "10s")]
    [InlineData("responseFormat", "8s")]
    public async Task Explicit_provider_duration_takes_precedence_and_preserves_other_response_format_fields(string option, string duration)
    {
        using var transport = new RecordingTransport();
        var request = InterpolationRequest();
        request.Duration = 15;
        request.AspectRatio = "16:9";
        request.ProviderOptions = Options($$$"""
            {"{{{option}}}":{"duration":"{{{duration}}}","resolution":"1080p","aspect_ratio":"9:16","delivery":"uri","custom":"preserved"}}
            """);

        var result = await Provider(transport).StartVideoOperation(request);

        var payload = Assert.Single(transport.Requests).Body;
        var format = payload.GetProperty("response_format");
        Assert.Equal(duration, format.GetProperty("duration").GetString());
        Assert.Equal("1080p", format.GetProperty("resolution").GetString());
        Assert.Equal("9:16", format.GetProperty("aspect_ratio").GetString());
        Assert.Equal("uri", format.GetProperty("delivery").GetString());
        Assert.Equal("preserved", format.GetProperty("custom").GetString());
        Assert.False(payload.TryGetProperty("responseFormat", out _));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("\"2s\"")]
    [InlineData("\"3.5s\"")]
    [InlineData("\"11s\"")]
    [InlineData("\"15s\"")]
    [InlineData("\"16s\"")]
    [InlineData("\"invalid\"")]
    [InlineData("\"\"")]
    [InlineData("\"s\"")]
    [InlineData("8")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task Validates_effective_provider_duration_instead_of_the_overridden_common_duration(string durationJson)
    {
        using var transport = new RecordingTransport();
        var request = InterpolationRequest();
        request.Duration = 5;
        request.ProviderOptions = Options($$$"""{"response_format":{"duration":{{{durationJson}}}}}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(transport).StartVideoOperation(request));

        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("text", 3)]
    [InlineData("text", 15)]
    [InlineData("image", 11)]
    [InlineData("image", 15)]
    [InlineData("first_frame", 15)]
    public async Task Non_interpolation_requests_keep_text_or_image_input_and_support_up_to_fifteen_seconds(string mode, double duration)
    {
        using var transport = new RecordingTransport();
        var request = Request();
        request.Duration = duration;
        if (mode == "image") request.Image = Image("first");
        if (mode == "first_frame") request.FrameImages = [Frame("first_frame", "first")];

        var result = await Provider(transport).StartVideoOperation(request);

        var payload = Assert.Single(transport.Requests).Body;
        Assert.Equal(duration.ToString("0", CultureInfo.InvariantCulture) + "s", payload.GetProperty("response_format").GetProperty("duration").GetString());
        var input = payload.GetProperty("input");
        if (mode == "text") Assert.Equal(Prompt, input.GetString());
        else
        {
            Assert.Equal(2, input.GetArrayLength());
            AssertImage(input[0], "first");
            var prompt = input[1].GetProperty("text").GetString()!;
            Assert.StartsWith(Prompt, prompt);
            Assert.DoesNotContain("<FIRST_FRAME>", prompt);
            if (mode == "image") Assert.Equal(Prompt, prompt);
            else Assert.Contains("first image as the starting frame", prompt);
        }
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Veo_requests_keep_native_frame_mapping_duration_and_operation_routing()
    {
        using var transport = new RecordingTransport();
        var request = InterpolationRequest();
        request.Model = "veo-3.1-generate-preview";
        request.Duration = 8;

        var result = await Provider(transport).StartVideoOperation(request);

        var sent = Assert.Single(transport.Requests);
        Assert.Equal("/v1beta/models/veo-3.1-generate-preview:predictLongRunning", sent.Path);
        Assert.Equal(8, sent.Body.GetProperty("parameters").GetProperty("durationSeconds").GetDouble());
        var instance = sent.Body.GetProperty("instances")[0];
        Assert.Equal(Data("first"), instance.GetProperty("image").GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal(Data("last"), instance.GetProperty("lastFrame").GetProperty("data").GetString());
        Assert.StartsWith("veo_", result.Operation);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Omni_polling_still_returns_base64_for_inline_or_remote_video(bool remote)
    {
        using var transport = new RecordingTransport { RemoteVideo = remote };

        var result = Assert.IsType<VideoOperationCompletedResult>(
            await Provider(transport).GetVideoOperationStatus("v1_omni_video_test"));

        var video = Assert.Single(result.Videos);
        Assert.Equal("base64", video.Type);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(Data("video"), video.Data);
        Assert.Equal("/v1beta/interactions/v1_omni_video_test", transport.Requests[0].Path);
        Assert.Equal(remote ? 2 : 1, transport.Requests.Count);
        Assert.Empty(result.Warnings);
    }

    private static VideoRequest Request() => new() { Model = Model, Prompt = Prompt };

    private static VideoRequest InterpolationRequest()
    {
        var request = Request();
        request.FrameImages = [Frame("first_frame", "first"), Frame("last_frame", "last")];
        return request;
    }

    private static string Data(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static VideoFile Image(string value) => new() { Data = Data(value), MediaType = "image/png" };
    private static VideoFrameImage Frame(string role, string value) => new() { FrameType = role, Image = Image(value) };
    private static Dictionary<string, JsonElement> Options(string json) => new() { ["google"] = JsonSerializer.Deserialize<JsonElement>(json) };

    private static void AssertImage(JsonElement content, string value, string mimeType = "image/png")
    {
        Assert.Equal("image", content.GetProperty("type").GetString());
        Assert.Equal(Data(value), content.GetProperty("data").GetString());
        Assert.Equal(mimeType, content.GetProperty("mime_type").GetString());
    }

    private static GoogleAIProvider Provider(RecordingTransport transport)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), NullLogger<GoogleAIProvider>.Instance, transport);

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class RecordingTransport : HttpMessageHandler, IHttpClientFactory
    {
        public List<(HttpMethod Method, string Path, JsonElement Body)> Requests { get; } = [];
        public bool RemoteVideo { get; init; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? default : JsonSerializer.Deserialize<JsonElement>(await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add((request.Method, path, body));
            if (path == "/download")
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("video")) { Headers = { ContentType = new("video/mp4") } } };

            var output = RemoteVideo
                ? "\"uri\":\"https://generativelanguage.googleapis.com/download\""
                : "\"data\":\"" + Data("video") + "\"";
            var response = path.EndsWith(":predictLongRunning", StringComparison.Ordinal)
                ? """{"name":"operations/veo-test"}"""
                : $$"""{"id":"v1_omni_video_test","model":"{{Model}}","status":"completed","steps":[{"type":"model_output","content":[{"type":"video","mime_type":"video/mp4",{{output}}}]}]}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
