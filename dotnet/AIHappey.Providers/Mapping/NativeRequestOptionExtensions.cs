using System.Text.Json;

namespace AIHappey.Responses
{
    internal static class NativeResponseRequestOptions
    {
        internal static Dictionary<string, object?> GetNativeProviderOptions(this ResponseRequest request, string providerId)
            => new() { [providerId] = NativeRoot(request) };

        internal static JsonElement NativeRoot<T>(T request)
        {
            var root = JsonSerializer.SerializeToElement(request, ResponseJson.Default);
            return JsonSerializer.SerializeToElement(root.EnumerateObject()
                .Where(property => property.Name != "metadata")
                .GroupBy(property => property.Name)
                .ToDictionary(group => group.Key, group => group.Last().Value.Clone()));
        }
    }
}

namespace AIHappey.Messages
{
    internal static class NativeMessagesRequestOptions
    {
        internal static Dictionary<string, object?> GetNativeProviderOptions(this MessagesRequest request, string providerId)
            => new() { [providerId] = AIHappey.Responses.NativeResponseRequestOptions.NativeRoot(request) };
    }
}
