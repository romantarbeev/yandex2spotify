using System.IO;
using System.Text.Json;

namespace Yandex2Spotify;

internal sealed record YandexOAuthClient(string ClientId, string ClientSecret)
{
    private const string FileName = "yandex-oauth.local.json";

    public static YandexOAuthClient Read()
    {
        var path = Path.Combine(LocalFiles.AppFolder, FileName);
        if (!File.Exists(path))
        {
            // Builds inside the checkout can use the project-local file without copying it to the output.
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Yandex2Spotify.csproj")))
                {
                    path = Path.Combine(directory.FullName, FileName);
                    break;
                }
            }
        }

        if (!File.Exists(path))
            throw new InvalidOperationException("Для входа по коду настройте yandex-oauth.local.json по инструкции в README или введите готовый OAuth-токен.");

        using var file = File.OpenRead(path);
        var client = JsonSerializer.Deserialize<YandexOAuthClient>(file, LocalFiles.Json);
        if (string.IsNullOrWhiteSpace(client?.ClientId) || string.IsNullOrWhiteSpace(client.ClientSecret))
            throw new InvalidOperationException("Заполните client_id и client_secret в yandex-oauth.local.json.");
        return new(client.ClientId.Trim(), client.ClientSecret.Trim());
    }
}
