using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace Yandex2Spotify;

public partial class SpotifyHelpWindow : Window
{
    internal SpotifyHelpWindow(string redirect)
    {
        InitializeComponent();
        RedirectValue.Text = redirect;
    }

    private void CopyRedirect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(RedirectValue.Text);
            Feedback.Text = "Redirect URI скопирован.";
        }
        catch (ExternalException)
        {
            Feedback.Text = "Буфер обмена занят. Скопируйте адрес из поля выше.";
        }
    }

    private void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://developer.spotify.com/dashboard";
        try { BrowserLauncher.Open(url); }
        catch (Win32Exception)
        {
            Feedback.Text = $"Откройте {url} в браузере.";
        }
    }
}
