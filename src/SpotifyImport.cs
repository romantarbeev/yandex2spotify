using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yandex2Spotify;

internal sealed record ImportProgress(int Checked, int Total, int Added, int AlreadyPresent, int Missing);

internal sealed class SpotifyImport(SpotifyClient spotify, string accountId, string cacheDirectory)
{
    private readonly string cachePath = Path.Combine(cacheDirectory, "matches-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))) + ".json");

    public async Task<ImportProgress> RunAsync(Track[] tracks, string? playlistId, Action<ImportProgress> progress,
        Action<Track> missing, CancellationToken cancellation)
    {
        var matches = File.Exists(cachePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllBytesAsync(cachePath, cancellation)) ?? []
            : new Dictionary<string, string>();
        var existing = playlistId is null ? new HashSet<string>() : await spotify.PlaylistTracksAsync(playlistId, cancellation);
        var pending = new List<string>();
        var inBatch = new HashSet<string>();
        var misses = new HashSet<string>();
        var counts = new ImportProgress(0, tracks.Length, 0, 0, 0);
        var cacheChanged = false;

        async Task SaveMatchesAsync()
        {
            if (!cacheChanged)
                return;
            await LocalFiles.WriteAsync(cachePath, JsonSerializer.SerializeToUtf8Bytes(matches), CancellationToken.None);
            cacheChanged = false;
        }

        async Task FlushAsync()
        {
            await SaveMatchesAsync();
            var toAdd = pending.ToArray();
            if (toAdd.Length == 0)
                return;
            if (playlistId is null)
            {
                var saved = await spotify.SavedAsync(toAdd, cancellation);
                for (var index = 0; index < saved.Length; index++)
                    if (saved[index])
                        existing.Add(toAdd[index]);
                toAdd = toAdd.Where((_, index) => !saved[index]).ToArray();
                counts = counts with { AlreadyPresent = counts.AlreadyPresent + pending.Count - toAdd.Length };
            }
            if (toAdd.Length > 0)
            {
                await spotify.AddAsync(playlistId, toAdd, cancellation);
                existing.UnionWith(toAdd);
                counts = counts with { Added = counts.Added + toAdd.Length };
            }
            pending.Clear();
            inBatch.Clear();
        }

        try
        {
            foreach (var track in tracks)
            {
                cancellation.ThrowIfCancellationRequested();
                var key = JsonSerializer.Serialize(new { track.Id, track.Title, track.Artists, track.Isrc, track.DurationMs });
                if (!matches.TryGetValue(key, out var uri) && !misses.Contains(key))
                {
                    uri = await FindAsync(track, cancellation);
                    if (uri is null)
                        misses.Add(key);
                    else
                    {
                        matches[key] = uri;
                        cacheChanged = true;
                    }
                }

                if (uri is null)
                {
                    missing(track);
                    counts = counts with { Missing = counts.Missing + 1 };
                }
                else if (existing.Contains(uri) || !inBatch.Add(uri))
                    counts = counts with { AlreadyPresent = counts.AlreadyPresent + 1 };
                else
                    pending.Add(uri);

                counts = counts with { Checked = counts.Checked + 1 };
                if (counts.Checked % 20 == 0 || counts.Checked == counts.Total)
                    await FlushAsync();
                progress(counts);
            }
            return counts;
        }
        finally { await SaveMatchesAsync(); }
    }

    private async Task<string?> FindAsync(Track track, CancellationToken cancellation)
    {
        if (!string.IsNullOrWhiteSpace(track.Isrc))
        {
            var isrc = new string(track.Isrc.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (isrc.Length == 12)
            {
                var exact = await spotify.SearchAsync($"isrc:{isrc}", cancellation);
                var uri = exact.Where(IsPlayable).Select(item => item.Text("uri")).OfType<string>().FirstOrDefault();
                if (uri is not null)
                    return uri;
            }
        }

        var title = Normalize(track.Title);
        var artists = track.Artists.Select(Normalize).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
        var query = string.Join(' ', track.Artists.Take(1).Append(track.Title));
        var result = await spotify.SearchAsync(query, cancellation);
        return result.Where(IsPlayable)
            .Where(item => Normalize(item.Text("name") ?? "") == title)
            .Select(item => new
            {
                Uri = item.Text("uri"),
                Artists = item.Field("artists").Entries().Select(artist => Normalize(artist.Text("name") ?? ""))
                    .Distinct().Count(artists.Contains),
                DurationDifference = track.DurationMs is > 0 && item.Number("duration_ms") is > 0 and var duration
                    ? Math.Abs((long)track.DurationMs.Value - duration) : (long?)null
            })
            .Where(item => item.DurationDifference is null or <= 20000 &&
                (artists.Count == 0 ? item.DurationDifference is not null : item.Artists > 0))
            .OrderByDescending(item => item.Artists)
            .ThenBy(item => item.DurationDifference ?? long.MaxValue)
            .Select(item => item.Uri).OfType<string>().FirstOrDefault();
    }

    private static bool IsPlayable(JsonElement item) =>
        item.Text("uri")?.StartsWith("spotify:track:", StringComparison.Ordinal) == true &&
        item.Field("is_playable").ValueKind != JsonValueKind.False && item.Field("restrictions").Text("reason") is null;

    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit));
}
