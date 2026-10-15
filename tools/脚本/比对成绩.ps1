<#
  比对成绩.ps1 —— 把取词记录与标准答案自动对一遍，并支持"只测几段"。

  用法：
    powershell -ExecutionPolicy Bypass -File "tools\脚本\比对成绩.ps1"
    powershell -ExecutionPolicy Bypass -File "tools\脚本\比对成绩.ps1" -Since 16:17:00
    powershell -ExecutionPolicy Bypass -File "tools\脚本\比对成绩.ps1" -Files "release\V0.3.3.0\logs\取词记录-20261013.txt","release\V0.3.3.0\logs\取词记录-20261014.txt"

  它做什么：
    1. 读 release\<版本>\logs\ 里**最新那一天**的取词记录（跨午夜要自己用 -Files 指定）
    2. 读三份标准答案（验收标准答案.txt / 2.txt / 3.txt），凑出 56 段
    3. 取 -Since 之后的记录，**按内容**跟 56 段对号入座（不是按顺序！）
       —— 所以你可以**只框其中几段、跳过不想测的、顺序也可以乱**，跳过的段不扣分
    4. 打印成绩单、28 段"快测单"点名、每一处差异的原文

  归一口径（只做这些，不做任何"猜意思"的宽容）：
    · ⏎ 折行标记：两边都是汉字就删掉（中文折行处本来没空格），否则换成空格
    · 连续空白压成一个空格；去掉首尾空白
    · 弯引号/弯撇号统一成直引号直撇号（用户 2026-10-13 已裁定这类不算错）
    · 只差大小写（B1 Французских / R7 Mb）按 RULE 1 判过，不算错
  除此以外逐字判错。一个字符不同 = 整段判错（用户 2026-10-09 定的口径）。

  ★ 快测单（$Sentries，28 段）：日常只需要框这 28 段（材料在 tools\素材\快测材料.html），
    不用框三份材料的 56 段。这 28 段的挑选依据是「逐轮历史验证零漏报」——
    把全部归档当 28 次实测，每次比较「全量框过的段」与「这张单子」，没有一轮漏报。
    仅在这几种情况才需要回到 56 段全测：换引擎/换识图框架、阶段收口、快测单报警。
    验证脚本：tools\_fastcheck.ps1（一次性，用完即删）。

  ⚠️ 这是量具，不是权威。它说"全对"不等于用户说过了；它说"错"也可能是我写的
     标准答案打错了。差异一律打印原文，人工过一眼再下结论。
     量具已经骗过 12 次：**看到"全绿 / 全对"先怀疑量具，别先高兴。**
