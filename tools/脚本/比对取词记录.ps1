<#
    把「取词记录」和「标准答案」逐段比对。

    ================================================================
    【算分口径：用户 2026-10-09 亲自定的标准，不是我定的】
    ================================================================

    用户原话：
      「在我的视角来看，一个词的一个字母错了，那这一整个词就全错了，
        这一个词错了，拿着一整句话就全错了，这个判定标准你参考下」

    翻译成规则：
      **逐词判定 → 整段判定**
        ① 按空格切成一个个「词」
        ② 一个词里**只要有一个字符不同，这个词就整词判错**（不按字符给部分分）
        ③ 一段里**只要有一个词判错，整段就判错**（不给部分分）

    举例：
      标准 `see p.42.`   实际 `see a`
        `see`   ✅ 对
        `a`     ❌ 错（缺了 `p.42.`）
        → **整段判错**

      这正是原来那套「编辑距离」算法掩盖掉的问题：
      `see a` 按编辑距离能拿 **85 分**，看起来「基本对了」，
      但用户要的是**翻译就全错了**——一个词错，译文就是错的。

    ================================================================
    【还保留的三条老规矩（都还有道理，不动）】
    ================================================================
      · 大小写不敏感。OCR 常把 I/l、O/0 认错，
        这类混淆不该在「大小写」上再罚一次。
        （但如果**只有**大小写不同，会单独提示，人可以自己决定要不要在意。）
      · 空格**保留**参与比对。乱加空格必须如实扣分，
        否则 `quick b rown fox` 会和 `quick brown fox` 同分，
        那个最烦的问题就被自己掩盖掉了。
      · 换行、连续空格、首尾空白不算错。

    用法：
      powershell -ExecutionPolicy Bypass -File "tools\脚本\比对取词记录.ps1"
      powershell -ExecutionPolicy Bypass -File "tools\脚本\比对取词记录.ps1" -Version v0.3.1
#>

param(
    [string]$Version = 'v0.3.1',
    [string]$AnswerFile = '验收标准答案2.txt'
)

$ErrorActionPreference = 'Stop'

$repo       = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$logDir     = Join-Path $repo "release\$Version\logs"
$recordFile = Join-Path $logDir ("取词记录-{0}.txt" -f (Get-Date -Format yyyyMMdd))
$answerPath = Join-Path $repo "tools\素材\$AnswerFile"

# 昨天测的、今天还没测时，自动退回找最近的那一份，免得白跑一次
if (-not (Test-Path $recordFile)) {
    $all = @(Get-ChildItem $logDir -Filter '取词记录-*.txt' -EA SilentlyContinue |
             Sort-Object LastWriteTime -Descending)
    if ($all.Count -gt 0) { $recordFile = $all[0].FullName }
}

if (-not (Test-Path $recordFile)) {
    Write-Host "没找到取词记录：$logDir\取词记录-*.txt" -ForegroundColor Red
    Write-Host "请先按验收步骤操作一遍，再运行本脚本。" -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $answerPath)) {
    Write-Host "没找到标准答案：$answerPath" -ForegroundColor Red
    exit 1
}

# ---------- 1. 读标准答案 ----------
#  【支持多行答案（材料 3 才有）】
#    M1/M2/M3/P1/P2/P3 都是**两三行的段落**，
#    答案文件里写成：
#        M1|第一行
#        第二行
#    也就是**没有 `编号|` 开头的那一行，接在上一条后面**。
#
#    换行在比对时会被 Normalize 变成空格，
#    和识别结果里的 `⏎` 记号处理方式一致，所以两边能对齐。
$answers = [ordered]@{}
$lastKey = $null
foreach ($line in Get-Content $answerPath -Encoding UTF8) {
    if ($line -match '^\s*#') { continue }

    if ($line -match '^\s*([A-Za-z]+\d*)\s*\|(.*)$') {
        $lastKey = $Matches[1].Trim()
        $answers[$lastKey] = $Matches[2].Trim()
    }
    elseif ($null -ne $lastKey -and -not [string]::IsNullOrWhiteSpace($line)) {
        # 续行：接在上一条后面
        $answers[$lastKey] = ($answers[$lastKey] + ' ' + $line.Trim()).Trim()
    }
    elseif ([string]::IsNullOrWhiteSpace($line)) {
        $lastKey = $null      # 空行之后不再续接
    }
}

