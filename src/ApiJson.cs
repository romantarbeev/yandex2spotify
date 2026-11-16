using System.Net.Http;
using System.Text.Json;

namespace Yandex2Spotify;

internal static class ApiJson
{
    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        return await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: cancellation);
    }

    public static JsonElement Field(this JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value : default;

    public static string? Text(this JsonElement item, string key) => item.Field(key).ValueKind switch
    {
        JsonValueKind.String => item.Field(key).GetString(),
        JsonValueKind.Number => item.Field(key).GetRawText(),
        _ => null
    };

    public static string RequiredText(this JsonElement item, string key, string service) =>
        item.Text(key) ?? throw new InvalidOperationException($"{service} не вернул поле {key}.");

    public static int? Number(this JsonElement item, string key) =>
        int.TryParse(item.Text(key), out var result) ? result : null;

    public static IEnumerable<JsonElement> Entries(this JsonElement item) =>
        item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : [];
}
