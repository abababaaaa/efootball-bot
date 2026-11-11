# 构建并运行 XInput 代理自测（不需要游戏、不需要 MSVC）
# 覆盖：正常 ini / 带 BOM 的 ini（部署脚本常见坑）/ 非白名单进程（不串台）/ ABI 偏移
param(
    [string]$Gcc = 'B:\Efootball挂机软件\tools\_toolchain\w64devkit\w64devkit\bin\gcc.exe'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$run = Join-Path $here 'run'
Remove-Item $run -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $run | Out-Null

if (-not (Test-Path $Gcc)) { Write-Host "缺少 gcc: $Gcc（用 -Gcc 指定）" -ForegroundColor Red; exit 2 }

Write-Host "== 构建 selftest ==" -ForegroundColor Cyan
& $Gcc -O2 -Wall -o (Join-Path $run 'pad_proxy_selftest.exe') (Join-Path $here 'selftest.c') -lkernel32 -luser32
if ($LASTEXITCODE -ne 0) { Write-Host "编译失败" -ForegroundColor Red; exit 2 }

Copy-Item (Join-Path $root 'xinput1_3.dll') $run

$ini = @"
[proxy]
TargetProcess=pad_proxy_selftest.exe
TargetId=1
SharedName=Local\EfootballBot.Pad.v1
Enable=1
Log=1
CounterHook=1
HeartbeatTimeoutMs=1500
"@

# 模拟 Steam overlay：DLL 放在"游戏目录"，调用方 exe 在别的目录
$foreignDir = Join-Path $run 'foreign-proxy'
New-Item -ItemType Directory -Force -Path $foreignDir | Out-Null
Copy-Item (Join-Path $root 'xinput1_3.dll') (Join-Path $foreignDir 'xinput1_3.dll') -Force
$foreignIni = $ini
[IO.File]::WriteAllText((Join-Path $foreignDir 'xinput_proxy.ini'), $foreignIni, (New-Object Text.UTF8Encoding($false)))
$foreignDll = Join-Path $foreignDir 'xinput1_3.dll'

$runs = @(
    @{ Name = 'ini 无 BOM（正常路径）'; Text = $ini; Args = @() },
    @{ Name = 'ini 带 BOM（部署脚本常见坑，代理自愈）'; Text = ([string][char]0xFEFF) + $ini; Args = @() },
    @{ Name = '非白名单进程（不串台）'; Text = $ini.Replace('TargetProcess=pad_proxy_selftest.exe', 'TargetProcess=not_me.exe'); Args = @('--expect-passthrough') },
    @{ Name = 'DLL 在游戏目录、调用方在别处（模拟 Steam overlay）'; Text = $ini; Args = @("--expect-foreign-caller", "--dll=$foreignDll") }
)

$code = 0
$abiLine = $null
foreach ($case in $runs) {
    Write-Host ("== 运行：" + $case.Name + " ==") -ForegroundColor Cyan
    [IO.File]::WriteAllText((Join-Path $run 'xinput_proxy.ini'), $case.Text, (New-Object Text.UTF8Encoding($false)))
    Push-Location $run
    $out = & (Join-Path $run 'pad_proxy_selftest.exe') @($case.Args) 2>&1
    $rc = $LASTEXITCODE
    Pop-Location
    $out | ForEach-Object { Write-Host $_ }
    if ($null -eq $abiLine) { $abiLine = ($out | Where-Object { "$_" -like 'ABI size=*' } | Select-Object -First 1) }
    if ($rc -ne 0) { $code = $rc }
}

Write-Host "== ABI 偏移断言（C 侧 vs C# SharedPadWriter） ==" -ForegroundColor Cyan
$expected = 'ABI size=104 seq=12 hb=20 flags=32 mask=36 buttons=40 lt=42 rt=43 lx=44 ly=46 rx=48 ry=50 seen=52 calls=64'
if ($abiLine -and ("$abiLine".Trim() -eq $expected)) {
    Write-Host ("  PASS  " + "$abiLine".Trim()) -ForegroundColor Green
} else {
    Write-Host ("  FAIL  期望: " + $expected) -ForegroundColor Red
    Write-Host ("        实际: " + "$abiLine") -ForegroundColor Red
    $code = 1
}

$log = Join-Path $run 'xinput_proxy.log'
if (Test-Path $log) { Write-Host "== 代理日志 ==" -ForegroundColor Cyan; Get-Content $log -Encoding utf8 | Select-Object -Last 6 }
Write-Host ("exit=" + $code)
exit $code