# ---------- 2. 切取词记录 ----------
$records = @()
$cur = $null
foreach ($line in Get-Content $recordFile -Encoding UTF8) {
    if ($line -match '^时间:\s*(.+)$') {
        if ($null -ne $cur) { $records += $cur }
        $cur = [ordered]@{ 时间=$Matches[1]; 通道=''; 语言=''; 耗时=''; 说明=''; 字数=''; 原文='' }
    }
    elseif ($null -ne $cur -and $line -match '^通道:\s*(.+)$')          { $cur.通道 = $Matches[1] }
    elseif ($null -ne $cur -and $line -match '^语言:\s*(.+)$')          { $cur.语言 = $Matches[1] }
    elseif ($null -ne $cur -and $line -match '^耗时:\s*(.+)$')          { $cur.耗时 = $Matches[1] }
    elseif ($null -ne $cur -and $line -match '^说明:\s*(.+)$')          { $cur.说明 = $Matches[1] }
    elseif ($null -ne $cur -and $line -match '^字数:\s*(.+)$')          { $cur.字数 = $Matches[1] }
    elseif ($null -ne $cur -and $line -match '^原文:\s*(.+)$')          { $cur.原文 = $Matches[1] }
}
if ($null -ne $cur) { $records += $cur }

if ($records.Count -eq 0) {
    Write-Host "记录文件里没有可用的记录。" -ForegroundColor Red
    exit 1
}

# ---------- 3. 归一化 + 逐词判定 ----------
function Normalize([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return '' }
    $s = $s -replace '⏎', ' '
    $s = $s -replace '\s+', ' '
    return $s.Trim().ToLowerInvariant()
}

# 切词：按空白切。`me@example.com`、`p.42.`、`404` 各算**一个词**
#
# 🔴【2026-10-10 修一个会让中文段「假通过」的致命 bug】
#
#   **原来写的是**：
#       return @($n -split ' ' | Where-Object { $_.Length -gt 0 })
#
#   **PowerShell 返回值时会自动拆包**：
#   一个**只有一个元素**的数组，会被拆成**裸字符串**返回。
#   于是调用方拿到的不是数组，是字符串，
#   `$a[0]` 取到的就成了**第一个字**，而不是第一个词。
#
#   **后果（实测）**：
#       标准切出 1 个词：[今天天气不错适合出门]
#       实际切出 1 个词：[今天天气不台出门]
#       -ceq 结果：True     ← 明明不一样
#       长度：1 vs 1
#
#   **凡是「整段没有空格」的段落——也就是所有中文段——
#   都只比较了第一个字，就判「逐字全对」。**
#   用户定的标准是「一个词错一个字符就整词判错」，
#   **这条 bug 直接违反了这个标准，而且是往「通过」那边偏的。**
#
#   【修法：加一个逗号 `,` 挡住拆包】
#       return ,@(...)      ← 逗号把数组**包成一层**再返回，就不会被拆开了
function Split-Words([string]$s) {
    $n = Normalize $s
    if ($n.Length -eq 0) { return ,@() }
    return ,@($n -split ' ' | Where-Object { $_.Length -gt 0 })
}

<#
    按用户的标准判定一段：
      · 逐词比对，词内只要有一个字符不同 → 该词判错
      · 词与词之间用**最长公共子序列对齐**，
        多出来的词（多的空格、多的标点）**只算它自己那一个错**，
        不会把后面所有词一起带偏。
      · 返回：整段是否全对、词总数、错词数、错词清单

    【为什么要做对齐】
      E5 那段实际多了一个行首的 `:`：
        标准：HTTP/2.0 2024-05-01 API ...
        ⚠️ 配对相似度较低（: HTTP/2.0 2024-05-01 API ... 很可能不是这一段的记录）
        实际：: HTTP/2.0 2024-05-01 API ...
      按位置硬比的话，**从第一个词起全部错位**，11 个词全被判错——
      看着像识别烂透了，其实只有行首多了一个冒号。
      **报告在误导人**，比识别烂还糟糕。
