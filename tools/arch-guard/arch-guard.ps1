<#
.SYNOPSIS
  架构锁：分层规则的构建期强制检查（v7.2：挂机模块 + 工具模块两套）。

.DESCRIPTION
  这不是「建议」，是硬约束：违反任意一条，dotnet build 直接失败。
  规则来源：AGENTS.md / docs/MODULES.md §7、§8。
  单测 ArchitectureTests 会真的跑一次本脚本，保证规则只有一份来源。

  挂机侧 Core/Bot：
    ARCH-MATCH-DEP        共享层 Core/Bot/Match 不许反向依赖模块（Scenarios.*）
    ARCH-SCENARIO-CROSS   模块之间不许引用别的模块的 *Scenario（白名单：国服我的联赛→国际服基类）
    ARCH-ID-LITERAL       模块 id 字面量只许出现在 Core/Bot/Modules
    ARCH-ENGINE-KNOWLEDGE BotEngine.cs 不许出现任何具体场景类型
    ARCH-SCENARIO-CREATE  *Scenario 只许在注册表里 new（工厂唯一入口）
    ARCH-MATCHPLAY-OWNED  场次记账（MatchesDone/KeyMatches）只许在 Core/Bot/Match
    ARCH-IDLE-OWNED       比赛内挂机节奏（IdleTapMin/MaxMs）只许在 Core/Bot/Match
  工具侧 Core/Tools：
    ARCH-TOOL-DEP         工具层不许依赖挂机层（EfootballBot.Core.Bot）
    ARCH-TOOL-ID-LITERAL  工具 id 字面量只许出现在 Core/Tools/ToolIds.cs
    ARCH-TOOL-CREATE      具体工具只许在 ToolCatalog 里实例化
    ARCH-UI-NO-PROCESS    界面层（MainWindow*.cs）不许直接 Process.Start

.PARAMETER SourceRoot
  主工程源码目录（默认相对脚本位置自动推断）。
#>
[CmdletBinding()]
param(
    [string] $SourceRoot = [string]::Empty
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$bs = [string][char]92      # 反斜杠：本脚本一律用 $bs 拼路径，避免转义地狱
$q  = [string][char]34      # 双引号：拼「字面量检测」用

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Join-Path $scriptDir ('..' + $bs + '..' + $bs + 'src' + $bs + 'EfootballBot')
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$violations = New-Object System.Collections.Generic.List[string]

function Norm([string] $p) { return $p.Replace($bs, '/') }

function Add-Violation([string] $rule, [string] $file, [int] $line, [string] $message) {
    $rel = (Norm $file).Substring((Norm $SourceRoot).Length).TrimStart('/')
    $violations.Add(('{0}: {1}:{2}: {3}' -f $rule, $rel, $line, $message))
}

# 注释行不算依赖（模块文档里会互相提到别人的名字）
function Get-CodeLines([string] $file) {
    $n = 0
    foreach ($raw in [System.IO.File]::ReadLines($file)) {
        $n++
        $t = $raw.Trim()
        if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
        [pscustomobject]@{ Line = $n; Text = $raw }
    }
}

$allCs = Get-ChildItem -LiteralPath $SourceRoot -Recurse -Filter *.cs -File |
    Where-Object { (Norm $_.FullName) -notmatch '/(bin|obj)/' }

# ---------- 元数据 ----------
$scenariosRoot = Join-Path $SourceRoot ('Core' + $bs + 'Bot' + $bs + 'Scenarios')
$scenarioTypes = @{}
if (Test-Path -LiteralPath $scenariosRoot) {
    Get-ChildItem -LiteralPath $scenariosRoot -Directory | ForEach-Object {
        $module = $_.Name
        Get-ChildItem -LiteralPath $_.FullName -Recurse -Filter '*Scenario.cs' -File | ForEach-Object {
            $scenarioTypes[$_.BaseName] = $module
        }
    }
}
$allowedCrossRef = @{ 'CnMyLeagueScenario->MyLeagueScenario' = $true }

$moduleIdsFile = Join-Path $SourceRoot ('Core' + $bs + 'Bot' + $bs + 'Modules' + $bs + 'ModuleIds.cs')
$moduleIds = @()
if (Test-Path -LiteralPath $moduleIdsFile) {
    Select-String -LiteralPath $moduleIdsFile -Pattern 'public const string [A-Za-z0-9_]+ = "([^"]+)"' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $moduleIds += $_.Groups[1].Value }
} else {
    $violations.Add('ARCH-ID-LITERAL: Core/Bot/Modules/ModuleIds.cs 不存在（注册表被改坏了？）')
}

