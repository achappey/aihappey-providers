using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.FrontiereAI;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests;

public sealed class FrontiereAIProviderTests
{
    private const string Native = """{"future":{"integer":9007199254740993,"nested":[true,null,{"a":[]}]},"future_null":null}""";
    private const string Models = """{"data":[{"id":"chat","name":"Chat","type":"language"},{"id":"portrait","type":"image"},{"id":"vector","type":"embedding"}]}""";
    private const string Chat = """{"id":"chat_1","object":"chat.completion","model":"chat","created":1700000000,"choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""";
    private const string Image = """{"created":1700000000,"output_format":"webp","data":[{"b64_json":"aW1hZ2U=","revised_prompt":"Portrait"}],"future":{"integer":9007199254740993},"future_null":null,"usage":{"input_tokens":2,"output_tokens":3,"total_tokens":5}}""";
    private const string Embedding = """{"object":"list","model":"vector","data":[{"object":"embedding","index":0,"embedding":[0.25,0.5]}],"usage":{"prompt_tokens":2,"total_tokens":2}}""";
    private const string Sse = """
        : keepalive

        data: {"id":"chat_1","object":"chat.completion.chunk","model":"chat","created":1700000000,"choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}

        data: {"id":"chat_1","object":"chat.completion.chunk","model":"chat","created":1700000000,"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

        data: [DONE]

        """;

    [Fact]
    public void Registry_resolves_the_correct_identifier()
    {
        var services = new ServiceCollection();
        services.AddProviders();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        Assert.IsType<FrontiereAIProvider>(root.GetRequiredService<ProviderRegistry>()
            .GetModelProvider(scope.ServiceProvider, "frontiereai"));
    }

