using System.Reflection;
using System.Text.Json;
using AltyaziDB.Player.Core.Models;
using AltyaziDB.Player.Telegram;
static void Verify(bool ok, string meaning) {
    if (!ok) throw new Exception("R5/P4.13h FAIL: "+meaning);
}
var classify = typeof(TdJsonTelegramService).GetMethod(
    "ClassifyDocumentKind", BindingFlags.NonPublic | BindingFlags.Static)!;
TelegramMediaKind Kind(string name,string mime="application/octet-stream") =>
    (TelegramMediaKind)classify.Invoke(null,[name,mime])!;
TelegramMediaItem Attachment(int i,string filename,string caption="📦 MEDIA INFO:\nFile metadata, 1080p",long chat=-1001987654321)
 => new(chat,2000+i,10000+i,"AMZN",filename,caption,"application/octet-stream",
    i*1000000L,0,DateTimeOffset.UtcNow,Kind(filename));

var names = new[] {
    "Siccin.9.1080p.mkv.7z.001","Siccin.9.1080p.mkv.7z.002",
    "Resident.Evil.2026.mkv.001","Resident.Evil.2026.mkv.002",
    "Spider.Man.2026.mkv.001","Spider.Man.2026.mkv.002",
    "Avatar.Aang.2026.mkv.001","Avatar.Aang.2026.mkv.002",
    "Weapons.2025.mkv.z01","Weapons.2025.mkv.z02","Weapons.2025.mkv.zip",
    "Standalone.Movie.mkv","Readme.txt"
};
var media = names.Select((n,i)=>Attachment(i+1,n)).ToArray();
Verify(media.All(m=>m.DisplayTitle==m.FileName),
    "MEDIA INFO captions do not override actual file names");
Verify(Kind("Standalone.Movie.mkv")==TelegramMediaKind.Video,
    "regular MKV uploaded as Telegram document is playable");
Verify(Kind("Readme.txt")==TelegramMediaKind.Document,
    "unknown files remain visible instead of filtered out");
Verify(Kind("Resident.Evil.mkv.001")==TelegramMediaKind.ArchivePart,
    "raw .mkv.001 is not omitted");
Verify(Kind("Weapons.z01")==TelegramMediaKind.ArchivePart &&
       Kind("Weapons.zip")==TelegramMediaKind.ArchivePart,
    "WinRAR-style .z01/.zip parts remain recognized");
Verify(Kind("Film.7z.001")==TelegramMediaKind.ArchivePart,
    "legacy 7z volumes remain recognized");
var groups=TelegramArchiveSetDetector.Group(media);
Verify(groups.Count==7, "13 physical files appear as 7 cards (5 multipart, 2 standalone)");
Verify(groups.Count(x=>x.IsArchiveSet)==5, "five independent multipart media cards");
Verify(groups.Sum(x=>x.ArchiveParts?.Count ?? 1)==13,
    "all 13 physical files retained including unknown document");
var raw=groups.Single(x=>x.FileName=="Resident.Evil.2026.mkv");
Verify(raw.ArchiveFormat==TelegramArchiveFormat.RawSplit && raw.ArchiveSetComplete,
    "Resident Evil .mkv.001/.002 is direct-range media");
Verify(raw.ArchiveStatusLabel.Contains("doğrulanamadı"),
    "raw split without terminal marker does not falsely promise completeness");
var siccin=groups.Single(x=>x.FileName=="Siccin.9.1080p.mkv.7z");
Verify(siccin.ArchiveFormat==TelegramArchiveFormat.Split7Zip &&
       siccin.ArchiveSetComplete && siccin.ArchiveParts!.Count==2,
    "Siccin 7z still displays one playable 2-part card");
var weapons=groups.Single(x=>x.FileName=="Weapons.2025.mkv");
Verify(weapons.ArchiveFormat==TelegramArchiveFormat.SplitZip &&
       weapons.ArchiveParts!.Count==3 && weapons.ArchiveSetComplete,
    "WinRAR split ZIP still groups z01+z02+zip");
