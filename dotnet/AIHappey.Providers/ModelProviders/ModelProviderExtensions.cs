using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderExtensions
{
    public static string CacheKeyFromApiKey(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash); // safe cache key
    }

    public static IEnumerable<Model> WithPricing(this IEnumerable<Model> models, string identifier)
    {
        var pricing = identifier.GetPricing() ?? [];
        List<Model> list = [.. models];

        foreach (var model in list)
        {
            if (pricing.TryGetValue(model.Id, out var value))
                model.Pricing = value;
        }

        return list;
    }


    public static async Task<ModelPricing?> ResolveCatalogPricingForModelAsync(this IModelProvider modelProvider,
        string? modelId, CancellationToken cancellationToken)
    {
        var model = await modelProvider.GetModel(modelId, cancellationToken);

        return model?.Pricing;
    }

    public static string GetCacheKey(this IModelProvider modelProvider, string? apiKey = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return $"models:{modelProvider.GetIdentifier()}";

        return $"models:{modelProvider.GetIdentifier()}:{CacheKeyFromApiKey(apiKey)}";
    }

    public static async Task<IEnumerable<Model>> ListModels(this IModelProvider modelProvider, string? key)
        => await Task.FromResult<IEnumerable<Model>>(modelProvider.GetIdentifier().GetModels());

    public static async Task<Model> GetModel(this IModelProvider modelProvider,
      string? modelId,
      CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var models = await modelProvider.ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id.EndsWith(modelId))
            ?? throw new ArgumentException(modelId);

        model.Type ??= model.Id.GuessModelType();

        return model;
    }

    public static Dictionary<string, Dictionary<string, object>?> CreateProviderMetadata(this IModelProvider modelProvider,
       Dictionary<string, object> metadata)
         => modelProvider.GetIdentifier().CreateProviderMetadata(metadata);

    public static Dictionary<string, Dictionary<string, object>?> CreateProviderMetadata(this string modelProviderId,
        Dictionary<string, object> metadata)
        => new()
        {
                { modelProviderId, metadata }
        };

    public static List<Model> GetModels(this string provider)
    {
        var catalogFiles = ProviderCatalogScope.Current.ReadModels(provider).ToList();
        if (catalogFiles.Count == 0)
            return [];

        List<Model> models = [];
        foreach (var catalogFile in catalogFiles)
        {
            var modelResponseJson = catalogFile.Value;
            if (string.IsNullOrWhiteSpace(modelResponseJson))
                continue;

            var modelResponse = JsonSerializer.Deserialize<ModelResponse>(modelResponseJson, JsonSerializerOptions.Web);
            if (modelResponse?.Data is null)
                continue;

            foreach (var m in modelResponse.Data)
            {
                if (string.IsNullOrWhiteSpace(m.Name))
                    m.Name = m.Id;
            }

            models.AddRange(modelResponse.Data);
        }

        if (models.Count == 0)
            return [];

        var pricing = provider.GetPricing() ?? [];

        foreach (var model in models)
        {
            if (pricing.TryGetValue(model.Id, out ModelPricing? value))
            {
                model.Pricing = value;
            }
        }

        return models;
    }


    public static Dictionary<string, ModelPricing>? GetPricing(this string provider)
    {
        var json = ProviderCatalogScope.Current.ReadPricing(provider);
        if (string.IsNullOrWhiteSpace(json))
            return [];

        using var doc = JsonDocument.Parse(json);

        var result = new Dictionary<string, ModelPricing>();

        foreach (var item in doc.RootElement.EnumerateObject())
        {
            if (!item.Value.TryGetProperty("pricing", out var pricing))
                continue;

            var parsed = pricing.Deserialize<ModelPricing>(JsonSerializerOptions.Web);
            if (parsed is not null)
                result[item.Name] = parsed;
        }

        return result;
    }

}