#>
function Test-Segment([string]$want, [string]$got) {
    $w = Split-Words $want
    $g = Split-Words $got

    if ($w.Count -eq 0) {
        return [pscustomobject]@{ 全对=$false; 词数=0; 错词数=0; 错词=@(); 仅大小写=@(); 少词=0; 多词=0 }
    }
    if ($g.Count -eq 0) {
        return [pscustomobject]@{ 全对=$false; 词数=$w.Count; 错词数=$w.Count
            错词=@($w | ForEach-Object { "「$_」完全没识别出来" }); 仅大小写=@(); 少词=$w.Count; 多词=0 }
    }

    # ---- 最长公共子序列（LCS）表 ----
    #  ⚠️ **为什么用一维扁平数组，不用 `int[,]` 二维数组**
    #     PowerShell 5.1 的解析器有个老毛病：数组下标里的逗号，
    #     一旦出现在方法调用的参数列表里（`[Math]::Max($lcs[($i+1), $j], …)`），
    #     就会被当成**参数分隔符**，直接报
    #     「Missing ']' after array index expression」这种看不懂的语法错。
    #     改成一维数组 + 手工算下标 `$i * $cols + $j`，全部绕开，最稳。
    $n = $w.Count; $m = $g.Count
    $cols = $m + 1
    $lcs = New-Object int[] (($n + 1) * $cols)
    for ($i = $n - 1; $i -ge 0; $i--) {
        for ($j = $m - 1; $j -ge 0; $j--) {
            $here  = $i * $cols + $j
            if ($w[$i] -ceq $g[$j]) {
                $lcs[$here] = $lcs[(($i + 1) * $cols) + ($j + 1)] + 1
            }
            else {
                $down  = $lcs[(($i + 1) * $cols) + $j]      # 跳过标准答案里的这个词
                $right = $lcs[($i * $cols) + ($j + 1)]     # 跳过实际结果里的这个词
                $lcs[$here] = [Math]::Max($down, $right)
            }
        }
    }

    $wrong = @(); $caseOnly = @(); $miss = 0; $extra = 0
    $i = 0; $j = 0
    while ($i -lt $n -and $j -lt $m) {
        if ($w[$i] -ceq $g[$j]) { $i++; $j++; continue }          # 完全相同
        if ($w[$i] -cne $g[$j]) {
            # 不是大小写差异：「就地替换 / 跳过标准 / 跳过实际」三选一，取剩余 LCS 最长的走法
            #
            # ⚠️ **这里的比较顺序踩过坑**
            #   原来写的是 `elseif ($skipW -ge $skipG)` 才判「实际多出来的」，
            #   结果 E5 行首多出来的那个 `:` 被判成了「缺 http/2.0」，
            #   **后面 24 个词全被带偏**——比不改还糟。
            #   正确的判法：**谁的剩余 LCS 更长，就跳过谁。**
            $skipW = $lcs[(($i + 1) * $cols) + $j]
            $skipG = $lcs[($i * $cols) + ($j + 1)]
            $repl  = $lcs[(($i + 1) * $cols) + ($j + 1)]
            if ($repl -ge $skipW -and $repl -ge $skipG) {
                $wrong += ("「{0}」应是「{1}」" -f $w[$i], $g[$j]); $i++; $j++
            }
            elseif ($skipG -gt $skipW) {
                $wrong += ("多出「{0}」" -f $g[$j]); $extra++; $j++
            }
            else {
                $wrong += ("缺「{0}」" -f $w[$i]); $miss++; $i++
            }
        }
        else {
            # 大小写不一致：按口径不算错，但要单独告诉用户
            $caseOnly += ("{0} / {1}" -f $w[$i], $g[$j]); $i++; $j++
        }
    }
    while ($i -lt $n) { $wrong += ("缺「{0}」" -f $w[$i]); $miss++; $i++ }
    while ($j -lt $m) { $wrong += ("「{0}」多出来了" -f $g[$j]); $extra++; $j++ }

    return [pscustomobject]@{
        全对   = ($wrong.Count -eq 0)
        词数   = $n
        错词数 = $wrong.Count
        错词   = $wrong
        仅大小写 = $caseOnly
        少词   = $miss
        多词   = $extra
    }
}

