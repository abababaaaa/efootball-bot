<#
.SYNOPSIS
  一站式把「主更新源」接到 Cloudflare R2：建桶 → 开 r2.dev 公网读 → 回填程序内置地址 → 打印剩下的手工步骤。

.DESCRIPTION
  能自动的都自动：
    1) npx wrangler 登录（首次弹浏览器，点 Allow；5 分钟内有效）
    2) 建桶（已存在则跳过）
    3) 开启 r2.dev 公开访问并取回 https://pub-xxxx.r2.dev
    4) 校验公网可读
    5) 把地址写进 src/EfootballBot/Core/System/Updater.cs 的 BuiltInPrimaryBaseUrl（幂等）
    6) 打印 R2 API Token 与 GitHub Secrets 的直达链接
  加 -Release 时再升版本、写 CHANGELOG、commit、打 tag 并推送（会先确认）。

.EXAMPLE
  .\tools\setup-r2-update-channel.ps1

.EXAMPLE
  # 不想走浏览器授权，只想看手工步骤
  .\tools\setup-r2-update-channel.ps1 -NoLogin

.EXAMPLE
  # 桶已建好、地址已知：跳过 Cloudflare，只回填代码
  .\tools\setup-r2-update-channel.ps1 -SkipCloudflare -PublicBaseUrl https://pub-1a2b3c4d5e.r2.dev

.EXAMPLE
  # 只看要改什么
  .\tools\setup-r2-update-channel.ps1 -SkipCloudflare -PublicBaseUrl https://pub-1a2b3c4d5e.r2.dev -WhatIf
#>
[CmdletBinding()]
param(
    [string]$Bucket = 'efootball-bot',
    [string]$PublicBaseUrl = '',
    [string]$RepoRoot = '',
    [switch]$SkipCloudflare,
    [switch]$NoLogin,
    [switch]$Release,
    [string]$Version = '',
    [string]$Changelog = '',
    [switch]$Force,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$NL = [Environment]::NewLine

function Step($msg) { Write-Host ($NL + '== ' + $msg) -ForegroundColor Cyan }
function Ok($msg)   { Write-Host ('   OK  ' + $msg) -ForegroundColor Green }
function Warn($msg) { Write-Host ('   !   ' + $msg) -ForegroundColor Yellow }
function Die($msg)  { Write-Host ($NL + '[失败] ' + $msg) -ForegroundColor Red; exit 1 }

# 调 wrangler：把 stderr 一起收进字符串，且不让 native stderr 触发 $ErrorActionPreference=Stop
# （Windows PowerShell 5.1 下 wrangler 的彩色 stderr 会被当成 NativeCommandError 直接终止脚本）
function Invoke-Wrangler {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$WranglerArgs)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = (& npx --yes wrangler@latest @WranglerArgs 2>&1 | Out-String)
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }
    return [pscustomobject]@{ Output = $text; Code = $code }
}

function Show-ManualSteps {
    Write-Host @'

授权没完成（等回调超时：通常是没有在 5 分钟内点 Allow），或你用了 -NoLogin。
两条路任选其一：

  1. 终端里先登录，再重跑本脚本（凭据会缓存到本机 %USERPROFILE%\.wrangler）：
       npx wrangler login
     浏览器点 Allow → 然后重跑： .\tools\setup-r2-update-channel.ps1

  2. 控制台手动三连（约 2 分钟）：
       a. R2 → Create bucket：efootball-bot
       b. 进桶 → Settings → Public access → 允许 r2.dev → 复制 https://pub-xxxx.r2.dev
       c. 重跑： .\tools\setup-r2-update-channel.ps1 -SkipCloudflare -PublicBaseUrl https://pub-xxxx.r2.dev

'@ -ForegroundColor Yellow
}

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath) }
if (-not (Test-Path $RepoRoot)) { Die ('找不到仓库目录：' + $RepoRoot) }
$RepoRoot = (Resolve-Path $RepoRoot).Path
$updaterPath = Join-Path $RepoRoot 'src\EfootballBot\Core\System\Updater.cs'
if (-not (Test-Path $updaterPath)) { Die ('找不到 ' + $updaterPath + '（-RepoRoot 指错了？）') }

