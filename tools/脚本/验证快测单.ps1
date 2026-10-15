<#
  _fastcheck.ps1 —— 一次性分析脚本（用完即删）

  问题：用户每一轮都要框 80 个框（三份材料 56 段 + A 系列 24 段），太累。
        想改成「只框十几段」。问题是：少框真的不会漏掉真问题吗？

  做法：把每个归档快照当成「一轮实测」。每一轮只取该轮**新增**的记录
        （归档是完整快照，本轮新增 = 上一条快照之后的记录数）。
        然后比较两件事：
          ① 这一轮「全部框过的段」里错了几段
          ② 这一轮「候选快测单」里错了几段
        如果存在「全量有错、快测单却全对」的轮次 → 快测单会漏报真问题 → 不能采用。

  用法：powershell -ExecutionPolicy Bypass -File "tools\_fastcheck.ps1"
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ---------- 候选快测单：按历史出错率挑（改动只碰一条路时用它） ----------
$Fast = @(
    '2-E6', '3-X2', '3-X1', '3-P2', '3-C4', '2-E4', '1-D1', '1-B3',
    '3-C1', '1-C2', '1-A3', '3-C3', '2-R2', '2-E3', '1-A5',
    # 2-Z1 是补进来的：第一版快测单漏了它 5 轮（原因见下），它是「今天天和气」那个反复犯的真毛病
    '2-Z1',
    # 以下是历史上从没错过、但覆盖各条识别路径的哨兵
    # ⚠️ 原来是 '3-R3' —— 材料3 根本没有 R 组，这条是死条目，
    #    等于「俄文夹符号」这个哨兵从来没生效过。正确的是材料2 的 R3（Ошибка 404 …橙底绿字）。已修。
    '1-A1', '1-A2', '1-A4', '3-P3', '3-P1', '1-B1', '3-M3', '2-R3', '3-X3', '1-C1', '2-Z2', '3-M2'
)

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
function NormCase([string]$s) { return (Norm $s).ToLowerInvariant() }

