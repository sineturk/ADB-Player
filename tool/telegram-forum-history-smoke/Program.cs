using System.Text.Json;
using AltyaziDB.Player.Telegram;

static void Assert(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException("R5/P4.13f forum-history smoke: " + message);
}

// Simulates the AMZN forum topic: Telegram may send 1-2 messages per
// getForumTopicHistory call even when "limit" is 100.
var files = new[]
{
    "Siccin.9.mkv.7z.001", "Siccin.9.mkv.7z.002",
    "Resident.Evil.mkv.001", "Resident.Evil.mkv.002",
    "Spider.Man.mkv.001", "Spider.Man.mkv.002",
    "Avatar.Aang.mkv.001", "Avatar.Aang.mkv.002",
    "Weapons.z01", "Weapons.z02", "Weapons.zip", "Unrelated.mkv"
};
var messages = files.Select((name,i) => (id: 20000L + i, name))
    .OrderByDescending(x=>x.id).ToArray();
int requests=0;
var cursors=new List<long>();
var fetched=await TelegramForumTopicHistoryPager.ReadAsync((cursor,requested,token)=>
{
    token.ThrowIfCancellationRequested();
    requests++;
    cursors.Add(cursor);
    var batch = messages.Where(x=>cursor==0||x.id<=cursor).Take(2).ToArray();
    var body = JsonSerializer.Serialize(new {
        total_count=messages.Length,
        messages=batch.Select(x=>new
        {
            id=x.id.ToString(),
            chat_id="-100000000",
            content=new
            {
                @type="messageDocument",
                document=new { file_name=x.name, mime_type="application/octet-stream"}
            }
        }).ToArray()
    });
    using var doc=JsonDocument.Parse(body);
    return Task.FromResult(doc.RootElement.Clone());
},limit:100);

Assert(fetched.Count==12, "all 12 Telegram topic documents returned");
Assert(requests>=6, "TDLib pages returning 2/100 are fully traversed");
Assert(cursors[0]==0 && cursors.Skip(1).All(x=>x>0), "older-page cursors passed");
Assert(cursors.Zip(cursors.Skip(1)).All(p=>p.First>p.Second||p.First==0),
    "history message cursor monotonically decreases");
var returned = fetched.Select(x=>x.GetProperty("content").GetProperty("document")
    .GetProperty("file_name").GetString()!).ToArray();
Assert(files.All(f=>returned.Contains(f)), "all named .001/.002/.z01/.zip assets retained");
Assert(fetched[0].GetProperty("id").GetString()=="20011", "latest item first");

var empty = await TelegramForumTopicHistoryPager.ReadAsync((cursor,requested,token)=>{
    using var doc=JsonDocument.Parse("""{"messages":[]}""");
    return Task.FromResult(doc.RootElement.Clone());
},limit:100);
Assert(empty.Count==0, "empty topic history handled safely");

Console.WriteLine("[PASS] R5/P4.13f: Telegram forum topic 12/12 physical files across short history pages");
