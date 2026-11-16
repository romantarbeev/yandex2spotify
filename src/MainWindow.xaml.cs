using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Yandex2Spotify;

public partial class MainWindow : Window
{
    private static readonly SpotifyDestination Liked = new("Любимые треки");
    private static readonly SpotifyDestination NewPlaylist = new("Создать новый плейлист…", Create: true);
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ObservableCollection<Track> missing = [];
    private readonly ObservableCollection<SpotifyDestination> destinations = [];
    private TrackRow[] rows = [];
    private ConnectionSettings settings = new();
    private YandexClient? yandex;
    private SpotifyClient? spotify;
    private SpotifyAuth? spotifyAuth;
    private SpotifyProfile? profile;
    private CancellationTokenSource? operation;
    private bool closeAfterOperation;
    private bool bulkSelection;
    private int selectedCount;
    private string sourceName = "";
    private string source = "yandex_music";
    private string missingSourceName = "";

    public MainWindow()
    {
        InitializeComponent();
        Destination.ItemsSource = destinations;
        MissingList.ItemsSource = missing;
        try { settings = ConnectionSettings.Read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            SetStatus("Сохранённые подключения не удалось прочитать. Введите настройки заново.");
        }
        RedirectUri.Text = settings.SpotifyRedirect;
        SpotifyId.Text = settings.SpotifyClientId;
        YandexToken.Password = settings.YandexToken;
        UpdateControls();
    }

