using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Yandex2Spotify;

internal sealed record SpotifyDestination(string Name, string? Id = null, bool Create = false);
internal sealed record SpotifyProfile(string Id, string AccountId, string Name);

internal sealed class SpotifyClient(HttpClient http, SpotifyAuth auth, Action<string> status)
{
    private static readonly Uri BaseUrl = new("https://api.spotify.com/v1/");
    private readonly Stopwatch searchTimer = Stopwatch.StartNew();
    private TimeSpan nextSearch = TimeSpan.Zero;

    public async Task<SpotifyProfile> ProfileAsync(CancellationToken cancellation)
    {
        var profile = await RequestAsync(HttpMethod.Get, "me", cancellation);
        var id = profile.RequiredText("id", "Spotify");
        return new(id, profile.Text("account_id") ?? id, profile.Text("display_name") ?? id);
    }

    public async Task<List<SpotifyDestination>> PlaylistsAsync(string userId, CancellationToken cancellation)
    {
        var playlists = new List<SpotifyDestination>();
        await foreach (var entry in PagesAsync("me/playlists?limit=50", cancellation))
        {
            if (entry.Text("id") is not { } id || entry.Text("name") is not { } name)
                continue;
            if (entry.Field("owner").Text("id") == userId ||
                entry.Field("collaborative").ValueKind == JsonValueKind.True && entry.Field("items").ValueKind == JsonValueKind.Object)
                playlists.Add(new(name, id));
        }
        return playlists;
    }

    public async Task<SpotifyDestination> CreatePlaylistAsync(string name, CancellationToken cancellation)
    {
        var result = await RequestAsync(HttpMethod.Post, "me/playlists", cancellation, new { name, @public = false });
        return new(name, result.RequiredText("id", "Spotify"));
    }

    public async Task<HashSet<string>> PlaylistTracksAsync(string playlistId, CancellationToken cancellation)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        var path = $"playlists/{Uri.EscapeDataString(playlistId)}/items?limit=50&fields=next,items(item(uri,linked_from(uri)),track(uri,linked_from(uri)))";
        await foreach (var entry in PagesAsync(path, cancellation))
        {
            var item = entry.Field("item");
            if (item.ValueKind == JsonValueKind.Undefined)
                item = entry.Field("track");
            if (item.Text("uri") is { } uri)
                existing.Add(uri);
            if (item.Field("linked_from").Text("uri") is { } original)
                existing.Add(original);
        }
        return existing;
    }

    public async Task<bool[]> SavedAsync(string[] uris, CancellationToken cancellation)
    {
        var result = await RequestAsync(HttpMethod.Get, $"me/library/contains?uris={Uri.EscapeDataString(string.Join(',', uris))}", cancellation);
        var entries = result.Entries().ToArray();
        if (entries.Length != uris.Length || entries.Any(entry => entry.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new InvalidOperationException("Spotify вернул неожиданный ответ при проверке любимых треков.");
        return entries.Select(entry => entry.GetBoolean()).ToArray();
    }

    public Task AddAsync(string? playlistId, string[] uris, CancellationToken cancellation) => playlistId is null
        ? RequestAsync(HttpMethod.Put, $"me/library?uris={Uri.EscapeDataString(string.Join(',', uris))}", cancellation)
        : RequestAsync(HttpMethod.Post, $"playlists/{Uri.EscapeDataString(playlistId)}/items", cancellation, new { uris });

    public async Task<JsonElement[]> SearchAsync(string query, CancellationToken cancellation)
    {
        var wait = nextSearch - searchTimer.Elapsed;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, cancellation);
        cancellation.ThrowIfCancellationRequested();
        nextSearch = searchTimer.Elapsed + TimeSpan.FromSeconds(1.2);
        var result = await RequestAsync(HttpMethod.Get, $"search?type=track&limit=10&q={Uri.EscapeDataString(query)}", cancellation);
        var items = result.Field("tracks").Field("items");
        if (items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Spotify вернул неожиданный результат поиска.");
        return items.Entries().ToArray();
    }

    private async IAsyncEnumerable<JsonElement> PagesAsync(string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        var visited = new HashSet<Uri>();
        string? next = path;
        while (next is not null)
        {
            if (!visited.Add(new Uri(BaseUrl, next)))
                throw new InvalidOperationException("Spotify вернул повторяющуюся страницу списка.");
            var page = await RequestAsync(HttpMethod.Get, next, cancellation);
            var items = page.Field("items");
            if (items.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Spotify вернул неожиданный формат списка.");
            foreach (var entry in items.Entries())
                yield return entry;
            next = page.Text("next");
        }
    }

    private async Task<JsonElement> RequestAsync(HttpMethod method, string path, CancellationToken cancellation, object? body = null)
    {
        var url = new Uri(BaseUrl, path);
        if (url.Scheme != BaseUrl.Scheme || url.Authority != BaseUrl.Authority || !url.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
            throw new InvalidOperationException("Spotify вернул неожиданный URL запроса.");

        var renewed = false;
        var token = await auth.GetTokenAsync(cancellation);
        for (int retries = 0; ;)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !renewed)
            {
                renewed = true;
                token = await auth.GetTokenAsync(cancellation, refresh: true);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                var wait = retry?.Delta ?? retry?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromSeconds(5);
                if (wait < TimeSpan.FromSeconds(1))
                    wait = TimeSpan.FromSeconds(1);
                if (wait > TimeSpan.FromMinutes(2) || retries++ >= 3)
                    throw new InvalidOperationException($"Достигнут лимит Spotify. Повторите после {DateTime.Now.Add(wait):g}.");
                status($"Лимит Spotify. Продолжение через {Math.Ceiling(wait.TotalSeconds)} сек.");
                await Task.Delay(wait, cancellation);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new SpotifySignInRequired();
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Spotify отказал в доступе (HTTP 403). Проверьте доступ аккаунта к приложению и выбранному плейлисту.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Spotify вернул ошибку HTTP {(int)response.StatusCode} для {url.AbsolutePath}.");
            if (method == HttpMethod.Put || method == HttpMethod.Post && path != "me/playlists")
                return default;
            return await ApiJson.ReadAsync(response, cancellation);
        }
    }
}