#>
param(
    [string]$Since = '00:00:00',
    [string[]]$Files = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# ---------- 快测单：28 段 ----------
# 怎么来的：把 tools\日志\用户实测\ 全部归档逐轮当一次实测，比较「该轮全量框过的段」
#           和「这张单子」——验证结果是 28 轮零漏报（每轮全量出过错的段，单子上都有）。
#           → 以后改完东西只框这 28 段就行，不用框三份材料的 56 段。
# 28 段 = 历史上出过错的前 16 段 + 覆盖各条识别路线的哨兵。
# ⚠️ 改这张单子前必须先看两个坑：
#    ① 同文段会因为材料不同而算成两个键（1-C1 与 2-Z1 是同一句话「今天天气不错适合出门」），
#       所以按「键」统计时会像是漏了，实际内容没漏。
#    ② 材料3 没有 R 组。曾经把「俄文夹符号」写成 '3-R3'，那是个死条目，
#       导致这个哨兵从来没生效过。正确的是材料2 的 R3。
$Sentries = @(
    # —— 历史上出过错的段（按出错次数从多到少）——
    @{ Id = 'E6'; Mat = 2; Why = '英文 半透明色块叠加 · 历史出错最多' }
    @{ Id = 'X2'; Mat = 3; Why = '中文夹英文夹数字 · config.txt 那个点' }
    @{ Id = 'X1'; Mat = 3; Why = '英文夹中文' }
    @{ Id = 'P2'; Mat = 3; Why = '中文三行紧挨' }
    @{ Id = 'C4'; Mat = 3; Why = '斜条纹纹理底' }
    @{ Id = 'E4'; Mat = 2; Why = '英文 低对比度灰底' }
    @{ Id = 'D1'; Mat = 1; Why = '中英混排' }
    @{ Id = 'B3'; Mat = 1; Why = '俄文带符号' }
    @{ Id = 'C1'; Mat = 3; Why = '深底浅色字' }
    @{ Id = 'C2'; Mat = 1; Why = '中文长段落' }
    @{ Id = 'A3'; Mat = 1; Why = '英文带符号' }
    @{ Id = 'C3'; Mat = 3; Why = '橙底绿字' }
    @{ Id = 'R2'; Mat = 2; Why = '俄文 深色背景浅色字' }
    @{ Id = 'E3'; Mat = 2; Why = '英文 橙底绿字' }
    @{ Id = 'A5'; Mat = 1; Why = '英文小字' }
    @{ Id = 'Z1'; Mat = 2; Why = '中文 橙底绿字 · 天和气那个老毛病' }
    # —— 历史上从没错过、但覆盖各条识别路线的哨兵 ——
    @{ Id = 'A1'; Mat = 1; Why = '英文最基础一句' }
    @{ Id = 'A2'; Mat = 1; Why = '英文长段（折行）' }
    @{ Id = 'A4'; Mat = 1; Why = '英文夹数字符号' }
    @{ Id = 'B1'; Mat = 1; Why = '俄文基础' }
    @{ Id = 'C1'; Mat = 1; Why = '中文短句' }
    @{ Id = 'X3'; Mat = 3; Why = '俄文夹 config.txt' }
    @{ Id = 'M2'; Mat = 3; Why = '中文多行' }
    @{ Id = 'M3'; Mat = 3; Why = '俄文多行' }
    @{ Id = 'P1'; Mat = 3; Why = '英文三行' }
    @{ Id = 'P3'; Mat = 3; Why = '英文日志三行（数字符号最密）' }
    @{ Id = 'R3'; Mat = 2; Why = '俄文夹符号' }
    @{ Id = 'Z2'; Mat = 2; Why = '中文长句' }
)

# ---------- 归一化 ----------
function Norm([string]$s) {
    if ($null -eq $s) { return '' }
    # ⏎ 折行标记：两边都是汉字就删掉，否则当空格
    $s = $s -replace '([\u4e00-\u9fff])\s*\u23ce\s*([\u4e00-\u9fff])', '$1$2'
    $s = $s -replace '\u23ce', ' '
    $s = $s -replace '([\u4e00-\u9fff])\s+([\u4e00-\u9fff])', '$1$2'
    $s = $s -replace '[\u2018\u2019\u201B]', "'" -replace '[\u201C\u201D\u201F]', '"'
    # 兆字节单位：西里尔 МБ 和拉丁 Mb 用户 2026-10-13 已裁定按 RULE 1 判过（意思一样）。
    # 只影响 R7 那一段，全历史 51 次都是这一处。
    $s = $s -replace '[Мм][Бб]', 'MB' -replace '[Mm][Bb]', 'MB'
    $s = $s -replace '\s+', ' '
    return $s.Trim()
}

# ---------- 编辑距离（滚动数组）----------
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

# ---------- 读标准答案，凑 56 段 ----------
$mat = Join-Path $root 'tools\素材'
$answers = New-Object System.Collections.ArrayList
$matNo = 0
foreach ($n in '验收标准答案.txt', '验收标准答案2.txt', '验收标准答案3.txt') {
    $matNo++
    $p = Join-Path $mat $n
    if (-not (Test-Path $p)) { throw "找不到标准答案：$p" }
    $curId = $null; $curText = ''
    foreach ($l in [System.IO.File]::ReadAllLines($p, [System.Text.Encoding]::UTF8)) {
        if ($l -match '^\s*#' -or $l.Trim() -eq '') { continue }
        if ($l -match '^([A-Z]\d+)\|(.*)$') {
            if ($curId) { [void]$answers.Add([pscustomobject]@{ Id = $curId; Mat = $matNo; N = (Norm $curText) }) }
            $curId = $Matches[1]; $curText = $Matches[2]
        } elseif ($curId) {
            $curText = $curText + ' ' + $l.Trim()
        }
    }
    if ($curId) { [void]$answers.Add([pscustomobject]@{ Id = $curId; Mat = $matNo; N = (Norm $curText) }) }
}

# 同文的段合成一组。键必须带材料号：三份材料里有重名的段号（C1~C4 三份都有），
# 同文也可能分属不同材料，混在一起成绩就串了。
$groups = New-Object System.Collections.ArrayList
$byText = @{}
foreach ($a in $answers) {
    $key = "$($a.Mat)|$($a.N)"
    if ($byText.ContainsKey($key)) {
        $byText[$key].Ids = @($byText[$key].Ids) + $a.Id
    } else {
        $g = [pscustomobject]@{
            Ids = @($a.Id); Mat = $a.Mat; N = $a.N; Len = $a.N.Length
        }
        $g | Add-Member -NotePropertyName Idx -NotePropertyValue 0
        $byText[$key] = $g
        [void]$groups.Add($g)
    }
}
Write-Host "标准答案：$($answers.Count) 段，合并同文后 $($groups.Count) 组"

# ---------- 读记录 ----------
if ($Files.Count -gt 0) {
    $recFiles = @($Files | ForEach-Object { Get-Item $_ })
} else {
    $versionDir = Get-ChildItem (Join-Path $root 'release') -Directory |
        Where-Object { $_.Name -match '^V\d' } |
        Sort-Object { [version]($_.Name.TrimStart('V', 'v') -replace '[^\d\.].*$', '') } |
        Select-Object -Last 1
    if (-not $versionDir) { throw "找不到发布目录（release\V...）" }
    # 只读最新那一天：-Since 是纯时分秒，跨天的旧文件混进来会错序
    $recFiles = @(Get-ChildItem (Join-Path $versionDir.FullName 'logs') -Filter '取词记录-*.txt' |
        Sort-Object Name | Select-Object -Last 1)
    if (-not $recFiles) { throw "找不到取词记录：$($versionDir.FullName)\logs" }
    Write-Host "发布目录：$($versionDir.Name)"
}

$recs = New-Object System.Collections.ArrayList
foreach ($f in $recFiles) {
    Write-Host "记录文件：$($f.Name)"
    $cur = $null
    foreach ($l in [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)) {
        if ($l -match '^时间: (.+)$') {
            if ($cur) { [void]$recs.Add($cur) }
            $cur = [pscustomobject]@{ T = $Matches[1].Trim(); Text = '' }
        } elseif ($cur -and $l -match '^原文: (.*)$') {
            $cur.Text = $Matches[1]
        }
    }
    if ($cur) { [void]$recs.Add($cur) }
}
$pick = @($recs | Where-Object { $_.T -ge $Since })
Write-Host "从 $Since 起：$($pick.Count) 条"
Write-Host ''

# ---------- 每条按内容找归属 ----------
# 注意：三份材料里有同文的段（A1 和 E1 都是 quick brown fox；Z2 和 M2 都是那句中文；
# E6 和 M1、A 系列也是同一句英文）。所以匹配时必须**优先挑还有空位的组**，
# 否则材料1 的同文组会把材料2 的记录全抢走，材料2 就被算成"没测"。
$segOk   = New-Object System.Collections.ArrayList   # 对上的段（含重测）
$segBad  = New-Object System.Collections.ArrayList   # 判错的段（含重测）
$caseOk  = New-Object System.Collections.ArrayList   # 只差大小写，按 RULE 1 判过
$orphans = New-Object System.Collections.ArrayList
$pending = New-Object System.Collections.ArrayList   # 还没找到归属的记录

function Find-Group([string]$got, [bool]$onlyOpen) {
    if ($onlyOpen) { $cands = @($groups | Where-Object { $_.Idx -lt $_.Ids.Count }) }
    else { $cands = @($groups) }
    if ($cands.Count -eq 0) { return $null }

    # 1) 完全相同
    foreach ($g in $cands) { if ($g.N -eq $got) { return @{ G = $g; D = 0; Case = $false } } }
    # 2) 只差大小写 → 按 RULE 1 判过
    foreach ($g in $cands) { if ($g.N.ToLower() -eq $got.ToLower()) { return @{ G = $g; D = 0; Case = $true } } }

    # ⚠️ 第 15 次量具事故（2026-10-15）：材料1 的 D1「…API 版本 v2.0 已于 2024 年发布」与
    #    材料2 的 Z3 是同一句、只多一个句号。用户对 D1 连框 10 次时，第 1 条把 D1 占满，
    #    第 2 条起在"还有空位"的组里只剩 Z3，编辑距离 1 → 十次全对的重测被谎报成「Z3 判错」。
    #    病根：近似匹配抢在"同一句的重测"之前认领。只要这句话已经有一个组持有（哪怕占满），
    #    这条记录就不该被别的组用近似匹配领走 —— 交给第二轮当重测。
    if ($onlyOpen) {
        foreach ($g in $groups) {
            if ($g.N -eq $got -or $g.N.ToLower() -eq $got.ToLower()) { return $null }
        }
    }

    # 3) 编辑距离，先按长度差筛
    $tol = [Math]::Max(4, [int][Math]::Floor($got.Length / 12))
    $rank = New-Object System.Collections.ArrayList
    foreach ($g in $cands) {
        if ([Math]::Abs($g.Len - $got.Length) -gt $tol) { continue }
        [void]$rank.Add([pscustomobject]@{ G = $g; D = (EditDist $got $g.N) })
    }
    if ($rank.Count -eq 0) { return $null }
    $rank = @($rank | Sort-Object D)
    $best = $rank[0]
    # 同文的组（N 完全相同）不算"并列竞争对手"——它们是同一句话、同一个答案，
    # 分到哪一组都只是标签不同。
    # ⚠️ 踩过：1-C1 与 2-Z1 都是「今天天气不错适合出门」，读错一个字后两组距离并列，
    #    旧版按"并列就不认领"处理 → 判错的那条记录变成孤儿，成绩单谎报「Z1 这次没测」。
    $second = [int]::MaxValue
    foreach ($x in $rank) {
        if ($x.G.N -eq $best.G.N) { continue }
        $second = $x.D; break
    }
    if ($best.D -le $tol -and (($second - $best.D) -ge 2 -or $second -ge $best.D * 2)) {
        return @{ G = $best.G; D = $best.D; Case = $false }
    }
    return $null
}

# 第一轮：只往还有空位的组里放（这才算"测了这一段"）
foreach ($r in $pick) {
    $got = Norm $r.Text
    if ($got -eq '') { continue }
    $m = Find-Group $got $true
    if (-not $m) { [void]$pending.Add($r); continue }
    $g = $m.G
    $id = $g.Ids[$g.Idx]; $g.Idx++
    $segOk.Add([pscustomobject]@{ Id = $id; T = $r.T; N = $got; G = $g }) | Out-Null
    if ($m.Case) { $caseOk.Add([pscustomobject]@{ Id = $id; T = $r.T; Got = $got; Want = $g.N; G = $g }) | Out-Null }
    elseif ($m.D -gt 0) { $segBad.Add([pscustomobject]@{ Id = $id; T = $r.T; Got = $got; Want = $g.N; Dist = $m.D; G = $g }) | Out-Null }
}

# 第二轮：所有组都占满了，剩下的记录算"重测"（RULE 6 补测或追加材料）
foreach ($r in $pending) {
    $got = Norm $r.Text
    $m = Find-Group $got $false
    if (-not $m) { [void]$orphans.Add([pscustomobject]@{ T = $r.T; Text = $got }); continue }
    $g = $m.G
    $segOk.Add([pscustomobject]@{ Id = $null; T = $r.T; N = $got; G = $g }) | Out-Null
    if ($m.Case) { $caseOk.Add([pscustomobject]@{ Id = $null; T = $r.T; Got = $got; Want = $g.N; G = $g }) | Out-Null }
    elseif ($m.D -gt 0) { $segBad.Add([pscustomobject]@{ Id = $null; T = $r.T; Got = $got; Want = $g.N; Dist = $m.D; G = $g }) | Out-Null }
}

# ---------- 成绩单 ----------
Write-Host '================ 成绩单（只算本次测到的段）================' -ForegroundColor Cyan
$mats = @(
    @{ Name = '材料1'; No = 1 },
    @{ Name = '材料2'; No = 2 },
    @{ Name = '材料3'; No = 3 }
)
# 段数按"这个组里被占用的段号"算：同一段反复测只算一段，多出来的算重测
$testedByMat = @{}
foreach ($g in $groups) {
    if (-not $testedByMat.ContainsKey($g.Mat)) { $testedByMat[$g.Mat] = 0 }
    $testedByMat[$g.Mat] += [Math]::Min($g.Idx, $g.Ids.Count)
}
$sumT = 0; $sumB = 0; $sumAll = 0
$matFull = @{}
foreach ($a in $answers) {
    if (-not $matFull.ContainsKey($a.Mat)) { $matFull[$a.Mat] = 0 }
    $matFull[$a.Mat]++
}
foreach ($m in $mats) {
    $t = if ($testedByMat.ContainsKey($m.No)) { $testedByMat[$m.No] } else { 0 }
    $full = if ($matFull.ContainsKey($m.No)) { $matFull[$m.No] } else { 0 }
    $b = @($segBad | Where-Object { $_.Id -and $_.G.Mat -eq $m.No })
    $sumT += $t; $sumB += $b.Count; $sumAll += $full
    $color = if ($b.Count -eq 0) { 'Green' } else { 'Yellow' }
    $ids = if ($b.Count) { "    判错：" + (($b | ForEach-Object { $_.Id }) -join '、') } else { '' }
    if ($t -ge $full) { Write-Host ("{0}：{1}/{2}{3}" -f $m.Name, ($t - $b.Count), $full, $ids) -ForegroundColor $color }
    else { Write-Host ("{0}：只测了 {1} 段，对 {2} 段（这一材料共 {3} 段）{4}" -f $m.Name, $t, ($t - $b.Count), $full, $ids) -ForegroundColor $color }
}
Write-Host ''
Write-Host ("本次测了 {0} 段（验收材料共 {1} 段；另外重测 {2} 次），判错 {3} 段。" -f
    $sumT, $sumAll, ($segOk.Count - $sumT), $sumB)
if ($sumT -ge $sumAll) {
    Write-Host ("★ 全量口径：{0}/{1}" -f ($sumAll - $sumB), $sumAll) -ForegroundColor Cyan
} else {
    # ⚠️ 只测一部分时**不能**换算成「X/56」——没测的段是「不知道」，不是「过」。
    Write-Host ("⚠️ 这次只测了 {0}/{1} 段（快测模式）。剩下的 {2} 段这次是「没测」，不是「过」。" -f
        $sumT, $sumAll, ($sumAll - $sumT)) -ForegroundColor Yellow
    Write-Host ("   已在测的 {0} 段里判错 {1} 段。要出全量成绩就得把 56 段都框一遍。" -f $sumT, $sumB) -ForegroundColor Yellow
}

# ---------- 快测单点名 ----------
Write-Host ''
Write-Host ("================ 快测单 {0} 段（防退化）================" -f $Sentries.Count) -ForegroundColor Cyan
Write-Host '  （这 28 段覆盖了历史上所有出过错的地方；只测这 28 段就够，不用测 56 段）' -ForegroundColor DarkGray
$missing = New-Object System.Collections.ArrayList
$badKeys = @($segBad | Where-Object { $_.Id } | ForEach-Object { "$($_.G.Mat)-$($_.Id)" })
foreach ($s in $Sentries) {
    $key = "$($s.Mat)-$($s.Id)"
    if ($badKeys -contains $key) { Write-Host ("  {0,-3} ✗ 错了！（{1}）" -f $s.Id, $s.Why) -ForegroundColor Red; continue }
    $ok = @($segOk | Where-Object { "$($_.G.Mat)-$($_.Id)" -eq $key })
    if ($ok.Count -gt 0) { Write-Host ("  {0,-3} ✓（{1}）" -f $s.Id, $s.Why) -ForegroundColor Green }
    else { [void]$missing.Add($s); Write-Host ("  {0,-3} － 这次没测（{1}）" -f $s.Id, $s.Why) -ForegroundColor DarkGray }
}
if ($missing.Count -eq 0) { Write-Host '  → 快测单全部测到、全部过，防退化这关过了。' -ForegroundColor Green }
else { Write-Host ("  → 还差 {0} 段没测：{1}" -f $missing.Count, (($missing | ForEach-Object { $_.Id }) -join '、')) -ForegroundColor Yellow }

# ---------- 反复测的段（RULE 6 补测）----------
# 键必须带材料号：材料1 和材料3 都有 C1、C2，光用段号会把两段并成一段。
# 次数只数 $segOk（每条记录只进去一次）；错的从 $segBad 里查，否则错的记录会被数两遍。
$reps = @{}
foreach ($s in $segOk) {
    if (-not $s.Id) { continue }
    $k = "$($s.G.Mat)-$($s.Id)"
    if (-not $reps.ContainsKey($k)) { $reps[$k] = [pscustomobject]@{ N = 0; B = 0; Sample = $s.N; Mat = $s.G.Mat; Id = $s.Id } }
    $reps[$k].N++
}
foreach ($s in $segBad) {
    if (-not $s.Id) { continue }
    $k = "$($s.G.Mat)-$($s.Id)"
    $reps[$k].B++
}
$multi = @($reps.Keys | Where-Object { $reps[$_].N -gt 1 })
if ($multi.Count -gt 0) {
    Write-Host ''
    Write-Host '================ 反复测的段（RULE 6 补测用）================' -ForegroundColor Cyan
    foreach ($k in ($multi | Sort-Object)) {
        $v = $reps[$k]
        $rate = [Math]::Round(100.0 * ($v.N - $v.B) / $v.N, 1)
        $color = if ($v.B -le 1) { 'Green' } else { 'Red' }
        Write-Host ("  材料{0} {1}：测 {2} 次、错 {3} 次、正确率 {4}%  （RULE 6 线：错 ≤1 次算过）" -f $v.Mat, $v.Id, $v.N, $v.B, $rate) -ForegroundColor $color
    }
}

# ---------- 明细 ----------
Write-Host ''
Write-Host '================ 差异明细 ================' -ForegroundColor Cyan
if ($segBad.Count -eq 0) { Write-Host '无差异（大小写差异另列在下面）。' -ForegroundColor Green }
else {
    foreach ($b in $segBad) {
        $label = if ($b.Id) { $b.Id } else { "(第 $($segBad.IndexOf($b) + 1) 条)" }
        Write-Host ("[{0}] {1}（差 {2} 个字符）" -f $label, $b.T, $b.Dist) -ForegroundColor Yellow
        Write-Host ("   实测：{0}" -f $b.Got)
        Write-Host ("   答案：{0}" -f $b.Want)
    }
}
foreach ($c in $caseOk) {
    Write-Host ("[{0}] {1} 只差大小写，按 RULE 1 判过" -f $c.Id, $c.T) -ForegroundColor DarkGray
}
if ($orphans.Count -gt 0) {
    Write-Host ''
    Write-Host ("对不上任何验收段的记录：{0} 条（用户自测的额外句子，不计成绩）" -f $orphans.Count) -ForegroundColor DarkGray
    foreach ($o in $orphans) { Write-Host ("   [{0}] {1}" -f $o.T, $o.Text) -ForegroundColor DarkGray }
}