    [Fact]
    public async Task Discovery_is_public_dynamic_namespaced_deduplicated_and_cached()
    {
        var calls = 0;
        using var client = Client((http, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, http.Method);
            Assert.Equal("https://getfrontiereai.eu/api/v1/models", http.RequestUri!.AbsoluteUri);
            Assert.Null(http.Headers.Authorization);
            return Task.FromResult(Json("""
                {"data":[{"id":"chat","name":"Chat Name","type":"chat","owned_by":"owner","created":123},
                {"id":"chat"},{"id":"portrait","type":"image"},{"id":"vector","type":"embeddings"},
                {"id":"text-embedding-new"},{"id":"flux-new"},{"id":"new-model","created":null},
                {"id":""},{"id":null},{}]}
                """));
        });
        var provider = Provider(client, new Keys(() => throw new InvalidOperationException("Public discovery must not resolve a key.")));
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(6, models.Length);
        Assert.All(models, model => Assert.StartsWith("frontiereai/", model.Id));
        Assert.Equal("Chat Name", models[0].Name);
        Assert.Equal("owner", models[0].OwnedBy);
        Assert.Equal(123, models[0].Created);
        Assert.Equal(["language", "image", "embedding", "embedding", "image", "language"], models.Select(model => model.Type));
        await provider.ListModels();
        Assert.Equal(1, calls);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":{}}""")]
    public async Task Discovery_rejects_malformed_catalogs(string payload)
    {
        using var client = Client((_, _) => Task.FromResult(Json(payload)));
        await Assert.ThrowsAsync<JsonException>(() => Provider(client).ListModels());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_preserves_scoped_raw_metadata_auth_and_caller_input(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            AssertRoute(http, "chat/completions");
            var body = await Body(http, ct);
            AssertNative(body);
            Assert.Equal(streaming, body.GetProperty("stream").GetBoolean());
            Assert.Equal("trace", http.Headers.GetValues("X-Trace").Single());
            return streaming ? EventStream() : Json(Chat);
        });
        var provider = Provider(client);
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>($$"""
            {"model":"chat","messages":[{"role":"user","content":"Hi"}],"providerMetadata":{"frontiereai":{{Native}},"other":{"leak":true} } }
            """)!;
        options.Headers = new() { ["X-Trace"] = "trace", ["Authorization"] = "******" };
        var before = JsonSerializer.Serialize(options);
        if (streaming)
        {
            var updates = await Collect(provider.CompleteChatStreamingAsync(options));
            Assert.Equal(2, updates.Count);
            Assert.All(updates, update => Assert.Equal("frontiereai/chat", update.Model));
        }
        else
            Assert.Equal("frontiereai/chat", (await provider.CompleteChatAsync(options)).Model);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("unified", false)]
    [InlineData("unified", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("vercel", true)]
    public async Task Conversation_adapters_use_chat_not_undocumented_endpoints(string adapter, bool streaming)
    {
        var posts = 0;
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get)
            {
                Assert.Null(http.Headers.Authorization);
                return Json(Models);
            }
            posts++;
            AssertRoute(http, "chat/completions");
            var body = await Body(http, ct);
            AssertNative(body);
            return streaming ? EventStream() : Json(Chat);
        });
        var provider = Provider(client);
        switch (adapter)
        {
            case "unified":
                var unified = Request();
                var before = JsonSerializer.Serialize(unified);
                if (streaming) Assert.NotEmpty(await Collect(provider.StreamUnifiedAsync(unified)));
                else Assert.NotNull((await provider.ExecuteUnifiedAsync(unified)).Output);
                Assert.Equal(before, JsonSerializer.Serialize(unified));
                break;
            case "responses":
                var responses = JsonSerializer.Deserialize<ResponseRequest>($$"""
                    {"model":"chat","input":"Hi",{{Native[1..^1]}}}
                    """)!;
                if (streaming) Assert.NotEmpty(await Collect(provider.ResponsesStreamingAsync(responses)));
                else Assert.NotNull(await provider.ResponsesAsync(responses));
                break;
            case "messages":
                var messages = JsonSerializer.Deserialize<MessagesRequest>($$"""
                    {"model":"chat","max_tokens":20,"messages":[{"role":"user","content":"Hi"}],{{Native[1..^1]}}}
                    """)!;
                if (streaming) Assert.NotEmpty(await Collect(provider.MessagesStreamingAsync(messages, [])));
                else Assert.NotNull(await provider.MessagesAsync(messages, []));
                break;
            default:
                var vercel = JsonSerializer.Deserialize<ChatRequest>($$"""
                    {"model":"chat","messages":[{"id":"u","role":"user","parts":[{"type":"text","text":"Hi"}]}],
                     "providerMetadata":{"frontiereai":{{Native}},"other":{"leak":true} } }
                    """)!;
                Assert.NotEmpty(await Collect(provider.StreamAsync(vercel)));
                break;
        }
        Assert.Equal(1, posts);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Embeddings_use_native_transport_and_shared_adapter(bool generic)
    {
        using var client = Client(async (http, ct) =>
        {
            AssertRoute(http, "embeddings");
            var body = await Body(http, ct);
            Assert.Equal("vector", body.GetProperty("model").GetString());
            Assert.Equal(2, body.GetProperty("dimensions").GetInt32());
            Assert.Equal("Hi", body.GetProperty("input")[0].GetString());
            AssertNative(body);
            return Json(Embedding);
        });
        var provider = Provider(client);
        if (generic)
        {
            var response = await provider.EmbeddingRequestAsync(new()
            {
                Model = "vector", Values = ["Hi"],
                ProviderOptions = new() { ["frontiereai"] = Element($$"""{"dimensions":2,{{Native[1..^1]}}}"""), ["other"] = Element("""{"leak":true}""") }
            });
            Assert.Single(response.Embeddings);
        }
        else
        {
            var response = await provider.OpenAIEmbeddingRequestAsync(new()
            {
                Model = "vector", Input = Element("""["Hi"]"""), Dimensions = 2,
                AdditionalProperties = new() { ["providerMetadata"] = Element($$"""{"frontiereai":{{Native}},"other":{"leak":true} }""") }
            });
            Assert.Equal("frontiereai/vector", response.Model);
            Assert.Single(response.Data);
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Native_images_preserve_metadata_and_adapt_synchronous_outputs(bool edit, bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            AssertRoute(http, edit ? "images/edits" : "images/generations");
            var body = await Body(http, ct);
            AssertNative(body);
            Assert.False(body.GetProperty("stream").GetBoolean());
            Assert.False(body.TryGetProperty("partial_images", out _));
            if (edit) Assert.Equal("data:image/png;base64,aW1hZ2U=", body.GetProperty("images")[0].GetProperty("image_url").GetString());
            return Json(Image);
        });
        var provider = Provider(client);
        var extra = new Dictionary<string, JsonElement>
        {
            ["providerMetadata"] = Element($$"""{"frontiereai":{{Native}},"other":{"leak":true} }""")
        };
        if (edit)
        {
            var request = new OpenAIImageEditRequest
            {
                Model = "portrait", Prompt = "Portrait", Stream = true, PartialImages = 3, AdditionalProperties = extra,
                Images = [new() { ImageUrl = "data:image/png;base64,aW1hZ2U=" }]
            };
            var before = JsonSerializer.Serialize(request);
            if (streaming)
                Assert.Equal("aW1hZ2U=", Assert.IsType<OpenAIImageEditCompleted>(Assert.Single(await Collect(provider.OpenAIImageEditStreamingAsync(request)))).B64Json);
            else
                Assert.Equal("aW1hZ2U=", Assert.Single((await provider.OpenAIImageEditRequestAsync(request)).Data!).B64Json);
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        else
        {
            var request = new OpenAIImageGenerationRequest { Model = "portrait", Prompt = "Portrait", Stream = true, PartialImages = 3, AdditionalProperties = extra };
            var before = JsonSerializer.Serialize(request);
            if (streaming)
                Assert.Equal("aW1hZ2U=", Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(await Collect(provider.OpenAIImageGenerationStreamingAsync(request)))).B64Json);
            else
            {
                var response = await provider.OpenAIImageGenerationRequestAsync(request);
                Assert.Equal("Portrait", Assert.Single(response.Data!).RevisedPrompt);
                Assert.Equal(9007199254740993L, response.AdditionalProperties!["future"].GetProperty("integer").GetInt64());
            }
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Multipart_edit_forwards_reference_photos_mask_and_raw_fields()
    {
        using var fileStream = new MemoryStream(Encoding.UTF8.GetBytes("photo"));
        var photo = new FormFile(fileStream, 0, fileStream.Length, "image", "face.png")
        { Headers = new HeaderDictionary(), ContentType = "image/png" };
        using var client = Client(async (http, ct) =>
        {
            AssertRoute(http, "images/edits");
            Assert.StartsWith("multipart/form-data", http.Content!.Headers.ContentType!.ToString());
            var multipart = Assert.IsType<MultipartFormDataContent>(http.Content);
            var parts = multipart.ToArray();
            Assert.Equal(2, parts.Count(part => part.Headers.ContentType?.MediaType == "image/png"));
            Assert.Contains(parts, part => part.Headers.ContentDisposition!.FileName == "face.png");
            var future = parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "future");
            Assert.True(JsonElement.DeepEquals(Element(Native).GetProperty("future"), Element(await future.ReadAsStringAsync(ct))));
            Assert.Equal("null", await parts.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "future_null").ReadAsStringAsync(ct));
            return Json(Image);
        });
        var request = new OpenAIImageEditRequest
        {
            Model = "portrait", Prompt = "Portrait", ImageFiles = [photo], MaskFile = photo,
            AdditionalProperties = Element(Native).EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())
        };
        await Provider(client).OpenAIImageEditRequestAsync(request);
        Assert.Same(photo, request.ImageFiles[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_images_route_references_and_preserve_options_outputs_and_usage(bool edit)
    {
        using var client = Client(async (http, ct) =>
        {
            AssertRoute(http, edit ? "images/edits" : "images/generations");
            AssertNative(await Body(http, ct));
            return Json(Image);
        });
        var request = new ImageRequest
        {
            Model = "portrait", Prompt = "Portrait", Size = "1024x1024", N = 1, Seed = 1, AspectRatio = "1:1",
            Files = edit ? [new() { Data = "aW1hZ2U=", MediaType = "image/png" }] : null,
            ProviderOptions = new() { ["frontiereai"] = Element(Native), ["other"] = Element("""{"leak":true}""") }
        };
        var before = JsonSerializer.Serialize(request);
        var response = await Provider(client).ImageRequest(request);
        Assert.Equal("data:image/webp;base64,aW1hZ2U=", Assert.Single(response.Images!));
        Assert.Equal("frontiereai/portrait", response.Response.ModelId);
        Assert.Equal(5, response.Usage!.TotalTokens);
        Assert.Equal(2, response.Warnings.Count());
        Assert.Equal(9007199254740993L, response.ProviderMetadata!["frontiereai"].GetProperty("future").GetProperty("integer").GetInt64());
        Assert.Equal(JsonValueKind.Null, response.ProviderMetadata["frontiereai"].GetProperty("future_null").ValueKind);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Unified_images_dispatch_from_public_catalog_and_isolate_metadata(bool edit, bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get) return Json(Models);
            AssertRoute(http, edit ? "images/edits" : "images/generations");
            AssertNative(await Body(http, ct));
            return Json(Image);
        });
        var request = Request("portrait", edit);
        var before = JsonSerializer.Serialize(request);
        var provider = Provider(client);
        if (streaming) Assert.NotEmpty(await Collect(provider.StreamUnifiedAsync(request)));
        else Assert.NotNull((await provider.ExecuteUnifiedAsync(request)).Output);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Url_image_outputs_are_retained_natively_and_downloaded_without_credentials_for_adapters(bool streaming)
    {
        using var client = Client((http, _) =>
        {
            if (http.Method == HttpMethod.Get)
            {
                Assert.Equal("https://images.example/output.png", http.RequestUri!.AbsoluteUri);
                Assert.Null(http.Headers.Authorization);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("image")) });
            }
            AssertRoute(http, "images/generations");
            return Task.FromResult(Json("""{"created":1700000000,"data":[{"url":"https://images.example/output.png"}]}"""));
        });
        var original = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "factory-key");
        client.DefaultRequestHeaders.Authorization = original;
        var provider = Provider(client);
        if (streaming)
            Assert.Equal("aW1hZ2U=", Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(await Collect(provider.OpenAIImageGenerationStreamingAsync(
                new() { Model = "portrait", Prompt = "Portrait" })))).B64Json);
        else
            Assert.Equal("https://images.example/output.png", Assert.Single((await provider.OpenAIImageGenerationRequestAsync(
                new() { Model = "portrait", Prompt = "Portrait" })).Data!).Url);
        Assert.Same(original, client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("chat-stream")]
    [InlineData("embedding")]
    [InlineData("image")]
    [InlineData("image-stream")]
    [InlineData("edit")]
    [InlineData("edit-stream")]
    public async Task Authenticated_endpoints_fail_without_a_key_before_network_io(string operation)
    {
        using var client = Client((_, _) => throw new InvalidOperationException("Unexpected network call"));
        var provider = Provider(client, new Keys(() => null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, operation));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("chat-stream")]
    [InlineData("embedding")]
    [InlineData("image")]
    [InlineData("image-stream")]
    [InlineData("edit")]
    [InlineData("edit-stream")]
    [InlineData("models")]
    public async Task Upstream_errors_are_not_swallowed(string operation)
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("""{"error":{"message":"frontiere failure"}}""") }));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(Provider(client), operation));
        Assert.Contains("frontiere failure", error.Message);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("chat-stream")]
    [InlineData("embedding")]
    [InlineData("image")]
    [InlineData("image-stream")]
    [InlineData("edit")]
    [InlineData("edit-stream")]
    [InlineData("models")]
    public async Task Cancellation_reaches_the_transport(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        using var client = Client(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(Provider(client), operation, cancellation.Token));
    }

    [Fact]
    public async Task Request_keys_do_not_leak_into_later_discovery_or_change_client_defaults()
    {
        string? key = "first";
        var observed = new List<string?>();
        using var client = Client((http, _) =>
        {
            observed.Add(http.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(http.Method == HttpMethod.Get ? Models : Chat));
        });
        var provider = Provider(client, new Keys(() => key));
        await provider.CompleteChatAsync(new() { Model = "chat" });
        key = "second";
        await provider.CompleteChatAsync(new() { Model = "chat" });
        key = null;
        await provider.ListModels();
        Assert.Equal(["first", "second", null], observed);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Factory_default_credentials_are_not_mutated_or_sent_to_public_routes()
    {
        using var client = Client((http, _) =>
        {
            Assert.Null(http.Headers.Authorization);
            return Task.FromResult(Json(Models));
        });
        var original = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "factory-key");
        client.DefaultRequestHeaders.Authorization = original;
        var provider = Provider(client, new Keys(() => null));
        await provider.ListModels();
        Assert.Same(original, client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Concurrent_requests_keep_their_own_keys_and_preserve_factory_defaults()
    {
        string? key = "first";
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(async (http, ct) =>
        {
            var actual = http.Headers.Authorization!.Parameter;
            if (actual == "first")
            {
                arrived.SetResult();
                await release.Task.WaitAsync(ct);
                Assert.Equal("first", http.Headers.Authorization.Parameter);
            }
            else
            {
                Assert.Equal("second", actual);
                release.SetResult();
            }
            return Json(Chat);
        });
        var original = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "factory-key");
        client.DefaultRequestHeaders.Authorization = original;
        var provider = Provider(client, new Keys(() => key));
        var first = provider.CompleteChatAsync(new() { Model = "chat" });
        await arrived.Task;
        key = "second";
        await Task.WhenAll(first, provider.CompleteChatAsync(new() { Model = "chat" }));
        Assert.Same(original, client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Image_validation_and_empty_outputs_fail_explicitly()
    {
        using var client = Client((_, _) => Task.FromResult(Json("""{"data":[]}""")));
        var provider = Provider(client);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIImageGenerationRequestAsync(new() { Prompt = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIImageEditRequestAsync(new() { Prompt = "Portrait" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new() { Model = "portrait", Prompt = "Portrait" }));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("chat-stream")]
    [InlineData("embedding")]
    [InlineData("image")]
    [InlineData("image-stream")]
    [InlineData("edit")]
    [InlineData("edit-stream")]
    public async Task Direct_calls_strip_only_the_provider_model_prefix(string operation)
    {
        using var client = Client(async (http, ct) =>
        {
            var body = await Body(http, ct);
            Assert.Equal("vendor/model", body.GetProperty("model").GetString());
            return operation.StartsWith("chat")
                ? operation.EndsWith("stream") ? EventStream() : Json(Chat)
                : Json(operation == "embedding" ? Embedding : Image);
        });
        await Invoke(Provider(client), operation, model: "frontiereai/vendor/model");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_chat_scoped_standard_fields_are_forwarded_without_mutating_headers(bool streaming)
    {
        using var client = Client(async (http, ct) =>
        {
            AssertRoute(http, "chat/completions");
            var body = await Body(http, ct);
            Assert.Equal("vendor/chat", body.GetProperty("model").GetString());
            Assert.Equal(0.25, body.GetProperty("temperature").GetDouble());
            AssertNative(body);
            Assert.Equal("trace", http.Headers.GetValues("X-Trace").Single());
            return streaming ? EventStream() : Json(Chat);
        });
        var options = new ChatCompletionOptions
        {
            Model = "fallback",
            Headers = new() { ["X-Trace"] = "trace" },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = Element($$"""
                    {"frontiereai":{"model":"frontiereai/vendor/chat","temperature":0.25,{{Native[1..^1]}}},"other":{"temperature":0.99} }
                    """)
            }
        };
        var before = JsonSerializer.Serialize(options);
        var originalHeaders = options.Headers;
        var provider = Provider(client);
        if (streaming) await Collect(provider.CompleteChatStreamingAsync(options));
        else await provider.CompleteChatAsync(options);
        Assert.Equal(before, JsonSerializer.Serialize(options));
        Assert.Same(originalHeaders, options.Headers);
    }

    [Fact]
    public async Task Generic_embedding_strips_namespaced_models_without_losing_native_options()
    {
        using var client = Client(async (http, ct) =>
        {
            AssertRoute(http, "embeddings");
            var body = await Body(http, ct);
            Assert.Equal("vendor/vector", body.GetProperty("model").GetString());
            Assert.Equal(2, body.GetProperty("dimensions").GetInt32());
            AssertNative(body);
            return Json(Embedding);
        });
        var request = new EmbeddingRequest
        {
            Model = "frontiereai/vendor/vector", Values = ["Hi"],
            ProviderOptions = new()
            {
                ["frontiereai"] = Element($$"""{"dimensions":2,{{Native[1..^1]}}}"""),
                ["other"] = Element("""{"leak":true}""")
            }
        };
        var before = JsonSerializer.Serialize(request);
        await Provider(client).EmbeddingRequestAsync(request);
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_image_scoped_standard_fields_and_namespaced_overrides_are_forwarded(bool edit)
    {
        using var client = Client(async (http, ct) =>
        {
            AssertRoute(http, edit ? "images/edits" : "images/generations");
            var body = await Body(http, ct);
            Assert.Equal("vendor/portrait", body.GetProperty("model").GetString());
            Assert.Equal("512x512", body.GetProperty("size").GetString());
            Assert.Equal("webp", body.GetProperty("output_format").GetString());
            Assert.Equal(2, body.GetProperty("n").GetInt32());
            AssertNative(body);
            return Json(Image);
        });
        var metadata = new Dictionary<string, JsonElement>
        {
            ["providerMetadata"] = Element($$"""
                {"frontiereai":{"model":"frontiereai/vendor/portrait","size":"512x512","n":2,"output_format":"webp",{{Native[1..^1]}}},
                "other":{"size":"bad","leak":true} }
                """)
        };
        var provider = Provider(client);
        if (edit)
        {
            var options = new OpenAIImageEditRequest
            {
                Model = "fallback", Prompt = "Portrait", AdditionalProperties = metadata,
                Images = [new() { ImageUrl = "data:image/png;base64,aW1hZ2U=" }]
            };
            var before = JsonSerializer.Serialize(options);
            await provider.OpenAIImageEditRequestAsync(options);
            Assert.Equal(before, JsonSerializer.Serialize(options));
        }
        else
        {
            var options = new OpenAIImageGenerationRequest { Model = "fallback", Prompt = "Portrait", AdditionalProperties = metadata };
            var before = JsonSerializer.Serialize(options);
            await provider.OpenAIImageGenerationRequestAsync(options);
            Assert.Equal(before, JsonSerializer.Serialize(options));
        }
    }

    [Theory]
    [InlineData(false, false, "jpeg")]
    [InlineData(false, true, "jpeg")]
    [InlineData(true, false, "jpeg")]
    [InlineData(true, true, "jpeg")]
    [InlineData(false, false, "webp")]
    [InlineData(false, true, "webp")]
    [InlineData(true, false, "webp")]
    [InlineData(true, true, "webp")]
    public async Task Unified_image_formats_override_adapter_defaults_for_generation_and_edit(bool edit, bool streaming, string format)
    {
        using var client = Client(async (http, ct) =>
        {
            if (http.Method == HttpMethod.Get) return Json(Models);
            AssertRoute(http, edit ? "images/edits" : "images/generations");
            var body = await Body(http, ct);
            AssertNative(body);
            Assert.Equal(format, body.GetProperty("output_format").GetString());
            Assert.False(body.TryGetProperty("partial_images", out _));
            return Json("""{"created":1700000000,"data":[{"b64_json":"aW1hZ2U="}]}""");
        });
        var request = Request("portrait", edit);
        var native = Element(Native);
        request.Metadata!["frontiereai"] = new
        {
            output_format = format,
            partial_images = 1,
            future = native.GetProperty("future"),
            future_null = (object?)null
        };
        var before = JsonSerializer.Serialize(request);
        var provider = Provider(client);
        object? value;
        if (streaming)
        {
            var events = await Collect(provider.StreamUnifiedAsync(request));
            var output = Assert.Single(events.Select(evt => evt.Event.Data)
                .OfType<AIToolOutputAvailableEventData>(), output => output.Preliminary != true);
            Assert.True(output.ProviderExecuted);
            Assert.False(output.Preliminary);
            value = output.Output;
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(response.Output!.Items!).Content!));
            Assert.True(tool.ProviderExecuted);
            value = tool.Output;
        }
        var result = Assert.IsType<CallToolResult>(value);
        Assert.False(result.IsError);
        var image = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content));
        Assert.Equal("image/" + format, image.MimeType);
        Assert.Equal(Encoding.UTF8.GetBytes("image"), image.DecodedData.ToArray());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    private static async Task Invoke(FrontiereAIProvider provider, string operation, CancellationToken ct = default, string? model = null)
    {
        switch (operation)
        {
            case "chat": await provider.CompleteChatAsync(new() { Model = model ?? "chat" }, ct); break;
            case "chat-stream": await Collect(provider.CompleteChatStreamingAsync(new() { Model = model ?? "chat" }, ct)); break;
            case "embedding": await provider.OpenAIEmbeddingRequestAsync(new() { Model = model ?? "vector", Input = Element("""["Hi"]""") }, ct); break;
            case "image": await provider.OpenAIImageGenerationRequestAsync(new() { Model = model ?? "portrait", Prompt = "Portrait" }, ct); break;
            case "image-stream": await Collect(provider.OpenAIImageGenerationStreamingAsync(new() { Model = model ?? "portrait", Prompt = "Portrait" }, ct)); break;
            case "edit": await provider.OpenAIImageEditRequestAsync(new() { Model = model ?? "portrait", Prompt = "Portrait", Images = [new() { ImageUrl = "data:image/png;base64,aW1hZ2U=" }] }, ct); break;
            case "edit-stream": await Collect(provider.OpenAIImageEditStreamingAsync(new() { Model = model ?? "portrait", Prompt = "Portrait", Images = [new() { ImageUrl = "data:image/png;base64,aW1hZ2U=" }] }, ct)); break;
            default: await provider.ListModels(ct); break;
        }
    }

    private static AIRequest Request(string model = "chat", bool image = false) => new()
    {
        ProviderId = "frontiereai", Model = model,
        Metadata = new() { ["frontiereai"] = Element(Native), ["other"] = Element("""{"leak":true,"output_format":"jpeg"}""") },
        Input = new()
        {
            Items = [new() { Role = "user", Content = image
                ? [new AITextContentPart { Type = "text", Text = "Portrait" }, new AIFileContentPart { Type = "file", MediaType = "image/png", Data = "aW1hZ2U=" }]
                : [new AITextContentPart { Type = "text", Text = "Hi" }] }]
        }
    };

    private static void AssertNative(JsonElement body)
    {
        foreach (var property in Element(Native).EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, body.GetProperty(property.Name)), property.Name);
        Assert.False(body.TryGetProperty("providerMetadata", out _));
        Assert.False(body.TryGetProperty("other", out _));
        Assert.False(body.TryGetProperty("leak", out _));
    }

    private static void AssertRoute(HttpRequestMessage http, string route)
    {
        Assert.Equal(HttpMethod.Post, http.Method);
        Assert.Equal("https://getfrontiereai.eu/api/v1/" + route, http.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", http.Headers.Authorization?.Scheme);
        Assert.Equal("sk-front-test", http.Headers.Authorization?.Parameter);
    }

    private static async Task<JsonElement> Body(HttpRequestMessage http, CancellationToken ct)
        => Element(await http.Content!.ReadAsStringAsync(ct));
    private static JsonElement Element(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static FrontiereAIProvider Provider(HttpClient client, Keys? keys = null)
        => new(keys ?? new Keys(), new Factory(client), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => new(new Handler(send));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage EventStream() => new(HttpStatusCode.OK) { Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream") };
    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> stream)
    {
        List<T> result = [];
        await foreach (var item in stream) result.Add(item);
        return result;
    }
    private sealed class Keys(Func<string?>? resolve = null) : IApiKeyResolver
    {
        public string? Resolve(string provider) => resolve is null ? "sk-front-test" : resolve();
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
