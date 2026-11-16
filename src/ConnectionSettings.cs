using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yandex2Spotify;

internal sealed record ConnectionSettings(string YandexToken = "", string SpotifyClientId = "",
    string SpotifyRedirect = SpotifyAuth.DefaultRedirect, string? SpotifyRefreshToken = null)
{
    private static readonly string Path = System.IO.Path.Combine(LocalFiles.AppFolder, "connections.dat");

    public static ConnectionSettings Read() => File.Exists(Path)
        ? JsonSerializer.Deserialize<ConnectionSettings>(ProtectedData.Unprotect(File.ReadAllBytes(Path), null, DataProtectionScope.CurrentUser))
            ?? new()
        : new();

    public Task SaveAsync() => LocalFiles.WriteAsync(Path,
        ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), null, DataProtectionScope.CurrentUser), CancellationToken.None);
}
