#!/usr/bin/env pwsh
<#
.SYNOPSIS
    把各 Medusa 模組的 Release 產物集中到 bin/medusae，依模組名稱分資料夾。

.DESCRIPTION
    各模組的 bin/Release/<TargetFramework> 只包含模組自己的檔案（相依組件由 bot 提供），
    本腳本只複製這些檔案到 bin/medusae/<模組名稱>/，可直接對應到 bot 的 data/medusae/。

    模組判定方式：資料夾內有 <名稱>.csproj，且具備指令字串（cmds.yml 或 strings/cmds/）。
    目標框架從各模組 csproj 的 <TargetFramework> 讀取，不寫死版本，
    升級 .NET 後只要 csproj 改了就會跟著抓對應的輸出資料夾。

    複製的檔案：<名稱>.dll、<名稱>.pdb、<名稱>.deps.json、<名稱>.runtimeconfig.json、
    res.yml，以及 strings/（含 strings/cmds/ 的 .yml，內含一個檔案名稱就是 .yml 的檔案）。
    若來源輸出含有非模組的 .dll，會發出警告（代表相依複製又被打開了）。

.PARAMETER SkipBuild
    跳過 Release 建置，只做複製。

.EXAMPLE
    ./stage-medusae.ps1
    建置後輸出到 bin/medusae

.EXAMPLE
    ./stage-medusae.ps1 -SkipBuild
    不建置，直接把現有產物複製到 bin/medusae
#>
[CmdletBinding()]
param(
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$outputRoot = Join-Path $repoRoot 'bin/medusae'

$modules = Get-ChildItem -LiteralPath $repoRoot -Directory | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_.FullName "$($_.Name).csproj")) -and
    ((Test-Path -LiteralPath (Join-Path $_.FullName 'cmds.yml')) -or
     (Test-Path -LiteralPath (Join-Path $_.FullName 'strings/cmds')))
} | Sort-Object Name

if (-not $modules) {
    throw "在 $repoRoot 找不到任何 Medusa 模組（需有 <名稱>.csproj 與 cmds.yml 或 strings/cmds/）"
}

# 先讀出各模組的目標框架，才知道產物在哪一個 bin/Release/<tfm> 底下
$frameworks = @{}
foreach ($module in $modules) {
    $csproj = Join-Path $module.FullName "$($module.Name).csproj"
    $project = [xml](Get-Content -LiteralPath $csproj -Raw)
    $tfm = $project.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1

    if (-not $tfm) {
        throw "讀不到 $($module.Name).csproj 的 <TargetFramework>（多目標的 <TargetFrameworks> 不支援）"
    }

    $frameworks[$module.Name] = $tfm
}

if (-not $SkipBuild) {
    Write-Host "建置 Release..." -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'Nadeko-Medusa.sln') -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "建置失敗（dotnet build 結束碼 $LASTEXITCODE），未進行複製"
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

foreach ($module in $modules) {
    $name = $module.Name
    $tfm = $frameworks[$name]
    $source = Join-Path $module.FullName "bin/Release/$tfm"

    if (-not (Test-Path -LiteralPath (Join-Path $source "$name.dll"))) {
        throw "找不到 $name 的產物：$source\$name.dll（csproj 的 TargetFramework 是 $tfm）"
    }

    # .yml（對應 InvariantCulture）必須與 zh-TW.yml 內容一致，否則顯示的說明會是舊的
    $cmdsDir = Join-Path $module.FullName 'strings/cmds'
    $invariantFile = Join-Path $cmdsDir '.yml'
    $sourceFile = Join-Path $cmdsDir 'zh-TW.yml'
    if ((Test-Path -LiteralPath $invariantFile) -and (Test-Path -LiteralPath $sourceFile)) {
        $strip = { param($path) (Get-Content -LiteralPath $path | Where-Object { $_ -notmatch '^\s*#' }) -join "`n" }
        if ((& $strip $invariantFile) -ne (& $strip $sourceFile)) {
            Write-Warning "$name 的 strings/cmds/.yml 與 zh-TW.yml 內容不一致（註解除外）"
            Write-Warning "  請把 zh-TW.yml 複製成 .yml，否則空 locale 顯示的說明會與原文脫節"
        }
    }

    # 目標資料夾整個重做，避免舊版殘留的檔案被一起帶走
    $target = Join-Path $outputRoot $name
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    # 只取模組自己的檔案：<名稱>.* 與 cmds.yml／res.yml
    $files = Get-ChildItem -LiteralPath $source -File | Where-Object {
        $_.Name.StartsWith("$name.", [StringComparison]::Ordinal) -or $_.Name -in 'cmds.yml', 'res.yml'
    }
    $files | Copy-Item -Destination $target

    $stringsCopied = $false
    $strings = Join-Path $source 'strings'
    if (Test-Path -LiteralPath $strings) {
        Copy-Item -LiteralPath $strings -Destination $target -Recurse
        $stringsCopied = $true
    }

    $unexpected = Get-ChildItem -LiteralPath $source -File -Filter *.dll |
        Where-Object { $_.Name -ne "$name.dll" }
    if ($unexpected) {
        Write-Warning "$name 的輸出含有非模組的組件：$(($unexpected.Name) -join ', ')"
        Write-Warning "  檢查 $name.csproj 的 EnableDynamicLoading 與 <Private>false</Private> 是否還在"
    }

    Write-Host "$name ($tfm) -> $target" -ForegroundColor Green
    Write-Host "    $(($files.Name) -join ', ')$(if ($stringsCopied) { ' (+ strings/)' })"
}

# bot 只會讀 outputRoot 下的子資料夾，根目錄的散檔是舊的完整傾印，可以直接刪掉
$stray = Get-ChildItem -LiteralPath $outputRoot -File
if ($stray) {
    Write-Warning "$outputRoot 根目錄還有 $($stray.Count) 個散檔（Medusa 不會讀取），可自行刪除"
}

Write-Host "完成：$($modules.Count) 個模組 -> $outputRoot" -ForegroundColor Cyan
