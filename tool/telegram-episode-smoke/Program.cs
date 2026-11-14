using AltyaziDB.Player.App.Services;

static void Check(bool condition, string reason)
{
    if (!condition) throw new Exception("Telegram season ZIP match failed: " + reason);
}
var examples = new[]
{
    "Has.Fallen.S01E05.1080p.mkv",
    "Has Fallen s1e5",
    "Has.Fallen.1x05.WEB-DL.mkv",
};
foreach (var title in examples)
{
    Check(TelegramEpisodeTrackMatcher.Code(title) == "S01E05", "current video code: " + title);
}
var names = new[]
{
    "Has.Fallen.S01E01.TR.EAC3.eac3",
    "Has.Fallen.S01E05.TR.EAC3.eac3",
    "Has.Fallen.S01E06.TR.EAC3.eac3",
    "Has.Fallen.S01E05.TR.Normal.srt",
    "Has.Fallen.S01E05.TR.Forced.srt",
    "Has.Fallen.S02E05.TR.eac3",
};
var matches = TelegramEpisodeTrackMatcher.MatchingIndices(examples[0], names);
Check(matches.SequenceEqual(new []{1, 3, 4}), "match only this episode audio / normal / forced");
Check(TelegramEpisodeTrackMatcher.MatchingIndices("Film title without code", names).Count == 0,
    "never guess episode without season code");
Check(TelegramEpisodeTrackMatcher.MatchingIndices("Has.Fallen.S01E07.mkv", names).Count == 0,
    "unknown episode does not silently select wrong audio");
Check(TelegramEpisodeTrackMatcher.MatchingIndices("Has.Fallen.S02E05.mkv", names).SequenceEqual(new []{5}),
    "season must match, not episode number alone");
Check(TelegramEpisodeTrackMatcher.Parse("Has Fallen 2026") is null,
    "release year is not an episode code");
Console.WriteLine("[PASS] Telegram season ZIP S01E05 / 1x05 matching, season boundaries and no guesses");
