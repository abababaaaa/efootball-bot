<#
.SYNOPSIS
  把 XInput 代理投放/卸载到目标游戏目录（只动游戏自己的 Binaries\Win64，不碰 System32）。

.DESCRIPTION
  默认只做 DryRun（列出将要写入的路径与文件哈希）。真正写入加 -Install；
  卸载加 -Remove（删掉代理与 ini，恢复 .bak 备份如果有）。

  为什么按目录投放而不是全局：只有该游戏进程会加载这个 DLL，其他应用调用的是
  System32\xinput1_3.dll 本体 —— 这是"不串前台"的物理保证。

.EXAMPLE
  .\install.ps1                       # 预览
  .\install.ps1 -Install              # 投放国际服（自动探测 Steam 库）
  .\install.ps1 -Install -TargetDir 'D:\...\Binaries\Win64' -ProcessName eFootballOnline.exe -TargetId 2
  .\install.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Remove,
    # 会写进游戏目录：必须显式确认，防止顺手装到自己不想装的游戏里
    [switch]$ConfirmProxy,
    [string]$TargetDir,
    [string]$ProcessName = 'eFootball.exe',
    [int]$TargetId = 1,
    [string]$SharedName = 'Local\EfootballBot.Pad.v1'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDll = Join-Path $here 'xinput1_3.dll'
if (-not (Test-Path $srcDll)) { Write-Host "缺少 $srcDll（先构建）" -ForegroundColor Red; exit 2 }
$hash = (Get-FileHash $srcDll -Algorithm SHA256).Hash

function Find-GameDirs {
    $dirs = @()
    $steamRoots = @('C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf') | Where-Object { Test-Path $_ }
    $libraries = @()
    foreach ($vdf in $steamRoots) {
        $libraries += (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches).Matches |
            ForEach-Object { $_.Groups[1].Value.Replace('\\', '\') }
    }
    $libraries += 'D:\SteamLibrary', 'E:\SteamLibrary', 'C:\SteamLibrary'
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $candidate = Join-Path $lib 'steamapps\common\eFootball\eFootball\eFootball\Binaries\Win64'
        if (Test-Path $candidate) { $dirs += $candidate }
    }
    return $dirs | Select-Object -Unique
}

if (-not $TargetDir) {
    $found = @(Find-GameDirs)
    if ($found.Count -eq 0) { Write-Host "未探测到 eFootball 目录，请用 -TargetDir 指定" -ForegroundColor Red; exit 2 }
    $TargetDir = $found[0]
    Write-Host ("探测到游戏目录：{0}" -f $TargetDir) -ForegroundColor Cyan
}

if (-not (Test-Path $TargetDir)) { Write-Host "目录不存在：$TargetDir" -ForegroundColor Red; exit 2 }

$dstDll = Join-Path $TargetDir 'xinput1_3.dll'
$dstIni = Join-Path $TargetDir 'xinput_proxy.ini'
$iniText = @"
[proxy]
TargetProcess=$ProcessName
TargetId=$TargetId
SharedName=$SharedName
Enable=1
Log=1
CounterHook=1
HeartbeatTimeoutMs=1500
"@

Write-Host ("目标目录 : {0}" -f $TargetDir)
Write-Host ("DLL      : {0}  (SHA256 {1})" -f $dstDll, $hash.Substring(0, 16))

if ($Remove) {
    foreach ($f in @($dstDll, $dstIni)) {
        if (Test-Path $f) { Remove-Item $f -Force; Write-Host ("已删除 " + $f) -ForegroundColor Green }
    }
    foreach ($bak in @("$dstDll.bak", "$dstIni.bak")) {
        $orig = $bak -replace '\.bak$', ''
        if (Test-Path $bak) { Move-Item $bak $orig -Force; Write-Host ("已恢复 " + $orig) -ForegroundColor Green }
    }
    exit 0
}

if (-not $Install) {
    Write-Host "DryRun：将写入 DLL 与 ini（加 -Install -ConfirmProxy 生效）" -ForegroundColor Yellow
    Write-Host $iniText
    exit 0
}

if (-not $ConfirmProxy) {
    Write-Host "拒绝投放：该脚本会写入游戏目录，必须显式加 -ConfirmProxy 确认。" -ForegroundColor Red
    Write-Host "用法：.\install.ps1 -Install -ConfirmProxy [-TargetDir ...] [-ProcessName ...] [-TargetId 2]" -ForegroundColor Yellow
    exit 3
}

if (Test-Path $dstDll) { Copy-Item $dstDll "$dstDll.bak" -Force }
if (Test-Path $dstIni) { Copy-Item $dstIni "$dstIni.bak" -Force }

Copy-Item $srcDll $dstDll -Force
[IO.File]::WriteAllText($dstIni, $iniText, (New-Object Text.UTF8Encoding($false)))
Write-Host "已投放代理。下次启动游戏生效；日志见同目录 xinput_proxy.log" -ForegroundColor Green
Write-Host "自检：启动游戏后 proxyCalls 会增长，joy.cpl 里不会多出手柄。" -ForegroundColor Green