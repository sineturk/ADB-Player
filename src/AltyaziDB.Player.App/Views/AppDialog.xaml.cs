using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AltyaziDB.Player.App.Localization;

namespace AltyaziDB.Player.App.Views;

public enum AppDialogKind
{
    Error,
    Warning,
    Info,
    Confirm
}

public partial class AppDialog : Window
{
    private readonly bool _isConfirm;

    public AppDialog(
        string title,
        string message,
        AppDialogKind kind = AppDialogKind.Error,
        string? primaryButtonText = null,
        string? cancelButtonText = null)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "ADB Player" : title.Trim();
        HeadingText.Text = HeadingFor(kind);
        MessageText.Text = LocalizationManager.ToUserFacingText(message);
        _isConfirm = kind == AppDialogKind.Confirm;

        ConfigureKind(kind);

        PrimaryButton.Content = string.IsNullOrWhiteSpace(primaryButtonText)
            ? LocalizationManager.Get(_isConfirm ? "Dialog.Button.Yes" : "Action.Confirm")
            : primaryButtonText;

        if (_isConfirm)
        {
            CancelButton.Visibility = Visibility.Visible;
            CancelButton.Content = string.IsNullOrWhiteSpace(cancelButtonText)
                ? LocalizationManager.Get("Dialog.Button.No")
                : cancelButtonText;
        }
    }

    public static void ShowError(Window? owner, string message, string title = "ADB Player")
    {
        var dialog = new AppDialog(title, message, AppDialogKind.Error);
        ApplyOwner(dialog, owner);
        dialog.ShowDialog();
    }

    public static bool Confirm(Window? owner, string message, string title = "ADB Player")
    {
        var dialog = new AppDialog(title, message, AppDialogKind.Confirm);
        ApplyOwner(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    private static void ApplyOwner(Window dialog, Window? owner)
    {
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }
    }

    private static string HeadingFor(AppDialogKind kind) => kind switch
    {
        AppDialogKind.Warning => LocalizationManager.Get("Dialog.Kind.Warning"),
        AppDialogKind.Info => LocalizationManager.Get("Dialog.Kind.Info"),
        AppDialogKind.Confirm => LocalizationManager.Get("Dialog.Kind.Confirm"),
        _ => LocalizationManager.Get("Dialog.Kind.Error")
    };

    private void ConfigureKind(AppDialogKind kind)
    {
        switch (kind)
        {
            case AppDialogKind.Warning:
                KindBadge.Background = Brush("#2A2414");
                KindBadge.BorderBrush = Brush("#6D5B2A");
                KindIcon.Foreground = Brush("#F4C66C");
                KindIcon.Text = "!";
                break;
            case AppDialogKind.Info:
                KindBadge.Background = Brush("#10263A");
                KindBadge.BorderBrush = Brush("#285B73");
                KindIcon.Foreground = Brush("#37D6FF");
                KindIcon.Text = "i";
                break;
            case AppDialogKind.Confirm:
                KindBadge.Background = Brush("#10263A");
                KindBadge.BorderBrush = Brush("#285B73");
                KindIcon.Foreground = Brush("#37D6FF");
                KindIcon.Text = "?";
                break;
            default:
                KindBadge.Background = Brush("#26171A");
                KindBadge.BorderBrush = Brush("#70404A");
                KindIcon.Foreground = Brush("#FF9AA0");
                KindIcon.Text = "!";
                break;
        }
    }

    private static System.Windows.Media.SolidColorBrush Brush(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = _isConfirm ? false : true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void PrimaryButton_OnClick(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
