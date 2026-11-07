using System.Buffers.Binary;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Isaacus;

public partial class IsaacusProvider
{
    private const int MaxTextsPerEmbeddingRequest = 128;

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => (await SendEmbeddingAsync(request, cancellationToken)).Response;

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await SendEmbeddingAsync(ToIsaacusEmbeddingRequest(request), cancellationToken);
        return new OpenAICompatibleEmbeddingResult(result.Response, result.Native.Headers)
            .ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Native.Root));
    }

    private OpenAIEmbeddingRequest ToIsaacusEmbeddingRequest(EmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) != true
            || options.ValueKind != JsonValueKind.Object
            || !options.EnumerateObject().Any(property =>
                property.Name.Equals("dimensions", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Null))
            return request.ToOpenAIEmbeddingRequest(GetIdentifier());

        // Isaacus accepts native null dimensions, unlike the shared standardized converter.
        var nativeOptions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in options.EnumerateObject())
            if (!property.Name.Equals("dimensions", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Null)
                nativeOptions[property.Name] = property.Value.Clone();
        var copy = new EmbeddingRequest
        {
            Model = request.Model,
            Values = request.Values,
            ProviderOptions = new(request.ProviderOptions)
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(nativeOptions)
            }
        };
        var converted = copy.ToOpenAIEmbeddingRequest(GetIdentifier());
        (converted.AdditionalProperties ??= new())["dimensions"] = JsonSerializer.SerializeToElement((int?)null);
        return converted;
    }

    private async Task<(OpenAIEmbeddingResponse Response, NativeResult Native)> SendEmbeddingAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderEmbeddingCompatibilityExtensions.ValidateOpenAIEmbeddingRequest(request);
        if (request.User is not null)
            throw new NotSupportedException("Isaacus text embeddings do not support the user field.");

        string[] texts;
        if (request.Input.ValueKind == JsonValueKind.String)
            texts = [request.Input.GetString()!];
        else if (request.Input.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
            texts = request.Input.EnumerateArray().Select(item => item.GetString()!).ToArray();
        else
            throw new NotSupportedException("Isaacus text embeddings require strings, not token IDs.");

        var model = NativeModel(request.Model);
        var data = new List<OpenAIEmbeddingData>(texts.Length);
        var nativeEmbeddings = new List<object>(texts.Length);
        var totalTokens = 0;
        NativeResult? lastNative = null;
        for (var offset = 0; offset < texts.Length; offset += MaxTextsPerEmbeddingRequest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = texts.Skip(offset).Take(MaxTextsPerEmbeddingRequest).ToArray();
            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in request.AdditionalProperties ?? [])
                payload[property.Key] = property.Value.Clone();
            payload["model"] = JsonSerializer.SerializeToElement(model);
            payload["texts"] = JsonSerializer.SerializeToElement(batch);
            if (request.Dimensions is not null)
                payload["dimensions"] = JsonSerializer.SerializeToElement(request.Dimensions.Value);

            var native = await SendAsync("embeddings", payload, cancellationToken);
            lastNative = native;
            try
            {
                var root = native.Root;
                if (!root.TryGetProperty("embeddings", out var embeddings) || embeddings.ValueKind != JsonValueKind.Array
                    || embeddings.GetArrayLength() != batch.Length)
                    throw new JsonException("Expected one embedding per input text.");

                var seen = new HashSet<int>();
                foreach (var item in embeddings.EnumerateArray())
                {
                    var index = ReadIndex(item, batch.Length, seen);
                    if (!item.TryGetProperty("embedding", out var embedding)
                        || embedding.ValueKind != JsonValueKind.Array || embedding.GetArrayLength() == 0)
                        throw new JsonException("Expected a non-empty embedding vector.");
                    var globalIndex = offset + index;
                    var values = embedding.EnumerateArray().Select(ReadFloat).ToArray();
                    data.Add(new OpenAIEmbeddingData
                    {
                        Index = globalIndex,
                        Embedding = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase)
                            ? JsonSerializer.SerializeToElement(EncodeBase64(values)) : embedding.Clone()
                    });
                    nativeEmbeddings.Add(new { index = globalIndex, embedding = embedding.Clone() });
                }

                totalTokens += InputTokens(root);
            }
            catch (JsonException exception)
            {
                throw InvalidResponse("embeddings", exception);
            }
        }

        var response = new OpenAIEmbeddingResponse
        {
            Model = $"{GetIdentifier()}/{model}",
            Data = data.ToArray(),
            Usage = new OpenAIEmbeddingUsage { PromptTokens = totalTokens, TotalTokens = totalTokens }
        };
        var finalNative = lastNative
            ?? throw new InvalidOperationException("Isaacus embedding request produced no batches.");
        if (texts.Length <= MaxTextsPerEmbeddingRequest)
            return (response, finalNative);

        var aggregate = JsonSerializer.SerializeToElement(new
        {
            embeddings = nativeEmbeddings,
            usage = new { input_tokens = totalTokens }
        });
        return (response, new NativeResult(aggregate, finalNative.Headers));
    }

    private static string EncodeBase64(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        return Convert.ToBase64String(bytes);
    }
}
