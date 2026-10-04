namespace AltyaziDB.Player.App.ViewModels;

public sealed record VideoEnhancementOption(
    string Code,
    string DisplayName,
    IReadOnlyList<string> ShaderFiles)
{
    public override string ToString() => DisplayName;
}
