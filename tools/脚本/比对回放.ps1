# 比对「图库回放」结果与三份验收材料的标准答案（56 段）
# 用法：& ".\tools\脚本\比对回放.ps1" -Result "tools\临时\回放-结果.txt"
#
# 归一化、编辑距离、读答案的规则与 比对成绩.ps1 完全一致，保证两把尺子量出来一样。

param(
    [string]$Result = 'tools\临时\回放-结果.txt',
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# ---------- 归一化（与 比对成绩.ps1 同一份）----------
function Norm([string]$s) {
    if ($null -eq $s) { return '' }
    $s = $s -replace '([\u4e00-\u9fff])\s*\u23ce\s*([\u4e00-\u9fff])', '$1$2'
    $s = $s -replace '\u23ce', ' '
    $s = $s -replace '([\u4e00-\u9fff])\s+([\u4e00-\u9fff])', '$1$2'
    $s = $s -replace '[\u2018\u2019\u201B]', "'" -replace '[\u201C\u201D\u201F]', '"'
    $s = $s -replace '[Мм][Бб]', 'MB' -replace '[Mm][Bb]', 'MB'
    $s = $s -replace '\s+', ' '
    return $s.Trim()
}

# ---------- 编辑距离 ----------
function EditDist([string]$a, [string]$b) {
    $n = $b.Length
    $prev = New-Object 'int[]' ($n + 1)
    $cur = New-Object 'int[]' ($n + 1)
    for ($j = 0; $j -le $n; $j++) { $prev[$j] = $j }
    for ($i = 1; $i -le $a.Length; $i++) {
        $cur[0] = $i
        $ci = $a[$i - 1]
        for ($j = 1; $j -le $n; $j++) {
            $cost = if ($ci -eq $b[$j - 1]) { 0 } else { 1 }
            $m = $prev[$j - 1] + $cost
            if (($prev[$j] + 1) -lt $m) { $m = $prev[$j] + 1 }
            if (($cur[$j - 1] + 1) -lt $m) { $m = $cur[$j - 1] + 1 }
            $cur[$j] = $m
        }
        $t = $prev; $prev = $cur; $cur = $t
    }
    return $prev[$n]
}

# ---------- 读标准答案 ----------
$answers = [ordered]@{}
$matNo = 0
foreach ($n in '验收标准答案.txt', '验收标准答案2.txt', '验收标准答案3.txt') {
    $matNo++
    $p = Join-Path $root "tools\素材\$n"
    if (-not (Test-Path $p)) { throw "找不到标准答案：$p" }
    $curId = $null; $curText = ''
    foreach ($l in [System.IO.File]::ReadAllLines($p, [System.Text.Encoding]::UTF8)) {
        if ($l -match '^\s*#' -or $l.Trim() -eq '') { continue }
        if ($l -match '^([A-Z]\d+)\|(.*)$') {
            if ($curId) { $answers["材料$matNo-$curId"] = Norm $curText }
            $curId = $Matches[1]; $curText = $Matches[2]
        } elseif ($curId) {
            $curText = $curText + ' ' + $l.Trim()
        }
    }
    if ($curId) { $answers["材料$matNo-$curId"] = Norm $curText }
}

# ---------- 读回放结果 ----------
$resultPath = if ([IO.Path]::IsPathRooted($Result)) { $Result } else { Join-Path $root $Result }
if (-not (Test-Path $resultPath)) { throw "找不到回放结果：$resultPath" }

$actual = @{}
foreach ($l in [System.IO.File]::ReadAllLines($resultPath, [System.Text.Encoding]::UTF8)) {
    if ($l -match '^([^|]+)\|(.*)$') { $actual[$Matches[1]] = Norm $Matches[2] }
}

# ---------- 比对 ----------
$ok = 0
$bad = New-Object System.Collections.ArrayList
$missing = New-Object System.Collections.ArrayList

foreach ($id in $answers.Keys) {
    if (-not $actual.ContainsKey($id)) { [void]$missing.Add($id); continue }
    if ($actual[$id] -eq $answers[$id]) { $ok++ }
    else { [void]$bad.Add([pscustomobject]@{ Id = $id; Want = $answers[$id]; Got = $actual[$id] }) }
}

$total = $answers.Count
Write-Host ""
Write-Host "=== 图库回放成绩 ===" -ForegroundColor Cyan
Write-Host ("  应测 {0} 段，判对 {1} 段，判错 {2} 段，缺 {3} 段" -f $total, $ok, $bad.Count, $missing.Count)
if ($bad.Count -gt 0) {
    Write-Host ""
    Write-Host "  判错明细：" -ForegroundColor Yellow
    foreach ($b in $bad) {
        $d = EditDist $b.Want $b.Got
        Write-Host ("    [{0}] 差 {1} 字符" -f $b.Id, $d) -ForegroundColor Yellow
        Write-Host ("      答案：{0}" -f $b.Want)
        Write-Host ("      实得：{0}" -f $b.Got)
    }
}
if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host ("  缺测：{0}" -f ($missing -join '、')) -ForegroundColor Red
}

# 同一段存了多张图时（例如 E4 有 3 张），多出来的单独列出来，不计入 56 段
$extra = $actual.Keys | Where-Object { -not $answers.Contains($_) } | Sort-Object
if ($extra.Count -gt 0) {
    Write-Host ""
    if (-not $All) {
        Write-Host "  额外图（不计入成绩，供参考）：" -ForegroundColor DarkGray
        foreach ($e in $extra) { Write-Host ("    [{0}] {1}" -f $e, $actual[$e]) -ForegroundColor DarkGray }
    } else {
        # -All：把每一张图都按它所属段的标准答案判一遍，看这一段到底稳不稳
        Write-Host "  逐张成绩（同一段多张图全部参与判定）：" -ForegroundColor Cyan
        $bySeg = @{}
        foreach ($e in ($actual.Keys | Sort-Object)) {
            $seg = $e -replace '-\d+$', ''
            if (-not $answers.Contains($seg)) { continue }
            if (-not $bySeg.ContainsKey($seg)) { $bySeg[$seg] = New-Object System.Collections.ArrayList }
            [void]$bySeg[$seg].Add([pscustomobject]@{ Key = $e; Ok = ($actual[$e] -eq $answers[$seg]) })
        }
        $perfect = 0; $shaky = 0
        foreach ($s in ($bySeg.Keys | Sort-Object)) {
            $rows = @($bySeg[$s])
            $n = @($rows | Where-Object Ok).Count
            if ($n -eq $rows.Count) { $perfect++; continue }
            $shaky++
            Write-Host ("    {0,-12} {1}/{2} 张对" -f $s, $n, $rows.Count) -ForegroundColor Yellow
            foreach ($r in ($rows | Where-Object { -not $_.Ok })) {
                Write-Host ("        错：{0}" -f $actual[$r.Key]) -ForegroundColor DarkYellow
            }
        }
        Write-Host ("  逐张看：{0} 段全对，{1} 段有图认错（共 {2} 段）" -f $perfect, $shaky, ($perfect + $shaky)) -ForegroundColor Cyan
    }
}
Write-Host ""
