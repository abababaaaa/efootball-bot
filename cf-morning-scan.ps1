$ErrorActionPreference = 'SilentlyContinue'
$out = 'C:\Users\lzx02\Documents\trae_projects\Efootball\cf-scan-result.txt'
"=== Cloudflare 节点扫描 $(Get-Date -Format 'yyyy-MM-dd HH:mm') ===" | Out-File $out -Encoding UTF8
ipconfig /flushdns | Out-Null

$rng = [Random]::new()
$ips = [System.Collections.Generic.List[string]]::new()
# 104.16.0.0/13 -> 104.16-23
foreach ($b in 16..23) { 1..12 | ForEach-Object { $ips.Add(("104.{0}.{1}.{2}" -f $b, $rng.Next(0,256), $rng.Next(1,255))) } }
# 104.24-25
foreach ($b in 24..25) { 1..8 | ForEach-Object { $ips.Add(("104.{0}.{1}.{2}" -f $b, $rng.Next(0,256), $rng.Next(1,255))) } }
# 162.158-159
foreach ($b in 158..159) { 1..10 | ForEach-Object { $ips.Add(("162.{0}.{1}.{2}" -f $b, $rng.Next(0,256), $rng.Next(1,255))) } }
# 172.64.0.0/13 -> 172.64-71
foreach ($b in 64..71) { 1..8 | ForEach-Object { $ips.Add(("172.{0}.{1}.{2}" -f $b, $rng.Next(0,256), $rng.Next(1,255))) } }
# 188.114.96-111
1..12 | ForEach-Object { $ips.Add(("188.114.{0}.{1}" -f $rng.Next(96,112), $rng.Next(1,255))) }
# 190.93.240-255
1..8 | ForEach-Object { $ips.Add(("190.93.{0}.{1}" -f $rng.Next(240,256), $rng.Next(1,255))) }
$ips = $ips | Sort-Object -Unique
"候选 IP 数: $($ips.Count)" | Out-File $out -Append

$results = $ips | ForEach-Object -Parallel {
    $r = Test-Connection -ComputerName $_ -Count 2 -BufferSize 16 -ErrorAction SilentlyContinue
    if ($r) {
        $avg = [math]::Round((($r | Measure-Object ResponseTime -Average).Average))
        [PSCustomObject]@{ IP = $_; Avg = $avg }
    }
} -ThrottleLimit 40

$alive = @($results | Sort-Object Avg)
"存活: $($alive.Count) / $($ips.Count)" | Out-File $out -Append
"" | Out-File $out -Append
"=== 延迟最低 Top 15 ===" | Out-File $out -Append
$alive | Select-Object -First 15 | ForEach-Object { "$($_.Avg)ms`t$($_.IP)" | Out-File $out -Append }

$good = @($alive | Where-Object Avg -lt 120)
"" | Out-File $out -Append
"=== <120ms 候选验证（cf-ray 后缀：HKG=香港最佳，NRT=东京，SEA=美西最差） ===" | Out-File $out -Append
if ($good.Count -eq 0) { "无 <120ms 节点，出口仍绕美西，建议换代理" | Out-File $out -Append }
foreach ($g in ($good | Select-Object -First 10)) {
    $hdr = curl.exe -s -I --max-time 8 --resolve "game.xsharing.cc:443:$($g.IP)" "https://game.xsharing.cc/" 2>$null
    $line = @($hdr | Where-Object { $_ -match 'cf-ray' } | Select-Object -First 1)
    $ray = if ($line.Count -gt 0 -and $line[0] -match 'cf-ray:\s*(\S+)') { $Matches[1] } else { '无响应' }
    "$($g.Avg)ms`t$($g.IP)`tcf-ray=$ray" | Out-File $out -Append
}
"完成: $(Get-Date -Format 'HH:mm:ss')" | Out-File $out -Append