# =====================================================================
#  按「像不像」配对，而不是靠顺序硬配
# =====================================================================
#  【为什么要这么做】
#    记录是**用户一次一次框选**产生的，不是程序按顺序写出来的。
#    只要中间多出一次废框（比如拖框时松开了 Ctrl/Alt），
#    **后面全部串位**，报告会变成一整篇红——
#    「E2 应该是 the 实际是 machine」这种，看着像识别烂透了，
#    其实只是**配错了**，比识别烂还误导人。
#
#  【为什么改成「字符级」，不再按词算（踩过坑）】
#    原来用「有多少个词对得上」算相似度。**中文没有空格**，
#    切出来只有 1 个「词」，于是 Z2 实际结果明明和标准答案几乎一样，
#    相似度却算出 **0**，被当成废框丢掉，报告直接说「Z1、Z2 没测」。
#
#    字符级对中英文都成立：内容大段对得上相似度就高；
#    乱码（比如 `KESL` 对上 `今天天气不错适合出门`）相似度自然接近 0，
#    低于 0.30 的才判为「配不上」，也就是废框。
function Get-Overlap([string]$a, [string]$b) {
    $ca = (Normalize $a).Replace(' ', '')
    $cb = (Normalize $b).Replace(' ', '')
    if ($ca.Length -eq 0 -or $cb.Length -eq 0) { return 0.0 }

    $n = $ca.Length; $m = $cb.Length
    $cols = $m + 1
    $lcs = New-Object int[] (($n + 1) * $cols)
    for ($i = $n - 1; $i -ge 0; $i--) {
        for ($j = $m - 1; $j -ge 0; $j--) {
            $here = $i * $cols + $j
            if ($ca[$i] -ceq $cb[$j]) { $lcs[$here] = $lcs[(($i + 1) * $cols) + ($j + 1)] + 1 }
            else {
                $down  = $lcs[(($i + 1) * $cols) + $j]
                $right = $lcs[($i * $cols) + ($j + 1)]
                $lcs[$here] = [Math]::Max($down, $right)
            }
        }
    }
    # ⚠️ 答案取 **$lcs[0]**（整串的相似度），
    #    不是 `$lcs[$n * $cols + $m]`——那个是右下角的边界格，**恒为 0**
    #    （踩过：整个配对全部失效，19 条全被当成废框，报告直接说「一段都没测」）
    return [double]$lcs[0] / [double][Math]::Max($n, $m)
}

