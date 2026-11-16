using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Yandex2Spotify;

internal sealed record YandexPlaylist(string Name, string? Kind = null);

internal sealed class YandexClient(HttpClient http, string token)
{
    private const string Service = "Яндекс Музыка";
    private const string BaseUrl = "https://api.music.yandex.net";
    private string? userId;

    public async Task<(string Account, YandexPlaylist[] Playlists)> ConnectAsync(CancellationToken cancellation)
    {
        var status = await RequestAsync("/account/status", cancellation);
        var account = status.Field("account");
        userId = Uri.EscapeDataString(account.RequiredText("uid", Service));
        var playlists = await RequestAsync($"/users/{userId}/playlists/list", cancellation);
        if (playlists.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Яндекс Музыка вернула неожиданный список плейлистов.");
        return (account.Text("login") ?? account.Text("displayName") ?? userId,
            [new("Мне нравится"), .. playlists.Entries()
                .Where(playlist => playlist.Text("kind") is not null && playlist.Text("title") is not null)
                .Select(playlist => new YandexPlaylist(playlist.Text("title")!, playlist.Text("kind")))]);
    }

    public async Task<(Track[] Tracks, int Unavailable)> LoadAsync(YandexPlaylist playlist, Action<int, int> progress,
        CancellationToken cancellation)
    {
        if (userId is null)
            throw new InvalidOperationException("Подключите Яндекс Музыку.");

        JsonElement entries;
        if (playlist.Kind is null)
        {
            var likes = await RequestAsync($"/users/{userId}/likes/tracks?if-modified-since-revision=0", cancellation);
            entries = likes.Field("library").Field("tracks");
        }
        else
        {
            var result = await RequestAsync($"/users/{userId}/playlists/{Uri.EscapeDataString(playlist.Kind)}", cancellation);
            if (result.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Яндекс Музыка вернула неожиданный ответ для плейлиста.");
            entries = result.Field("tracks");
        }

        var references = entries.Entries().ToArray();
        var tracks = new List<Track>(references.Length);
        var processed = 0;
        foreach (var batch in references.Chunk(50))
        {
            var fullTracks = batch.Select(entry => ParseTrack(entry.Field("track"))).ToArray();
            var ids = batch.Where((_, index) => fullTracks[index] is null).Select(TrackKey).OfType<string>().Distinct().ToArray();
            Dictionary<string, Track> lookup = [];
            if (ids.Length > 0)
            {
                var result = await RequestAsync("/tracks", cancellation,
                    new FormUrlEncodedContent(ids.Select(id => KeyValuePair.Create("track-ids", id))));
                if (result.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Яндекс Музыка вернула неожиданный список треков.");
                foreach (var entry in result.Entries())
                {
                    var track = ParseTrack(entry);
                    if (track?.Id is not { } id)
                        continue;
                    lookup.TryAdd(id, track);
                    foreach (var albumId in entry.Field("albums").Entries().Select(album => album.Text("id")).OfType<string>())
                        lookup.TryAdd($"{id}:{albumId}", track);
                }
            }

            for (var index = 0; index < batch.Length; index++)
            {
                var key = TrackKey(batch[index]);
                var track = fullTracks[index];
                if (track is null && key is not null)
                    track = lookup.GetValueOrDefault(key) ?? lookup.GetValueOrDefault(key.Split(':')[0]);
                if (track is not null)
                    tracks.Add(track);
            }

            processed += batch.Length;
            progress(processed, references.Length);
        }
        return (tracks.ToArray(), references.Length - tracks.Count);
    }

    private static string? TrackKey(JsonElement reference)
    {
        var id = reference.Text("id") ?? reference.Field("track").Text("id");
        var album = reference.Text("albumId");
        return id is not null && album is not null && album != "0" && !id.Contains(':') ? $"{id}:{album}" : id;
    }

    private static Track? ParseTrack(JsonElement entry)
    {
        var title = entry.Text("title");
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var albums = entry.Field("albums").Entries().ToArray();
        var isrc = entry.Field("externalIds").Text("isrc") ?? entry.Field("externalId").Text("isrc") ??
                   entry.Field("externalIds").Text("ISRC") ?? entry.Field("externalId").Text("ISRC");
        return new(entry.Text("id"), title, entry.Field("artists").Entries().Select(artist => artist.Text("name")).OfType<string>().ToArray(),
            albums.Select(album => album.Text("title")).OfType<string>().ToArray(), entry.Number("durationMs"), isrc,
            albums.Select(album => album.Number("year")).FirstOrDefault(year => year.HasValue));
    }

    private async Task<JsonElement> RequestAsync(string path, CancellationToken cancellation, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(content is null ? HttpMethod.Get : HttpMethod.Post, BaseUrl + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        request.Headers.Add("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Яндекс Музыка отклонила токен. Проверьте OAuth-токен и подключитесь снова.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Яндекс Музыка вернула ошибку HTTP {(int)response.StatusCode}.");
        var data = (await ApiJson.ReadAsync(response, cancellation)).Field("result");
        return data.ValueKind == JsonValueKind.Undefined
            ? throw new InvalidOperationException("Яндекс Музыка вернула ответ без результата.")
            : data;
    }
}
