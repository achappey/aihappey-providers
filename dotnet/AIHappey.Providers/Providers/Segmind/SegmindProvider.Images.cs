using System.Net.Http.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Segmind;

public partial class SegmindProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var model = request.Model.StartsWith("segmind/", StringComparison.OrdinalIgnoreCase)
            ? request.Model["segmind/".Length..]
            : request.Model;
        var payload = new Dictionary<string, object?>();
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var metadata) == true)
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();

        if (request.Prompt is not null)
            payload["prompt"] = request.Prompt;
        if (request.Seed is not null)
            payload.TryAdd("seed", request.Seed.Value);
        if (request.AspectRatio is not null)
            payload.TryAdd("aspect_ratio", request.AspectRatio);

        List<object> warnings = [];
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Size is not null)
            warnings.Add(new { type = "unsupported", feature = "size", details = "Pass the model's native size parameters through provider metadata." });
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Segmind image inputs are model-specific. Pass native input parameters through provider metadata.");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/{Uri.EscapeDataString(model)}")
        {
            Content = JsonContent.Create(payload)
        };
        ApplyAuthHeader(httpRequest);

        var now = DateTime.UtcNow;
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Segmind image request failed ({(int)response.StatusCode}): {error}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException($"Segmind returned '{mediaType ?? "no content type"}' instead of image bytes.");

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
            throw new InvalidOperationException("Segmind returned an empty image.");

        return new ImageResponse
        {
            Images = [Convert.ToBase64String(bytes).ToDataUrl(mediaType)],
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = model.ToModelId(GetIdentifier()),
                Headers = response.GetHeaders()
            }
        };
    }
}
