using System.Windows;
using System.Windows.Input;
using AltyaziDB.Player.App.Localization;

namespace AltyaziDB.Player.App.Views;

public partial class OpenUrlDialog : Window
{
    public OpenUrlDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UrlTextBox.Focus();
            TryPasteUrlFromClipboard();
        };
    }

    public string? Url { get; private set; }

    private void Open_OnClick(object sender, RoutedEventArgs e) => Accept();

    private void UrlTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
    }

    private void Accept()
    {
        var candidate = UrlTextBox.Text.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            System.Windows.MessageBox.Show(
                this,
                LocalizationManager.Get("Dialog.InvalidUrl"),
                LocalizationManager.Get("Dialog.OpenUrlFailed"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Url = candidate;
        DialogResult = true;
    }

    private void TryPasteUrlFromClipboard()
    {
        try
        {
            var text = System.Windows.Clipboard.GetText().Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                UrlTextBox.Text = text;
                UrlTextBox.SelectAll();
            }
        }
        catch
        {
        }
    }
}
