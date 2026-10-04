# ============================================================================
#  GitHub 直连优化脚本
#  流程：备份 hosts -> 多 DNS 解析域名 -> TCP/443 测速选最快 IP -> 写入 hosts
#  由 github-optimize.bat 以管理员权限调用
# ============================================================================
$ErrorActionPreference = 'SilentlyContinue'

$hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
$backupPath = "$hostsPath.github.bak"

# ---------- 1. 备份 ----------
Copy-Item $hostsPath $backupPath -Force
Write-Host "已备份 hosts -> $backupPath"

# ---------- 2. 配置 ----------
$domains = @(
    'github.com',
    'api.github.com',
    'raw.githubusercontent.com',
    'codeload.github.com',
    'github.global.ssl.fastly.net',
    'gist.github.com',
    'assets-cdn.github.com',
    'objects.githubusercontent.com'
)
# 国外 DNS（真实解析）+ 国内 DNS（对比）
$dnsServers = @('8.8.8.8', '1.1.1.1', '223.5.5.5', '119.29.29.29')

# ---------- 3. TCP/443 延迟测试 ----------
function Test-Latency([string]$ip) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $c.BeginConnect($ip, 443, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne(1500)) {
            $c.EndConnect($iar)
            $sw.Stop()
            return $sw.ElapsedMilliseconds
        }
    } catch {}
    finally { $c.Close() }
    return -1
}

# ---------- 4. 解析 + 测速 ----------
Write-Host ""
Write-Host "开始解析并测速（仅 IPv4）..."
$results = @()

foreach ($d in $domains) {
    $ips = @()
    foreach ($ns in $dnsServers) {
        $r = Resolve-DnsName $d -Server $ns -ErrorAction SilentlyContinue
        if ($r) {
            foreach ($x in $r) {
                $a = $x.IPAddress
                if ($a -and $a.Contains('.') -and ($ips -notcontains $a)) { $ips += $a }
            }
        }
    }
    if ($ips.Count -eq 0) {
        Write-Host ("  [跳过] {0}  未解析到 IPv4" -f $d) -ForegroundColor Yellow
        continue
    }

    $best = $null; $bestMs = 99999
    foreach ($ip in $ips) {
        $ms = Test-Latency $ip
        if ($ms -ge 0 -and $ms -lt $bestMs) { $bestMs = $ms; $best = $ip }
    }
    if ($best) {
        Write-Host ("  {0,-34} -> {1,-16} ({2} ms)" -f $d, $best, $bestMs)
        $results += "{0}`t{1}" -f $best, $d
    } else {
        Write-Host ("  [跳过] {0}  TCP/443 全部超时" -f $d) -ForegroundColor Yellow
    }
}

if ($results.Count -eq 0) {
    Write-Host ""
    Write-Host "未找到任何可用 IP，hosts 未修改。" -ForegroundColor Red
    exit 1
}

# ---------- 5. 去旧：删除旧 github 映射与旧优化段 ----------
$existing = Get-Content $hostsPath -Encoding UTF8 | Where-Object {
    $line = $_.Trim()
    if ($line -eq '') { return $true }
    if ($line.StartsWith('#')) {
        # 我们自己的旧 marker/footer 段删除，其余注释保留
        return -not $line.Contains('GitHub optimize')
    }
    $domain = ($line -split '\s+')[-1]
    return -not ($domains -contains $domain)
}

# ---------- 6. 写入 ----------
$marker  = '# ==== GitHub optimize (auto generated) ===='
$footer  = '# ==== GitHub optimize end ===='
$newLines = @($marker) + $results + @($footer)
$all = @($existing) + $newLines

$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllLines($hostsPath, $all, $utf8NoBom)

Write-Host ""
Write-Host ("已更新 hosts，写入 {0} 条映射（无 BOM UTF-8）。" -f $results.Count) -ForegroundColor Green
Write-Host "如需还原，可复制备份：$backupPath"