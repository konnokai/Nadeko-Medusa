// 外掛回歸自我檢查（純 assert，無測試框架）。
// 執行：dotnet run --project Nadeko-Medusa.Tests
// 全部通過時結束碼為 0，否則為失敗數。
//
// 未涵蓋（需要 Discord.Net 物件模擬，本 repo 沒有 mock 相依）：
//   - HardMute 的並行更新與舊 timer 失效（_userGates 序列化 + callback 自我核對）
//   - 互動確認先 DeferAsync 再執行回呼
//   - 購券退款失敗後的訊息分流
extern alias gateway;
using System.Text.Json;
using CharacterDesign;
using CharacterDesign.Service;
using Discord;
using LogExtension.Service;
using McpBridge;
using gateway::McpGateway;
using McpGateway.Contracts;
using MuteReborn.Services;
using NadekoBot.Modules.Utility.AiAgent;

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

// 新的段落裡有例外時記成一次失敗，後面的段落照樣跑
void Section(string name, Action body)
{
    try
    {
        body();
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}：丟出 {ex.GetType().Name}：{ex.Message}");
    }
}

static JsonElement Json(string json)
    => JsonDocument.Parse(json).RootElement.Clone();

static HashSet<string> SchemaNames(IEnumerable<JsonElement> schemas)
    => schemas.Select(s => s.GetProperty("function").GetProperty("name").GetString()!).ToHashSet();

// ---- McpBridge：bot 的私有成員都找得到（bot 升級後先看這裡） ----
Section("BotInternals", () =>
{
    var missing = BotInternals.Validate();
    Check(missing.Count == 0
            ? "BotInternals：成員都找得到"
            : "BotInternals：成員都找得到（缺：" + string.Join("、", missing) + "）",
        missing.Count == 0);
});

// ---- McpBridge：換掉 AiToolRegistry 的工具清單 ----
Section("登記工具", () =>
{
    var builtinCore = new FakeTool("send_message", false);
    var builtinData = new FakeTool("get_user_xp", true);
    var registry = new AiToolRegistry([builtinCore, builtinData]);
    var mcpCore = new FakeTool("srv__core", false);
    var mcpData = new FakeTool("srv__data", true);

    BotInternals.ReplaceTools(registry, [], [mcpCore, mcpData]);
    Check("登記：核心工具出現在 GetAllTools", registry.GetAllTools().Contains(mcpCore));
    Check("登記：GetTool 找得到兩個工具", registry.GetTool("srv__core") == mcpCore && registry.GetTool("srv__data") == mcpData);
    Check("登記：核心工具的 schema 會送給 AI", SchemaNames(registry.GetToolSchemas()).Contains("srv__core"));
    Check("登記：資料工具不放進核心 schema", !SchemaNames(registry.GetToolSchemas()).Contains("srv__data"));
    Check("登記：資料工具出現在 GetDataTools", registry.GetDataTools().ContainsKey("srv__data"));
    Check("登記：內建工具還在",
        registry.GetTool("send_message") == builtinCore
        && registry.GetDataTools().ContainsKey("get_user_xp")
        && SchemaNames(registry.GetToolSchemas()).Contains("send_message"));

    BotInternals.ReplaceTools(registry, ["srv__core", "srv__data"], []);
    Check("移除：MCP 工具都不見了",
        registry.GetTool("srv__core") is null
        && registry.GetTool("srv__data") is null
        && !registry.GetDataTools().ContainsKey("srv__data"));
    Check("移除：核心 schema 也拿掉了", !SchemaNames(registry.GetToolSchemas()).Contains("srv__core"));
    Check("移除：內建工具不受影響",
        registry.GetAllTools().Count == 2
        && SchemaNames(registry.GetToolSchemas()).SetEquals(["send_message"]));
});

// ---- McpBridge：跟內建工具撞名的 MCP 工具要跳過 ----
Section("撞名", () =>
{
    static ToolInfo Info(string name) => new(name, name, "srv", "d", Json("{}"), true, null);

    var selected = McpBridgeModule.SelectTools(
        [Info("srv__a"), Info("send_message"), Info("srv__b")],
        new HashSet<string> { "send_message" },
        out var skipped);
    Check("撞名：其他工具照樣登記", selected.Select(t => t.Name).SequenceEqual(["srv__a", "srv__b"]));
    Check("撞名：撞名的工具列在 skipped", skipped.SequenceEqual(["send_message"]));
});

