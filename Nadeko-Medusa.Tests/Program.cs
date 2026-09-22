// 外掛回歸自我檢查（純 assert，無測試框架）。
// 執行：dotnet run --project Nadeko-Medusa.Tests
// 全部通過時結束碼為 0，否則為失敗數。
//
// 未涵蓋（需要 Discord.Net 物件模擬，本 repo 沒有 mock 相依）：
//   - HardMute 的並行更新與舊 timer 失效（_userGates 序列化 + callback 自我核對）
//   - 互動確認先 DeferAsync 再執行回呼
//   - 購券退款失敗後的訊息分流
using CharacterDesign;
using CharacterDesign.Service;
using LogExtension.Service;
using MuteReborn.Services;

var failures = 0;

void Check(string name, bool condition)
{
    if (condition)
    {
        Console.WriteLine($"PASS  {name}");
        return;
    }

    failures++;
    Console.WriteLine($"FAIL  {name}");
}

// ---- MuteReborn：進行中的勞改要先保留發起者的券，否則加時階段會把同一張券花掉 ----
var reservations = new TicketReservations();

Check("保留：餘額 1 可保留一張", reservations.TryReserve("g1-u1", 1));
Check("保留：單券已被保留時不能再保留（加時階段拿不到同一張券）", !reservations.TryReserve("g1-u1", 1));
Check("保留：保留量為 1", reservations.ReservedFor("g1-u1") == 1);

reservations.Release("g1-u1");
Check("保留：釋放後保留量歸零", reservations.ReservedFor("g1-u1") == 0);
Check("保留：釋放後可再次保留", reservations.TryReserve("g1-u1", 1));
reservations.Release("g1-u1");

Check("保留：餘額 0 不能保留", !reservations.TryReserve("g1-u2", 0));

Check("保留：餘額 3 可保留三張",
    reservations.TryReserve("g2-u1", 3)
    && reservations.TryReserve("g2-u1", 3)
    && reservations.TryReserve("g2-u1", 3));
Check("保留：保留三張後第四張失敗", !reservations.TryReserve("g2-u1", 3));
Check("保留：保留量為 3", reservations.ReservedFor("g2-u1") == 3);

Check("保留：不同使用者互不影響", reservations.TryReserve("g2-u2", 1));

reservations.Release("g2-u1");
Check("保留：釋放一張後保留量為 2", reservations.ReservedFor("g2-u1") == 2);
reservations.Release("g2-u1");
reservations.Release("g2-u1");
reservations.Release("g2-u1");
Check("保留：重複釋放不會變成負數", reservations.ReservedFor("g2-u1") == 0);
reservations.Release("never-reserved");
Check("保留：釋放未保留的 key 不出錯", true);

// ---- MuteReborn：購券總價不得溢位 ----
Check("價格：10000 x 268435456 不溢位（原 int 乘法會得到 0）",
    MuteRebornService.CalculateBuyCost(10000, 268435456) == 2684354560000L);
Check("價格：10000 x 429497 不溢位", MuteRebornService.CalculateBuyCost(10000, 429497) == 4294970000L);
var defaultUnitCost = 10000;
var hugeNum = 268435456;
Check("價格：int 乘法確實會溢位（修正原因）", defaultUnitCost * hugeNum == 0);

// ---- CharacterDesign：人設名稱不得跳脫 data/char_design ----
Check("路徑：正常名稱可用", "abc".IsValidDesignName());
Check("路徑：中文名稱可用", "測試人設".IsValidDesignName());
Check("路徑：含空白名稱可用", "a b".IsValidDesignName());
Check("路徑：空字串不可用", !"".IsValidDesignName());
Check("路徑：全空白不可用", !"   ".IsValidDesignName());
Check("路徑：.. 不可用", !"..".IsValidDesignName());
Check("路徑：../x 不可用", !"../x".IsValidDesignName());
Check("路徑：..\\x 不可用", !"..\\x".IsValidDesignName());
Check("路徑：a/b 不可用", !"a/b".IsValidDesignName());
Check("路徑：a\\b 不可用", !"a\\b".IsValidDesignName());
Check("路徑：絕對路徑不可用", !"C:\\Windows".IsValidDesignName());
Check("路徑：磁碟機代號不可用", !"a:b".IsValidDesignName());
Check("路徑：null 不可用", !((string?)null).IsValidDesignName());

