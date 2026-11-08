using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Perplexity;

namespace AIHappey.Tests;

public sealed class PerplexitySkillsTests
{
    [Fact]
    public async Task List_skills_follows_page_tokens_and_maps_native_metadata()
    {
        var requests = new List<string>();
        using var client = CreateClient(request =>
        {
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(JsonResponse(requests.Count == 1
                ? """{"skills":[{"skill_id":"skill-1","name":"First","revision":"rev-1","created_at":"2025-01-02T03:04:05Z"}],"next_page_token":"next +/"}"""
                : """{"skills":[{"skill_id":"skill-2","name":"Second","revision":"rev-2","updated_at":"2025-01-03T03:04:05Z"}]}"""));
        });
        var provider = CreateProvider(client);

        var skills = (await provider.ListSkills()).ToArray();

        Assert.Equal(2, skills.Length);
        Assert.Equal("perplexity/skill-1", skills[0].Id);
        Assert.Equal("rev-1", skills[0].LatestVersion);
        Assert.Equal(DateTimeOffset.Parse("2025-01-02T03:04:05Z").ToUnixTimeSeconds(), skills[0].CreatedAt);
        Assert.Equal("perplexity/skill-2", skills[1].Id);
        Assert.Contains("limit=200", requests[0]);
        Assert.Contains("page_token=next%20%2B%2F", requests[1]);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task List_skill_versions_follows_page_tokens_and_escapes_the_native_skill_id()
    {
        var requests = new List<string>();
        using var client = CreateClient(request =>
        {
            Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
            requests.Add(request.RequestUri!.OriginalString);
            return Task.FromResult(JsonResponse(requests.Count == 1
                ? """{"revisions":[{"revision":"rev-1","name":"Skill","created_at":"2025-01-02T03:04:05Z"}],"next_page_token":"rev cursor"}"""
                : """{"revisions":[{"revision":"rev-2","name":"Skill"}]}"""));
        });
        var provider = CreateProvider(client);

        var versions = (await provider.ListSkillVersions("perplexity/skill/id")).ToArray();

        Assert.Equal(2, versions.Length);
        Assert.Equal("perplexity/skill/id", versions[0].SkillId);
        Assert.Equal("rev-1", versions[0].Version);
        Assert.Equal(DateTimeOffset.Parse("2025-01-02T03:04:05Z").ToUnixTimeSeconds(), versions[0].CreatedAt);
        Assert.Contains("/v1/skills/skill%2Fid/revisions?limit=200", requests[0]);
        Assert.Contains("page_token=rev%20cursor", requests[1]);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Skill_content_downloads_active_and_version_bundles_without_forwarding_auth()
    {
        var requests = new List<string>();
        using var client = CreateClient(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");

            if (request.RequestUri.Host == "api.perplexity.ai")
            {
                Assert.Equal("test-api-key", request.Headers.Authorization?.Parameter);
                return Task.FromResult(JsonResponse("""{"url":"https://download.example/bundle.zip?signature=secret"}"""));
            }

            Assert.Equal("download.example", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        });
        var provider = CreateProvider(client);

        await using var activeBundle = await provider.RetrieveSkillContent("perplexity/skill-1");
        await using var versionBundle = await provider.RetrieveSkillVersionContent("perplexity/skill-1", "rev /1");

        Assert.Equal(new byte[] { 1, 2, 3 }, await ReadBytesAsync(activeBundle));
        Assert.Equal(new byte[] { 1, 2, 3 }, await ReadBytesAsync(versionBundle));
        Assert.Contains("GET /v1/skills/skill-1/download", requests);
        Assert.Contains("GET /v1/skills/skill-1/download?revision=rev%20%2F1", requests);
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    private static PerplexityProvider CreateProvider(HttpClient client)
        => new(new TestKeyResolver(), new AsyncCacheHelper(), new TestHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static async Task<byte[]> ReadBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return responder(request);
        }
    }
}
