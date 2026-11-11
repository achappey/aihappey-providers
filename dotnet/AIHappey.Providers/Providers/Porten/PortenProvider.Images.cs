using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Porten;

public sealed partial class PortenProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<(JsonElement Body, IDictionary<string, string> Headers)> GenerateImageAsync(
        Dictionary<string, JsonElement> payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = JsonContent.Create(payload, options: ImageJson)
        };
        request.Headers.Authorization = new("Bearer", Key());
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return (document.RootElement.Clone(), response.GetHeaders());
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("Porten does not document image edits.");

        var payload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true && options.ValueKind == JsonValueKind.Object)
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        payload["model"] = JsonSerializer.SerializeToElement(NativeModel(request.Model));
        payload["prompt"] = JsonSerializer.SerializeToElement(request.Prompt);
        if (request.N is not null) payload["n"] = JsonSerializer.SerializeToElement(request.N);
        if (!string.IsNullOrWhiteSpace(request.Size)) payload["size"] = JsonSerializer.SerializeToElement(request.Size);
        if (request.Seed is not null) payload["seed"] = JsonSerializer.SerializeToElement(request.Seed);
        payload["response_format"] = JsonSerializer.SerializeToElement("b64_json");

        var (body, headers) = await GenerateImageAsync(payload, cancellationToken);
        var images = body.GetProperty("data").EnumerateArray()
            .Select(item => item.GetProperty("b64_json").GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.ToDataUrl("image/png")).ToList();
        if (images.Count == 0) throw new JsonException("Porten image response did not contain images.");
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body),
            Response = new HeaderResponseData
            {
                Timestamp = body.TryGetProperty("created", out var created) && created.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.UtcNow,
                Headers = headers,
                ModelId = NativeModel(request.Model).ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var payload = NativeFields(options);
        if (payload.TryGetValue("model", out var model) && model.ValueKind == JsonValueKind.String)
            payload["model"] = JsonSerializer.SerializeToElement(NativeModel(model.GetString()!));
        payload.Remove("stream");
        var (body, _) = await GenerateImageAsync(payload, cancellationToken);
        return body.Deserialize<OpenAIImagesResponse>(ImageJson) ?? throw new JsonException("Invalid Porten image response.");
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = image.B64Json, CreatedAt = response.Created, Size = response.Size ?? options.Size,
                    Quality = response.Quality ?? options.Quality, Background = response.Background ?? options.Background,
                    OutputFormat = response.OutputFormat ?? options.OutputFormat, Usage = response.Usage
                };
        }
    }
}