// ---- McpBridge：requiredPerm 的解讀，沒寫時要最嚴格 ----
Section("權限", () =>
{
    Check("權限：沒寫時是 Administrator", McpAiTool.TryParsePermission(null, out var p) && p == ToolPermission.Of(GuildPermission.Administrator));
    Check("權限：空字串也是 Administrator", McpAiTool.TryParsePermission("  ", out p) && p == ToolPermission.Of(GuildPermission.Administrator));
    Check("權限：None 代表不限", McpAiTool.TryParsePermission("None", out p) && p == ToolPermission.Anyone);
    Check("權限：BotOwner 不分大小寫", McpAiTool.TryParsePermission("botowner", out p) && p == ToolPermission.BotOwner);
    Check("權限：不分大小寫", McpAiTool.TryParsePermission("managemessages", out p) && p == ToolPermission.Of(GuildPermission.ManageMessages));
    Check("權限：不存在的名稱要失敗", !McpAiTool.TryParsePermission("NotAPerm", out _));
    Check("權限：數字不接受（Enum.TryParse 會接受數字）", !McpAiTool.TryParsePermission("8", out _));

    var admin = new GuildPermissions(administrator: true);
    var manage = new GuildPermissions(manageMessages: true);
    Check("權限：BotOwner 只看是不是 owner", McpAiTool.IsAllowed(ToolPermission.BotOwner, isOwner: true, GuildPermissions.None)
                                       && !McpAiTool.IsAllowed(ToolPermission.BotOwner, isOwner: false, admin));
    Check("權限：owner 沒有伺服器權限也不能用", !McpAiTool.IsAllowed(ToolPermission.Of(GuildPermission.ManageMessages), isOwner: true, GuildPermissions.None));
    Check("權限：有伺服器權限就能用", McpAiTool.IsAllowed(ToolPermission.Of(GuildPermission.ManageMessages), isOwner: false, manage));
    Check("權限：None 誰都能用", McpAiTool.IsAllowed(ToolPermission.Anyone, isOwner: false, GuildPermissions.None));

    Check("權限說明：預設", McpAiTool.DescribePermission(null).Contains("Administrator"));
    Check("權限說明：BotOwner", McpAiTool.DescribePermission("BotOwner").Contains("bot owner"));
    Check("權限說明：寫錯要警告", McpAiTool.DescribePermission("Typo").Contains("⚠️"));
    Check("權限說明：空字串不是「沒寫」", !McpAiTool.DescribePermission("").Contains("沒寫") && McpAiTool.DescribePermission("").Contains("Administrator"));
});

// ---- McpBridge：MCP 給的 schema 要整理成 AI 接受的格式 ----
Section("schema", () =>
{
    var full = McpAiTool.NormalizeSchema(Json("""{"type":"object","properties":{"q":{"type":"string"}},"required":["q"]}"""));
    Check("schema：正常的 schema 原樣保留",
        full.GetProperty("properties").GetProperty("q").GetProperty("type").GetString() == "string"
        && full.GetProperty("required")[0].GetString() == "q");

    var missing = McpAiTool.NormalizeSchema(default);
    Check("schema：沒有 schema 時給空物件",
        missing.GetProperty("type").GetString() == "object"
        && missing.GetProperty("properties").ValueKind == JsonValueKind.Object);

    var noProps = McpAiTool.NormalizeSchema(Json("""{"type":"object"}"""));
    Check("schema：缺 properties 時補上", noProps.GetProperty("properties").ValueKind == JsonValueKind.Object);

    var notObject = McpAiTool.NormalizeSchema(Json("""{"type":"string"}"""));
    Check("schema：不是 object 時改成空物件", notObject.GetProperty("type").GetString() == "object");
});

// ---- McpBridge：回給 AI 的結果格式 ----
Section("結果格式", () =>
{
    var ok = McpAiTool.FormatResult(new CallResponse(false, "hello"));
    Check("結果：成功時加上不可信標記", ok.StartsWith("[UNTRUSTED", StringComparison.Ordinal) && ok.Contains("hello"));

    var err = McpAiTool.FormatResult(new CallResponse(true, "boom"));
    Check("結果：失敗時是 bot 認得的錯誤 JSON",
        err.StartsWith("{\"error\"", StringComparison.Ordinal)
        && JsonDocument.Parse(err).RootElement.GetProperty("message").GetString()!.Contains("boom"));
    Check("結果：失敗訊息也加上不可信標記",
        JsonDocument.Parse(err).RootElement.GetProperty("message").GetString()!.StartsWith("[UNTRUSTED", StringComparison.Ordinal));
});

