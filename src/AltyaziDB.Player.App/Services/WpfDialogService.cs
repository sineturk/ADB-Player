using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using AltyaziDB.Player.App.Localization;
using AltyaziDB.Player.App.Views;
using Forms = System.Windows.Forms;

namespace AltyaziDB.Player.App.Services;

public sealed class WpfDialogService : IUserDialogService
{
    public IReadOnlyList<string> PickVideos(string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationManager.Get("Dialog.VideoPickerTitle"),
            Filter = $"{LocalizationManager.Get("Dialog.VideoFiles")}|*.mkv;*.mp4;*.avi;*.webm;*.mov;*.m4v;*.ts;*.m2ts;*.mts;*.mpeg;*.mpg;*.wmv;*.flv;*.ogv|{LocalizationManager.Get("Dialog.AllFiles")}|*.*",
            CheckFileExists = true,
            Multiselect = true,
            RestoreDirectory = true
        };
        SetInitialDirectory(dialog, initialDirectory);
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickFolder(string? initialDirectory = null)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = LocalizationManager.Get("Dialog.LibraryFolder"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory) ? initialDirectory : string.Empty
        };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? PickSubtitle(string? initialDirectory = null) => PickFile(
        LocalizationManager.Get("Dialog.SubtitlePicker"),
        $"{LocalizationManager.Get("Dialog.SubtitleFiles")}|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx;*.sup|{LocalizationManager.Get("Dialog.AllFiles")}|*.*",
        initialDirectory);

    public string? PickAudio(string? initialDirectory = null) => PickFile(
        LocalizationManager.Get("Dialog.AudioPicker"),
        $"{LocalizationManager.Get("Dialog.AudioFiles")}|*.aac;*.ac3;*.eac3;*.dts;*.dtshd;*.thd;*.truehd;*.flac;*.mka;*.m4a;*.mp3;*.ogg;*.opus;*.wav;*.zip;*.rar;*.7z|{LocalizationManager.Get("Dialog.AllFiles")}|*.*",
        initialDirectory);

    public string? PickTorrentFile(string? initialDirectory = null) => PickFile(
        LocalizationManager.Get("Dialog.TorrentPicker"),
        $"{LocalizationManager.Get("Dialog.TorrentFiles")}|*.torrent|{LocalizationManager.Get("Dialog.AllFiles")}|*.*",
        initialDirectory);

    public string? PromptForUrl()
    {
        var dialog = new OpenUrlDialog { Owner = System.Windows.Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Url : null;
    }

    public IReadOnlyList<int> SelectItems(
        string title,
        IReadOnlyList<string> items,
        IReadOnlyList<int>? initiallySelected = null,
        bool allowMultiple = false)
    {
        if (items.Count == 0) return [];
        var choices = items.Select((label, index) => new DialogChoice(index, label)).ToArray();
        var list = new System.Windows.Controls.ListBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(DialogChoice.Label),
            SelectionMode = allowMultiple
                ? System.Windows.Controls.SelectionMode.Multiple
                : System.Windows.Controls.SelectionMode.Single,
            Margin = new Thickness(12),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 34, 22)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 77, 51))
        };
        foreach (var index in initiallySelected ?? [])
        {
            if (index >= 0 && index < choices.Length) list.SelectedItems.Add(choices[index]);
        }
        if (!allowMultiple && list.SelectedIndex < 0) list.SelectedIndex = 0;

        IReadOnlyList<int> result = [];
        var ok = new System.Windows.Controls.Button { Content = LocalizationManager.Get("Action.Confirm"), MinWidth = 88, Margin = new Thickness(5), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = LocalizationManager.Get("Action.Cancel"), MinWidth = 88, Margin = new Thickness(5), IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(7)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(list);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(buttons);
        var window = new Window
        {
            Title = title,
            Width = 720,
            Height = 480,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 25, 15)),
            Foreground = System.Windows.Media.Brushes.White,
            Content = grid,
            ShowInTaskbar = false
        };
        if (System.Windows.Application.Current.MainWindow is { } owner) window.Owner = owner;
        ok.Click += (_, _) =>
        {
            result = list.SelectedItems.Cast<DialogChoice>().Select(choice => choice.Index).Order().ToArray();
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? result : [];
    }

    public void ShowError(string message, string title = "AltyazıDB Player") => System.Windows.MessageBox.Show(
        System.Windows.Application.Current.MainWindow,
        LocalizationManager.ToUserFacingText(message),
        LocalizationManager.TranslateMessage(title),
        MessageBoxButton.OK,
        MessageBoxImage.Error);

    public bool Confirm(string message, string title = "AltyazıDB Player") => System.Windows.MessageBox.Show(
        System.Windows.Application.Current.MainWindow,
        LocalizationManager.TranslateMessage(message),
        LocalizationManager.TranslateMessage(title),
        MessageBoxButton.YesNo,
        MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static string? PickFile(string title, string filter, string? initialDirectory)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false, RestoreDirectory = true };
        SetInitialDirectory(dialog, initialDirectory);
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    private static void SetInitialDirectory(Microsoft.Win32.FileDialog dialog, string? initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
    }

    private sealed record DialogChoice(int Index, string Label);
}