    private async void ConnectYandex_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Подключение к Яндекс Музыке…", async cancellation =>
        {
            var token = YandexToken.Password.Trim();
            if (token.Length == 0)
                throw new InvalidOperationException("Вставьте OAuth-токен или нажмите «Войти через Яндекс».");
            await ConnectYandexAsync(token, cancellation);
        });

    private async void SignInYandex_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Получение кода подтверждения Яндекса…", async cancellation =>
        {
            var auth = new YandexDeviceAuth(http, YandexOAuthClient.Read());
            var code = await auth.RequestCodeAsync(cancellation);
            var dialog = new YandexSignInWindow(code) { Owner = this };
            CancelEventHandler cancelSignIn = (_, _) => operation?.Cancel();
            dialog.Closing += cancelSignIn;
            string token;
            try
            {
                SetStatus("Подтвердите вход на странице Яндекса.");
                dialog.Show();
                token = await auth.WaitForTokenAsync(code, cancellation);
            }
            finally
            {
                dialog.Closing -= cancelSignIn;
                if (dialog.IsVisible)
                    dialog.Close();
            }

            cancellation.ThrowIfCancellationRequested();
            SetStatus("Подключение к Яндекс Музыке…");
            await ConnectYandexAsync(token, cancellation);
        });

    private async Task ConnectYandexAsync(string token, CancellationToken cancellation)
    {
        var client = new YandexClient(http, token);
        var result = await client.ConnectAsync(cancellation);
        var updated = settings with { YandexToken = token };
        await updated.SaveAsync();
        settings = updated;
        yandex = client;
        YandexToken.Password = token;
        YandexAccount.Text = result.Account;
        YandexSource.ItemsSource = result.Playlists;
        YandexSource.SelectedIndex = 0;
        SetStatus("Выберите плейлист Яндекс Музыки и нажмите «Загрузить треки».");
    }

    private async void LoadYandex_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Загрузка списка Яндекс Музыки…", async cancellation =>
        {
            if (yandex is null || YandexSource.SelectedItem is not YandexPlaylist playlist)
                return;
            var result = await yandex.LoadAsync(playlist, (done, total) =>
                ShowProgress(done, total, $"Загрузка из Яндекс Музыки · {done} из {total}"), cancellation);
            SetTracks(result.Tracks, playlist.Name, "yandex_music");
            var unavailable = result.Unavailable > 0 ? $" · Без метаданных {result.Unavailable}" : "";
            SetStatus($"Загружено треков {result.Tracks.Length}{unavailable}.");
        });

    private async void ConnectSpotify_Click(object sender, RoutedEventArgs e) => await ConnectSpotifyAsync(forceSignIn: false);
    private async void SwitchSpotify_Click(object sender, RoutedEventArgs e) => await ConnectSpotifyAsync(forceSignIn: true);

    private Task ConnectSpotifyAsync(bool forceSignIn) => RunAsync("Подключение к Spotify…", async cancellation =>
    {
        var clientId = SpotifyId.Text.Trim();
        if (clientId.Length == 0)
            throw new InvalidOperationException("Введите Client ID из Spotify Dashboard.");
        var redirect = SpotifyAuth.ValidateRedirect(RedirectUri.Text.Trim());
        var auth = new SpotifyAuth(http, clientId, redirect);
        var client = new SpotifyClient(http, auth, SetStatus);
        var canRestore = !forceSignIn && clientId == settings.SpotifyClientId && settings.SpotifyRefreshToken is not null;
        if (canRestore)
            auth.RefreshToken = settings.SpotifyRefreshToken;
        else
            await SignInAsync();

        SpotifyProfile account;
        try { account = await client.ProfileAsync(cancellation); }
        catch (SpotifySignInRequired) when (canRestore)
        {
            await SignInAsync();
            account = await client.ProfileAsync(cancellation);
        }
        SetStatus("Загрузка плейлистов Spotify…");
        var playlists = await client.PlaylistsAsync(account.Id, cancellation);

        spotify = client;
        spotifyAuth = auth;
        var previous = profile?.AccountId == account.AccountId ? Destination.SelectedItem as SpotifyDestination : null;
        profile = account;
        SpotifyAccount.Text = account.Name;
        destinations.Clear();
        destinations.Add(Liked);
        foreach (var playlist in playlists.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            destinations.Add(playlist);
        destinations.Add(NewPlaylist);
        Destination.SelectedItem = previous is null ? Liked : destinations.FirstOrDefault(item => item == previous) ?? Liked;
        settings = settings with { SpotifyClientId = clientId, SpotifyRedirect = redirect.AbsoluteUri, SpotifyRefreshToken = auth.RefreshToken };
        await settings.SaveAsync();
        SetStatus("Spotify подключён. Выберите, куда добавить треки.");

        async Task SignInAsync()
        {
            SetStatus("Подтвердите вход в Spotify в браузере.");
            await auth.SignInAsync(cancellation);
        }
    });

    private void SpotifyHelp_Click(object sender, RoutedEventArgs e)
    {
        string redirect;
        try { redirect = SpotifyAuth.ValidateRedirect(RedirectUri.Text.Trim()).AbsoluteUri; }
        catch (ArgumentException) { redirect = SpotifyAuth.DefaultRedirect; }
        new SpotifyHelpWindow(redirect) { Owner = this }.ShowDialog();
    }

    private void Destination_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        NewPlaylistFields.Visibility = Destination.SelectedItem is SpotifyDestination { Create: true }
            ? Visibility.Visible : Visibility.Collapsed;
        if (NewPlaylistFields.Visibility == Visibility.Visible && string.IsNullOrWhiteSpace(NewPlaylistName.Text))
            NewPlaylistName.Text = SuggestedPlaylistName();
        UpdateControls();
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Список треков JSON (*.json)|*.json", Title = "Открыть список для импорта" };
        if (dialog.ShowDialog(this) != true)
            return;
        await RunAsync("Открытие списка треков…", async cancellation =>
        {
            var file = await TrackFile.ReadAsync(dialog.FileName, cancellation);
            SetTracks(file.Tracks, file.Playlist, file.Source);
            SetStatus($"Из файла загружено треков {file.Tracks.Length}.");
        });
    }

    private async void SaveTracks_Click(object sender, RoutedEventArgs e) =>
        await SaveFileAsync(sourceName, source, SelectedTracks(), "tracks.json");

    private async void SaveMissing_Click(object sender, RoutedEventArgs e) =>
        await SaveFileAsync(missingSourceName, source, missing.ToArray(), "not_found.json");

    private async Task SaveFileAsync(string playlist, string fileSource, Track[] tracks, string name)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Список треков JSON (*.json)|*.json", FileName = name, DefaultExt = ".json", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await RunAsync("Сохранение списка…", async cancellation =>
        {
            var file = new TrackFile(fileSource, playlist, DateTimeOffset.UtcNow, tracks.Length, tracks);
            await LocalFiles.WriteAsync(dialog.FileName, JsonSerializer.SerializeToUtf8Bytes(file, LocalFiles.Json), cancellation);
            SetStatus($"Сохранено треков {tracks.Length} · {Path.GetFileName(dialog.FileName)}.");
        });
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (spotify is null || profile is null || Destination.SelectedItem is not SpotifyDestination destination)
            return;
        var tracks = SelectedTracks();
        if (tracks.Length == 0)
            return;

        await RunAsync("Проверка назначения в Spotify…", async cancellation =>
        {
            if (destination.Create)
            {
                var name = NewPlaylistName.Text.Trim();
                if (name.Length == 0)
                    throw new InvalidOperationException("Введите название нового плейлиста Spotify.");
                destination = await spotify.CreatePlaylistAsync(name, cancellation);
                destinations.Insert(destinations.Count - 1, destination);
                Destination.SelectedItem = destination;
            }
            missing.Clear();
            missingSourceName = sourceName;
            MissingTab.Header = "Не найдено";
            var importer = new SpotifyImport(spotify, profile.AccountId, LocalFiles.AppFolder);
            try
            {
                var result = await importer.RunAsync(tracks, destination.Id, counts =>
                    ShowProgress(counts.Checked, counts.Total, Describe($"Обработано {counts.Checked} из {counts.Total}", counts)),
                    track =>
                    {
                        missing.Add(track);
                        MissingTab.Header = $"Не найдено ({missing.Count})";
                    }, cancellation);
                SetStatus(Describe($"Готово · {destination.Name}", result));
            }
            finally
            {
                if (spotifyAuth?.RefreshToken is { } refreshed && refreshed != settings.SpotifyRefreshToken)
                {
                    settings = settings with { SpotifyRefreshToken = refreshed };
                    await settings.SaveAsync();
                }
            }
        });
    }

    private static string Describe(string heading, ImportProgress counts) =>
        $"{heading} · Добавлено {counts.Added} · Уже в списке или повторяются {counts.AlreadyPresent} · Не найдено {counts.Missing}";

    private void SetTracks(Track[] tracks, string playlist, string fileSource)
    {
        foreach (var row in rows)
            row.PropertyChanged -= TrackSelectionChanged;
        rows = tracks.Select(track => new TrackRow(track)).ToArray();
        foreach (var row in rows)
            row.PropertyChanged += TrackSelectionChanged;
        sourceName = playlist;
        source = fileSource;
        TrackGrid.ItemsSource = rows;
        TrackSource.Text = playlist;
        selectedCount = rows.Length;
        missing.Clear();
        MissingTab.Header = "Не найдено";
        TrackTabs.SelectedIndex = 0;
        NewPlaylistName.Text = SuggestedPlaylistName();
        UpdateControls();
    }

    private string SuggestedPlaylistName() => sourceName.Length == 0 ? "Из Яндекс Музыки" : $"Яндекс — {sourceName}";
    private Track[] SelectedTracks() => rows.Where(row => row.Selected).Select(row => row.Track).ToArray();

    private void TrackSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (bulkSelection || sender is not TrackRow row)
            return;
        selectedCount += row.Selected ? 1 : -1;
        UpdateControls();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectTracks(true);
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => SelectTracks(false);

    private void SelectTracks(bool selected)
    {
        bulkSelection = true;
        foreach (var row in rows)
            row.Selected = selected;
        bulkSelection = false;
        selectedCount = selected ? rows.Length : 0;
        UpdateControls();
    }

    private async Task RunAsync(string initialStatus, Func<CancellationToken, Task> work)
    {
        if (operation is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        CancelButton.IsEnabled = true;
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;
        SetStatus(initialStatus);
        UpdateControls();
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetStatus("Операция отменена.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Сервис не ответил вовремя. Повторите операцию.");
        }
        catch (JsonException)
        {
            SetStatus("Не удалось прочитать JSON. Проверьте формат файла или повторите запрос к сервису.");
        }
        catch (Exception error)
        {
            SetStatus(error.Message);
        }
        finally
        {
            operation = null;
            Progress.Visibility = Visibility.Collapsed;
            UpdateControls();
            if (closeAfterOperation)
                Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        operation?.Cancel();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (operation is not null)
        {
            closeAfterOperation = true;
            operation.Cancel();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        http.Dispose();
        base.OnClosed(e);
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void ShowProgress(int done, int total, string message)
    {
        Progress.IsIndeterminate = false;
        Progress.Maximum = total;
        Progress.Value = done;
        SetStatus(message);
    }

    private void UpdateControls()
    {
        var idle = operation is null;
        Connections.IsEnabled = idle;
        TrackActions.IsEnabled = idle;
        TrackGrid.IsEnabled = idle;
        YandexSource.IsEnabled = yandex is not null;
        LoadYandexButton.IsEnabled = yandex is not null;
        Destination.IsEnabled = spotify is not null;
        SwitchSpotifyButton.Visibility = spotify is null ? Visibility.Collapsed : Visibility.Visible;
        SelectionCount.Text = $"Выбрано {selectedCount} из {rows.Length}";
        SelectAllButton.IsEnabled = idle && selectedCount < rows.Length;
        ClearSelectionButton.IsEnabled = idle && selectedCount > 0;
        SaveTracksButton.IsEnabled = selectedCount > 0;
        SaveMissingButton.IsEnabled = idle && missing.Count > 0;
        ImportButton.IsEnabled = idle && spotify is not null && Destination.SelectedItem is not null && selectedCount > 0;
        CancelButton.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
    }
}

internal sealed class TrackRow(Track track) : INotifyPropertyChanged
{
    private bool selected = true;
    public Track Track { get; } = track;
    public string Title => Track.Title;
    public string Artists => string.Join(", ", Track.Artists);
    public string Albums => string.Join(", ", Track.Albums);
    public string Duration => Track.DurationMs is >= 0 and var duration
        ? $"{duration / 60000}:{duration / 1000 % 60:00}" : "—";

    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value)
                return;
            selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
