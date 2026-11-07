# 比对「图库回放」结果与三份验收材料的标准答案（56 段）
# 用法：& ".\tools\脚本\比对回放.ps1" -Result "tools\临时\回放-结果.txt"
#
# 归一化、编辑距离、读答案的规则与 比对成绩.ps1 完全一致，保证两把尺子量出来一样。
#
# 【按行（S0，2026-10-26）】段级 = 整段一字不差；行级 = 这一行一字不差。用户真实用法是
# 框一行或框一块面板，所以阶段三主指标换成行级（见 tools\阶段三-方案.md）。
#   跑行级成绩：& ".\tools\脚本\比对回放.ps1" -按行 -Result "tools\语料\回放-结果.txt"
#   验量具自检：& ".\tools\脚本\比对回放.ps1" -自检      ← 不过 = 尺子坏，停，不许改识别代码

param(
    [string]$Result = 'tools\临时\回放-结果.txt',
    [switch]$All,
    [switch]$Approve,
    [switch]$按行,
    [string]$行真值 = 'tools\素材\行真值.json'
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

# ---------- 实战图：答案就存在图库清单里，不进 56 段 ----------
# 不加进 $answers 是故意的：56 段是验收尺子，加进实战图会让这把尺子永远动。
# 实战图单独列一段「实战」成绩，有错的就大声报出来。
$corpusManifest = Join-Path $root 'tools\语料\清单.json'
$battle = [ordered]@{}
if (Test-Path $corpusManifest) {
    $doc = [System.IO.File]::ReadAllText($corpusManifest, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($it in @($doc.images)) {
        if ($it.mat -ne 0 -or -not $it.answer) { continue }
        $n = [int]([IO.Path]::GetFileNameWithoutExtension($it.file))
        $key = if ($n -gt 1) { "实战-$($it.id)-$n" } else { "实战-$($it.id)" }
        $battle[$key] = Norm ([string]$it.answer)
    }
}

# ---------- 行级量具（S0）----------
# 回放结果把换行折成了 ⏎（ReplayRunner.cs:107），所以先按 ⏎ 拆行，再逐行用同一个 Norm()。
# 不新建第二套归一化：段级和行级必须说同一种「算不算一样」。
function Split-Lines([string]$raw) {
    if ([string]::IsNullOrEmpty($raw)) { return @() }
    $out = New-Object System.Collections.ArrayList
    foreach ($p in ($raw -split ([string][char]0x23CE))) {
        $t = Norm $p
        if ($t -ne '') { [void]$out.Add($t) }
    }
    return @($out)
}

# 按行对齐：真值行序列 vs 实得行序列做行级编辑距离。
# 为什么不能按位置硬比：少认一行时，后面每一行都会错位，报出来是「错 N-1 行」——
# 那会把「漏了一行」谎报成「全崩了」，正是本项目量具骗人的老毛病。
function Align-Lines([string[]]$want, [string[]]$got) {
    $n = $want.Count; $m = $got.Count
    $d = New-Object 'int[,]' ($n + 1), ($m + 1)
    for ($i = 0; $i -le $n; $i++) { $d[$i, 0] = $i }
    for ($j = 0; $j -le $m; $j++) { $d[0, $j] = $j }
    for ($i = 1; $i -le $n; $i++) {
        for ($j = 1; $j -le $m; $j++) {
            $cost = if ($want[$i - 1] -eq $got[$j - 1]) { 0 } else { 1 }
            # 二维数组下标里的算式必须加括号：$d[$i - 1, $j - 1] 会被当成「两个下标」各减 1。
            $v = $d[($i - 1), ($j - 1)] + $cost
            if (($d[($i - 1), $j] + 1) -lt $v) { $v = $d[($i - 1), $j] + 1 }
            if (($d[$i, ($j - 1)] + 1) -lt $v) { $v = $d[$i, ($j - 1)] + 1 }
            $d[$i, $j] = $v
        }
    }
    $ops = New-Object System.Collections.ArrayList
    $i = $n; $j = $m
    while ($i -gt 0 -or $j -gt 0) {
        if ($i -gt 0 -and $j -gt 0 -and $want[$i - 1] -eq $got[$j - 1] -and $d[$i, $j] -eq $d[($i - 1), ($j - 1)]) {
            [void]$ops.Insert(0, [pscustomobject]@{ Kind = '对'; Want = $want[$i - 1]; Got = $got[$j - 1] }); $i--; $j--; continue
        }
        if ($i -gt 0 -and $j -gt 0 -and $d[$i, $j] -eq $d[($i - 1), ($j - 1)] + 1) {
            [void]$ops.Insert(0, [pscustomobject]@{ Kind = '改'; Want = $want[$i - 1]; Got = $got[$j - 1] }); $i--; $j--; continue
        }
        if ($i -gt 0 -and $d[$i, $j] -eq $d[($i - 1), $j] + 1) {
            [void]$ops.Insert(0, [pscustomobject]@{ Kind = '丢'; Want = $want[$i - 1]; Got = $null }); $i--; continue
        }
        [void]$ops.Insert(0, [pscustomobject]@{ Kind = '多'; Want = $null; Got = $got[$j - 1] }); $j--
    }
    return @($ops)
}

# 一张图一条真值记录：{ id, mode, lines[] }。返回总行数/对/错（错 = 改+丢+多）。
# 缺测不算「0 行」：那会把没跑的图洗成满分，按整行丢计。
function Get-LineReport($entries, [hashtable]$actualRaw) {
    $rows = New-Object System.Collections.ArrayList
    $missing = New-Object System.Collections.ArrayList
    $modes = @{}
    $total = 0; $ok = 0; $changed = 0; $lost = 0; $extra = 0

    foreach ($e in $entries) {
        $mode = if ($e.mode) { [string]$e.mode } else { '未标' }
        if (-not $modes.ContainsKey($mode)) {
            $modes[$mode] = [pscustomobject]@{ 总 = 0; 对 = 0; 改 = 0; 丢 = 0; 多 = 0 }
        }
        $st = $modes[$mode]
        $want = @($e.lines | ForEach-Object { Norm ([string]$_) } | Where-Object { $_ -ne '' })
        $total += $want.Count
        $st.总 += $want.Count

        if (-not $actualRaw.ContainsKey([string]$e.id)) {
            [void]$missing.Add([string]$e.id)
            $lost += $want.Count
            $st.丢 += $want.Count
            foreach ($w in $want) {
                [void]$rows.Add([pscustomobject]@{ Id = [string]$e.id; Mode = $mode; Kind = '丢'; Want = $w; Got = '<缺测>'; Dist = $w.Length })
            }
            continue
        }

        foreach ($op in (Align-Lines $want (Split-Lines $actualRaw[[string]$e.id]))) {
            switch ($op.Kind) {
                '对' { $ok++; $st.对++ }
                '改' {
                    $changed++; $st.改++
                    [void]$rows.Add([pscustomobject]@{ Id = [string]$e.id; Mode = $mode; Kind = '改'; Want = $op.Want; Got = $op.Got; Dist = (EditDist $op.Want $op.Got) })
                }
                '丢' {
                    $lost++; $st.丢++
                    [void]$rows.Add([pscustomobject]@{ Id = [string]$e.id; Mode = $mode; Kind = '丢'; Want = $op.Want; Got = '<没认出来>'; Dist = $op.Want.Length })
                }
                '多' {
                    $extra++; $st.多++
                    [void]$rows.Add([pscustomobject]@{ Id = [string]$e.id; Mode = $mode; Kind = '多'; Want = '<无此行>'; Got = $op.Got; Dist = $op.Got.Length })
                }
            }
        }
    }

    return [pscustomobject]@{
        Total = $total; Ok = $ok; Changed = $changed; Lost = $lost; Extra = $extra
        Bad = ($changed + $lost + $extra)
        Rows = @($rows); Missing = @($missing); Modes = $modes
    }
}

function Show-LineReport($r) {
    $pct = if ($r.Total -gt 0) { 100.0 * $r.Ok / $r.Total } else { 0 }
    Write-Host ""
    Write-Host "=== 行级成绩（按行）===" -ForegroundColor Cyan
    Write-Host ("  真值 {0} 行：对 {1} / 错 {2}（改 {3}、丢 {4}、多 {5}）→ 全量 {6:N1}%" -f `
        $r.Total, $r.Ok, $r.Bad, $r.Changed, $r.Lost, $r.Extra, $pct) -ForegroundColor $(if ($r.Bad -eq 0) { 'Green' } else { 'Yellow' })

    foreach ($mode in ($r.Modes.Keys | Sort-Object)) {
        $st = $r.Modes[$mode]
        $p = if ($st.总 -gt 0) { 100.0 * $st.对 / $st.总 } else { 0 }
        $label = switch ($mode) { '单行' { 'L_行' } '整块' { 'L_整块' } default { $mode } }
        Write-Host ("  {0}（{1}）：对 {2} / 错 {3}（改 {4}、丢 {5}、多 {6}）→ {0} {7:N1}%" -f `
            $label, $mode, $st.对, ($st.改 + $st.丢 + $st.多), $st.改, $st.丢, $st.多, $p)
    }

    if ($r.Bad -gt 0) {
        $sum = [double]$r.Bad
        Write-Host ("  错法归类：改字 {0} 例（{1:N0}%）、整行丢 {2} 例（{3:N0}%）、多出行 {4} 例（{5:N0}%）" -f `
            $r.Changed, (100 * $r.Changed / $sum), $r.Lost, (100 * $r.Lost / $sum), $r.Extra, (100 * $r.Extra / $sum)) -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  逐行明细：" -ForegroundColor Yellow
        foreach ($row in $r.Rows) {
            Write-Host ("    [{0}][{1}] {2}  差 {3}" -f $row.Id, $row.Mode, $row.Kind, $row.Dist) -ForegroundColor Yellow
            Write-Host ("      答案：{0}" -f $row.Want)
            Write-Host ("      实得：{0}" -f $row.Got)
        }
    }
    if ($r.Missing.Count -gt 0) {
        Write-Host ""
        Write-Host ("  缺测（按整行丢计）：{0}" -f ($r.Missing -join '、')) -ForegroundColor Red
    }
    Write-Host ""
}



$actual = @{}
# $actualRaw 是「没归一化的原文整段」，按行判分要用它再按 ⏎ 拆行（Split-Lines 内部自己 Norm）。
# 【2026-11-07 补】9775bc3 那次合并把 -按行 整段删掉时连这个变量一起删了，
#   回放-行.ps1 一直在调 -按行，但传进去的是 $null，报「不能对 Null 值表达式调用方法」。
$actualRaw = @{}
foreach ($l in [System.IO.File]::ReadAllLines($resultPath, [System.Text.Encoding]::UTF8)) {
    if ($l -match '^([^|]+)\|(.*)$') {
        $actualRaw[$Matches[1]] = $Matches[2]
        $actual[$Matches[1]] = Norm $Matches[2]
    }
}

# ---------- 按行判分（-按行）----------
# 真值文件：tools\语料\行真值.json，每张图一条 { file, mode, lines[], 游戏, 档, 极性 }。
# 图用「回放清单（id|路径）」对到结果上的 id —— 真值按图存，不跟回放命名绑死。
if ($按行) {
    $truthPath = if ([IO.Path]::IsPathRooted($行真值)) { $行真值 } else { Join-Path $root $行真值 }
    if (-not (Test-Path $truthPath)) { throw "找不到行真值：$truthPath（S1 才会填）" }
    $truthDoc = [System.IO.File]::ReadAllText($truthPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if (@($truthDoc.images).Count -eq 0) {
        Write-Host ("  行真值还是空的（S1 才填）：{0}" -f $truthPath) -ForegroundColor Yellow
        Write-Host ""
        return
    }

    $corpus = Join-Path $root 'tools\语料'
    $pathToId = @{}
    $listPath = Join-Path $corpus '回放-清单.txt'
    if (Test-Path $listPath) {
        foreach ($l in [System.IO.File]::ReadAllLines($listPath, [System.Text.Encoding]::UTF8)) {
            if ($l -match '^([^|]+)\|(.*)$') {
                $pathToId[([IO.Path]::GetFullPath($Matches[2].Trim())).ToLowerInvariant()] = $Matches[1].Trim()
            }
        }
    }

    $entries = foreach ($e in @($truthDoc.images)) {
        $id = [string]$e.id
        if (-not $id) {
            $full = [IO.Path]::GetFullPath((Join-Path $corpus ([string]$e.file)))
            $key = $full.ToLowerInvariant()
            $id = if ($pathToId.ContainsKey($key)) { $pathToId[$key] } else { "未入清单:$($e.file)" }
        }
        [pscustomobject]@{ id = $id; mode = [string]$e.mode; lines = @($e.lines) }
    }

    Show-LineReport (Get-LineReport @($entries) $actualRaw)
    return
}

# ---------- 比对 ----------
$ok = 0
$bad = New-Object System.Collections.ArrayList
$missing = New-Object System.Collections.ArrayList
$nowOk = @{}
# 差多少字符也记下来：只记「过没过」的话，「差 1 个字」恶化成「整行丢了」
# 两头都是 false，成绩单上看不出变化，等于没报警。
$nowDist = @{}

foreach ($id in $answers.Keys) {
    if (-not $actual.ContainsKey($id)) { [void]$missing.Add($id); continue }
    $nowOk[$id] = ($actual[$id] -eq $answers[$id])
    $nowDist[$id] = EditDist $answers[$id] $actual[$id]
    if ($nowOk[$id]) { $ok++ }
    else { [void]$bad.Add([pscustomobject]@{ Id = $id; Want = $answers[$id]; Got = $actual[$id] }) }
}

$total = $answers.Count
Write-Host ""
Write-Host "=== 图库回放成绩 ===" -ForegroundColor Cyan
Write-Host ("  应测 {0} 段，判对 {1} 段，判错 {2} 段，缺 {3} 段" -f $total, $ok, $bad.Count, $missing.Count)

# ---------- 实战图成绩（单独一把尺子）----------
$battleOk = 0
$battleBad = New-Object System.Collections.ArrayList
$battleMissing = New-Object System.Collections.ArrayList
foreach ($k in $battle.Keys) {
    if (-not $actual.ContainsKey($k)) { [void]$battleMissing.Add($k); continue }
    $nowOk[$k] = ($actual[$k] -eq $battle[$k])
    $nowDist[$k] = EditDist $battle[$k] $actual[$k]
    if ($nowOk[$k]) { $battleOk++ }
    else { [void]$battleBad.Add([pscustomobject]@{ Id = $k; Want = $battle[$k]; Got = $actual[$k] }) }
}
if ($battle.Count -gt 0) {
    Write-Host ("  实战图 {0} 张：对 {1}，错 {2}，缺 {3}" -f $battle.Count, $battleOk, $battleBad.Count, $battleMissing.Count) -ForegroundColor $(if ($battleBad.Count -eq 0) { 'Green' } else { 'Yellow' })
    if ($battleBad.Count -gt 0) {
        foreach ($b in $battleBad) {
            Write-Host ("    [{0}] 差 {1} 字符" -f $b.Id, (EditDist $b.Want $b.Got)) -ForegroundColor Yellow
            Write-Host ("      答案：{0}" -f $b.Want)
            Write-Host ("      实得：{0}" -f $b.Got)
        }
    }
    if ($battleMissing.Count -gt 0) {
        Write-Host ("    实战图缺测：{0}" -f ($battleMissing -join '、')) -ForegroundColor Red
    }
}
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
$extra = $actual.Keys | Where-Object { -not $answers.Contains($_) -and -not $battle.Contains($_) } | Sort-Object
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

# ---------- 与基线对比（判退步）----------
# 基线只记「每段过没过」，不存原文 —— 原文长得多，基线要小到能进 git、一眼看得懂。
# 放 `tools\素材\`（进 git）而不是 `tools\语料\`（被 .gitignore 忽略，会被清掉）。
$baselinePath = Join-Path $root 'tools\素材\基线.json'

if ($Approve) {
    if ($missing.Count -gt 0 -or $battleMissing.Count -gt 0) {
        Write-Host ("  ⛔ 缺测 {0} 段（56 段里缺 {1}、实战图缺 {2}），不能当基线（缺的段会被记成「错」，下次就会谎报退步）" -f ($missing.Count + $battleMissing.Count), $missing.Count, $battleMissing.Count) -ForegroundColor Red
        Write-Host ""
        return
    }
    $obj = [ordered]@{
        时间 = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        结果 = $Result
        段   = $nowOk
        # 差多少字符：只给「本来就错」的段用。它让「错得更厉害」也能报警。
        差   = $nowDist
    }
    $dir = Split-Path -Parent $baselinePath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllText($baselinePath, ($obj | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("  ✅ 已把这轮写成新基线（{0} 段判对）：{1}" -f $ok, $baselinePath) -ForegroundColor Green
    Write-Host ""
    return
}

if (-not (Test-Path $baselinePath)) {
    Write-Host "  没有基线可比（第一次用）。确认这轮成绩没问题后，加 -Approve 跑一次存成基线。" -ForegroundColor DarkGray
    Write-Host ""
    return
}

$base = Get-Content $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
$baseOk = @{}
foreach ($p in $base.段.PSObject.Properties) { $baseOk[$p.Name] = [bool]$p.Value }
# 老基线没有「差」这一栏，读不到就当没有（那几张就只能按老办法，只判过没过）。
$baseDist = @{}
if ($base.PSObject.Properties['差']) {
    foreach ($p in $base.差.PSObject.Properties) { $baseDist[$p.Name] = [int]$p.Value }
}

$regressed = New-Object System.Collections.ArrayList
$improved = New-Object System.Collections.ArrayList
$stillBad = New-Object System.Collections.ArrayList
# 「一直错」里又错得更厉害的那些：原来差 1 个字，现在整行没了。
$worsened = New-Object System.Collections.ArrayList

foreach ($id in $nowOk.Keys) {
    if (-not $baseOk.ContainsKey($id)) { continue }
    $was = $baseOk[$id]; $now = $nowOk[$id]
    if ($was -and -not $now) { [void]$regressed.Add($id) }
    elseif (-not $was -and $now) { [void]$improved.Add($id) }
    elseif (-not $was) {
        [void]$stillBad.Add($id)
        if ($baseDist.ContainsKey($id) -and $nowDist.ContainsKey($id) -and $nowDist[$id] -gt $baseDist[$id]) {
            [void]$worsened.Add([pscustomobject]@{ Id = $id; Was = $baseDist[$id]; Now = $nowDist[$id] })
        }
    }
}

Write-Host ("=== 与基线对比（基线 {0}）===" -f $base.时间) -ForegroundColor Cyan
if ($regressed.Count -eq 0) {
    Write-Host "  ✅ 没有退步：基线里对的段，这轮还都对" -ForegroundColor Green
} else {
    Write-Host ("  ❌ 退步 {0} 段（基线对、这轮错）：" -f $regressed.Count) -ForegroundColor Red
    foreach ($id in ($regressed | Sort-Object)) { Write-Host ("     {0}" -f $id) -ForegroundColor Red }
}
if ($improved.Count -gt 0) {
    Write-Host ("  ✅ 修好 {0} 段：{1}" -f $improved.Count, (($improved | Sort-Object) -join '、')) -ForegroundColor Green
}
if ($worsened.Count -gt 0) {
    Write-Host ("  ❌ 错得更厉害 {0} 段（本来就是错的，但差得更多了）：" -f $worsened.Count) -ForegroundColor Red
    foreach ($w in ($worsened | Sort-Object Id)) {
        Write-Host ("     {0}  差 {1} 字符 → 差 {2} 字符" -f $w.Id, $w.Was, $w.Now) -ForegroundColor Red
    }
}
# 上面已经点过名的，不再重复列进「一直错」，免得同一段出现两遍。
$worsenedIds = @($worsened | ForEach-Object { $_.Id })
$stillBadOnly = @($stillBad | Where-Object { $worsenedIds -notcontains $_ })
if ($stillBadOnly.Count -gt 0) {
    Write-Host ("  ⚪ 一直错 {0} 段：{1}" -f $stillBadOnly.Count, (($stillBadOnly | Sort-Object) -join '、')) -ForegroundColor DarkGray
}
$newSeg = @($nowOk.Keys | Where-Object { -not $baseOk.ContainsKey($_) } | Sort-Object)
if ($newSeg.Count -gt 0) {
    Write-Host ("  🆕 基线里没有的新段 {0} 段：{1}" -f $newSeg.Count, ($newSeg -join '、')) -ForegroundColor DarkGray
}
Write-Host ""
