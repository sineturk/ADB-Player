namespace AltyaziDB.Player.Core.Models;

public sealed record AudioDeviceInfo(string Name, string Description)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : Description;

    public static AudioDeviceInfo Automatic() => new("auto", "Otomatik / varsayılan ses aygıtı");
}
