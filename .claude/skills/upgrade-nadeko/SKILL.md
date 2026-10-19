---
name: upgrade-nadeko
description: 把外掛升級到新版 NadekoBot。用法：/upgrade-nadeko <tag>，例如 /upgrade-nadeko 7.7.0
disable-model-invocation: true
---

目標版本：`$ARGUMENTS`。沒有給版本就先問使用者。

建置規則與驗證指令以 `AGENTS.md` 為準，這裡只寫升級的順序。

## 1. 記下舊版本

在建置之前執行，建置會覆蓋舊 dll：

```powershell
(Get-Item nadekobot/src/NadekoBot/bin/Release/net9.0/NadekoBot.dll).VersionInfo.ProductVersion
```

`+` 後面是舊 commit，記為 `<old>`。dll 不存在時，問使用者目前跑的是哪一版。

完成條件：手上有 `<old>`。

## 2. 切到新版並建置

```powershell
git -C nadekobot fetch --tags
git -C nadekobot checkout $ARGUMENTS
dotnet build Nadeko-Medusa.sln -c Release
```

`nadekobot/` 的 gitlink 沒有被追蹤，切換 tag 不會出現在外層 repo 的 diff。新 commit 記為 `<new>`（`git -C nadekobot rev-parse HEAD`）。

完成條件：每個外掛 dll 參考的 NadekoBot 都是新版。

```powershell
foreach ($n in (Get-ChildItem -Directory | ? { (Test-Path "$($_.Name)/$($_.Name).csproj") -and (Test-Path "$($_.Name)/strings/cmds") }).Name) {
  $p = (Resolve-Path "$n/bin/Release/net9.0/$n.dll").Path
  "$n -> $(([Reflection.Assembly]::LoadFile($p).GetReferencedAssemblies() | ? Name -eq 'NadekoBot').Version)"
}
```

沒用到 bot 型別的外掛不會有這個參考，結果是空的，這是正常的。

## 3. 比對 bot 的變動

編譯通過不代表沒事。每一項都要查完：

- **套件版本**：`git -C nadekobot diff <old> <new> -- src/NadekoBot/NadekoBot.csproj src/Nadeko.Medusa`。有變就同步外掛的 `PackageReference`。
- **Medusa 載入器**：`git -C nadekobot diff <old> <new> -- src/NadekoBot/_common/Medusa`。注意會讓外掛不被載入的條件，例如 7.6 的 `BotPolicy.Managed`。
- **回傳型別**：列出改過的公開方法簽章，再到外掛資料夾搜尋每個方法名稱。

  ```powershell
  git -C nadekobot diff -U0 <old> <new> -- src/NadekoBot src/Nadeko.Medusa | Select-String '^-\s+(public|Task|ValueTask).*\('
  ```

  `Task` 改成 `Task<T>` 時，忽略回傳值的呼叫照樣能編譯。7.6 的 `AddReminderAsync` 就是這樣：達到上限時回傳 false，外掛卻照樣回「已設定提醒」。
- **注入的服務**：外掛建構式注入的每個 bot 型別，看 diff 裡有沒有改到外掛呼叫的那幾個方法的行為。

完成條件：外掛用到的每個 bot 方法都對照過 diff，每個都標成「沒變」、「已修」或「列進 Found」。

## 4. 驗證

跑 `AGENTS.md` 的測試和 `./stage-medusae.ps1`。完成條件：測試 0 失敗、打包結束碼 0，兩個的真實輸出都貼進回報。

## 5. 回報

照使用者的長任務格式（Blocked on me／Changed／Found）。另外要寫：

- 沒有在真的 bot 上載入測試。
- 新版有沒有把任何設定類別的 `Version` 加一（例如 `BotConfig`）。有的話，bot 第一次啟動會重寫 `data/*.yml`，寫回時不會幫 `~`、`null` 這類字串加引號，下次啟動會讀成 null。7.6.6 的 `prefix: '~'` 就因此變成空的。請使用者升級後檢查這類設定。
