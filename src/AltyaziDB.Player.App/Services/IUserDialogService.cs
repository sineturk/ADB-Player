namespace AltyaziDB.Player.App.Services;

public interface IUserDialogService
{
    IReadOnlyList<string> PickVideos(string? initialDirectory = null);
    string? PickFolder(string? initialDirectory = null);
    string? PickSubtitle(string? initialDirectory = null);
    string? PickAudio(string? initialDirectory = null);
    string? PickTorrentFile(string? initialDirectory = null);
    string? PromptForUrl();
    IReadOnlyList<int> SelectItems(
        string title,
        IReadOnlyList<string> items,
        IReadOnlyList<int>? initiallySelected = null,
        bool allowMultiple = false);
    void ShowError(string message, string title = "AltyazıDB Player");
    bool Confirm(string message, string title = "AltyazıDB Player");
}
