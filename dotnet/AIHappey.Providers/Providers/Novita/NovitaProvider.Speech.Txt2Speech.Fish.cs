using System.Text.Json;
using System.Text;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{
    private const string V4BaseUrl = "https://api.novita.ai/v4beta/txt2speech";

    private async Task<SpeechResponse> SpeechRequestAsyncTxt2SpeechFish(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        // ---- output audio type ----
        var audioType =
            request.OutputFormat
            //  ?? metadata?.Text2Speech?.
            ?? "mp3";

        var submitPayload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["format"] = audioType
        };

        using var submitContent = new StringContent(
            JsonSerializer.Serialize(submitPayload),
            Encoding.UTF8,
            "application/json"
        );

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, V4BaseUrl)
        {
            Content = submitContent
        };
        ApplyAuthHeader(submitRequest);
        submitRequest.Headers.TryAddWithoutValidation("model", request.Model);
        using var submitResp = await _client.SendAsync(submitRequest, cancellationToken);

        var bytes = await submitResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!submitResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speechmatics TTS failed ({(int)submitResp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = audioType.StartsWith("pcm") ? "application/octet-stream"
        : audioType == "mp3" ? "audio/mpeg"
        : audioType == "wav" ? "audio/wav"
        : audioType == "opus" ? "audio/opus"
        : "application/octet-stream";

        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = audioType ?? "wav"
            },
            Request = new()
            {
                Body = submitPayload
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }
}
