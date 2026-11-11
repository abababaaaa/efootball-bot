<#
.SYNOPSIS
  手柄设备节点卫生：识别真实手柄，仅在“未开启真实手柄”时卸载并重载模拟手柄节点。

.DESCRIPTION
  需求来源：完全后台模拟手柄方案的第 1 条——先判断用户是否开着真实手柄；
  只有“没有真实手柄在用”时，才在设备管理器层面把 ViGEm 虚拟手柄的（残留）节点
  卸载掉再重新加载，保证新会话拿到干净的 XInput 玩家 1。
  真实手柄在用 → 一律只读，不动任何设备（避免干扰用户硬件）。

  判定依据（实测，2026-11-11）：
    · 真实手柄  : Status=Started 且父链不含 ViGEmBus
    · 虚拟残留  : 父链祖先 Service=ViGEmBus（本机 ROOT\SYSTEM\0001）且 Status≠Started
    · 虚拟在线  : 父链祖先 Service=ViGEmBus 且 Status=Started

.PARAMETER Apply
  真正执行卸载 + 重扫。缺省是只读 DryRun。

.PARAMETER RestartBus
  Apply 后额外重启 ViGEmBus 根设备，强制重新枚举。

.PARAMETER Force
  即使检测到真实手柄也执行（默认禁止）。

.EXAMPLE
  .\PadNodeHygiene.ps1
  .\PadNodeHygiene.ps1 -Apply -RestartBus   # 管理员
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$RestartBus,
    [switch]$Force,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrWhiteSpace($here)) { $here = (Get-Location).Path }
    $LogPath = Join-Path $here 'pad-node-hygiene.log'
}
$script:log = New-Object System.Collections.Generic.List[string]

function Write-Log([string]$msg, [string]$level = 'INFO') {
    $line = "[{0}] {1,-5} {2}" -f (Get-Date -Format 'HH:mm:ss'), $level, $msg
    $script:log.Add($line)
    switch ($level) {
        'OK'   { Write-Host $line -ForegroundColor Green }
        'WARN' { Write-Host $line -ForegroundColor Yellow }
        'ERR'  { Write-Host $line -ForegroundColor Red }
        default { Write-Host $line }
    }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DevProp([string]$instanceId, [string]$key) {
    (Get-PnpDeviceProperty -InstanceId $instanceId -KeyName $key -ErrorAction SilentlyContinue).Data
}

function Get-Ancestors([string]$instanceId, [int]$depth = 4) {
    $chain = @(); $cur = $instanceId
    for ($i = 0; $i -lt $depth; $i++) {
        $parent = Get-DevProp $cur 'DEVPKEY_Device_Parent'
        if (-not $parent) { break }
        $chain += $parent
        $cur = $parent
    }
    return $chain
}

function Test-VirtualBusDescendant([string]$instanceId) {
    if ((Get-DevProp $instanceId 'DEVPKEY_Device_Service') -eq 'ViGEmBus') { return $true }
    foreach ($a in Get-Ancestors $instanceId) {
        if ((Get-DevProp $a 'DEVPKEY_Device_Service') -eq 'ViGEmBus') { return $true }
        if ($a -match 'VIGEMBUS') { return $true }
    }
    return $false
}

function Get-PadNodes {
    $patterns = 'game controller|Xbox.*controller|手柄|Gamepad'
    Get-PnpDevice -Class XnaComposite, HIDClass -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -and $_.FriendlyName -match $patterns } |
        ForEach-Object {
            [pscustomobject]@{
                InstanceId = $_.InstanceId
                Name       = $_.FriendlyName
                Class      = $_.Class
                # Get-PnpDevice 的 Status 枚举是 OK/Error/Degraded/Unknown；
                # pnputil 显示成 Started 的仅仅是 Status=OK 且 Present=True。
                Started    = ($_.Status -eq 'OK' -and $_.Present)
                Status     = $_.Status
                Present    = $_.Present
                # 真实总线枚举（USB 有线 / HID 蓝牙 / BTHENUM）；SWD\、ROOT\、VHF\ 都是软件枚举
                RealBus    = ($_.InstanceId -cmatch '^(USB|HID|BTHENUM|BTHLE|BTHHFENUM)\\')
                IsVirtual  = (Test-VirtualBusDescendant $_.InstanceId)
            }
        }
}

function Invoke-Pnputil([string[]]$argList) {
    $out = & pnputil @argList 2>&1
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($out -join [Environment]::NewLine) }
}