var incomplete=TelegramArchiveSetDetector.Group(new [] {
    Attachment(30,"Missing.mkv.001"),Attachment(31,"Missing.mkv.003")
});
Verify(incomplete.Single().NeedsArchiveRepair,
    "missing .002 never appears as a playable raw split");
var onlyOne=TelegramArchiveSetDetector.Group(new [] {
    Attachment(32,"Only.mkv.001")
});
Verify(onlyOne.Count==1 && onlyOne[0].Kind==TelegramMediaKind.ArchivePart,
    "lone .001 shown as physical part instead of hidden");
Verify(groups.Single(x=>x.FileName=="Readme.txt").Kind==TelegramMediaKind.Document,
    "non-media Telegram document is listed without claiming video support");
// A Telegram uploader may strip the .mkv/.zip extension before writing
// bare numeric parts. These must not remain two unrelated Document rows.
var generic=TelegramArchiveSetDetector.Group(new [] {
    Attachment(50,"Heart_of_the_Beast_2026_TURG.001"),
    Attachment(51,"Heart_of_the_Beast_2026_TURG.002")
});
Verify(generic.Count==1 && generic[0].ArchiveFormat==TelegramArchiveFormat.GenericSplit &&
       generic[0].ArchiveSetComplete && generic[0].ArchiveParts?.Count==2,
       "extensionless .001/.002 appears as one playable candidate set");
var genericGap=TelegramArchiveSetDetector.Group(new [] {
    Attachment(52,"Gapped_Store_ZIP.001"), Attachment(53,"Gapped_Store_ZIP.003")
});
Verify(genericGap.Single().NeedsArchiveRepair,
       "extensionless part gaps must be diagnosed, never streamed incorrectly");

var otherChat=TelegramArchiveSetDetector.Group(new [] {
    Attachment(40,"Test.mkv.001",chat:-1002),
    Attachment(41,"Test.mkv.002",chat:-1003)
});
Verify(otherChat.All(x=>x.Kind==TelegramMediaKind.ArchivePart),
    "volumes from different chats never merge");
// Verify real archive-map Range byte reads crossing .mkv.001 -> .mkv.002.
// This must work without downloading both parts or invoking ZIP extraction.
var rawVolumeA = Attachment(101,"Range.Test.mkv.001") with {Size=4};
var rawVolumeB = Attachment(102,"Range.Test.mkv.002") with {Size=5};
var rawRangeGroup = TelegramArchiveSetDetector.Group([rawVolumeA,rawVolumeB]).Single();
var payloads = new Dictionary<int,byte[]> {
    [rawVolumeA.FileId]=[1,2,3,4],
    [rawVolumeB.FileId]=[5,6,7,8,9]
};
Func<TelegramMediaItem,long,int,CancellationToken,Task<byte[]>> rangeReader =
    (part,offset,count,token) => {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(payloads[part.FileId].AsSpan((int)offset,count).ToArray());
    };
var mapType = typeof(TdJsonTelegramService).Assembly.GetType(
    "AltyaziDB.Player.Telegram.SplitArchiveVolumeMap",true)!;
var ctor = mapType.GetConstructors(
    BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Single();
var rangeMap = ctor.Invoke([rawRangeGroup.ArchiveParts!,rangeReader]);
var readMethod = mapType.GetMethod("ReadExactAsync",BindingFlags.Public|BindingFlags.Instance)!;
var crossing = await (Task<byte[]>)readMethod.Invoke(
    rangeMap,[2L,5,CancellationToken.None])!;
Verify(crossing.SequenceEqual(new byte[]{3,4,5,6,7}),
    "direct Range crosses .001/.002 without downloading whole video");
Console.WriteLine("[PASS] 13 Telegram files, MEDIA INFO, 7zip, split ZIP, raw video and Range across part boundary");
