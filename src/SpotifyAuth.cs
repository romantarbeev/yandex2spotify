using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Yandex2Spotify;

internal sealed class SpotifyAuth(HttpClient http, string clientId, Uri redirect)
{
    private const string Scope = "playlist-read-private playlist-read-collaborative playlist-modify-private " +
                                 "playlist-modify-public user-library-read user-library-modify";
    public const string DefaultRedirect = "http://127.0.0.1:8888/callback";

    private string? token;
    private DateTimeOffset expiresAt;
    public string? RefreshToken { get; set; }

    public static Uri ValidateRedirect(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.Port is < 1024 or > 65535 || uri.AbsolutePath == "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException($"Укажите локальный Redirect URI, например {DefaultRedirect}.");
        return uri;
    }

    public async Task SignInAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var state = RandomUrlToken();
        var verifier = RandomUrlToken();
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect.AbsoluteUri,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge,
            ["show_dialog"] = "true"
        };

        var listener = new TcpListener(IPAddress.Loopback, redirect.Port);
        try
        {
            listener.Start();
            BrowserLauncher.Open("https://accounts.spotify.com/authorize?" +
                string.Join('&', parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}")));
            var code = await ReceiveCodeAsync(listener, state, deadline.Token);
            await ExchangeAsync(new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect.AbsoluteUri,
                ["code_verifier"] = verifier
            }, cancellation);
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new InvalidOperationException($"Порт {redirect.Port} недоступен. Укажите свободный порт в Redirect URI и Spotify Dashboard.", exception);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException("Истекло время ожидания входа в Spotify. Попробуйте подключиться снова.");
        }
        finally { listener.Stop(); }
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellation, bool refresh = false)
    {
        if (!refresh && token is not null && expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return token;
        if (RefreshToken is null)
            throw new SpotifySignInRequired();
        await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = RefreshToken }, cancellation);
        return token!;
    }

    private async Task ExchangeAsync(Dictionary<string, string> parameters, CancellationToken cancellation)
    {
        parameters["client_id"] = clientId;
        using var form = new FormUrlEncodedContent(parameters);
        using var response = await http.PostAsync("https://accounts.spotify.com/api/token", form, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            if (parameters["grant_type"] == "refresh_token" && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                throw new SpotifySignInRequired();
            throw new InvalidOperationException($"Spotify отклонил авторизацию (HTTP {(int)response.StatusCode}). Проверьте Client ID и Redirect URI.");
        }

        var data = await ApiJson.ReadAsync(response, cancellation);
        token = data.RequiredText("access_token", "Spotify");
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(data.Number("expires_in") ?? 3600);
        RefreshToken = data.Text("refresh_token") ?? RefreshToken;
    }

    private async Task<string> ReceiveCodeAsync(TcpListener listener, string expectedState, CancellationToken cancellation)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token);
                var query = new Dictionary<string, string>();
                if (request.Length >= 2 && request[0] == "GET" && request[1].StartsWith('/') &&
                    Uri.TryCreate(redirect.GetLeftPart(UriPartial.Authority) + request[1], UriKind.Absolute, out var callback) &&
                    callback.AbsolutePath == redirect.AbsolutePath)
                {
                    foreach (var pair in callback.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var fields = pair.Split('=', 2);
                        if (fields.Length == 2)
                            query.TryAdd(WebUtility.UrlDecode(fields[0]), WebUtility.UrlDecode(fields[1]));
                    }
                }

                var valid = query.GetValueOrDefault("state") == expectedState;
                var code = valid ? query.GetValueOrDefault("code") : null;
                var denied = valid && query.ContainsKey("error");
                await RespondAsync(stream, code is not null ? "Код получен. Вернитесь в приложение." :
                    denied ? "Вход отменён. Вернитесь в приложение." : "Запрос не распознан.", valid, timeout.Token);
                if (denied)
                    throw new InvalidOperationException("Вход в Spotify отменён в браузере.");
                if (!string.IsNullOrWhiteSpace(code))
                    return code;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            catch (System.IO.IOException) { }
        }
    }

    private static async Task<string[]> ReadRequestAsync(NetworkStream stream, CancellationToken cancellation)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellation);
            if (read == 0)
                break;
            length += read;
            var headers = Encoding.ASCII.GetString(buffer, 0, length);
            if (headers.Contains("\r\n\r\n", StringComparison.Ordinal))
                return headers.Split("\r\n", 2)[0].Split(' ');
        }
        return [];
    }

    private static async Task RespondAsync(NetworkStream stream, string message, bool valid, CancellationToken cancellation)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var status = valid ? "200 OK" : "400 Bad Request";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"), cancellation);
        await stream.WriteAsync(body, cancellation);
    }

    private static string RandomUrlToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

}

internal sealed class SpotifySignInRequired() : Exception("Войдите в Spotify заново через кнопку «Сменить аккаунт».");
