using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace Yandex2Spotify;

internal sealed record YandexDeviceCode(string DeviceCode, string UserCode, Uri VerificationUrl,
    DateTimeOffset ExpiresAt, TimeSpan Interval);

internal sealed class YandexDeviceAuth(HttpClient http, YandexOAuthClient client)
{
    private const string BaseUrl = "https://oauth.yandex.ru";

    public async Task<YandexDeviceCode> RequestCodeAsync(CancellationToken cancellation)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["device_id"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            ["device_name"] = "Yandex2Spotify Windows"
        });
        using var response = await http.PostAsync(BaseUrl + "/device/code", form, cancellation);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Яндекс не выдал код подтверждения (HTTP {(int)response.StatusCode}). Попробуйте ещё раз.");

        var data = await ApiJson.ReadAsync(response, cancellation);
        var url = data.RequiredText("verification_url", "Яндекс ID");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var verification) || verification.Scheme != Uri.UriSchemeHttps ||
            !verification.IsDefaultPort || verification.UserInfo.Length != 0 ||
            verification.Host is not ("ya.ru" or "oauth.yandex.ru" or "oauth.yandex.com"))
            throw new InvalidOperationException("Яндекс вернул неожиданный адрес страницы входа.");
        if (data.Number("expires_in") is not (> 0 and var expiresIn))
            throw new InvalidOperationException("Яндекс вернул код подтверждения без срока действия.");

        return new(data.RequiredText("device_code", "Яндекс ID"), data.RequiredText("user_code", "Яндекс ID"),
            verification, DateTimeOffset.UtcNow.AddSeconds(expiresIn), TimeSpan.FromSeconds(Math.Max(1, data.Number("interval") ?? 5)));
    }

    public async Task<string> WaitForTokenAsync(YandexDeviceCode code, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var remaining = code.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new InvalidOperationException("Срок действия кода истёк. Запросите новый код.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(remaining);
        var interval = code.Interval;
        try
        {
            while (true)
            {
                await Task.Delay(interval, deadline.Token);
                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "device_code",
                    ["code"] = code.DeviceCode,
                    ["client_id"] = client.ClientId,
                    ["client_secret"] = client.ClientSecret
                });
                using var response = await http.PostAsync(BaseUrl + "/token", form, deadline.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    interval += TimeSpan.FromSeconds(5);
                    if (response.Headers.RetryAfter?.Delta is { } retry && retry > interval)
                        interval = retry;
                    continue;
                }

                if (response.IsSuccessStatusCode)
                    return (await ApiJson.ReadAsync(response, deadline.Token)).RequiredText("access_token", "Яндекс ID");

                if (response.Content.Headers.ContentType?.MediaType == "application/json")
                {
                    var data = await ApiJson.ReadAsync(response, deadline.Token);
                    switch (data.Text("error"))
                    {
                        case "authorization_pending":
                            continue;
                        case "slow_down":
                            interval += TimeSpan.FromSeconds(5);
                            continue;
                        case "expired_token":
                        case "invalid_grant":
                            throw new InvalidOperationException("Яндекс отклонил код. Запросите новый код и подтвердите вход.");
                        case "access_denied":
                            throw new InvalidOperationException("Доступ к Яндекс Музыке отклонён на странице подтверждения.");
                    }
                }
                throw new InvalidOperationException($"Яндекс не выдал токен (HTTP {(int)response.StatusCode}). Попробуйте ещё раз.");
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException("Срок действия кода истёк. Запросите новый код.");
        }
    }
}
