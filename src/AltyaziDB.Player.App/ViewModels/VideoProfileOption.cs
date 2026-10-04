namespace AltyaziDB.Player.App.ViewModels;

public sealed record VideoProfileOption(
    string Code,
    string DisplayName,
    string Description)
{
    public override string ToString() => DisplayName;
}
