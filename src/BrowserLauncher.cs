using System.Diagnostics;

namespace Yandex2Spotify;

internal static class BrowserLauncher
{
    public static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