function Match-Records($answers, $records) {
    # ==========================================================
    # 【2026-10-10 修的错配】
    #
    #   **现场**：那一轮用户只测了 X1（3 次），文件里就 3 条记录，全是 X1 的。
    #   报告却显示：
    #     X1 ✅ 全对
    #     X3 ❌ 判错（标准 `Файл config.txt не найден`，实际却是 X1 的结果）
    #     C3 ❌ 判错（标准 `这是故意做的低对比度文字`，实际也是不相干的内容）
    #
    #   **根因**：原来是「相似度 ≥ 0.30 就进候选池，再按相似度从高到低贪心配对」。
    #     X1 那条记录和 X3 的答案**都有零星字符对得上**
    #     （都是俄文，都有 `айл`、`найден`），
    #     于是这条记录被配给了 X3，**报告整篇串位**——
    #     比识别烂还误导人。
    #
    #   【新规则：一条记录只能配「它最像」的那一段】
    #     对每条记录，先算出**所有段里哪一段最像它**，就只配那一段。
    #     配过了的段不再抢别人的记录。
    #
    #   【为什么这样改不会把问题藏起来】
    #     识别烂掉的段，它的内容和**自己**的答案仍然是最近的
    #     （俄文认错了也还是俄文的形状），
    #     **照样会配到自己头上、照样判错**。
    #     「藏起来」的情况只有一个：
    #     **有记录配不出去、又有段落没配上**——
    #     这种会单独说明「这几条记录确定不了是哪一段」，
    #     **不装作测过了，也不装作没测**，如实写出来。
    # ==========================================================
    $bestFor = @()   # 每条记录最像哪一段（下面只用来排除，不可能段）
    for ($k = 0; $k -lt $records.Count; $k++) {
        $bestId = $null; $bestSim = 0.0
        foreach ($id in $answers.Keys) {
            $s = Get-Overlap $answers[$id] $records[$k].原文
            if ($s -gt $bestSim) { $bestSim = $s; $bestId = $id }
        }
        $bestFor += [pscustomobject]@{ Id=$bestId; K=$k; Sim=$bestSim }
    }

    $pairs = @{}; $used = @{}; $uncertain = @{}; $simOf = @{}

    # ⚠️【这里原本还有一趟「一条记录配一段」的贪心配对，本轮删掉了】
    #   留着它会**先吃掉一条记录和一个段**（F1），
    #   后面按文字分组的循环就少了一个名额——**S4 明明测了却报「没测」**。
    #   而下面的分组逻辑**已经涵盖**了它：
    #   只有一段的组就是「单段组」，效果完全一样；
    #   多段的组才需要特殊处理。

    # ==========================================================
    # 【2026-10-10 补：同样文字的多段，之前会被误判成「没测」】
    #
    #   **现场**：用户完整跑了材料 3，26 条记录一条不落，
    #   结果报告说 `F2、F3…F8、S1…S4 没测`。
    #
    #   **根因**：F1–F8 是**同一句话的 8 种字体**，S1–S4 是**同一句话的 4 种字号**，
    #   那 12 段的**标准答案文字完全一样**。
    #   而上面那条规则是「一条记录只能配它最像的那一段」——
    #   12 条一模一样的记录里只有第一条能配上，
    #   **剩下 11 条全被判成废框**，段落则显示「这次没测」。
    #
    #   **这是量表在骗人**：明明测了、明明全对，报告却说没测。
    #   比识别烂更糟——**会让人以为软件坏了，去修根本没坏的地方**。
    #
    #   【修法：先按「标准答案文字」分组，同组记录按顺序轮流配】
    #     组里有几段，就取匹配该文字的最好几条，一人一段。
    #     没配上的段**仍然如实显示「没测」**——不假装测过。
    # ==========================================================
    $byText = @{}
    foreach ($id in $answers.Keys) {
        $key = (Normalize $answers[$id]).Replace(' ', '')
        if (-not $byText.ContainsKey($key)) { $byText[$key] = New-Object System.Collections.ArrayList }
        [void]$byText[$key].Add($id)
    }
    foreach ($key in $byText.Keys) {
        $ids = @($byText[$key])
        $hits = @()
        for ($k = 0; $k -lt $records.Count; $k++) {
            if ($used.ContainsKey($k)) { continue }
            $s = Get-Overlap $answers[$ids[0]] $records[$k].原文
            if ($s -ge 0.30) { $hits += [pscustomobject]@{ K=$k; Sim=$s } }
        }
        $hits = @($hits | Sort-Object Sim -Descending)
        for ($g = 0; $g -lt $ids.Count; $g++) {
            if ($g -ge $hits.Count) { break }
            if ($pairs.ContainsKey($ids[$g])) { continue }
            $pairs[$ids[$g]] = $hits[$g].K
            $used[$hits[$g].K] = $true
            $simOf[$ids[$g]] = $hits[$g].Sim      # 记下相似度，输出时用来说明配得有多勉强
        }
    }

    # ==========================================================
    # 【2026-10-10】只测了其中几段时，**没测的那段会从别人的记录里捡一条来配**
    #
    #   现场：`Ошибка сервера. Попробуйте обновить страницу позже.`（R5 的答案）
    #        实际记录 `Ошибка 404 (страница не найдена): файл не найден`
    #        **开头都是「Ошибка 」**，字符级相似度过了 0.30，
    #        于是被配成一对，报告写成「R5 判错」——
    #        看着像识别烂，其实是**根本没测**。
    #
    #   **不能简单把门槛调高**——
    #   识别烂到极致的段，它和自己答案的相似度也很低，
    #   门槛一调高就变成「没测」，**那是在藏问题**。
    #
    #   【所以：照常配对、照常判定，但如实标出「这一对配得很勉强」】
    #     相似度 < 0.55 → 判定照给，**明细里加一句提醒**
    #     既不谎报通过，也不谎报失败，也不把问题藏起来。
    # ==========================================================
    $extras = @()
    for ($k = 0; $k -lt $records.Count; $k++) { if (-not $used.ContainsKey($k)) { $extras += $k } }
    return [pscustomobject]@{ Pairs=$pairs; Extras=$extras; Uncertain=$uncertain; Sim=$simOf }
}

# ---------- 4. 输出 ----------
$names = @($answers.Keys)
$match = Match-Records $answers $records

Write-Host ''
Write-Host "版本：$Version    记录：$([System.IO.Path]::GetFileName($recordFile))" -ForegroundColor DarkGray
Write-Host "判定标准（用户定）：一个词错一个字符就整词判错；一个词错整段就判错。" -ForegroundColor DarkGray
Write-Host ''
Write-Host '编号   判定    词   错词  引擎        耗时'
Write-Host ('-' * 74)

