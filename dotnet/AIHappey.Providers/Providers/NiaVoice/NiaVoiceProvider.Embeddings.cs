using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NiaVoice;

public sealed partial class NiaVoiceProvider
{
    public async Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Dimensions is not (null or 768))
            throw new NotSupportedException("NiaVoice embeddings have 768 dimensions.");
        var values = request.Values.ToArray();
        var raw = await Embed(JsonSerializer.SerializeToElement(values), Options(request.ProviderOptions), cancellationToken);
        var vectors = raw.GetProperty("embeddings").Deserialize<double[][]>()
            ?? throw new JsonException("NiaVoice response requires embeddings.");
        if (vectors.Length != values.Length)
            throw new JsonException("NiaVoice returned an unexpected number of embeddings.");
        return new EmbeddingResponse
        {
            Embeddings = vectors,
            Usage = raw.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var count)
                ? new EmbeddingUsage { Tokens = count.GetInt32() } : null,
            ProviderMetadata = new() { [ProviderId] = raw },
            Response = new() { Body = raw }
        };
    }

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Dimensions is not (null or 768))
            throw new NotSupportedException("NiaVoice embeddings have 768 dimensions.");
        if (request.EncodingFormat is not (null or "float" or "base64"))
            throw new NotSupportedException("Embedding encoding_format must be float or base64.");
        var raw = await Embed(request.Input, NativeOptions(request.AdditionalProperties), cancellationToken);
        var vectors = raw.GetProperty("embeddings").EnumerateArray().ToArray();
        var tokens = raw.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var count)
            ? count.GetInt32() : 0;
        return new OpenAIEmbeddingResponse
        {
            Model = $"{ProviderId}/{LocalModel(request.Model)}",
            Data = vectors.Select((vector, index) => new OpenAIEmbeddingData
            {
                Index = index,
                Embedding = request.EncodingFormat == "base64"
                    ? JsonSerializer.SerializeToElement(EncodeEmbedding(vector)) : vector.Clone()
            }).ToArray(),
            Usage = new() { PromptTokens = tokens, TotalTokens = tokens }
        };
    }

    private async Task<JsonElement> Embed(JsonElement input, JsonObject payload, CancellationToken ct)
    {
        var values = input.ValueKind switch
        {
            JsonValueKind.String => new[] { input.GetString()! },
            JsonValueKind.Array => input.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new ArgumentException("NiaVoice embeddings accept text, not token IDs.")).ToArray(),
            _ => throw new ArgumentException("NiaVoice embedding input must be text or an array of text.")
        };
        if (values.Length is < 1 or > 64 || values.Sum(value => value.Length) > 32000)
            throw new ArgumentException("NiaVoice embedding input requires 1-64 items and at most 32,000 characters.");
        payload["input"] = JsonNode.Parse(input.GetRawText());
        var raw = await SendJson("v1/embeddings", payload, ct);
        if (raw.GetProperty("embeddings").GetArrayLength() != values.Length)
            throw new JsonException("NiaVoice returned an unexpected number of embeddings.");
        return raw;
    }

    private static string EncodeEmbedding(JsonElement vector)
    {
        var bytes = new byte[vector.GetArrayLength() * sizeof(float)];
        var index = 0;
        foreach (var value in vector.EnumerateArray())
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index, sizeof(float)), value.GetSingle());
            index += sizeof(float);
        }
        return Convert.ToBase64String(bytes);
    }
}
