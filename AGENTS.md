# Nadeko-Medusa

NadekoBot 的 Medusa 外掛集合。每個外掛一個資料夾（`<名稱>/<名稱>.csproj`），由 bot 在執行期以 AssemblyLoadContext 載入。`nadekobot/` 是上游 bot 原始碼。

## 外掛怎麼編譯到 bot

- 外掛用 HintPath 參考 `nadekobot/src/NadekoBot/bin/Release/net9.0/NadekoBot.dll`，不是參考原始碼。bin 裡的 dll 是哪一版，外掛就支援哪一版。查目前版本：`(Get-Item nadekobot/src/NadekoBot/bin/Release/net9.0/NadekoBot.dll).VersionInfo.ProductVersion`，`+` 後面是該版 bot 的 commit。
- sln 只在 Release 建置 bot，Debug 不會。一律用 `dotnet build Nadeko-Medusa.sln -c Release`。
- 每個外掛 csproj 都要有指向 `NadekoBot.csproj` 的 `ProjectReference`（`ReferenceOutputAssembly="False"`、`Private=false`）。它只負責讓 bot 先建好。少了它，平行建置時外掛可能比 bot 先編譯；`--no-incremental` 會先清掉 bot 的 dll，這時一定失敗。新增外掛時照抄現有外掛的 csproj。
- 相依組件由 bot 提供（`EnableDynamicLoading=false`、`Private=false`）。外掛的 `PackageReference` 版本要跟 `nadekobot/src/NadekoBot/NadekoBot.csproj` 一致，否則執行期型別不合。
- `nadekobot/` 在 `.gitmodules` 有登記，但 gitlink 沒有被追蹤，所以 `git status` 會顯示 `?? nadekobot/`。使用者決定暫時不固定版本，不要 `git add nadekobot`。

## McpBridge 與 McpGateway

- `McpGateway/` 是獨立的 sidecar 程序，不是 medusa 模組（沒有 `strings/cmds`，`stage-medusae.ps1` 不會打包它）。它自己帶相依組件，不受上面「相依由 bot 提供」的規則限制。它是 net10.0，其他外掛跟著 bot 用 net9.0。`Nadeko-Medusa.Tests` 參考了它，所以也是 net10.0。部署方式和 `mcp.json` 格式寫在 `McpGateway/README.md`。
- 兩邊共用的 HTTP 協定只寫在 `McpGateway/Contracts.cs`，McpBridge 用 `<Compile Include>` 連結同一個檔案。改協定只改這一個檔案。
- McpBridge 用反射修改 bot 的私有成員，成員清單在 `McpBridge/BotInternals.cs`。bot 升級後，測試裡的「BotInternals：成員都找得到」失敗，就是這些成員改名或改型別了。

## 驗證

- 測試：`dotnet run --project Nadeko-Medusa.Tests -c Release`。這是純 assert 的 console 程式，結束碼等於失敗數。它沒有 mock，從 bot 注入的服務（例如 `RemindService`）測不到，要在回報裡明講。
- 建置後馬上跑測試或打包，可能出現 `NadekoBot.StringsMerger.dll` 被 `.NET Host` 鎖住（MSB3027）。這是上一次建置留下的 MSBuild 常駐程序。先跑 `dotnet build-server shutdown` 再重跑。
- 打包：`./stage-medusae.ps1`，產物在 `bin/medusae/<模組>/`，對應 bot 的 `data/medusae/`。輸出裡出現非模組的 dll 時它會警告，代表相依複製被打開了。
- 本機不能跑 bot。外掛能不能在 bot 裡載入、行為對不對，只能請使用者實測。

## 指令字串

- `strings/cmds/zh-TW.yml` 是原文，`en-US.yml` 是翻譯。
- `strings/cmds/.yml`（空 locale）的內容要跟 `zh-TW.yml` 一樣（註解除外），改 zh-TW 時一起複製過去。`stage-medusae.ps1` 會檢查。
- key 是 C# 方法名稱的小寫，不是指令別名。

## 升級 bot 版本

照 `.claude/skills/upgrade-nadeko/SKILL.md` 的步驟做。使用者也可以直接打 `/upgrade-nadeko <tag>`。
