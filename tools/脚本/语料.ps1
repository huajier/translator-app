# ============================================================
# 语料库工具
#   用法：
#     & ".\tools\脚本\语料.ps1" status
#     & ".\tools\脚本\语料.ps1" add -Mat 2 -Id E4 -Img "x.png" -Note "灰底"
#
#   目录结构：
#     tools\语料\清单.json            每张图的 SHA-256 + 归属段 + 备注
#     tools\语料\材料2\E4\1.png       段号目录，序号从 1 开始
#
#   为什么要 SHA-256：图被重截/替换/压缩过时能立刻发现，
#   否则会出现「代码没变但结果变了」的假回归。
# ============================================================
param(
    [Parameter(Position = 0)]
    [ValidateSet('add', 'remove', 'status', '实机')]
    [string]$Action = 'status',

    [int]$Mat = 0,
    [string]$Id = '',
    [string]$Img = '',
    [string]$Note = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$corpus = Join-Path $root 'tools\语料'
$manifest = Join-Path $corpus '清单.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

# ---------- 读三份标准答案，拿到 56 个 (材料号, 段号) ----------
function ReadAnswers {
    $out = New-Object System.Collections.ArrayList
    $matNo = 0
    foreach ($n in '验收标准答案.txt', '验收标准答案2.txt', '验收标准答案3.txt') {
        $matNo++
        $p = Join-Path $root "tools\素材\$n"
        if (-not (Test-Path $p)) { throw "找不到标准答案：$p" }
        foreach ($l in [System.IO.File]::ReadAllLines($p, [System.Text.Encoding]::UTF8)) {
            if ($l -match '^\s*([A-Z]\d+)\|') {
                [void]$out.Add([pscustomobject]@{ Mat = $matNo; Id = $Matches[1] })
            }
        }
    }
    return $out
}

function ReadManifest {
    if (-not (Test-Path $manifest)) { return @() }
    $raw = [System.IO.File]::ReadAllText($manifest, [System.Text.Encoding]::UTF8)
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
    return @(($raw | ConvertFrom-Json).images)
}

function WriteManifest($items) {
    if (-not (Test-Path $corpus)) { New-Item -ItemType Directory -Path $corpus -Force | Out-Null }
    $doc = [pscustomobject]@{ version = 1; images = @($items) }
    [System.IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject $doc -Depth 6), $utf8)
}

function Get-Sha([string]$p) { return (Get-FileHash $p -Algorithm SHA256).Hash.ToLower() }

# ========================= 收一张图 =========================
function Add-One([int]$Mat, [string]$Id, [string]$Img, [string]$Note) {
    if ($Mat -lt 1 -or $Mat -gt 3) { throw '-Mat 必须是 1、2 或 3（对应三份材料）' }
    if ($Id -notmatch '^[A-Z]\d+$') { throw '-Id 必须是 A1 这种格式（大写字母 + 数字）' }
    if (-not $Img) { throw '-Img 必填，指向要收进来的 png' }
    if (-not (Test-Path $Img)) { throw "找不到图片：$Img" }

    $src = (Resolve-Path $Img).Path
    $hash = Get-Sha $src
    $items = ReadManifest

    # 同一张图重复收：直接跳过，避免越堆越多
    $dup = @($items | Where-Object { $_.mat -eq $Mat -and $_.id -eq $Id -and $_.sha256 -eq $hash })
    if ($dup.Count -gt 0) {
        Write-Host "这张图之前已经收过了，没重复存：$($dup[0].file)" -ForegroundColor Yellow
        return $false
    }

    $dir = Join-Path $corpus "材料$Mat\$Id"
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $n = 1
    while (Test-Path (Join-Path $dir "$n.png")) { $n++ }
    Copy-Item -LiteralPath $src -Destination (Join-Path $dir "$n.png")

    $rel = "材料$Mat\$Id\$n.png"
    $size = (Get-Item (Join-Path $dir "$n.png")).Length
    $items = @($items) + [pscustomobject]@{
        file       = $rel
        mat        = $Mat
        id         = $Id
        sha256     = $hash
        bytes      = $size
        addedAt    = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        note       = $Note
    }
    WriteManifest $items
    Write-Host ("已收进图库：{0}  （{1} 字节，SHA-256 {2}…）" -f $rel, $size, $hash.Substring(0, 12)) -ForegroundColor Green
    return $true
}

# ============================ add ============================
if ($Action -eq 'add') {
    [void](Add-One $Mat $Id $Img $Note)
    return
}

# =========================== 实机 ===========================
# 扫 tools\语料\实机\ 里的 png，按文件名收进对应段，收完把原图删掉。
# 文件名规则：2-R7.png 或 材料2-R7.png —— 只要能认出「材料号-段号」就行。
if ($Action -eq '实机') {
    $drop = Join-Path $corpus '实机'
    if (-not (Test-Path $drop)) { New-Item -ItemType Directory -Path $drop -Force | Out-Null }
    $files = @(Get-ChildItem $drop -Filter *.png -File)
    if ($files.Count -eq 0) {
        Write-Host "tools\语料\实机\ 里没有 png。" -ForegroundColor Yellow
        return
    }
    # 实机图要顶替网页截图，所以第一次碰到某一段时先把旧图整段拿掉，
    # 让实机图从 1.png 开始 —— 回放只认 1.png 当这一段的成绩。
    $seen = @{}
    foreach ($f in $files | Sort-Object Name) {
        if ($f.BaseName -notmatch '(\d)\s*-\s*([A-Z]\d+)') {
            Write-Host ("认不出是哪一段，留在原地没动：{0}  （文件名要像 2-R7.png）" -f $f.Name) -ForegroundColor Red
            continue
        }
        $m = [int]$Matches[1]
        $seg = $Matches[2]
        $key = "$m-$seg"
        if (-not $seen.ContainsKey($key)) {
            $seen[$key] = $true
            $items = ReadManifest
            $mine = @($items | Where-Object { $_.mat -eq $m -and $_.id -eq $seg })
            if ($mine.Count -gt 0) {
                WriteManifest @($items | Where-Object { -not ($_.mat -eq $m -and $_.id -eq $seg) })
                $dir = Join-Path $corpus "材料$m\$seg"
                if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
                Write-Host ("  先把旧图拿掉：材料{0}\{1}（{2} 张）" -f $m, $seg, $mine.Count) -ForegroundColor DarkGray
            }
        }
        [void](Add-One $m $seg $f.FullName "实机截图 $($f.Name)")
        # 不管收没收进去都要删掉源文件：重复图留着的话，下一轮会被当成
        # 「这一段的新图」，先把整段拿掉再只放这一张，把同段其他图全冲掉。
        Remove-Item -LiteralPath $f.FullName -Force
    }
    return
}