Write-Host ('仓库：' + $RepoRoot) -ForegroundColor DarkGray
Write-Host ('桶名：' + $Bucket) -ForegroundColor DarkGray

# ---------------------------------------------------------------- 1. Cloudflare
$accountId = ''
if (-not $SkipCloudflare -and -not $PublicBaseUrl) {
    Step '1/4 检查 wrangler 与登录状态'
    $npx = Get-Command npx -ErrorAction SilentlyContinue
    if (-not $npx) { Die '没装 Node.js/npx。装好 Node 20+ 再跑，或用 -SkipCloudflare -PublicBaseUrl 手动填地址。' }

    $whoRes = Invoke-Wrangler whoami
    $who = $whoRes.Output
    $notAuthed = ($who -match '(?i)not authenticated|not logged in|please run .?wrangler login')
    if ($notAuthed -and -not $NoLogin) {
        Warn '尚未登录 Cloudflare，正在拉起浏览器授权（点 Allow，5 分钟内有效）…'
        $null = Invoke-Wrangler login
        $whoRes = Invoke-Wrangler whoami
        $who = $whoRes.Output
        $notAuthed = ($who -match '(?i)not authenticated|not logged in|please run .?wrangler login')
    }
    if ($notAuthed) {
        Show-ManualSteps
        exit 2
    }

    $m = [regex]::Match($who, '(?im)Account ID\s*[│|]?\s*([0-9a-f]{32})')
    if (-not $m.Success) { $m = [regex]::Match($who, '[0-9a-f]{32}') }
    if ($m.Success) {
        $accountId = $m.Groups[$m.Groups.Count - 1].Value
        Ok ('已登录，Account ID：' + $accountId)
    } else { Ok '已登录' }

    Step ('2/4 建桶 ' + $Bucket + '（已存在会自动跳过）')
    $createRes = Invoke-Wrangler r2 bucket create $Bucket
    if ($createRes.Code -ne 0) {
        if ($createRes.Output -match '(?i)already exists|10004|10058') { Warn '桶已存在，继续' }
        else { Die ('建桶失败：' + $NL + $createRes.Output) }
    } else { Ok '桶已创建' }

    Step '3/4 开启 r2.dev 公开访问'
    $enableRes = Invoke-Wrangler r2 bucket dev-url enable $Bucket --force
    $getRes = Invoke-Wrangler r2 bucket dev-url get $Bucket
    $urlMatch = [regex]::Match(($enableRes.Output + $NL + $getRes.Output), 'https://[a-z0-9-]+\.r2\.dev')
    if (-not $urlMatch.Success) {
        Die ('没能拿到 r2.dev 地址，wrangler 输出：' + $NL + $enableRes.Output + $getRes.Output + $NL + '也可以去控制台 R2 → 桶 → Settings → Public access 手动开启，再用 -SkipCloudflare -PublicBaseUrl 跑一次。')
    }
    $PublicBaseUrl = $urlMatch.Value.TrimEnd('/')
    Ok ('公网地址：' + $PublicBaseUrl)
}
elseif ($SkipCloudflare) {
    Step '1-3/4 已指定 -SkipCloudflare，跳过 Cloudflare 操作'
    if (-not $PublicBaseUrl) { Die '用了 -SkipCloudflare 就必须给 -PublicBaseUrl（例如 https://pub-xxxx.r2.dev）' }
}

$PublicBaseUrl = $PublicBaseUrl.Trim().TrimEnd('/')
if ($PublicBaseUrl -notmatch '^https?://[^/]+') { Die ('地址不合法：' + $PublicBaseUrl) }

Step '4/4 校验公网可读'
$status = -1
try {
    $resp = Invoke-WebRequest -Uri ($PublicBaseUrl + '/latest.json') -Method Head -TimeoutSec 15 -UseBasicParsing
    $status = [int]$resp.StatusCode
}
catch {
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
}
switch ($status) {
    200 { Ok '清单已在（说明之前发过版）' }
    404 { Ok '域名可达（桶还是空的，清单等首次发版由 CI 写入）' }
    403 { Warn '返回 403：公网读还没生效，去 R2 → 桶 → Settings → Public access 打开 r2.dev' }
    -1  { Warn ('连不上 ' + $PublicBaseUrl + '。新开的 r2.dev 可能要 1~2 分钟生效；再不行就检查地址是否抄错。') }
    default { Warn ('返回 ' + $status + '，稍后自行确认') }
}