// ---- CharacterDesign：PlayingStatus 格式錯誤必須擋下來，不能只存部分 ----
Check("狀態：正常單筆",
    CharacterDesignService.TryParsePlayingList("Playing:hello", out var parsedOne, out _)
    && parsedOne.Count == 1
    && parsedOne[0].Status == "hello");
Check("狀態：多筆以 | 分隔",
    CharacterDesignService.TryParsePlayingList("Playing:a|Streaming:b", out var parsedTwo, out _)
    && parsedTwo.Count == 2);
Check("狀態：狀態文字含冒號時保留後半段",
    CharacterDesignService.TryParsePlayingList("Playing:a:b", out var parsedColon, out _)
    && parsedColon.Count == 1
    && parsedColon[0].Status == "a:b");
Check("狀態：缺冒號要失敗", !CharacterDesignService.TryParsePlayingList("Playing", out var badNoColon, out var errNoColon));
Check("狀態：缺冒號不留下部分結果", badNoColon.Count == 0);
Check("狀態：缺冒號的訊息會指出該項目", errNoColon.Contains("Playing"));
Check("狀態：型別不存在要失敗", !CharacterDesignService.TryParsePlayingList("NotARealType:x", out _, out var errBadType));
Check("狀態：型別不存在的訊息會指出該型別", errBadType.Contains("NotARealType"));
Check("狀態：狀態為空要失敗", !CharacterDesignService.TryParsePlayingList("Playing:", out _, out _));
Check("狀態：空字串要失敗", !CharacterDesignService.TryParsePlayingList("", out _, out _));
Check("狀態：只有分隔線要失敗", !CharacterDesignService.TryParsePlayingList("|", out _, out _));
Check("狀態：好壞混雜要整體失敗（不存部分）",
    !CharacterDesignService.TryParsePlayingList("Playing:ok|garbage", out var mixed, out _)
    && mixed.Count == 0);

// ---- LogExtension：下載中斷不得留下被視為成功的截斷檔 ----
var root = Path.Combine(Path.GetTempPath(), "nadeko-medusa-selfcheck-" + Guid.NewGuid().ToString("N"));

try
{
    var okPath = Path.Combine(root, "sub", "ok.png");
    var payload = new byte[] { 1, 2, 3, 4, 5 };

    Check("下載：完整串流寫入成功", await Ext.SaveStreamAtomicallyAsync(new MemoryStream(payload), okPath));
    Check("下載：內容完整", File.Exists(okPath) && File.ReadAllBytes(okPath).SequenceEqual(payload));
    Check("下載：成功後不留下暫存檔", !File.Exists(okPath + ".tmp"));

    var brokenPath = Path.Combine(root, "sub", "broken.png");
    Check("下載：串流中斷回報失敗", !await Ext.SaveStreamAtomicallyAsync(new BrokenStream(), brokenPath));
    Check("下載：中斷後沒有正式檔案", !File.Exists(brokenPath));
    Check("下載：中斷後沒有暫存檔", !File.Exists(brokenPath + ".tmp"));

    var overPath = Path.Combine(root, "sub", "over.png");
    File.WriteAllBytes(overPath, new byte[] { 9, 9, 9 });
    Check("下載：成功時覆蓋既有檔案",
        await Ext.SaveStreamAtomicallyAsync(new MemoryStream(payload), overPath)
        && File.ReadAllBytes(overPath).SequenceEqual(payload));
}
finally
{
    try
    {
        Directory.Delete(root, true);
    }
    catch
    {
        // 清理失敗不影響檢查結果
    }
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
return failures;

/// <summary>先回傳一個 byte 再中斷，模擬下載到一半連線中斷。</summary>
internal sealed class BrokenStream : Stream
{
    private int _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_read++ == 0)
        {
            buffer[offset] = 42;
            return 1;
        }

        throw new IOException("simulated connection reset");
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