$toolIdsFile = Join-Path $SourceRoot ('Core' + $bs + 'Tools' + $bs + 'ToolIds.cs')
$toolIds = @()
if (Test-Path -LiteralPath $toolIdsFile) {
    Select-String -LiteralPath $toolIdsFile -Pattern 'public const string [A-Za-z0-9_]+ = "([^"]+)"' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $toolIds += $_.Groups[1].Value }
} else {
    $violations.Add('ARCH-TOOL-ID-LITERAL: Core/Tools/ToolIds.cs 不存在（工具层被改坏了？）')
}

$toolsDir = Join-Path $SourceRoot ('Core' + $bs + 'Tools')
$toolImpls = @()
if (Test-Path -LiteralPath $toolsDir) {
    $toolImpls = Get-ChildItem -LiteralPath $toolsDir -Filter '*Tool.cs' -File | ForEach-Object { $_.BaseName }
}

# ---------- 逐文件检查 ----------
foreach ($f in $allCs) {
    $full = $f.FullName
    $norm = Norm $full
    $isShared = $norm.Contains('/Core/Bot/Match/')
    $isRegistry = $norm.Contains('/Core/Bot/Modules/')
    $isScenario = $norm.Contains('/Core/Bot/Scenarios/')
    $isTool = $norm.Contains('/Core/Tools/')
    $isToolIds = $norm.EndsWith('/Core/Tools/ToolIds.cs')
    $isToolCatalog = $norm.EndsWith('/Core/Tools/ToolCatalog.cs')
    $isUi = ($norm -match '/MainWindow[^/]*[.]cs$')
    $isServerKeyFile = $norm.EndsWith('/Core/ServerRegion.cs')

    $module = ''
    if ($isScenario) {
        $rel = (Norm $full).Substring((Norm $scenariosRoot).Length).TrimStart('/')
        $parts = $rel -split '/'
        $module = if ($parts.Count -gt 1) { $parts[0] } else { '(root)' }
    }
    $self = $f.BaseName

    foreach ($entry in Get-CodeLines $full) {
        $line = $entry.Line
        $text = $entry.Text

        # ARCH-MATCH-DEP
        if ($isShared -and $text.Contains('EfootballBot.Core.Bot.Scenarios')) {
            Add-Violation 'ARCH-MATCH-DEP' $full $line '共享层引用了模块命名空间（共享件必须自洽）'
        }

        # ARCH-SCENARIO-CROSS
        if ($isScenario) {
            foreach ($type in $scenarioTypes.Keys) {
                $owner = $scenarioTypes[$type]
                if ($type -eq $self -or $owner -eq $module) { continue }
                if ($allowedCrossRef.ContainsKey(($self + '->' + $type))) { continue }
                if ($text.Contains($type)) {
                    Add-Violation 'ARCH-SCENARIO-CROSS' $full $line ('模块「{0}」引用了模块「{1}」的场景类型 {2}' -f $module, $owner, $type)
                }
            }
        }

        # ARCH-ID-LITERAL（模块 id；分区键文件豁免）
        if (-not $isRegistry -and -not $isServerKeyFile) {
            foreach ($id in $moduleIds) {
                if ($text.Contains($q + $id + $q)) {
                    Add-Violation 'ARCH-ID-LITERAL' $full $line ('出现模块 id 字面量 ' + $q + $id + $q + '，请用 ModuleIds 常量')
                }
            }
        }

        # ARCH-MATCHPLAY-OWNED（仅模块层）
        if ($isScenario -and ($text.Contains('Stats.MatchesDone++') -or $text.Contains('Stats.KeyMatches++'))) {
            Add-Violation 'ARCH-MATCHPLAY-OWNED' $full $line '场次记账只许在 Core/Bot/Match/MatchPlay（模块不要自己加计数）'
        }

        # ARCH-IDLE-OWNED（仅模块层：配置定义/GUI 绑定读配置是正当的）
        if ($isScenario -and ($text.Contains('IdleTapMinMs') -or $text.Contains('IdleTapMaxMs'))) {
            Add-Violation 'ARCH-IDLE-OWNED' $full $line '比赛内挂机节奏只许在 MatchPlay.IdleTapAsync（模块不要自己写随机点击）'
        }

        # ARCH-TOOL-DEP
        if ($isTool -and $text.Contains('EfootballBot.Core.Bot')) {
            Add-Violation 'ARCH-TOOL-DEP' $full $line '工具层引用了挂机层（Core.Bot.*）——公共件应下沉到 Core'
        }

        # ARCH-TOOL-ID-LITERAL
        if (-not $isToolIds) {
            foreach ($tid in $toolIds) {
                if ($text.Contains($q + $tid + $q)) {
                    Add-Violation 'ARCH-TOOL-ID-LITERAL' $full $line ('出现工具 id 字面量 ' + $q + $tid + $q + '，请用 ToolIds 常量')
                }
            }
        }

        # ARCH-TOOL-CREATE
        if (-not $isToolCatalog) {
            foreach ($impl in $toolImpls) {
                if ($text.Contains('new ' + $impl + '(')) {
                    Add-Violation 'ARCH-TOOL-CREATE' $full $line ('工具只许在 ToolCatalog 里实例化（出现 new ' + $impl + '）')
                }
            }
        }

        # ARCH-UI-NO-PROCESS
        if ($isUi -and ($text.Contains('Process.Start') -or $text.Contains('ProcessStartInfo'))) {
            Add-Violation 'ARCH-UI-NO-PROCESS' $full $line '界面层不许直接 Process.Start —— 起进程/打开路径归 Core/Tools（走 ToolCatalog）'
        }
    }
}

