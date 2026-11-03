using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Cursor;
using AIHappey.Core.Providers.OpenAI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AIHappey.Tests;

public sealed class ExtractionBoundaryTests
{
    [Fact]
    public void Registration_and_provider_contract_match_pre_extraction_baseline()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "baseline-manifest.json")));
        var services = new ServiceCollection();
        services.AddSingleton<IApiKeyResolver>(new Keys());
        services.AddProviders();
        using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var registry = root.GetRequiredService<ProviderRegistry>();
        Assert.Equal(baseline.RootElement.GetProperty("ModelProviderIds").EnumerateArray().Select(x => x.GetString()), registry.ModelProviderIds);
        Assert.Equal(baseline.RootElement.GetProperty("SkillProviderIds").EnumerateArray().Select(x => x.GetString()), registry.SkillProviderIds);
        Assert.Equal(baseline.RootElement.GetProperty("Methods").EnumerateArray().Select(x => x.GetString()),
            typeof(IModelProvider).GetMethods().Select(x => x.ToString()).Order());
        var registrations = services.Where(d => d.ServiceType == typeof(IModelProvider) || d.ServiceType == typeof(ISkillProvider))
            .Select(d => new { Contract = d.ServiceType.FullName, Key = d.IsKeyedService ? d.ServiceKey?.ToString() : null, Lifetime = d.Lifetime.ToString() });
        Assert.True(JsonElement.DeepEquals(baseline.RootElement.GetProperty("Registrations"), JsonSerializer.SerializeToElement(registrations)));
        using var scope = root.CreateScope();
        var openai = scope.ServiceProvider.GetRequiredService<OpenAIProvider>();
        Assert.Same(openai, registry.GetModelProvider(scope.ServiceProvider, "OPENAI"));
        Assert.Same(openai, registry.GetSkillProvider(scope.ServiceProvider, "openai"));
        Assert.Same(openai, scope.ServiceProvider.GetRequiredKeyedService<IModelProvider>("openai"));
    }

    [Fact]
    public void Package_has_no_gateway_assembly_or_default_web_storage_cache_worker_dependency()
    {
        var assembly = typeof(IModelProvider).Assembly;
        Assert.Equal("AIHappey.Providers", assembly.GetName().Name);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a =>
            a.Name is "AIHappey.Core" or "AIHappey.Common" or "AIHappey.Telemetry" || a.Name!.StartsWith("AIHappey.Gateway"));
        var services = new ServiceCollection();
        services.AddProviders();
        Assert.DoesNotContain(services, d => d.ServiceType.FullName is
            "Microsoft.AspNetCore.Http.IHttpContextAccessor" or "Microsoft.Extensions.Caching.Memory.IMemoryCache"
            or "Microsoft.Extensions.Hosting.IHostedService");
        using var root = services.BuildServiceProvider();
        Assert.False(root.GetRequiredService<IProviderDebugEmitter>().Enabled);
        Assert.All(assembly.GetTypes().Where(t => typeof(IModelProvider).IsAssignableFrom(t)), type =>
            Assert.DoesNotContain(type.GetConstructors().SelectMany(c => c.GetParameters()),
                p => p.ParameterType.FullName == "Microsoft.AspNetCore.Http.IHttpContextAccessor"));
    }

    [Fact]
    public async Task Cache_is_disabled_unless_the_host_supplies_one()
    {
        var calls = 0;
        var cache = new AsyncCacheHelper();
        Task<int> Factory(CancellationToken _) => Task.FromResult(++calls);
        Assert.Equal(1, await cache.GetOrCreateAsync("same", Factory, TimeSpan.FromMinutes(1)));
        Assert.Equal(2, await cache.GetOrCreateAsync("same", Factory, TimeSpan.FromMinutes(1)));
        cache.Set("same", 42, TimeSpan.FromMinutes(1));
        Assert.False(cache.TryGetValue<int>("same", out _));
    }

    [Fact]
    public async Task Enabled_model_cache_returns_copies_and_partitions_catalog_inputs()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AsyncCacheHelper(memory);
        var calls = 0;
        Task<IEnumerable<Model>> Factory(CancellationToken _) { calls++; return Task.FromResult<IEnumerable<Model>>([new() { Id = "p/model", Name = "original" }]); }
        using (ProviderCatalogScope.Begin(new TestCatalogSource("p", "{}", partition: "catalog-a")))
        {
            var first = Assert.Single(await cache.GetOrCreateAsync("same", Factory, TimeSpan.FromMinutes(1)));
            first.Name = "mutated";
            Assert.Equal("original", Assert.Single(await cache.GetOrCreateAsync("same", Factory, TimeSpan.FromMinutes(1))).Name);
            Assert.Equal(1, calls);
        }
        using (ProviderCatalogScope.Begin(new TestCatalogSource("p", "{}", partition: "catalog-b")))
        {
            await cache.GetOrCreateAsync("same", Factory, TimeSpan.FromMinutes(1));
            Assert.Equal(2, calls);
        }
    }

    [Fact]
    public async Task Read_only_catalog_inputs_do_not_leak_between_concurrent_or_nested_operations()
    {
        async Task<string> Read(string id)
        {
            using var scope = ProviderCatalogScope.Begin(new TestCatalogSource("p", $$"""{"data":[{"id":"p/{{id}}"}]}"""));
            await Task.Yield();
            Assert.Equal("p/inner", ReadInner());
            return Assert.Single("p".GetModels()).Id;
        }
        static string ReadInner()
        {
            using var inner = ProviderCatalogScope.Begin(new TestCatalogSource("p", """{"data":[{"id":"p/inner"}]}"""));
            return Assert.Single("p".GetModels()).Id;
        }
        Assert.Equal(["p/a", "p/b"], await Task.WhenAll(Read("a"), Read("b")));
        using var empty = ProviderCatalogScope.Begin(EmptyProviderCatalogSource.Instance);
        Assert.Empty("p".GetModels());
        Assert.Empty("p".GetPricing()!);
    }

    [Fact]
    public async Task Direct_client_freezes_credentials_and_never_shares_provider_instances_between_operations()
    {
        var factory = new Factory(async (request, token) =>
        {
            await Task.Delay(5, token);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(body.RootElement.GetProperty("model").GetString(), request.Headers.Authorization?.Parameter);
            return Json("""{"id":"c","model":"m","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });
        var client = new ProviderClient(factory);
        var firstKeys = new Dictionary<string, string> { ["inceptron"] = "a" };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IModelProvider? firstProvider = null;
        var first = client.ExecuteAsync("inceptron", firstKeys, async (provider, token) =>
        {
            firstProvider = provider;
            await gate.Task.WaitAsync(token);
            return await provider.CompleteChatAsync(Request("a"), token);
        });
        firstKeys["inceptron"] = "changed-after-call";
        var second = client.ExecuteAsync("inceptron", new Dictionary<string, string> { ["inceptron"] = "b" }, async (provider, token) =>
        {
            Assert.NotSame(firstProvider, provider);
            gate.SetResult();
            return await provider.CompleteChatAsync(Request("b"), token);
        });
        await Task.WhenAll(first, second);
        Assert.Equal(2, factory.Created);
        Assert.Equal(2, factory.Disposed);
    }

    [Fact]
    public async Task Direct_client_disposes_operation_clients_when_the_provider_fails()
    {
        var factory = new Factory((_, _) => throw new HttpRequestException("test failure"));
        var client = new ProviderClient(factory);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ExecuteAsync("inceptron",
            new Dictionary<string, string> { ["inceptron"] = "key" }, (p, ct) => p.CompleteChatAsync(Request("m"), ct)));
        Assert.Equal(1, factory.Created);
        Assert.Equal(1, factory.Disposed);
    }

    [Fact]
    public async Task Suno_requires_explicit_callback_configuration_without_a_web_context()
    {
        var factory = new Factory((_, _) => throw new InvalidOperationException("Unexpected network request."));
        var client = new ProviderClient(factory);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExecuteAsync("sunoapi",
            new Dictionary<string, string> { ["sunoapi"] = "key" },
            (p, ct) => p.SpeechRequest(new AIHappey.Vercel.Models.SpeechRequest { Model = "V4_5ALL", Text = "hello" }, ct)));
        Assert.Contains("callback URL", error.Message);
        Assert.Equal(factory.Created, factory.Disposed);
    }

    [Fact]
    public async Task Cursor_restricted_endpoint_requires_explicit_host_policy_and_never_sends_without_it()
    {
        var sent = 0;
        using var http = new HttpClient(new Handler((_, _) => { sent++; return Task.FromResult(Json("{}")); }));
        var client = new CursorApiClient(http, "secret");
        Assert.Throws<InvalidOperationException>(() => { _ = client.ListRepositoriesAsync(); });
        Assert.Equal(0, sent);
        var policy = new Limiter();
        await new CursorApiClient(http, "secret", policy).ListRepositoriesAsync();
        Assert.Equal(1, sent);
        Assert.Equal(64, policy.Partition!.Length);
        Assert.DoesNotContain("secret", policy.Partition);
    }

    [Fact]
    public async Task Catalog_dependent_adapter_preserves_discovery_and_rejects_unlisted_models()
    {
        var source = new TestCatalogSource("replicate", """{"data":[{"id":"replicate/owner/model","type":"image"}]}""");
        var client = new ProviderClient(new Factory((_, _) => throw new InvalidOperationException("Unexpected network request.")));
        var credentials = new Dictionary<string, string> { ["replicate"] = "key" };
        var models = await client.ExecuteAsync("replicate", credentials, (p, ct) => p.ListModels(ct), catalog: source);
        Assert.Equal("replicate/owner/model", Assert.Single(models).Id);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.ExecuteAsync("replicate", credentials,
            (p, ct) => p.ImageRequest(new AIHappey.Vercel.Models.ImageRequest { Model = "not-listed", Prompt = "test" }, ct), catalog: source));
    }

    private static AIHappey.ChatCompletions.Models.ChatCompletionOptions Request(string model)
        => JsonSerializer.Deserialize<AIHappey.ChatCompletions.Models.ChatCompletionOptions>(
            $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hello"}]}""", JsonSerializerOptions.Web)!;
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test"; }
    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        public int Created;
        public int Disposed;
        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref Created);
            return new HttpClient(new Handler(send, () => Interlocked.Increment(ref Disposed)));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, Action? disposed = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) disposed?.Invoke(); base.Dispose(disposing); }
    }
    private sealed class Limiter : ICursorRepositoryRateLimiter
    {
        public string? Partition;
        public void CheckAndRecord(string credentialPartition) => Partition = credentialPartition;
    }
}
