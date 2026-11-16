using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Yandex2Spotify;

internal sealed record Track(string? Id, string Title, string[] Artists, string[] Albums, int? DurationMs, string? Isrc, int? Year)
{
    public string Label => $"{string.Join(", ", Artists)} — {Title}";
}

internal sealed record TrackFile(string Source, string Playlist, DateTimeOffset ExportedAt, int Count, Track[] Tracks)
{
    public static async Task<TrackFile> ReadAsync(string path, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync<TrackFile>(stream, LocalFiles.Json, cancellation);
        if (file?.Tracks is null || file.Tracks.Any(track => track is null || string.IsNullOrWhiteSpace(track.Title)))
            throw new InvalidDataException("В файле ожидается список tracks с названиями треков.");

        return file with
        {
            Source = string.IsNullOrWhiteSpace(file.Source) ? "yandex_music" : file.Source,
            Playlist = string.IsNullOrWhiteSpace(file.Playlist) ? Path.GetFileNameWithoutExtension(path) : file.Playlist,
            Tracks = file.Tracks.Select(track => track with
            {
                Artists = track.Artists?.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray() ?? [],
                Albums = track.Albums?.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray() ?? []
            }).ToArray()
        };
    }
}

internal static class LocalFiles
{
    public static readonly string AppFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yandex2Spotify");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
        WriteIndented = true,
        IgnoreReadOnlyProperties = true
    };

    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellation);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