// ---- McpGateway：AI 看到的工具名稱 ----
Section("工具名稱", () =>
{
    Check("名稱：server__tool", ToolNaming.Expose("everything", "echo") == "everything__echo");
    Check("名稱：不合法的字元換成底線", ToolNaming.Expose("my api", "get.thing") == "my_api__get_thing");

    var longA = ToolNaming.Expose("server", new string('a', 80) + "1");
    var longB = ToolNaming.Expose("server", new string('a', 80) + "2");
    Check("名稱：太長時截到 64 個字元以內", longA.Length <= 64 && longB.Length <= 64);
    Check("名稱：截斷後不同的名稱還是不同", longA != longB);
    Check("名稱：同樣的輸入得到同樣的名稱", longA == ToolNaming.Expose("server", new string('a', 80) + "1"));
});

// ---- McpGateway：mcp.json ----
const string CONFIG_JSON = """
{
  // 可以寫註解
  "mcpServers": {
    "a": { "command": "npx", "args": ["-y", "x"], "env": { "K": "${SECRET}" } },
    "b": {
      "url": "http://h/mcp",
      "headers": { "Authorization": "Bearer ${TOKEN}" },
      "dataTool": false,
      "enabledTools": ["t1"],
      "requiredPerm": "None",
      // t9 不存在，用來測回報；值不做 ${} 替換
      "toolPerms": { "t1": "BotOwner", "t9": "${NOT_EXPANDED}" },
      "timeoutSeconds": 5,
    },
    "c": { "command": "x", "disabled": true }
  }
}
""";
var testEnv = new Dictionary<string, string> { ["SECRET"] = "s1", ["TOKEN"] = "t0" };

Section("設定檔", () =>
{
    var cfg = GatewayConfig.Parse(CONFIG_JSON, k => testEnv.GetValueOrDefault(k));
    var a = cfg.Servers.Single(s => s.Name == "a");
    var b = cfg.Servers.Single(s => s.Name == "b");
    var c = cfg.Servers.Single(s => s.Name == "c");

    Check("設定：讀到三個 server", cfg.Servers.Count == 3);
    Check("設定：有 command 是 stdio", !a.IsHttp && a.Command == "npx" && a.Args.SequenceEqual(["-y", "x"]));
    Check("設定：${} 換成環境變數", a.Env["K"] == "s1" && b.Headers["Authorization"] == "Bearer t0");
    Check("設定：預設值", a.DataTool && a.TimeoutSeconds == 60 && a.RequiredPerm is null && a.EnabledTools is null && !a.Disabled);
    Check("設定：有 url 是 http", b.IsHttp && b.Url == "http://h/mcp");
    Check("設定：自訂欄位", !b.DataTool && b.EnabledTools!.Contains("t1") && b.RequiredPerm == "None" && b.TimeoutSeconds == 5);
    Check("設定：disabled", c.Disabled);
    Check("設定：toolPerms", b.ToolPerms["t1"] == "BotOwner" && a.ToolPerms.Count == 0);
    Check("設定：toolPerms 不替換環境變數", b.ToolPerms["t9"] == "${NOT_EXPANDED}");

    var changed = GatewayConfig.Parse(CONFIG_JSON.Replace("\"-y\", \"x\"", "\"-y\", \"y\""), k => testEnv.GetValueOrDefault(k));
    Check("設定：內容改了指紋就變", changed.Servers.Single(s => s.Name == "a").Fingerprint != a.Fingerprint);
    Check("設定：別的 server 改了指紋不變", changed.Servers.Single(s => s.Name == "b").Fingerprint == b.Fingerprint);
});