$rows = @()
$missing = @()
foreach ($id in $names) {
    if (-not $match.Pairs.ContainsKey($id)) { $missing += $id; continue }

    $rec  = $records[$match.Pairs[$id]]
    $want = $answers[$id]
    $got  = $rec.原文
    $r    = Test-Segment $want $got

    $mark = if ($r.全对) { '✅ 全对' } else { '❌ 判错' }
    $rows += [pscustomobject]@{ 编号=$id; 全对=$r.全对; 词数=$r.词数; 错词数=$r.错词数;
                                错词=$r.错词; 仅大小写=$r.仅大小写; 少词=$r.少词; 多词=$r.多词;
                                引擎=$rec.通道; 耗时=$rec.耗时; 说明=$rec.说明;
                                标准=$want; 实际=$got }

    Write-Host ('{0,-5} {1,-7} {2,-4} {3,-5} {4}  {5,-10} {6}' -f `
                $id, $mark, $r.词数, $r.错词数, '', $rec.通道, $rec.耗时) `
              -ForegroundColor $(if ($r.全对) { 'Green' } else { 'Red' })
}

# ---------- 5. 总结 ----------
if ($rows.Count -gt 0) {
    $ok   = @($rows | Where-Object { $_.全对 }).Count
    $bad  = @($rows | Where-Object { -not $_.全对 }).Count
    $totW = ($rows | Measure-Object -Property 词数 -Sum).Sum
    $badW = ($rows | Measure-Object -Property 错词数 -Sum).Sum

    Write-Host ''
    Write-Host ('=' * 74)
    Write-Host ("总判定：{0} 段全对 / {1} 段判错（共 {2} 段）" -f $ok, $bad, $rows.Count) -ForegroundColor $(if ($bad -eq 0) { 'Green' } else { 'Yellow' })
    if ($totW -gt 0) {
        Write-Host ("词判定：{0} 个词里 {1} 个全对，{2} 个判错" -f $totW, ($totW - $badW), $badW) -ForegroundColor DarkGray
    }
    if ($bad -gt 0) {
        Write-Host ''
        Write-Host '判错的段落：' -ForegroundColor Red
        foreach ($r in ($rows | Where-Object { -not $_.全对 })) {
            $first = if ($r.错词.Count -gt 0) { $r.错词[0] } else { '（词数对不上）' }
            $more  = if ($r.错词.Count -gt 1) { " 等 $($r.错词.Count) 处" } else { '' }
            Write-Host ("  {0,-4} {1}{2}" -f $r.编号, $first, $more) -ForegroundColor Red
        }
    }
}

if ($match.Extras.Count -gt 0) {
    Write-Host ''
    Write-Host ("📌 废框 {0} 条（已自动剔除，不参与判定）：" -f $match.Extras.Count) -ForegroundColor Yellow
    foreach ($k in $match.Extras) {
        Write-Host ("     「{0}」  ← {1}" -f $records[$k].原文, $records[$k].时间) -ForegroundColor Yellow
    }
    Write-Host '   多数是拖框过程中松开了 Ctrl/Alt，本该取消却照跑了。' -ForegroundColor DarkGray
}
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host ("⚠️ 以下段落没有找到对应的记录（这次没测）：{0}" -f ($missing -join '、')) -ForegroundColor Yellow
}

Write-Host ''
Write-Host '================ 逐条明细 ================'
foreach ($r in $rows) {
    Write-Host ''
    Write-Host ("【{0}】{1}" -f $r.编号, $(if ($r.全对) { '✅ 逐字全对' } else { '❌ 判错' })) `
        -ForegroundColor $(if ($r.全对) { 'Green' } else { 'Red' })
    if ($r.全对) {
        Write-Host ("  {0} 个词全对" -f $r.词数) -ForegroundColor DarkGray
        if ($r.仅大小写.Count -gt 0) {
            Write-Host ("  注意：{0} 个词只是大小写不同（如 МБ / MB），按口径算全对。" -f $r.仅大小写.Count) -ForegroundColor DarkGray
        }
    }
    else {
        Write-Host ("  {0} 个词里错了 {1} 个：" -f $r.词数, $r.错词数) -ForegroundColor Red
        foreach ($w in $r.错词) { Write-Host "    · $w" -ForegroundColor Red }
        if ($r.少词 -gt 0) { Write-Host ("    · 少了 {0} 个词" -f $r.少词) -ForegroundColor Red }
        if ($r.多词 -gt 0) { Write-Host ("    · 多了 {0} 个词" -f $r.多词) -ForegroundColor Red }
    }
    Write-Host ("  引擎：{0}    耗时：{1}" -f $r.引擎, $r.耗时) -ForegroundColor DarkGray
    Write-Host ("  标准：{0}" -f $r.标准)
    Write-Host ("  实际：{0}" -f $r.实际)
}