# ---------------- 主流程 ----------------
$mode = if ($Apply) { 'APPLY' } else { 'DRY-RUN' }
Write-Log ("=== 手柄节点卫生 ({0}) ===" -f $mode)
$admin = Test-Admin
Write-Log ("管理员权限: {0}" -f $admin)

$nodes = @(Get-PadNodes)
if ($nodes.Count -eq 0) { Write-Log "未发现任何手柄类设备节点" 'WARN' }

$physical     = @($nodes | Where-Object { -not $_.IsVirtual -and $_.Started -and $_.RealBus })
$virtualStale = @($nodes | Where-Object { $_.IsVirtual -and -not $_.Present })
$virtualLive  = @($nodes | Where-Object { $_.IsVirtual -and $_.Started })
$otherVirtual = @($nodes | Where-Object { -not $_.IsVirtual -and $_.Started -and -not $_.RealBus })

Write-Log ("真实手柄(启用中): {0}" -f $physical.Count)
$physical | ForEach-Object { Write-Log ("    · {0}  [{1}]  {2}" -f $_.Name, $_.Status, $_.InstanceId) }
Write-Log ("虚拟手柄残留节点: {0}" -f $virtualStale.Count)
$virtualStale | ForEach-Object { Write-Log ("    · {0}  [{1}]  {2}" -f $_.Name, $_.Status, $_.InstanceId) }
Write-Log ("虚拟手柄在线节点: {0}" -f $virtualLive.Count)
if ($otherVirtual.Count -gt 0) {
    Write-Log ("其他虚拟手柄（非 ViGEm，同样会抢 slot0）: {0}" -f $otherVirtual.Count) 'WARN'
    $otherVirtual | ForEach-Object { Write-Log ("    · {0}  [{1}]  {2}" -f $_.Name, $_.Status, $_.InstanceId) 'WARN' }
}

# 需求 1 的闸门：真实手柄在用 → 不动设备
if ($physical.Count -gt 0 -and -not $Force) {
    Write-Log "真实手柄处于启用状态 → 不做任何设备卸载/重载（需求：仅在未开启真实手柄时执行）" 'WARN'
    Write-Log "挂机时正确路径：走 XInput 进程内代理（tools/xinput-proxy），真实手柄原地不动" 'INFO'
    $script:log | Set-Content -Path $LogPath -Encoding utf8 -ErrorAction SilentlyContinue
    return
}

if ($virtualStale.Count -eq 0) {
    Write-Log "没有残留虚拟手柄节点，无需卸载" 'OK'
} elseif (-not $Apply) {
    Write-Log "将依次卸载以上节点，然后 pnputil /scan-devices 重新枚举" 'WARN'
} else {
    if (-not $admin) {
        Write-Log "APPLY 需要管理员权限，当前进程未提权 → 中止" 'ERR'
        $script:log | Set-Content -Path $LogPath -Encoding utf8 -ErrorAction SilentlyContinue
        throw '需要管理员权限'
    }
    foreach ($n in $virtualStale) {
        $r = Invoke-Pnputil @('/remove-device', $n.InstanceId)
        if ($r.Exit -eq 0) { Write-Log ("已卸载: " + $n.InstanceId) 'OK' }
        else { Write-Log ("卸载失败({0}): {1} :: {2}" -f $r.Exit, $n.InstanceId, $r.Text) 'ERR' }
    }
    $r = Invoke-Pnputil @('/scan-devices')
    $lvl = if ($r.Exit -eq 0) { 'OK' } else { 'ERR' }
    Write-Log ("重新扫描设备: exit={0}" -f $r.Exit) $lvl

    if ($RestartBus) {
        $bus = Get-PnpDevice | Where-Object {
            $_.InstanceId -like 'ROOT\*' -and (Get-DevProp $_.InstanceId 'DEVPKEY_Device_Service') -eq 'ViGEmBus'
        } | Select-Object -First 1
        if ($bus) {
            $r = Invoke-Pnputil @('/restart-device', $bus.InstanceId)
            $lvl2 = if ($r.Exit -eq 0) { 'OK' } else { 'ERR' }
            Write-Log ("重启 ViGEmBus 根设备 {0}: exit={1}" -f $bus.InstanceId, $r.Exit) $lvl2
        } else {
            Write-Log "未找到 ViGEmBus 根设备" 'WARN'
        }
    }
}

$after = @(Get-PadNodes)
$left = @($after | Where-Object { $_.IsVirtual -and -not $_.Present }).Count
Write-Log ("复检：节点总数 {0}，残留 {1}" -f $after.Count, $left) 'OK'

$script:log | Set-Content -Path $LogPath -Encoding utf8 -ErrorAction SilentlyContinue
