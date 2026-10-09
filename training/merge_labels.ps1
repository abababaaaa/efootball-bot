# 把 X-AnyLabeling 导出的 YOLO 标注合并进数据集并重新打包上传 zip
# 用法: powershell -File merge_labels.ps1 [-LblDir <标注导出目录>]
# 前提: 标注 txt 文件名与 dataset\images\{train,val}\ 里的图片同名（仅扩展名不同）
param([string]$LblDir = "training\labels_export")
$ErrorActionPreference = "Stop"

$root = "training\dataset"
$moved = 0; $missing = 0
Get-ChildItem $LblDir -Filter *.txt -File | ForEach-Object {
    $stem = $_.BaseName
    $img = Get-ChildItem "$root\images" -Recurse -File | Where-Object { $_.BaseName -eq $stem } | Select-Object -First 1
    if ($img) {
        $subset = if ($img.Directory.Name -eq "train") { "train" } else { "val" }
        Copy-Item $_.FullName (Join-Path "$root\labels\$subset" "$stem.txt") -Force
        $moved++
    } else { $missing++; Write-Host "跳过（找不到对应图片）: $stem" }
}

# 清掉占位文件
Remove-Item "$root\labels\train\.keep","$root\labels\val\.keep" -Force -ErrorAction SilentlyContinue

$nTrainLbl = (Get-ChildItem "$root\labels\train" -Filter *.txt -ErrorAction SilentlyContinue | Measure-Object).Count
$nValLbl   = (Get-ChildItem "$root\labels\val"   -Filter *.txt -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host "标注合并完成：train $nTrainLbl / val $nValLbl（未匹配 $missing）"
if ($nTrainLbl + $nValLbl -eq 0) { Write-Error "没有任何标注被合并，请检查导出目录"; exit 1 }

$zip = "$root\dataset.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$root\data.yaml", "$root\images", "$root\labels" -DestinationPath $zip
Write-Host "已重新打包：$zip（上传到 Ultralytics Platform）"