# =========================== remove ===========================
# 把某一段的图从图库里整个拿掉（清单条目 + 文件）。
# 用途：图截错了要重截。不先拿掉的话 add 会存成 2.png、3.png，越堆越多。
if ($Action -eq 'remove') {
    if ($Mat -lt 1 -or $Mat -gt 3) { throw '-Mat 必须是 1、2 或 3（对应三份材料）' }
    if ($Id -notmatch '^[A-Z]\d+$') { throw '-Id 必须是 A1 这种格式（大写字母 + 数字）' }

    $dir = [IO.Path]::GetFullPath((Join-Path $corpus "材料$Mat\$Id"))
    $corpusFull = [IO.Path]::GetFullPath($corpus)
    # 删目录前先确认它真在图库下面（防止 $Mat/$Id 拼出奇怪的路径）
    if (-not $dir.StartsWith($corpusFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除图库以外的路径：$dir"
    }

    $items = ReadManifest
    $mine = @($items | Where-Object { $_.mat -eq $Mat -and $_.id -eq $Id })
    if ($mine.Count -eq 0) {
        Write-Host ("图库里没有这一段：材料{0}\{1}" -f $Mat, $Id) -ForegroundColor Yellow
        return
    }

    WriteManifest @($items | Where-Object { -not ($_.mat -eq $Mat -and $_.id -eq $Id) })
    if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    Write-Host ("已从图库移除 材料{0}\{1}（{2} 张图）" -f $Mat, $Id, $mine.Count) -ForegroundColor Green
    return
}

# ========================== status ==========================
$ans = ReadAnswers
$items = ReadManifest

Write-Host "=========== 图库状态 ===========" -ForegroundColor Cyan
Write-Host ("清单：{0}" -f $manifest)
Write-Host ("已收 {0} 张图" -f @($items).Count)
Write-Host ""

$missing = New-Object System.Collections.ArrayList
$stillWeb = New-Object System.Collections.ArrayList
$have = 0
$real = 0
$matNo = 0
foreach ($a in $ans) {
    if ($a.Mat -ne $matNo) {
        $matNo = $a.Mat
        Write-Host ("—— 材料{0} ——" -f $matNo) -ForegroundColor DarkGray
    }
    $mine = @($items | Where-Object { $_.mat -eq $a.Mat -and $_.id -eq $a.Id })
    if ($mine.Count -eq 0) {
        Write-Host ("  {0,-4} 缺图" -f $a.Id) -ForegroundColor DarkYellow
        [void]$missing.Add(("{0}-{1}" -f $a.Mat, $a.Id))
    } else {
        $have++
        # 实=1.png 是实机截图（回放只认 1.png 算这一段成绩）；网=还是浏览器截图
        $first = @($mine | Where-Object { $_.file -match '\\1\.png$' })
        $isReal = $first.Count -gt 0 -and $first[0].note -notmatch '浏览器'
        if ($isReal) { $real++ } else { [void]$stillWeb.Add(("{0}-{1}" -f $a.Mat, $a.Id)) }
        $mark = if ($isReal) { '实' } else { '网' }
        Write-Host ("  {0,-4} {1} 张  {2}" -f $a.Id, $mine.Count, $mark) -ForegroundColor Green
    }
}

Write-Host ""
Write-Host ("合计：56 段里有图 {0} 段，缺 {1} 段" -f $have, $missing.Count) -ForegroundColor Cyan
Write-Host ("实机图：{0} 段已换，{1} 段还是网页截图" -f $real, $stillWeb.Count) -ForegroundColor Cyan
if ($stillWeb.Count -gt 0 -and $stillWeb.Count -le 12) {
    Write-Host ("还没换实机图的：{0}" -f ($stillWeb -join '、')) -ForegroundColor DarkGray
}
if ($missing.Count -gt 0) {
    Write-Host ("缺图：{0}" -f ($missing -join '、')) -ForegroundColor DarkYellow
}

# ---------- 校验：文件在不在、有没有被改过 ----------
$bad = New-Object System.Collections.ArrayList
foreach ($it in $items) {
    $p = Join-Path $corpus $it.file
    if (-not (Test-Path $p)) {
        [void]$bad.Add("$($it.file) —— 文件不见了")
    } elseif ((Get-Sha $p) -ne $it.sha256) {
        [void]$bad.Add("$($it.file) —— 内容被改过（SHA-256 对不上）")
    }
}
Write-Host ""
if ($bad.Count -eq 0) {
    Write-Host "校验：所有图都在，内容和入库时一致。" -ForegroundColor Green
} else {
    Write-Host "校验发现问题：" -ForegroundColor Red
    foreach ($b in $bad) { Write-Host ("  {0}" -f $b) -ForegroundColor Red }
}
