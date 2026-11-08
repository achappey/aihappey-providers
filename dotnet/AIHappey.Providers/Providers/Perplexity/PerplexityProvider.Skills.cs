using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Skills;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    private const int SkillsPageLimit = 200;

    public async Task<IEnumerable<Skill>> ListSkills(CancellationToken cancellationToken = default)
    {
        var skills = new List<Skill>();
        string? pageToken = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            var uri = BuildSkillsPageUri("v1/skills", pageToken);
            var page = await GetSkillsPageAsync<SkillListPage>(uri, cancellationToken);

            if (page.Skills is not null)
                skills.AddRange(page.Skills.Select(MapSkill));

            pageToken = GetNextPageToken(page.NextPageToken, seenTokens);
        }
        while (pageToken is not null);

        return [.. skills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Id))
            .GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    public async Task<IEnumerable<SkillVersion>> ListSkillVersions(
        string skillId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripSkillProviderPrefix(skillId);
        var versions = new List<SkillVersion>();
        string? pageToken = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var baseUri = $"v1/skills/{Uri.EscapeDataString(upstreamSkillId)}/revisions";

        do
        {
            var uri = BuildSkillsPageUri(baseUri, pageToken);
            var page = await GetSkillsPageAsync<SkillRevisionListPage>(uri, cancellationToken);

            if (page.Revisions is not null)
                versions.AddRange(page.Revisions.Select(revision => MapSkillVersion(skillId, revision)));

            pageToken = GetNextPageToken(page.NextPageToken, seenTokens);
        }
        while (pageToken is not null);

        return [.. versions
            .Where(version => !string.IsNullOrWhiteSpace(version.Version))
            .GroupBy(version => version.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    public Task<Stream> RetrieveSkillContent(
        string skillId,
        CancellationToken cancellationToken = default)
        => DownloadSkillContentAsync(skillId, revision: null, cancellationToken);

    public Task<Stream> RetrieveSkillVersionContent(
        string skillId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return DownloadSkillContentAsync(skillId, version, cancellationToken);
    }

    private async Task<TPage> GetSkillsPageAsync<TPage>(string uri, CancellationToken cancellationToken)
        where TPage : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetSkillApiKey());

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TPage>(cancellationToken: cancellationToken)
            ?? throw new JsonException("Perplexity returned an empty skills response.");
    }

    private async Task<Stream> DownloadSkillContentAsync(
        string skillId,
        string? revision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);

        var upstreamSkillId = StripSkillProviderPrefix(skillId);
        var uri = $"v1/skills/{Uri.EscapeDataString(upstreamSkillId)}/download";
        if (!string.IsNullOrWhiteSpace(revision))
            uri += $"?revision={Uri.EscapeDataString(revision)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetSkillApiKey());

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        var downloadUrl = ReadDownloadUrl(document.RootElement);
        var downloadUri = Uri.TryCreate(downloadUrl, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri
            : new Uri(_client.BaseAddress!, downloadUrl);
        if (downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new JsonException("Perplexity skill download URL must use HTTPS.");

        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        using var downloadResponse = await _skillDownloadClient.SendAsync(
            downloadRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        downloadResponse.EnsureSuccessStatusCode();

        await using var bundleStream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
        var result = new MemoryStream();
        await bundleStream.CopyToAsync(result, cancellationToken);
        result.Position = 0;
        return result;
    }

    private static string ReadDownloadUrl(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "url", "download_url" })
            {
                if (response.TryGetProperty(propertyName, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!;
            }
        }

        throw new JsonException("Perplexity skill download response did not contain a download URL.");
    }

    private Skill MapSkill(SkillSummary summary)
    {
        var id = summary.SkillId ?? string.Empty;
        var prefixedId = id.StartsWith($"{GetIdentifier()}/", StringComparison.OrdinalIgnoreCase)
            ? id
            : $"{GetIdentifier()}/{id}";

        return new Skill
        {
            Id = prefixedId,
            Name = string.IsNullOrWhiteSpace(summary.Name) ? id : summary.Name,
            DefaultVersion = summary.Revision ?? string.Empty,
            LatestVersion = summary.Revision,
            CreatedAt = ToUnixTimeSeconds(summary.CreatedAt)
        };
    }

    private static SkillVersion MapSkillVersion(string skillId, SkillRevision revision)
        => new()
        {
            Id = $"{skillId}:{revision.Revision}",
            Object = "skill.version",
            SkillId = skillId,
            Version = revision.Revision ?? string.Empty,
            Name = revision.Name ?? skillId,
            CreatedAt = ToUnixTimeSeconds(revision.CreatedAt)
        };

    private string GetSkillApiKey()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Perplexity)} API key.");

        return key;
    }

    private string StripSkillProviderPrefix(string skillId)
    {
        var prefix = $"{GetIdentifier()}/";
        return skillId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? skillId[prefix.Length..]
            : skillId;
    }

    private static string BuildSkillsPageUri(string baseUri, string? pageToken)
    {
        var uri = $"{baseUri}?limit={SkillsPageLimit.ToString(CultureInfo.InvariantCulture)}";
        return string.IsNullOrWhiteSpace(pageToken)
            ? uri
            : $"{uri}&page_token={Uri.EscapeDataString(pageToken)}";
    }

    private static string? GetNextPageToken(string? pageToken, HashSet<string> seenTokens)
    {
        if (string.IsNullOrWhiteSpace(pageToken))
            return null;

        if (!seenTokens.Add(pageToken))
            throw new JsonException("Perplexity returned a repeated skills pagination token.");

        return pageToken;
    }

    private static long? ToUnixTimeSeconds(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp.ToUnixTimeSeconds()
            : null;

    private sealed class SkillListPage
    {
        [JsonPropertyName("skills")]
        public List<SkillSummary>? Skills { get; set; }

        [JsonPropertyName("next_page_token")]
        public string? NextPageToken { get; set; }
    }

    private sealed class SkillRevisionListPage
    {
        [JsonPropertyName("revisions")]
        public List<SkillRevision>? Revisions { get; set; }

        [JsonPropertyName("next_page_token")]
        public string? NextPageToken { get; set; }
    }

    private sealed class SkillSummary
    {
        [JsonPropertyName("skill_id")]
        public string? SkillId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("revision")]
        public string? Revision { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }
    }

    private sealed class SkillRevision
    {
        [JsonPropertyName("revision")]
        public string? Revision { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }
    }
}
