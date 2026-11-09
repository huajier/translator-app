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
    [switch]$自检,
    [switch]$Force,
    [switch]$按行,
    [string]$行真值 = 'tools\素材\行真值.json',
    [switch]$忽略配置
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# ---------- 配置指纹（2026-11-07 补）----------
# 为什么要有：2026-11-07 报出「假退步 28 段」，根因不是代码退步，是基线在
#   rapidOcrPrimary=false（全走 Tesseract）时生成，今天配置改成 true 换了通道，
#   拿新配置的成绩比旧配置的基线，比出来的「退步」根本不是代码干的。
# 所以基线必须自己记下它是「在什么配置下」跑出来的；比成绩之前先比配置，
#   配置一变就拒绝比对，免得配置漂移长期伪装成代码退步。
# 默认值照抄 src\Core\Capture\AppSettings.cs（:45 / :65 / :82 / :106 / :124 / :152）。
# 读不到 / 缺字段 / JSON 坏了都退回默认值，绝不因为读配置失败中断量具。
function Get-ConfigFingerprint {
    $fp = [ordered]@{
        rapidOcrPrimary           = $false
        rapidOcrRec               = 'en'
        rapidOcrUseOriginal       = $false
        rapidOcrKeepTrailingPunct = $true
        rapidOcrSmallBoxUpscale   = $true
        windowsOnly               = $false
    }
    $settingsPath = Join-Path $env:APPDATA 'TranslatorApp\settings.json'
    if (-not (Test-Path $settingsPath)) { return $fp }
    try {
        $doc = [System.IO.File]::ReadAllText($settingsPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    } catch {
        return $fp
    }
    if ($null -eq $doc) { return $fp }
    foreach ($p in $doc.PSObject.Properties) {
        if (-not $fp.Contains($p.Name)) { continue }
        if ($null -eq $p.Value) { continue }
        if ($fp[$p.Name] -is [bool]) { $fp[$p.Name] = [bool]$p.Value }
        else { $fp[$p.Name] = [string]$p.Value }
    }
    return $fp
}

# 返回真正变了的字段（空数组 = 一致）。$old 可能是 $null（老格式基线没有「配置」）。
#
# 【2026-11-09 修：这里连踩过两次坑，两条都得留着】
#
#   坑①「单元素被拆包」：写成 `return @($diff)` 时，只有一个字段变了的话 return 会把
#     单元素数组拆成对象本身，调用方拿到 PSCustomObject，.Count 为 $null，
#     `-gt 0` 静默为假 —— 配置不一致却 exit 0（2026-11-07 自测 A4 当场踩到）。
#
#   坑②「空数组被多包一层」：为了修坑① 改成 `return , $diff.ToArray()`，
#     结果**一致时**（空数组）返回 @(@())，调用方 `@(...)` 再包一层 → Count=1，
#     于是「配置完全一样」也被判成不一样，正常比对全被拦住（2026-11-09 踩到）。
#
#   正确写法：用 PowerShell 的 0/1/N 元素一致语义 —— 直接输出数组本体，不要逗号：
#     · 空       → 调用方 @() 得到 Count=0  ✅
#     · 1 个元素 → 调用方 @() 得到 Count=1  ✅
#     · N 个元素 → 调用方 @() 得到 Count=N  ✅
#   唯一的例外是 1 个元素时不加 @() 会被拆包 —— 但**所有调用点都写了 @()**，所以安全。
function Compare-ConfigFingerprint($old, $cur) {
    $diff = New-Object System.Collections.ArrayList
    foreach ($k in $cur.Keys) {
        $o = Get-ConfigValue $old $k
        $n = $cur[$k]
        # 老基线里的 bool 可能存成了字符串（手改过），统一按原文比，避免 $false→'false' 假报警。
        if ("$o" -ne "$n") { [void]$diff.Add([pscustomobject]@{ Name = $k; Was = $o; Now = $n }) }
    }
    return $diff.ToArray()
}

function Get-ConfigValue($cfg, [string]$name) {
    if ($null -eq $cfg) { return $null }
    if ($cfg -is [System.Collections.IDictionary]) { if ($cfg.Contains($name)) { return $cfg[$name] } else { return $null } }
    $p = $cfg.PSObject.Properties[$name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

# 打印变了哪些字段。bool 打成 settings.json 那样的 false/true，方便照着改文件。
function Show-ConfigDiff($diff, $color = 'Red') {
    foreach ($d in @($diff)) {
        $was = if ($d.Was -is [bool]) { "$($d.Was)".ToLower() } else { $d.Was }
        $now = if ($d.Now -is [bool]) { "$($d.Now)".ToLower() } else { $d.Now }
        Write-Host ("   {0}: {1} → {2}" -f $d.Name, $was, $now) -ForegroundColor $color
    }
}


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
    # 行级基线要的是「每张图每一行过没过」。Rows 只记失败的，回答不了这个问题，
    # 所以另外记全量键。键 = 图 id + '#' + 行序号（从 0 起，只数真值里的非空行，和 Total 同口径）。
    $allKeys = New-Object System.Collections.ArrayList
    $okKeys = New-Object System.Collections.ArrayList
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
            $mi = -1
            foreach ($w in $want) {
                $mi++
                # 缺测的图：键照记（否则下次拿它当基线会「少一批行」而不是「这批行错了」）
                [void]$allKeys.Add("$($e.id)#$mi")
                [void]$rows.Add([pscustomobject]@{ Id = [string]$e.id; Mode = $mode; Kind = '丢'; Want = $w; Got = '<缺测>'; Dist = $w.Length })
            }
            continue
        }

        # 对/改/丢 三件事都占掉一行真值，「多」是凭空多出来的、不占 —— 序号要跟着它跳。
        $wi = -1
        foreach ($op in (Align-Lines $want (Split-Lines $actualRaw[[string]$e.id]))) {
            if ($op.Kind -ne '多') {
                $wi++
                [void]$allKeys.Add("$($e.id)#$wi")
            }
            $key = "$($e.id)#$wi"
            switch ($op.Kind) {
                '对' { $ok++; $st.对++; [void]$okKeys.Add($key) }
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
        AllKeys = @($allKeys); OkKeys = @($okKeys)
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

# 量具自检：注入「改 1 字 / 删 1 行 / 加 1 行」，必须各报 1 错；恢复后必须 0 错。
# 这是 S0 的闸门：自检不过 = 尺子坏，停，不许改识别代码（方案第 4 节 S0）。
function Invoke-LineSelfCheck {
    Write-Host ""
    Write-Host "=== 行级量具自检（S0）===" -ForegroundColor Cyan
    $fixture = @(
        [pscustomobject]@{ id = '自检-单行-1'; mode = '单行'; lines = @('STRE', 'ATTRIBUTES') },
        [pscustomobject]@{ id = '自检-整块-1'; mode = '整块'; lines = @('Name: Barony', 'Level 3', 'HP 20/20') }
    )
    $base = @{
        '自检-单行-1' = 'STRE⏎ATTRIBUTES'
        '自检-整块-1' = 'Name: Barony⏎Level 3⏎HP 20/20'
    }
    $cases = @(
        [pscustomobject]@{ Name = '基准（无注入）'; Expect = 0; Kind = ''; Map = $base },
        [pscustomobject]@{ Name = '改 1 字（Level 3 → Level 8）'; Expect = 1; Kind = '改'; Map = @{
                '自检-单行-1' = 'STRE⏎ATTRIBUTES'
                '自检-整块-1' = 'Name: Barony⏎Level 8⏎HP 20/20'
            } },
        [pscustomobject]@{ Name = '删 1 行（删掉 ATTRIBUTES）'; Expect = 1; Kind = '丢'; Map = @{
                '自检-单行-1' = 'STRE'
                '自检-整块-1' = 'Name: Barony⏎Level 3⏎HP 20/20'
            } },
        [pscustomobject]@{ Name = '加 1 行（多出 EXTRA LINE）'; Expect = 1; Kind = '多'; Map = @{
                '自检-单行-1' = 'STRE⏎ATTRIBUTES'
                '自检-整块-1' = 'Name: Barony⏎Level 3⏎EXTRA LINE⏎HP 20/20'
            } },
        [pscustomobject]@{ Name = '恢复（改回原样）'; Expect = 0; Kind = ''; Map = $base }
    )

    $fail = 0
    $n = 0
    foreach ($c in $cases) {
        $n++
        $r = Get-LineReport $fixture $c.Map
        $badOk = ($r.Bad -eq $c.Expect)
        $kindOk = $true
        if ($c.Kind -ne '') {
            $kindCount = switch ($c.Kind) { '改' { $r.Changed } '丢' { $r.Lost } '多' { $r.Extra } }
            $kindOk = ($kindCount -eq 1)
        }
        $totalOk = ($r.Total -eq 5)
        if ($badOk -and $kindOk -and $totalOk) {
            Write-Host ("  [{0}] {1}：真值 {2} 行、错 {3}（改 {4}/丢 {5}/多 {6}）→ PASS" -f `
                $n, $c.Name, $r.Total, $r.Bad, $r.Changed, $r.Lost, $r.Extra) -ForegroundColor Green
        } else {
            $fail++
            Write-Host ("  [{0}] {1}：真值 {2} 行、错 {3}（改 {4}/丢 {5}/多 {6}）→ FAIL（期望错 {7}）" -f `
                $n, $c.Name, $r.Total, $r.Bad, $r.Changed, $r.Lost, $r.Extra, $c.Expect) -ForegroundColor Red
        }
    }

    Write-Host ""
    if ($fail -eq 0) {
        Write-Host ("  自检 {0}/{0} PASS：行级尺子能用" -f $n) -ForegroundColor Green
        Write-Host ""
        return $true
    }
    Write-Host ("  自检 {0}/{1} PASS —— 量具坏，停，不许改识别代码" -f ($n - $fail), $n) -ForegroundColor Red
    Write-Host ""
    return $false
}

# 自检只用量具本身，不依赖语料/答案文件 —— 放在最前面，语料还没造也能验尺子。
if ($自检) {
    if (-not (Invoke-LineSelfCheck)) { exit 1 }
    return
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

    $r = Get-LineReport @($entries) $actualRaw
    Show-LineReport $r

    if ($Approve) {
        # ---------- 行级基线的闸门（同段级三步，只是键换成「图 id#行号」）----------
        # 为什么单独一个文件（不塞进基线.json）：段级 -Approve 是整文件覆写，
        # 行级的键写进同一个文件会被下一次段级 -Approve 冲掉。
        # 放 tools\素材\（进 git），和段级基线同一个理由：这里才不会被语料清理带走。
        $lineBasePath = Join-Path $root 'tools\素材\行基线.json'
        if ($r.Missing.Count -gt 0) {
            Write-Host ("  ⛔ 缺测 {0} 张图（{1}），不能当基线（缺的图会被记成「错」，下次就会谎报退步）" -f $r.Missing.Count, ($r.Missing -join '、')) -ForegroundColor Red
            Write-Host ""
            exit 1
        }
        # 这份 map 必须在 if 之前建：第一次跑没有基线文件，走完 skipped 的 if 还得用它写文件。
        $nowLineOk = @{}
        foreach ($k in $r.OkKeys) { $nowLineOk[$k] = $true }
        # 配置指纹也必须在 if 之前取：没有行基线文件（第一次跑）也要把它写进新基线。
        $curCfg = Get-ConfigFingerprint
        if (Test-Path $lineBasePath) {
            $oldLine = Get-Content $lineBasePath -Raw -Encoding UTF8 | ConvertFrom-Json

            # 配置一改，旧行基线就悄悄作废了 —— 和段级同一道坎，这里比它先拦。
            # 读不出来就当老格式（返回空指纹），不会把坏文件当成「配置一致」。
            $oldCfg = if ($null -ne $oldLine -and $null -ne $oldLine.PSObject.Properties['配置']) { $oldLine.配置 } else { $null }
            if ($忽略配置) {
                Write-Host "  ⚠️ -忽略配置：跳过了配置比对，本次成绩可能不可比" -ForegroundColor Yellow
            } elseif ($null -eq $oldCfg) {
                if ($null -ne $oldLine) { Write-Host "  行基线没有配置指纹（老格式），本轮跳过配置比对" -ForegroundColor DarkGray }
            } else {
                $cfgDiff = @(Compare-ConfigFingerprint $oldCfg $curCfg)
                # 这段整块就在 if ($Approve) 里（行基线只有 -Approve 才会写），
                # 所以和段级不一样：这里只提示，不 exit 1。配置变了正是它要重写基线的时候。
                if ($cfgDiff.Count -gt 0) {
                    Write-Host "  配置与旧基线不同，将写入新配置：" -ForegroundColor DarkGray
                    Show-ConfigDiff $cfgDiff 'DarkGray'
                }
            }

            $oldLineOk = @{}
            foreach ($p in $oldLine.行.PSObject.Properties) { $oldLineOk[$p.Name] = [bool]$p.Value }
            # 只看「两边都有」的键：新图多了行、旧图删了行，都不该算退步。
            $lineRegress = @(
                $r.AllKeys | Where-Object { $oldLineOk.ContainsKey($_) -and $oldLineOk[$_] -and -not $nowLineOk.ContainsKey($_) } | Sort-Object
            )
            if ($lineRegress.Count -gt 0 -and -not $Force) {
                Write-Host ("  拒绝写新行基线：这轮比旧基线退步 {0} 行（{1}）。" -f $lineRegress.Count, (($lineRegress | Select-Object -First 20) -join '、')) -ForegroundColor Red
                if ($lineRegress.Count -gt 20) { Write-Host ("  （只列前 20 行，共 {0} 行）" -f $lineRegress.Count) -ForegroundColor Red }
                Write-Host "  退步不该被写成标准。先修好再 -Approve；确实要接受这个退步，加 -Force。" -ForegroundColor Red
                Write-Host ("  行基线文件没有改动：{0}" -f $lineBasePath) -ForegroundColor DarkGray
                Write-Host ""
                exit 1
            }
            if ($lineRegress.Count -gt 0 -and $Force) {
                Write-Host ("  ⚠️ -Force：明知这轮比旧行基线退步 {0} 行（{1}），照样写。" -f $lineRegress.Count, (($lineRegress | Select-Object -First 20) -join '、')) -ForegroundColor Yellow
            }
        }

        $lineOkMap = [ordered]@{}
        foreach ($k in $r.AllKeys) { $lineOkMap[$k] = $nowLineOk.ContainsKey($k) }
        $lineObj = [ordered]@{
            时间 = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            结果 = $Result
            配置 = $curCfg
            行   = $lineOkMap
        }
        $lineDir = Split-Path -Parent $lineBasePath
        if (-not (Test-Path $lineDir)) { New-Item -ItemType Directory -Path $lineDir -Force | Out-Null }
        [System.IO.File]::WriteAllText($lineBasePath, ($lineObj | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
        Write-Host ("  ✅ 已写入 {0} 行行级基线（{1} 行判对）：{2}" -f $r.AllKeys.Count, $r.Ok, $lineBasePath) -ForegroundColor Green
        Write-Host ""
        return
    }

    # 没加 -Approve：跟以前一样只打印成绩，一个文件都不写。
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

# ---------- 比成绩之前先比配置（2026-11-07 补，本轮核心）----------
# 规则：老格式（没「配置」）→ 灰字放行；一致 → 静默；不一致 → 纯比对拒绝（exit 1），
#       -Approve 只提示（配置变了正是它要写新基线的时候）。
# -忽略配置 是唯一逃生门，但要说清楚这次成绩不可比。
$curCfg = Get-ConfigFingerprint
$oldBase = $null
$oldCfg = $null
if (Test-Path $baselinePath) {
    $oldBase = Get-Content $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -ne $oldBase -and $null -ne $oldBase.PSObject.Properties['配置']) { $oldCfg = $oldBase.配置 }
}
$cfgDiff = @()
if ($忽略配置) {
    Write-Host "  ⚠️ -忽略配置：跳过了配置比对，本次成绩可能不可比" -ForegroundColor Yellow
} elseif ($null -ne $oldBase -and $null -eq $oldCfg) {
    Write-Host "  基线没有配置指纹（老格式），本轮跳过配置比对" -ForegroundColor DarkGray
} elseif ($null -ne $oldCfg) {
    # @() 兜底：保证 $cfgDiff 永远是数组，.Count 永远有值。
    $cfgDiff = @(Compare-ConfigFingerprint $oldCfg $curCfg)
}

if ($Approve) {
    if ($cfgDiff.Count -gt 0) {
        Write-Host "  配置与旧基线不同，将写入新配置：" -ForegroundColor DarkGray
        Show-ConfigDiff $cfgDiff 'DarkGray'
    }
} elseif ($cfgDiff.Count -gt 0 -and -not $忽略配置) {
    Write-Host "  ⛔ 基线的配置和当前不一致，不可比：" -ForegroundColor Red
    Show-ConfigDiff $cfgDiff
    Write-Host "  先把配置改回基线那样，或者用 -Approve 重新生成基线。" -ForegroundColor Red
    Write-Host "  这道检查是为了防止「配置改了、比出来的退步其实不是代码退步」。" -ForegroundColor Red
    Write-Host "  确实要拿不同配置的成绩硬比，加 -忽略配置。" -ForegroundColor DarkGray
    Write-Host ""
    exit 1
}

if ($Approve) {
    if ($missing.Count -gt 0 -or $battleMissing.Count -gt 0) {
        Write-Host ("  ⛔ 缺测 {0} 段（56 段里缺 {1}、实战图缺 {2}），不能当基线（缺的段会被记成「错」，下次就会谎报退步）" -f ($missing.Count + $battleMissing.Count), $missing.Count, $battleMissing.Count) -ForegroundColor Red
        Write-Host ""
        return
    }
    # ---------- 写基线前的闸门：有退步就不许写 ----------
    # 为什么要有这一关：-Approve 是整个流程里唯一会「写标准」的操作。
    # 这轮要是跑得比上次差，按下去就等于把退步洗白成「本来就这样」，从此再也不报警。
    # 这一关不许靠人记得查 —— 让工具自己拒绝。逃生门是 -Force（明知会掉几段也要接受时用）。
    if (Test-Path $baselinePath) {
        $oldBase = Get-Content $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
        $oldOk = @{}
        foreach ($p in $oldBase.段.PSObject.Properties) { $oldOk[$p.Name] = [bool]$p.Value }
        $wouldRegress = @(
            $nowOk.Keys | Where-Object { $oldOk.ContainsKey($_) -and $oldOk[$_] -and -not $nowOk[$_] } | Sort-Object
        )
        if ($wouldRegress.Count -gt 0 -and -not $Force) {
            Write-Host ("  拒绝写新基线：这轮比旧基线退步 {0} 段（{1}）。" -f $wouldRegress.Count, ($wouldRegress -join '、')) -ForegroundColor Red
            Write-Host "  退步不该被写成标准。先修好再 -Approve；确实要接受这个退步，加 -Force。" -ForegroundColor Red
            Write-Host ("  基线文件没有改动：{0}" -f $baselinePath) -ForegroundColor DarkGray
            Write-Host ""
            exit 1
        }
        if ($wouldRegress.Count -gt 0 -and $Force) {
            Write-Host ("  ⚠️ -Force：明知这轮比旧基线退步 {0} 段（{1}），照样写。" -f $wouldRegress.Count, ($wouldRegress -join '、')) -ForegroundColor Yellow
        }
    }

    $obj = [ordered]@{
        时间 = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        结果 = $Result
        配置 = $curCfg
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