# ---------- 引擎不许认识具体场景 ----------
$engineFile = Join-Path $SourceRoot ('Core' + $bs + 'Bot' + $bs + 'BotEngine.cs')
if (Test-Path -LiteralPath $engineFile) {
    $n = 0
    foreach ($raw in [System.IO.File]::ReadLines($engineFile)) {
        $n++
        $t = $raw.Trim()
        if ($t.StartsWith('//') -or $t.StartsWith('*')) { continue }
        foreach ($type in $scenarioTypes.Keys) {
            if ($raw.Contains($type)) {
                Add-Violation 'ARCH-ENGINE-KNOWLEDGE' $engineFile $n ('引擎出现具体场景类型 ' + $type + '（应只通过 BotModuleCatalog / IModuleLifecycle）')
            }
        }
    }
}

# ---------- 场景只许在注册表里 new ----------
foreach ($f in $allCs) {
    $norm = Norm $f.FullName
    if ($norm.Contains('/Core/Bot/Modules/')) { continue }
    if ($norm.Contains('/Core/Bot/Scenarios/')) { continue }
    foreach ($entry in Get-CodeLines $f.FullName) {
        foreach ($type in $scenarioTypes.Keys) {
            if ($entry.Text.Contains('new ' + $type + '(')) {
                Add-Violation 'ARCH-SCENARIO-CREATE' $f.FullName $entry.Line ('场景只许在 BotModuleCatalog 里创建（出现 new ' + $type + '）')
            }
        }
    }
}

# ---------- 结果 ----------
if ($violations.Count -eq 0) {
    Write-Host ('[arch-guard] 通过：分层规则无违反（{0} 个源文件，11 条规则）' -f $allCs.Count)
    exit 0
}

Write-Host ''
Write-Host '[arch-guard] 架构锁被触发——以下写法违反了 AGENTS.md / docs/MODULES.md 的分层规则：'
Write-Host '（这些规则是硬约束；只有用户明确要求更换写法时才可修改规则本身）'
Write-Host ''
foreach ($v in $violations) { Write-Host ('  ' + $v) }
Write-Host ''
Write-Host ('共 {0} 处。修法见 docs/MODULES.md §7/§8。' -f $violations.Count)
exit 1
