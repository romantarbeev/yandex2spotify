using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace Yandex2Spotify;

public partial class YandexSignInWindow : Window
{
    private readonly YandexDeviceCode code;

    internal YandexSignInWindow(YandexDeviceCode code)
    {
        this.code = code;
        InitializeComponent();
        CodeText.Text = code.UserCode;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(code.UserCode);
            Feedback.Text = "Код скопирован. Вставьте его на странице Яндекса.";
        }
        catch (ExternalException)
        {
            Feedback.Text = "Буфер обмена занят. Введите код вручную.";
        }
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        try { BrowserLauncher.Open(code.VerificationUrl.AbsoluteUri); }
        catch (Win32Exception)
        {
            Feedback.Text = $"Откройте {code.VerificationUrl.AbsoluteUri} в браузере и введите код.";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
