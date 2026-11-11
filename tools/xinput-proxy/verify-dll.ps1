<#
.SYNOPSIS
  校验 xinput1_3.dll 的导出/序号/导入（不依赖 dumpbin，本机无 MSVC 也能跑）。

.DESCRIPTION
  eFootball.exe 按序号导入 #2/#3；序号错位 = 游戏加载阶段直接退出。
  本脚本解析 PE 导出表，逐个比对 System32\xinput1_3.dll 的序号布局，并打印导入表
  （确认有没有意外拖进依赖）。

.EXAMPLE
  .\verify-dll.ps1 -Path .\xinput1_3.dll
#>
param(
    [string]$Path = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'xinput1_3.dll')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Path)) { Write-Host "missing: $Path" -ForegroundColor Red; exit 2 }
$Path = (Resolve-Path $Path).Path

function Get-PeInfo([string]$file) {
    $fs = [IO.File]::OpenRead($file); $br = New-Object IO.BinaryReader($fs)
    $fs.Position = 0x3C; $peOff = $br.ReadInt32(); $fs.Position = $peOff + 4
    $br.ReadUInt16() | Out-Null; $numSec = $br.ReadUInt16()
    $br.ReadUInt32() | Out-Null; $br.ReadUInt32() | Out-Null; $br.ReadUInt32() | Out-Null
    $optSize = $br.ReadUInt16(); $br.ReadUInt16() | Out-Null
    $optStart = $fs.Position; $magic = $br.ReadUInt16()
    $dirBase = if ($magic -eq 0x20B) { $optStart + 112 } else { $optStart + 96 }
    $fs.Position = $dirBase; $expRva = $br.ReadUInt32(); $br.ReadUInt32() | Out-Null
    $fs.Position = $dirBase + 8; $impRva = $br.ReadUInt32(); $br.ReadUInt32() | Out-Null
    $secStart = $optStart + $optSize; $secs = @()
    for ($i = 0; $i -lt $numSec; $i++) {
        $fs.Position = $secStart + $i * 40
        $name = [Text.Encoding]::ASCII.GetString($br.ReadBytes(8)).Trim([char]0)
        $vsize = $br.ReadUInt32(); $vaddr = $br.ReadUInt32(); $rsize = $br.ReadUInt32(); $rptr = $br.ReadUInt32()
        $secs += [pscustomobject]@{ N = $name; VA = $vaddr; VS = $vsize; RS = $rsize; RP = $rptr }
    }
    function R2O([uint32]$rva) {
        foreach ($s in $secs) { if ($rva -ge $s.VA -and $rva -lt ($s.VA + $s.RS)) { return [int]($s.RP + ($rva - $s.VA)) } }
        return -1
    }

    # ---- exports ----
    $exports = @()
    if ($expRva -ne 0) {
        $fs.Position = R2O $expRva
        $br.ReadUInt32() | Out-Null; $br.ReadUInt32() | Out-Null; $br.ReadUInt16() | Out-Null; $br.ReadUInt16() | Out-Null
        $nameRva = $br.ReadUInt32(); $base = $br.ReadUInt32(); $nf = $br.ReadUInt32(); $nn = $br.ReadUInt32()
        $funcRva = $br.ReadUInt32(); $namesRva = $br.ReadUInt32(); $ordsRva = $br.ReadUInt32()
        $fo = R2O $namesRva; $oo = R2O $ordsRva
        for ($i = 0; $i -lt $nn; $i++) {
            $fs.Position = $fo + $i * 4; $nr = $br.ReadUInt32(); $no = R2O $nr; $save = $fs.Position
            $fs.Position = $no; $sb = New-Object System.Collections.Generic.List[byte]
            while ($true) { $b = $br.ReadByte(); if ($b -eq 0) { break }; $sb.Add($b) }
            $nm = [Text.Encoding]::ASCII.GetString($sb.ToArray()); $fs.Position = $save
            $fs.Position = $oo + $i * 2; $idx = $br.ReadUInt16()
            $exports += [pscustomobject]@{ Ordinal = [int]($base + $idx); Name = $nm }
        }
        # 无名导出（NONAME）：从 EAT 反推
        $eat = R2O $funcRva
        for ($i = 0; $i -lt $nf; $i++) {
            $fs.Position = $eat + $i * 4; $rva = $br.ReadUInt32()
            if ($rva -eq 0) { continue }
            $ord = [int]($base + $i)
            if (-not ($exports | Where-Object { $_.Ordinal -eq $ord })) {
                $exports += [pscustomobject]@{ Ordinal = $ord; Name = '(NONAME)' }
            }
        }
    }

    # ---- imports ----
    $imports = @()
    if ($impRva -ne 0) {
        $fs.Position = R2O $impRva
        for ($i = 0; $i -lt 200; $i++) {
            $ilt = $br.ReadUInt32(); $ts = $br.ReadUInt32(); $fc = $br.ReadUInt32(); $nr = $br.ReadUInt32(); $iat = $br.ReadUInt32()
            if ($nr -eq 0) { break }
            $no = R2O $nr; $save = $fs.Position; $fs.Position = $no
            $sb = New-Object System.Collections.Generic.List[byte]
            while ($true) { $b = $br.ReadByte(); if ($b -eq 0) { break }; $sb.Add($b) }
            $imports += [Text.Encoding]::ASCII.GetString($sb.ToArray()); $fs.Position = $save
        }
    }

    $br.Close(); $fs.Close()
    [pscustomobject]@{ Exports = $exports; Imports = $imports }
}

$info = Get-PeInfo $Path
Write-Host "== $Path ==" -ForegroundColor Cyan
Write-Host ("imports: " + (($info.Imports | Sort-Object -Unique) -join ', '))

$expected = [ordered]@{
    2 = 'XInputGetState'
    3 = 'XInputSetState'
    4 = 'XInputGetCapabilities'
    5 = 'XInputEnable'
    6 = 'XInputGetDSoundAudioDeviceGuids'
    7 = 'XInputGetBatteryInformation'
    8 = 'XInputGetKeystroke'
}

$fail = 0
foreach ($kv in $expected.GetEnumerator()) {
    $hit = $info.Exports | Where-Object { $_.Ordinal -eq [int]$kv.Key -and $_.Name -eq $kv.Value }
    if ($hit) { Write-Host ("  OK   #{0,-3} {1}" -f $kv.Key, $kv.Value) -ForegroundColor Green }
    else {
        $actual = ($info.Exports | Where-Object { $_.Ordinal -eq [int]$kv.Key } | Select-Object -First 1)
        Write-Host ("  FAIL #{0,-3} 期望 {1}，实际 {2}" -f $kv.Key, $kv.Value, $(if ($actual) { $actual.Name } else { '<无>' })) -ForegroundColor Red
        $fail++
    }
}

foreach ($ord in 100, 101, 102, 103) {
    $hit = $info.Exports | Where-Object { $_.Ordinal -eq $ord }
    if ($hit) { Write-Host ("  OK   #{0,-3} (NONAME)" -f $ord) -ForegroundColor Green }
    else { Write-Host ("  WARN #{0,-3} 缺未文档化导出" -f $ord) -ForegroundColor Yellow }
}

Write-Host ""
if ($fail -eq 0) { Write-Host "PASS: 序号布局与 System32\\xinput1_3.dll 一致" -ForegroundColor Green; exit 0 }
Write-Host "FAIL: $fail 项序号不匹配" -ForegroundColor Red; exit 1
