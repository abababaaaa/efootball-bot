# FrameDump 截图去重 + 打包 HUB 上传数据集
# 用法: powershell -File dedupe_pack.ps1 [-Src <截图目录>] [-Out <输出目录>]
# 逻辑: 8x8 平均哈希(aHash)，与已保留集合汉明距离<=10 视为重复 → 丢弃件移入 _duplicates
# 产出: dataset/images/train(约90%) + dataset/images/val(约10%) + dataset.zip（Ultralytics HUB 可直接上传）
param(
    [string]$Src = "publish\captures",
    [string]$Out = "training\dataset"
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function Get-AHash([string]$path) {
    $bmp = [System.Drawing.Bitmap]::new($path)
    try {
        $small = [System.Drawing.Bitmap]::new(8, 8)
        $g = [System.Drawing.Graphics]::FromImage($small)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.DrawImage($bmp, 0, 0, 8, 8); $g.Dispose()
        $bits = New-Object byte[] 64
        $sum = 0L
        for ($y = 0; $y -lt 8; $y++) { for ($x = 0; $x -lt 8; $x++) {
            $c = $small.GetPixel($x, $y)
            $v = [int](($c.R * 299 + $c.G * 587 + $c.B * 114) / 1000)
            $bits[$y * 8 + $x] = $v; $sum += $v
        } }
        $avg = $sum / 64
        $mask = New-Object bool[] 64
        for ($i = 0; $i -lt 64; $i++) { $mask[$i] = ($bits[$i] -ge $avg) }
        return ,@($mask)
    } finally { $bmp.Dispose(); if ($small) { $small.Dispose() } }
}
function Hamming($a, $b) { $d = 0; for ($i = 0; $i -lt 64; $i++) { if ($a[$i] -ne $b[$i]) { $d++ } }; $d }

$imgs = Get-ChildItem $Src -Recurse -Include *.jpg,*.jpeg,*.png -File | Sort-Object Name
if (-not $imgs) { Write-Error "未找到图片：$Src" }
Write-Host "共 $($imgs.Count) 张，开始去重…"

$dupDir = Join-Path $Out "_duplicates"
New-Item $dupDir -ItemType Directory -Force | Out-Null
$trainDir = Join-Path $Out "images\train"
$valDir   = Join-Path $Out "images\val"
New-Item $trainDir -ItemType Directory -Force | Out-Null
New-Item $valDir   -ItemType Directory -Force | Out-Null

$kept = @()
$nDup = 0
foreach ($img in $imgs) {
    $h = Get-AHash $img.FullName
    $isDup = $false
    foreach ($k in $kept) { if ((Hamming $h $k[0]) -le 10) { $isDup = $true; break } }
    if ($isDup) {
        $nDup++
        Move-Item $img.FullName (Join-Path $dupDir $img.Name) -Force
    } else {
        $kept += ,@($h)
        Move-Item $img.FullName (Join-Path $trainDir $img.Name) -Force
    }
}
Write-Host "去重完成：保留 $($kept.Count) 张，移除重复 $nDup 张 → $dupDir"

# 抽 10% 到 val（固定种子可复现）
$files = Get-ChildItem $trainDir -File
$nVal = [Math]::Max(1, [int]($files.Count * 0.1))
$rand = New-Object Random 42
$picked = $files | Get-Random -Count $nVal -SetSeed 42
foreach ($f in $picked) { Move-Item $f.FullName (Join-Path $valDir $f.Name) -Force }
Write-Host "划分：train $($files.Count - $nVal) 张 / val $nVal 张"

# 占位文件保证 zip 里空 labels 目录存在（标注后删除即可）
foreach ($d in @("labels\train", "labels\val")) {
    $p = Join-Path $Out $d
    New-Item $p -ItemType Directory -Force | Out-Null
    Set-Content (Join-Path $p ".keep") "" -Force
}

# 打 zip（HUB 上传格式：data.yaml + images/ + labels/）
$dataYaml = Join-Path $Out "data.yaml"
$yamlText = @"
path: .
train: images/train
val: images/val
names:
  0: class_a
  1: class_b
  2: class_c
"@
Set-Content $dataYaml $yamlText -Encoding UTF8

$zip = Join-Path $Out "dataset.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $Out "data.yaml"), (Join-Path $Out "images"), (Join-Path $Out "labels") -DestinationPath $zip
Write-Host "已打包：$zip"