# ---------- 标准答案 ----------
$answers = New-Object System.Collections.ArrayList
$matNo = 0
foreach ($n in '验收标准答案.txt', '验收标准答案2.txt', '验收标准答案3.txt') {
    $matNo++
    $p = Join-Path $root ("tools\素材\" + $n)
    $curId = $null; $curText = ''
    foreach ($l in [System.IO.File]::ReadAllLines($p, [System.Text.Encoding]::UTF8)) {
        if ($l -match '^\s*#' -or $l.Trim() -eq '') { continue }
        if ($l -match '^([A-Z]\d+)\|(.*)$') {
            if ($curId) { [void]$answers.Add([pscustomobject]@{ Mat = $matNo; N = $curId; Key = "$matNo-$curId"; Text = (Norm $curText) }) }
            $curId = $Matches[1]; $curText = $Matches[2]
        } else { $curText = $curText + ' ' + $l.Trim() }
    }
    if ($curId) { [void]$answers.Add([pscustomobject]@{ Mat = $matNo; N = $curId; Key = "$matNo-$curId"; Text = (Norm $curText) }) }
}
# 同文段合并：文本一样就算同一段（材料1 和材料2 都有 quick brown fox）
$byText = @{}
foreach ($a in $answers) {
    $k = NormCase $a.Text
    if (-not $byText.ContainsKey($k)) { $byText[$k] = New-Object System.Collections.ArrayList }
    [void]$byText[$k].Add($a)
}

function EditDist([string]$a, [string]$b) {
    $n = $b.Length
    $prev = New-Object 'int[]' ($n + 1); $cur = New-Object 'int[]' ($n + 1)
    for ($j = 0; $j -le $n; $j++) { $prev[$j] = $j }
    for ($i = 1; $i -le $a.Length; $i++) {
        $cur[0] = $i; $ci = $a[$i - 1]
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

# ⚠️ 必须模糊匹配：读错的记录跟标准答案不可能逐字相同。
#    用精确匹配对号 → 读错的永远归不到任何段 → 成绩永远是「0 段错」，量具骗人。
function Match-Seg([string]$got) {
    $g = NormCase $got
    if ($byText.ContainsKey($g)) { return @($byText[$g] | ForEach-Object { $_.Key }) }
    $best = 99999; $second = 99999; $bestKeys = @()
    foreach ($k in $byText.Keys) {
        $d = EditDist $g $k
        if ($d -lt $best) { $second = $best; $best = $d; $bestKeys = @($byText[$k] | ForEach-Object { $_.Key }) }
        elseif ($d -lt $second) { $second = $d }
    }
    $tol = [Math]::Max(4, [int]($g.Length / 12))
    if ($best -gt $tol) { return @() }
    if ($best -gt 0 -and (($second - $best) -lt 2 -and $second -lt $best * 2)) { return @() }
    return $bestKeys
}

# ---------- 逐轮分析 ----------
$files = @(Get-ChildItem (Join-Path $root 'tools\日志\用户实测') -Filter '第*轮-*.txt' | Sort-Object {
    [int](($_.BaseName -split '-')[0] -replace '第', '' -replace '轮', '')
})
$prevCount = 0
$rounds = New-Object System.Collections.ArrayList
foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    # 每条记录以 "##### 记录结束 #####" 收尾
    $idx = 0; $recs = New-Object System.Collections.ArrayList
    $t = $null; $txt = $null
    foreach ($l in $lines) {
        if ($l -match '^时间: (.+)$') { $t = $Matches[1] }
        elseif ($l -match '^原文: (.*)$') { $txt = $Matches[1] }
        elseif ($l -match '^#####') {
            $idx++
            if ($idx -gt $prevCount -and $txt -ne $null) { [void]$recs.Add([pscustomobject]@{ T = $t; Text = $txt }) }
        }
    }
    $prevCount = $idx
    if ($recs.Count -eq 0) { continue }
    [void]$rounds.Add([pscustomobject]@{ Name = $f.BaseName; Recs = $recs })
}

Write-Host ''
Write-Host '================ 逐轮：全量 vs 快测单 ================' -ForegroundColor Cyan
Write-Host ('快测单共 {0} 段' -f $Fast.Count)
Write-Host ''
$miss = 0; $roundsWithFullErr = 0; $roundsFastBad = 0
foreach ($r in $rounds) {
    $seenFull = @{}; $badFull = @{}
    $seenFast = @{}; $badFast = @{}
    foreach ($rec in $r.Recs) {
        # ⚠️ 这里必须走 Match-Seg（模糊匹配）。用 $byText.ContainsKey 精确匹配的话，
        #    读错的记录永远归不到任何段 → 每轮都是「0 段错」——量具骗人（已踩过）。
        $hit = @(Match-Seg $rec.Text)
        foreach ($kk in $hit) {
            if (-not $seenFull.ContainsKey($kk)) { $seenFull[$kk] = $true }
        }
        if ($hit.Count -eq 0) { continue }
        # 这段的正确答案：同文组里的任意一个都算对
        foreach ($kk in $hit) {
            $ans = $answers | Where-Object { $_.Key -eq $kk } | Select-Object -First 1
            $ok = (NormCase $rec.Text) -eq (NormCase $ans.Text)
            if (-not $ok) { $badFull[$kk] = $true }
        }
    }
    foreach ($kk in $seenFull.Keys) {
        if ($Fast -contains $kk) { $seenFast[$kk] = $true; if ($badFull.ContainsKey($kk)) { $badFast[$kk] = $true } }
    }
    $nFast = $seenFast.Count; $nFastBad = @($badFast.Keys).Count
    if ($nFast -eq 0) { continue }
    $r | Add-Member -NotePropertyName BadKeys -NotePropertyValue @($badFull.Keys) -Force
    $flag = ''
    if ($nFastBad -ge 2) { $roundsFastBad++ }
    if (@($badFull.Keys).Count -ge 2) { $roundsWithFullErr++ }
    if ($nFastBad -eq 0 -and @($badFull.Keys).Count -ge 2) { $flag = '  ← ⚠️ 快测单漏报！'; $miss++ }
    $missed = @($badFull.Keys | Where-Object { $Fast -notcontains $_ })
    if ($missed.Count -gt 0) { $flag = '  ← 漏：' + ($missed -join '、') }
    Write-Host ('{0,-22} 快测单 {1,2} 段错 {2}  ｜ 该轮全部框过的段错 {3}{4}' -f $r.Name, $nFast, $nFastBad, @($badFull.Keys).Count, $flag) `
        -ForegroundColor $(if ($flag) { 'Red' } elseif ($nFastBad -ge 1) { 'Yellow' } else { 'DarkGray' })
}
Write-Host ''
Write-Host '================ 全程统计：漏掉的是哪几段 ================' -ForegroundColor Cyan
$missedSegs = @{}
foreach ($r in $rounds) {
    foreach ($kk in $r.BadKeys) {
        if ($Fast -notcontains $kk) {
            $ans = $answers | Where-Object { $_.Key -eq $kk } | Select-Object -First 1
            if (-not $missedSegs.ContainsKey($kk)) { $missedSegs[$kk] = [pscustomobject]@{ Key = $kk; N = 0; Rounds = @() } }
            $missedSegs[$kk].N++
            $missedSegs[$kk].Rounds += $r.Name
        }
    }
}
if ($missedSegs.Count -eq 0) { Write-Host '没有漏掉任何一段。' -ForegroundColor Green }
else {
    Write-Host '以下段「在某一轮里出过错，但不在快测单上」——要么加进快测单，要么认这个漏：' -ForegroundColor Yellow
    foreach ($k in ($missedSegs.Keys | Sort-Object { -$missedSegs[$_].N })) {
        $v = $missedSegs[$k]
        $ans = $answers | Where-Object { $_.Key -eq $k } | Select-Object -First 1
        Write-Host ("  {0,-7} 出错 {1} 轮：{2}" -f $v.Key, $v.N, ($v.Rounds -join '、')) -ForegroundColor Yellow
        Write-Host ("          答案：{0}" -f $ans.Text.Substring(0, [Math]::Min(40, $ans.Text.Length)))
    }
}
Write-Host ''
Write-Host ("用了快测单的轮次数：{0}" -f $rounds.Count) -ForegroundColor Gray
Write-Host ("其中「全量有 ≥1 段错」的轮次：{0}" -f (@($rounds | Where-Object { $_.BadKeys.Count -ge 1 }).Count)) -ForegroundColor Gray
Write-Host ("快测单漏掉过段的轮次：{0}" -f (@($rounds | Where-Object { $_.PSObject.Properties['BadKeys'] -and @($_.BadKeys | Where-Object { $Fast -notcontains $_ }).Count -ge 1 }).Count)) -ForegroundColor Yellow
