# ============================================================
# 把「调试截图」批量收成实机语料
#
# 用法（材料1，每段 5 张，从 A1 开始）：
#   & ".\tools\脚本\收实机.ps1" -Mat 1 -Per 5
#
# 它做什么：
#   1) 按时间顺序取 release\v0.3.4.2\debug-screenshots 里的 png
#   2) 已经进过图库的（SHA-256 一样）自动跳过，所以可以反复跑
#   3) 每 $Per 张归一段，按材料里的段顺序（A1、A2、A3…）分配
#   4) 复制到 tools\语料\实机\ 并起名 1-A1.png / 1-A1-2.png…
#   5) 调 语料.ps1 实机 正式入库（会先把该段的网页截图拿掉，实机图成为 1.png）
#
# -DryRun 只打印分配表，不动任何东西。
# ============================================================
param(
    [int]$Mat = 1,
    [int]$Per = 5,
    [string]$Counts = '',
    [string]$From = '',
    [string]$Dir = 'release\v0.3.4.2\debug-screenshots',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$corpus = Join-Path $root 'tools\语料'
$manifest = Join-Path $corpus '清单.json'
$drop = Join-Path $corpus '实机'

if ($Mat -lt 1 -or $Mat -gt 3) { throw '-Mat 必须是 1、2 或 3' }
if ($Per -lt 1) { throw '-Per 至少 1' }

$shotDir = if ([IO.Path]::IsPathRooted($Dir)) { $Dir } else { Join-Path $root $Dir }
if (-not (Test-Path $shotDir)) { throw "找不到截图目录：$shotDir" }

# ---------- 该材料的段顺序 ----------
$ansFile = @('验收标准答案.txt', '验收标准答案2.txt', '验收标准答案3.txt')[$Mat - 1]
$ansPath = Join-Path $root "tools\素材\$ansFile"
$segments = New-Object System.Collections.ArrayList
foreach ($l in [IO.File]::ReadAllLines($ansPath, [Text.Encoding]::UTF8)) {
    if ($l -match '^\s*([A-Z]\d+)\|') { [void]$segments.Add($Matches[1]) }
}

$startAt = 0
if ($From) {
    $startAt = $segments.IndexOf($From.ToUpper())
    if ($startAt -lt 0) { throw "材料$Mat 里没有段号 $From" }
}
$todo = @($segments | Select-Object -Skip $startAt)

# ---------- 图库里已有的 SHA ----------
$known = @{}
if (Test-Path $manifest) {
    $doc = [IO.File]::ReadAllText($manifest, [Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($it in @($doc.images)) { $known[$it.sha256] = $it.file }
}

# ---------- 新截图（按时间排） ----------
$shots = @(Get-ChildItem $shotDir -Filter *.png -File | Sort-Object LastWriteTime, Name)
$fresh = New-Object System.Collections.ArrayList
$seen = @{}
foreach ($s in $shots) {
    $h = (Get-FileHash $s.FullName -Algorithm SHA256).Hash.ToLower()
    if ($known.ContainsKey($h)) { continue }   # 已经入库过，跳过
    if ($seen.ContainsKey($h)) { continue }    # 这一批里重复框的同一张，跳过（否则后面全错位）
    $seen[$h] = $true
    [void]$fresh.Add([pscustomobject]@{ File = $s; Sha = $h })
}

if ($fresh.Count -eq 0) {
    Write-Host "没有新截图（$shotDir 里的图都已经在库里了）。" -ForegroundColor Yellow
    return
}

$chunks = [math]::Ceiling($fresh.Count / $Per)

# -Counts 用来处理「某一段少框了一张」的情况，例如 "5,5,5,5,4,5,5,5,5,5,5"
$groupCounts = $null
if ($Counts) {
    $groupCounts = @($Counts -split '[,，\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
    $sum = ($groupCounts | Measure-Object -Sum).Sum
    if ($sum -ne $fresh.Count) {
        throw ("-Counts 合计 {0} 张，但新截图有 {1} 张，对不上" -f $sum, $fresh.Count)
    }
    $chunks = $groupCounts.Count
}

Write-Host ("新截图 {0} 张 → 按每段 {1} 张算，够分 {2} 段；材料{3} 还剩 {4} 段没分配。" -f `
        $fresh.Count, $Per, $chunks, $Mat, $todo.Count) -ForegroundColor Cyan

if ($chunks -gt $todo.Count) {
    Write-Host ("★ 张数比剩余段数多：{0} 段 vs 只剩 {1} 段。后面会分不出去，先停下。" -f $chunks, $todo.Count) -ForegroundColor Red
    return
}
if (-not $Counts -and $fresh.Count % $Per -ne 0) {
    Write-Host ("★ 张数不是 {0} 的整数倍（余 {1} 张），最后一段会不足数，请确认是不是少框了。" -f $Per, ($fresh.Count % $Per)) -ForegroundColor Yellow
}

# ---------- 分配 ----------
$plan = New-Object System.Collections.ArrayList
$idx = 0
for ($i = 0; $i -lt $chunks; $i++) {
    $seg = $todo[$i]
    $take = if ($groupCounts) { $groupCounts[$i] } else { $Per }
    $mine = @($fresh | Select-Object -Skip $idx -First $take)
    $idx += $take
    [void]$plan.Add([pscustomobject]@{ Seg = $seg; Files = $mine })
}

Write-Host ""
Write-Host "分配表：" -ForegroundColor Cyan
foreach ($p in $plan) {
    $t = $p.Files[0].File.LastWriteTime.ToString('HH:mm:ss')
    Write-Host ("  材料{0}-{1}  {2} 张   首张 {3}  {4}" -f $Mat, $p.Seg, $p.Files.Count, $t, $p.Files[0].File.Name)
}

if ($DryRun) {
    Write-Host ""
    Write-Host "（-DryRun，没有动任何文件）" -ForegroundColor DarkGray
    return
}

# ---------- 落地 ----------
if (-not (Test-Path $drop)) { New-Item -ItemType Directory -Path $drop -Force | Out-Null }
foreach ($p in $plan) {
    $n = 0
    foreach ($f in $p.Files) {
        $n++
        $name = if ($n -eq 1) { "$Mat-$($p.Seg).png" } else { "$Mat-$($p.Seg)-$n.png" }
        Copy-Item -LiteralPath $f.File.FullName -Destination (Join-Path $drop $name) -Force
    }
}
Write-Host ""
& (Join-Path $PSScriptRoot '语料.ps1') 实机