Section("設定檔錯誤", () =>
{
    static bool Throws(string json, Func<string, string?> env)
    {
        try
        {
            GatewayConfig.Parse(json, env);
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    Check("設定錯誤：環境變數沒設要失敗", Throws(CONFIG_JSON, _ => null));
    Check("設定錯誤：沒有 command 也沒有 url 要失敗", Throws("""{"mcpServers":{"x":{}}}""", _ => null));
    Check("設定錯誤：JSON 壞掉要失敗", Throws("{ not json", _ => null));
    Check("設定錯誤：沒有 mcpServers 要失敗", Throws("{}", _ => null));
    Check("設定錯誤：args 不是字串要失敗", Throws("""{"mcpServers":{"x":{"command":"n","args":["--port",3000]}}}""", _ => null));
    Check("設定錯誤：env 不是字串要失敗", Throws("""{"mcpServers":{"x":{"command":"n","env":{"DEBUG":true}}}}""", _ => null));
    Check("設定錯誤：toolPerms 不是字串要失敗", Throws("""{"mcpServers":{"x":{"command":"n","toolPerms":{"t":1}}}}""", _ => null));
    Check("設定錯誤：server 名稱重複要失敗", Throws("""{"mcpServers":{"x":{"command":"a"},"x":{"command":"b"}}}""", _ => null));
});

Section("設定檔：停用的 server", () =>
{
    var cfg = GatewayConfig.Parse("""{"mcpServers":{"x":{"command":"n","args":["${NOPE}"],"disabled":true}}}""", _ => null);
    Check("設定：停用的 server 不檢查環境變數", cfg.Servers.Single().Disabled);
});

// ---- McpGateway：重讀設定時哪些 server 要重連 ----
Section("重讀計畫", () =>
{
    var cfg = GatewayConfig.Parse(CONFIG_JSON, k => testEnv.GetValueOrDefault(k));
    var a = cfg.Servers.Single(s => s.Name == "a");
    var b = cfg.Servers.Single(s => s.Name == "b");

    var current = new Dictionary<string, (string Fingerprint, bool Connected)>
    {
        ["a"] = (a.Fingerprint, true),
        ["b"] = ("old", true),
        ["c"] = (cfg.Servers.Single(s => s.Name == "c").Fingerprint, true),
        ["z"] = ("gone", true),
    };
    var plan = ReloadPlan.Compute(current, cfg.Servers);
    Check("重讀：沒改的保留", plan.Keep.SequenceEqual(["a"]));
    Check("重讀：改過的重連", plan.Connect.SequenceEqual(["b"]));
    Check("重讀：被刪掉和停用的斷線", plan.Remove.OrderBy(x => x).SequenceEqual(["c", "z"]));

    current["a"] = (a.Fingerprint, false);
    var retry = ReloadPlan.Compute(current, cfg.Servers);
    Check("重讀：之前連不上的會重連", retry.Connect.Contains("a") && !retry.Keep.Contains("a"));

    var fresh = ReloadPlan.Compute(new Dictionary<string, (string, bool)>(), cfg.Servers);
    Check("重讀：第一次全部連線（停用的除外）", fresh.Connect.OrderBy(x => x).SequenceEqual(["a", "b"]));
});

// ---- McpGateway：整理給 McpBridge 的工具清單 ----
Section("工具清單", () =>
{
    var cfg = GatewayConfig.Parse(CONFIG_JSON, k => testEnv.GetValueOrDefault(k));
    var a = cfg.Servers.Single(s => s.Name == "a");
    var b = cfg.Servers.Single(s => s.Name == "b");
    var schema = Json("""{"type":"object"}""");

    IReadOnlyList<RemoteTool> aTools = [new("echo", "Echo", schema), new("add", null, schema)];
    IReadOnlyList<RemoteTool> bTools = [new("t1", "one", schema), new("t2", "two", schema)];
    var table = ToolTable.Build([(a, aTools), (b, bTools)]);

    Check("清單：名稱加上 server 前綴",
        table.Tools.Select(t => t.Name).OrderBy(x => x).SequenceEqual(["a__add", "a__echo", "b__t1"]));
    Check("清單：enabledTools 以外的工具不列出", table.Tools.All(t => t.Name != "b__t2"));
    Check("清單：帶上 server 的設定",
        table.Tools.Single(t => t.Name == "b__t1") is { DataTool: false, Server: "b", OriginalName: "t1" });
    Check("清單：toolPerms 蓋過 requiredPerm", table.Tools.Single(t => t.Name == "b__t1").RequiredPerm == "BotOwner");
    Check("清單：沒寫 toolPerms 的工具用 requiredPerm", table.Tools.Single(t => t.Name == "a__echo").RequiredPerm is null);
    Check("清單：enabledTools 關掉的工具記下來", table.Hidden["b"].SequenceEqual(["t2"]) && !table.Hidden.ContainsKey("a"));
    Check("清單：對不到工具的 toolPerms 記下來", table.UnknownToolPerms["b"].SequenceEqual(["t9"]) && !table.UnknownToolPerms.ContainsKey("a"));
    Check("清單：沒有說明時給空字串", table.Tools.Single(t => t.Name == "a__add").Description == "");
    Check("清單：用 AI 的名稱找回原本的工具", table.TryResolve("a__echo", out var srv, out var orig) && srv == "a" && orig == "echo");
    Check("清單：找不到的名稱回 false", !table.TryResolve("a__nope", out _, out _));

    var same = ToolTable.Build([(a, aTools), (b, bTools)]);
    Check("清單：內容一樣 etag 一樣", same.Etag == table.Etag);
    IReadOnlyList<RemoteTool> aChanged = [new("echo", "Echo v2", schema), new("add", null, schema)];
    Check("清單：說明改了 etag 就變", ToolTable.Build([(a, aChanged), (b, bTools)]).Etag != table.Etag);
    Check("清單：空清單的 etag 不一樣", ToolTable.Empty.Etag != table.Etag);

    var dupCfg = GatewayConfig.Parse("""{"mcpServers":{"x.y":{"command":"a"},"x_y":{"command":"b"}}}""", _ => null);
    IReadOnlyList<RemoteTool> one = [new("t", "d", schema)];
    var dup = ToolTable.Build(dupCfg.Servers.Select(s => (s, one)));
    Check("清單：整理後同名的工具只留一個", dup.Tools.Count == 1 && dup.Tools[0].Name == "x_y__t");
    Check("清單：被丟掉的工具要記下來", dup.Dropped.Count == 1 && dup.Dropped[0].Contains("x_y__t"));
    Check("清單：沒撞名時不記", table.Dropped.Count == 0);
    Check("清單：撞名沒列出的工具也算關掉的", dup.Hidden["x_y"].SequenceEqual(["t"]));
});

// ---- McpBridge：.mcptools 的輸出 ----
Section("工具權限列表", () =>
{
    var schema = Json("""{"type":"object"}""");
    var list = new ToolListResponse(
        "e",
        [
            new ToolInfo("qdrant__qdrant-find", "qdrant-find", "qdrant", "find", schema, true, "BotOwner"),
            new ToolInfo("qdrant__qdrant-store", "qdrant-store", "qdrant", "store", schema, false, "Typo"),
            new ToolInfo("fetch__fetch", "fetch", "fetch", "fetch", schema, true, null),
        ],
        [
            new ServerStatus("fetch", true, 1, null, [], []),
            new ServerStatus("qdrant", true, 2, null, ["qdrant-delete"], ["qdrant-fnd"]),
        ],
        null);

    var registered = new HashSet<string> { "qdrant__qdrant-find", "fetch__fetch" };
    var q = McpBridgeModule.FormatTools(list, "QDRANT", registered);
    Check("列表：跟內建工具撞名、沒登記的工具要標出來",
        q.Split("\n- ").Any(e => e.StartsWith("`qdrant-store`") && e.Contains("沒有登記"))
        && q.Split("\n- ").Any(e => e.StartsWith("`qdrant-find`") && !e.Contains("沒有登記")));
    Check("列表：server 名稱不分大小寫，只列那個 server", q.Contains("qdrant-find") && !q.Contains("fetch__fetch"));
    Check("列表：顯示權限", q.Contains("bot owner"));
    Check("列表：權限寫錯要警告", q.Contains("Typo") && q.Contains("⚠️"));
    Check("列表：顯示被關掉的工具", q.Contains("qdrant-delete"));
    Check("列表：顯示對不到工具的 toolPerms", q.Contains("qdrant-fnd"));

    var all = McpBridgeModule.FormatTools(list, null, registered);
    Check("列表：沒給名稱時列出所有 server", all.Contains("fetch") && all.Contains("qdrant"));
    Check("列表：找不到 server 時列出所有 server", McpBridgeModule.FormatTools(list, "nope", registered).Contains("qdrant"));
});

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
return failures;

/// <summary>只用來測試登記與移除，不會被執行。</summary>
internal sealed class FakeTool(string name, bool isData) : IAiTool
{
    public string Name => name;
    public string Description => name;
    public JsonElement ParameterSchema { get; } = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
    public bool IsDataTool => isData;

    public Task<string> ExecuteAsync(AiToolContext context, JsonElement arguments)
        => Task.FromResult("");
}

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
