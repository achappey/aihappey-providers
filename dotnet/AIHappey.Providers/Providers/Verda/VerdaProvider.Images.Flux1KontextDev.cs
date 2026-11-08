using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Verda;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
{
    private const string Flux1KontextDevEndpoint = "flux-kontext-dev/predict";

    public async Task<ImageResponse> Flux1KontextDevImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var providerMetadata = imageRequest.GetProviderMetadata<VerdaImageProviderMetadata>(GetIdentifier());
        var flux1 = GetFlux1Metadata(providerMetadata);

        var inputImage = imageRequest.Files?.FirstOrDefault();
        if (imageRequest.Files?.Skip(1).Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Multiple input images are not supported; used files[0]."
            });
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        var normalizedSize = NormalizeFluxSize(imageRequest.Size, imageRequest.AspectRatio);
        var outputFormat = GetFlux1StringOption(flux1, "output_format") ?? "jpeg";

        var input = new Flux1Input
        {
            Prompt = imageRequest.Prompt,
            Size = normalizedSize,
            Seed = imageRequest.Seed,
            NumImages = imageRequest.N,
            NumInferenceSteps = GetFlux1Option<int>(flux1, "num_inference_steps"),
            GuidanceScale = GetFlux1Option<float>(flux1, "guidance_scale"),
            EnableSafetyChecker = GetFlux1Option<bool>(flux1, "enable_safety_checker"),
            OutputFormat = GetFlux1StringOption(flux1, "output_format"),
            OutputQuality = GetFlux1Option<int>(flux1, "output_quality"),
            EnableBase64Output = true,
            Image = inputImage is null ? null : ToDataUrl(inputImage)
        };

        var payload = new Flux1Request { Input = input };
        var json = SerializeFlux1Payload(
            payload,
            providerMetadata,
            "input",
            "num_inference_steps",
            "guidance_scale",
            "enable_safety_checker",
            "output_format",
            "output_quality");

        using var resp = await _client.PostAsync(
            Flux1KontextDevEndpoint,
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception(ExtractVerdaError(raw) ?? $"{resp.StatusCode}: {raw}");

        var mimeType = MapOutputFormatToMimeType(outputFormat);
        var images = ExtractFluxImages(raw, mimeType);
        if (images.Count == 0)
            throw new Exception("Verda returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model.ToModelId(GetIdentifier()) 
            }
        };
    }
}
