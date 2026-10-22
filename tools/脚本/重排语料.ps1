# ============================================================
# 把图库里的图统一重排：文字居中、四周补一圈底色边。
#
#   为什么要做：实测发现浏览器截图和真机截图的文字像素完全一样，
#   但四周留白不一样（真机 6~29 像素，浏览器截图上下只有 5~7）。
#   留白会改变俄文模型给出的分数，正好卡在 0.90 那道重试门槛上，
#   于是同一段文字在回放和真机上给出不同结果。
#
#   用法：
#     & ".\tools\脚本\重排语料.ps1"            # 默认补 12 像素
#     & ".\tools\脚本\重排语料.ps1" -Margin 15
#     & ".\tools\脚本\重排语料.ps1" -DryRun    # 只看会改成什么样，不写
#
#   重复跑是安全的：文字位置不动，补完再补还是那个大小。
# ============================================================
param(
    [int]$Margin = 12,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$corpus = Join-Path $root 'tools\语料'
$manifest = Join-Path $corpus '清单.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path $manifest)) { throw "找不到图库清单：$manifest" }
$doc = [System.IO.File]::ReadAllText($manifest, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$items = @($doc.images)

$changed = 0
$skipped = 0

foreach ($it in $items) {
    $p = Join-Path $corpus $it.file
    if (-not (Test-Path $p)) { Write-Host ("文件不见了，跳过：{0}" -f $it.file) -ForegroundColor Red; $skipped++; continue }

    $src = [System.Drawing.Bitmap]::FromFile($p)
    try {
        $w = $src.Width; $h = $src.Height

        # 底色 = 出现次数最多的那个颜色
        $counts = @{}
        for ($y = 0; $y -lt $h; $y++) {
            for ($x = 0; $x -lt $w; $x++) {
                $c = $src.GetPixel($x, $y)
                $k = "$($c.R),$($c.G),$($c.B)"
                if ($counts.ContainsKey($k)) { $counts[$k]++ } else { $counts[$k] = 1 }
            }
        }
        $mode = ($counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1).Key
        $mc = $mode -split ','
        $bg = [System.Drawing.Color]::FromArgb([int]$mc[0], [int]$mc[1], [int]$mc[2])

        # 文字实际占的框
        $minX = $w; $minY = $h; $maxX = -1; $maxY = -1
        for ($y = 0; $y -lt $h; $y++) {
            for ($x = 0; $x -lt $w; $x++) {
                $c = $src.GetPixel($x, $y)
                $d = [Math]::Abs($c.R - $bg.R) + [Math]::Abs($c.G - $bg.G) + [Math]::Abs($c.B - $bg.B)
                if ($d -gt 60) {
                    if ($x -lt $minX) { $minX = $x }
                    if ($x -gt $maxX) { $maxX = $x }
                    if ($y -lt $minY) { $minY = $y }
                    if ($y -gt $maxY) { $maxY = $y }
                }
            }
        }
        if ($maxX -lt 0) { Write-Host ("找不到文字，跳过：{0}" -f $it.file) -ForegroundColor Red; $skipped++; continue }

        $inkW = $maxX - $minX + 1
        $inkH = $maxY - $minY + 1
        $newW = $inkW + 2 * $Margin
        $newH = $inkH + 2 * $Margin

        if (-not $DryRun) {
            $dst = New-Object System.Drawing.Bitmap $newW, $newH, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $g = [System.Drawing.Graphics]::FromImage($dst)
                try {
                    $g.Clear($bg)
                    $srcRect = New-Object System.Drawing.Rectangle $minX, $minY, $inkW, $inkH
                    $dstRect = New-Object System.Drawing.Rectangle $Margin, $Margin, $inkW, $inkH
                    $g.DrawImage($src, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
                } finally { $g.Dispose() }
                $src.Dispose()
                $src = $null
                $dst.Save($p, [System.Drawing.Imaging.ImageFormat]::Png)
            } finally {
                if ($null -ne $src) { $src.Dispose() }
                $dst.Dispose()
            }

            $it.sha256 = (Get-FileHash $p -Algorithm SHA256).Hash.ToLower()
            $it.bytes = (Get-Item $p).Length
        }
        $changed++
        Write-Host ("{0,-16} {1}x{2} 文字 {3}x{4}  ->  {5}x{6}" -f $it.file, $w, $h, $inkW, $inkH, $newW, $newH)
    } finally {
        if ($null -ne $src) { $src.Dispose() }
    }
}

if (-not $DryRun) {
    $out = [pscustomobject]@{ version = 1; images = @($items) }
    [System.IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject $out -Depth 6), $utf8)
}

Write-Host ""
if ($DryRun) {
    Write-Host ("预演：会重排 {0} 张，跳过 {1} 张（没有写任何文件）" -f $changed, $skipped) -ForegroundColor Cyan
} else {
    Write-Host ("重排完成：改了 {0} 张，跳过 {1} 张，清单已更新" -f $changed, $skipped) -ForegroundColor Green
}