# ---------------------------------------------------------------- 2. 回填代码
Step '把地址写进 Updater.BuiltInPrimaryBaseUrl'
# 必须按 UTF-8 读：Windows PowerShell 的 Get-Content -Raw 默认按 ANSI(GBK) 解码，
# 无 BOM 的 UTF-8 源码会被读成乱码再写回去，直接把 Updater.cs 的中文注释毁掉（实测踩过）。
$text = [System.IO.File]::ReadAllText($updaterPath, [System.Text.Encoding]::UTF8)
$pattern = 'public const string BuiltInPrimaryBaseUrl = "[^"]*";'
$replacement = 'public const string BuiltInPrimaryBaseUrl = "' + $PublicBaseUrl + '";'
if ($text -notmatch $pattern) { Die 'Updater.cs 里找不到 BuiltInPrimaryBaseUrl 常量，可能被改过名' }

$current = [regex]::Match($text, $pattern).Value
if ($current -eq $replacement) {
    Ok '已经是这个地址，无需修改'
}
elseif ($WhatIf) {
    Warn ('WhatIf：将把' + $NL + '     ' + $current + $NL + ' 改成' + $NL + '     ' + $replacement)
}
else {
    $text = $text -replace $pattern, $replacement
    [System.IO.File]::WriteAllText($updaterPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    Ok ('已写入：' + $replacement)
    Write-Host '   下一步编译验证： dotnet build src\EfootballBot\EfootballBot.csproj -c Debug' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 3. 剩下的手工步骤
$secretsUrl = 'https://github.com/abababaaaa/efootball-bot/settings/secrets/actions'
$tokenUrl = if ($accountId) { 'https://dash.cloudflare.com/' + $accountId + '/r2/api-tokens' } else { 'https://dash.cloudflare.com/?to=/:account/r2/api-tokens' }

Write-Host ($NL + '====================================================================' + $NL + ' 还差两步（只有你能点，约 3 分钟）' + $NL + '====================================================================' + $NL + ' A. 建 R2 API Token（给 CI 上传用），打开：') -ForegroundColor Gray
Write-Host ('    ' + $tokenUrl) -ForegroundColor Cyan
Write-Host ('    → Create API Token → 权限选 Object Read & Write → 只勾桶 ' + $Bucket) -ForegroundColor Gray
Write-Host ('    → 记下 Access Key ID / Secret Access Key / Account ID' + $NL) -ForegroundColor Gray
Write-Host (' B. 填 4 个 GitHub Secrets，打开：') -ForegroundColor Gray
Write-Host ('    ' + $secretsUrl) -ForegroundColor Cyan
Write-Host ('    R2_ACCOUNT_ID          = <Account ID>') -ForegroundColor Gray
Write-Host ('    R2_ACCESS_KEY_ID       = <Access Key ID>') -ForegroundColor Gray
Write-Host ('    R2_SECRET_ACCESS_KEY   = <Secret Access Key>') -ForegroundColor Gray
Write-Host ('    R2_BUCKET              = ' + $Bucket + $NL) -ForegroundColor Gray
Write-Host (' 完成后打 tag 发一版，CI 会把 exe + latest.json + 补丁传上主源：') -ForegroundColor Gray
Write-Host ('    git tag v6.1.3') -ForegroundColor Gray
Write-Host ('    git push origin --tags') -ForegroundColor Gray
Write-Host (' 或一条命令（升版本 + 写 CHANGELOG + 提交 + 打 tag）：') -ForegroundColor Gray
Write-Host ('    .\tools\setup-r2-update-channel.ps1 -SkipCloudflare -PublicBaseUrl ' + $PublicBaseUrl + ' -Release -Version 6.1.3 -Changelog "本次更新说明"') -ForegroundColor Gray
Write-Host ('====================================================================') -ForegroundColor Gray

# ---------------------------------------------------------------- 4. 可选：发版
if ($Release) {
    if (-not $Version) { Die '-Release 需要 -Version（例如 6.1.3）' }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { Die '-Version 用三段数字（6.1.3）；测试版的第 4 段由发版脚本自己加' }
    if (-not $Changelog) { Die '-Release 需要 -Changelog "本次更新说明"（会写进 CHANGELOG 并进 Release 正文）' }

    Step ('发版准备 v' + $Version)
    $csprojPath = Join-Path $RepoRoot 'src\EfootballBot\EfootballBot.csproj'
    $changelogPath = Join-Path $RepoRoot 'CHANGELOG.md'
    $csproj = [System.IO.File]::ReadAllText($csprojPath, [System.Text.Encoding]::UTF8)
    $bumped = 0
    foreach ($vm in [regex]::Matches($csproj, '<Version Condition="[^"]*">([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)</Version>')) {
        $oldVer = $vm.Groups[1].Value
        $parts = $oldVer.Split('.')
        $newVer = $Version + '.' + $parts[3]
        if ($oldVer -ne $newVer) { $csproj = $csproj.Replace($oldVer, $newVer); $bumped++ }
    }
    if ($bumped -eq 0) { Warn 'csproj 版本号已是目标版本' } else { Ok ('csproj 版本 → ' + $Version + '.x（' + $bumped + ' 处）') }

    $cl = [System.IO.File]::ReadAllText($changelogPath, [System.Text.Encoding]::UTF8)
    if ($cl -match ('(?m)^## v' + [regex]::Escape($Version) + '\b')) {
        Warn ('CHANGELOG 已有 v' + $Version + ' 段落，跳过')
    } else {
        # 必须是「行首的 ## v」：CHANGELOG 开头的格式说明里也出现过 ## v<版本号>，
        # 按子串找会把新段落插进那句话中间（实测踩过）
        $anchor = [regex]::Match($cl, '(?m)^## vd')
        if (-not $anchor.Success) { Die 'CHANGELOG.md 格式不认识（找不到行首的 ## v 段落）' }
        $idx = $anchor.Index
        $section = '## v' + $Version + $NL + $NL + $Changelog.Trim() + $NL + $NL
        $cl = $cl.Insert($idx, $section)
        if ($WhatIf) { Ok ('CHANGELOG 将插入 v' + $Version) } else { Ok ('CHANGELOG 已插入 v' + $Version) }
    }

    if ($WhatIf) {
        Warn 'WhatIf：不写文件、不提交、不打 tag'
    }
    else {
        [System.IO.File]::WriteAllText($csprojPath, $csproj, (New-Object System.Text.UTF8Encoding($false)))
        [System.IO.File]::WriteAllText($changelogPath, $cl, (New-Object System.Text.UTF8Encoding($false)))
        if (-not $Force) {
            Write-Host ($NL + '即将执行：git commit → git tag v' + $Version + ' → git push origin HEAD --tags') -ForegroundColor Yellow
            $ans = Read-Host '继续？(y/N)'
            if ($ans -ne 'y') { Warn '已取消，改动保留在工作区，确认后再手动提交'; exit 0 }
        }
        Push-Location $RepoRoot
        try {
            # 只提交本次改动：仓库里还有 _research/ etk_work/ feedback_analysis/ 等未跟踪的大目录，
            # git add -A 会把它们一起卷进来
            & git add -u
            & git add 'tools/setup-r2-update-channel.ps1'
            & git commit -m ('feat(update): 主更新源接入 R2（' + $PublicBaseUrl + '）') | Out-Host
            if ($LASTEXITCODE -ne 0) { Die 'git commit 失败（可能没有改动或预提交钩子报错）' }
            & git tag ('v' + $Version)
            if ($LASTEXITCODE -ne 0) { Die ('打 tag 失败：v' + $Version + ' 可能已存在') }
            & git push origin HEAD --tags
            if ($LASTEXITCODE -ne 0) { Die 'git push 失败，检查远端凭据' }
            Ok ('已推送 v' + $Version + '，去 Actions 看 release job 的「Publish to Cloudflare R2」是否上传成功')
        }
        finally { Pop-Location }
    }
}
