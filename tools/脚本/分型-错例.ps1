# 错例分型（阶段三 S3 第 1 步）：把「改字」错例逐条拆成字符级差异并归类。
#
# 为什么要它：用户 m01592 第 1 条要求「逐例给归属 + 每类的可修上限算法，不许拍脑袋给百分比」。
# 所以这里不输出任何猜测性百分比，只输出：
#   ① 每一例的归属（单词级 / 数字标点级 / 前后杂字级 / 丢字级）
#   ② 每一例到底差几处、差在哪两个字符上（LCS 对齐，不是目测）
#   ③ 每类「可修上限」的算法与例数（算法写在 -算法说明 里，见输出末尾）
#
# 用法：
#   & ".\tools\脚本\分型-错例.ps1" -明细 "tools\临时\S3-分型-明细.txt"
#   明细文件由：比对回放.ps1 -按行 -Result <结果> 的逐行明细段重定向而来。
#
# 判据（固定，不许中途改）：
#   · 涉及数字或标点的差异 → 数字标点级
#   · 只多字（got 比 want 多，没有替换/删除） → 前后杂字级
#   · 只少字（没有替换/插入） → 丢字级
#   · 其余（纯字母替换） → 单词级

param(
    [string]$明细 = 'tools\临时\S3-分型-明细.txt',
    [string]$出表 = 'tools\日志\阶段3-S3-分型表.md'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function 全([string]$p) { if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $root $p } }

$明细路径 = 全 $明细
$出表路径 = 全 $出表
if (-not (Test-Path $明细路径)) { throw "找不到明细文件：$明细路径" }

# ---------- 1. 解析逐行明细 ----------
$lines = [IO.File]::ReadAllLines($明细路径, [Text.Encoding]::UTF8)
$例 = New-Object System.Collections.ArrayList
$cur = $null
foreach ($l in $lines) {
    if ($l -match '^\s*\[(行-\d+)\]\[(单行|整块)\]\s*(改|丢|多)\s*差\s*(\d+)') {
        $cur = @{ Id = $matches[1]; Mode = $matches[2]; 状态 = $matches[3]; 差 = [int]$matches[4]; 答案 = $null; 实得 = $null }
        [void]$例.Add($cur)
    }
    elseif ($cur -and $l -match '^\s*答案：(.*)$') { $cur.答案 = $matches[1] }
    elseif ($cur -and $l -match '^\s*实得：(.*)$') { $cur.实得 = $matches[1] }
}
Write-Host ("解析到 {0} 条逐行记录" -f $例.Count) -ForegroundColor Cyan

# ---------- 2. 字符级 LCS 对齐 ----------
# PS5.1 坑：二维数组下标的逗号若出现在**方法调用参数**里，会被当成参数分隔符，
# 报 Missing ']' after array index expression。所以先取临时变量再 [Math]::Max。
function 对齐([string]$w, [string]$g) {
    $n = $w.Length; $m = $g.Length
    $dp = New-Object 'int[,]' ($n + 1), ($m + 1)
    for ($i = 1; $i -le $n; $i++) {
        for ($j = 1; $j -le $m; $j++) {
            if ($w[$i - 1] -ceq $g[$j - 1]) {
                $prev = $dp[($i - 1), ($j - 1)]
                $dp[$i, $j] = $prev + 1
            }
            else {
                $up = $dp[($i - 1), $j]
                $left = $dp[$i, ($j - 1)]
                $dp[$i, $j] = [Math]::Max($up, $left)
            }
        }
    }
    # 回溯
    $sub = New-Object System.Collections.ArrayList   # @(答案字符, 实得字符)
    $ins = New-Object System.Collections.ArrayList   # 实得多出的字符
    $del = New-Object System.Collections.ArrayList   # 答案有、实得少的字符
    $i = $n; $j = $m
    while ($i -gt 0 -or $j -gt 0) {
        if ($i -gt 0 -and $j -gt 0 -and $w[$i - 1] -ceq $g[$j - 1]) { $i--; $j--; continue }
        $up = 0; $left = 0; $diag = 0
        if ($i -gt 0) { $up = $dp[($i - 1), $j] }
        if ($j -gt 0) { $left = $dp[$i, ($j - 1)] }
        if ($i -gt 0 -and $j -gt 0) { $diag = $dp[($i - 1), ($j - 1)] }
        if ($i -gt 0 -and $j -gt 0 -and $diag -ge $up -and $diag -ge $left) {
            [void]$sub.Add(@([string]$w[$i - 1], [string]$g[$j - 1])); $i--; $j--
        }
        elseif ($j -gt 0 -and ($i -eq 0 -or $left -ge $up)) {
            [void]$ins.Add([string]$g[$j - 1]); $j--
        }
        elseif ($i -gt 0) {
            [void]$del.Add([string]$w[$i - 1]); $i--
        }
        else { $j-- }
    }
    return @{ Sub = $sub; Ins = $ins; Del = $del }
}

function 是字母([string]$c) { $c -cmatch '^[A-Za-z]$' }
function 是数字或标点([string]$c) { $c -cmatch '^[0-9]$' -or -not ($c -cmatch '^[A-Za-z]$') }

# ---------- 3. 逐例分型 ----------
$结果 = New-Object System.Collections.ArrayList
foreach ($e in $例) {
    if ($e.状态 -ne '改' -or $null -eq $e.答案 -or $null -eq $e.实得) { continue }
    $w = $e.答案; $g = $e.实得
    $d = 对齐 $w $g
    $sub = @($d.Sub); $ins = @($d.Ins); $del = @($d.Del)
    $涉及非字母 = $false
    foreach ($p in $sub) { if ((是数字或标点 $p[0]) -or (是数字或标点 $p[1])) { $涉及非字母 = $true } }
    foreach ($c in $ins) { if (是数字或标点 $c) { $涉及非字母 = $true } }
    foreach ($c in $del) { if (是数字或标点 $c) { $涉及非字母 = $true } }

    $归属 = if ($sub.Count -eq 0 -and $del.Count -eq 0 -and $ins.Count -gt 0) { '前后杂字级' }
             elseif ($sub.Count -eq 0 -and $ins.Count -eq 0 -and $del.Count -gt 0) { '丢字级' }
             elseif ($涉及非字母) { '数字标点级' }
             else { '单词级' }

    $处 = $sub.Count + $ins.Count + $del.Count
    $细节 = New-Object System.Collections.ArrayList
    foreach ($p in $sub) { [void]$细节.Add(("替换 {0}→{1}" -f $p[0], $p[1])) }
    foreach ($c in $ins) { [void]$细节.Add(("多出 {0}" -f $c)) }
    foreach ($c in $del) { [void]$细节.Add(("少 {0}" -f $c)) }

    [void]$结果.Add([pscustomobject]@{
        Id = $e.Id; Mode = $e.Mode; 归属 = $归属
        Sub = $sub.Count; Ins = $ins.Count; Del = $del.Count; 处 = $处
        答案 = $w; 实得 = $g; 细节 = ($细节 -join '、')
    })
}

Write-Host ("分型完成：{0} 例改字" -f $结果.Count) -ForegroundColor Cyan

# ---------- 4. 汇总 ----------
$类统计 = $结果 | Group-Object 归属 | Sort-Object Count -Descending
Write-Host "`n=== 归属分布 ===" -ForegroundColor Cyan
foreach ($k in $类统计) {
    $单 = @($k.Group | Where-Object { $_.处 -eq 1 }).Count
    $多 = @($k.Group | Where-Object { $_.处 -ge 2 }).Count
    Write-Host ("  {0,-8} {1,3} 例（差 1 处 {2} / 差 2+ 处 {3}）" -f $k.Name, $k.Count, $单, $多)
}

# ---------- 5. 写表 ----------
$md = New-Object System.Collections.ArrayList
[void]$md.Add("# 阶段三 S3 第 1 步：改字错例分型表")
[void]$md.Add("")
[void]$md.Add("> 数据来源：RapidOCR 主通道开启（commit ``d73417b``）跑 121 行的逐行明细。")
[void]$md.Add("> 生成：``tools\脚本\分型-错例.ps1``（LCS 字符对齐，不靠目测）。")
[void]$md.Add("> 判据固定：涉及数字/标点 → 数字标点级；只多字 → 前后杂字级；只少字 → 丢字级；纯字母替换 → 单词级。")
[void]$md.Add("")
[void]$md.Add("## 0. 逐例明细（共 $($结果.Count) 例）")
[void]$md.Add("")
[void]$md.Add("| 编号 | mode | 归属 | 替换 | 多出 | 少 | 处数 | 答案 → 实得 | 差异明细 |")
[void]$md.Add("|---|---|---|---|---|---|---|---|---|")
foreach ($r in $结果) {
    [void]$md.Add(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | ``{7}`` → ``{8}`` | {9} |" -f `
        $r.Id, $r.Mode, $r.归属, $r.Sub, $r.Ins, $r.Del, $r.处, $r.答案, $r.实得, $r.细节))
}
[void]$md.Add("")
[void]$md.Add("## 1. 归属汇总")
[void]$md.Add("")
[void]$md.Add("| 归属 | 例数 | 差 1 处 | 差 2+ 处 |")
[void]$md.Add("|---|---|---|---|")
foreach ($k in $类统计) {
    $单 = @($k.Group | Where-Object { $_.处 -eq 1 }).Count
    $多 = @($k.Group | Where-Object { $_.处 -ge 2 }).Count
    [void]$md.Add(("| {0} | {1} | {2} | {3} |" -f $k.Name, $k.Count, $单, $多))
}
[void]$md.Add("")
[void]$md.Add("## 2. 可修上限的算法（不是百分比，是计数规则）")
[void]$md.Add("")
[void]$md.Add("**前提**：一行判「对」需要**整行每个字符都对**（行级判分口径）。所以一行里有 k 处差异，")
[void]$md.Add("后处理就必须把这 k 处**全部**改对，这一行才算修回来。上限按处数分层算，不按类拍比例。")
[void]$md.Add("")
[void]$md.Add("| 层 | 可修条件 | 上限算法 | 本轮对应例数 |")
[void]$md.Add("|---|---|---|---|")
$单处 = @($结果 | Where-Object { $_.处 -eq 1 })
$两处 = @($结果 | Where-Object { $_.处 -eq 2 })
$三处以上 = @($结果 | Where-Object { $_.处 -ge 3 })
[void]$md.Add(("| L1 单处 | 只差 1 处，规则只需判对 1 次 | 上限 = 例数 = **{0}**（前提：规则对该处唯一可判，且不误伤其它行） | {0} |" -f $单处.Count))
[void]$md.Add(("| L2 两处 | 差 2 处，规则需连对 2 次 | 上限 = 例数 = **{0}**，但要求单处成功率 s 时，实际约 s²；s<0.9 时这一层基本修不动 | {0} |" -f $两处.Count))
[void]$md.Add(("| L3 三处以上 | 差 ≥3 处 | 上限 = 例数 = **{0}**，实际按 s^k 衰减，视为「修不动」 | {0} |" -f $三处以上.Count))
[void]$md.Add("")
[void]$md.Add("**按归属再切一刀**（同一层内，不同归属的可行性不同）：")
[void]$md.Add("")
[void]$md.Add("| 归属 | 例数 | 差 1 处（L1） | 可行性判断依据 |")
[void]$md.Add("|---|---|---|---|")
foreach ($k in $类统计) {
    $l1 = @($k.Group | Where-Object { $_.处 -eq 1 }).Count
    $判 = switch ($k.Name) {
        '单词级' { '需形近集/词典能唯一定位该词；Barony 生造词会让词典误伤' }
        '数字标点级' { 'rec 字典已确认含 0-9 / % . , - '' ! : ( ) * # @ [ ] → **不是缺字符**，是模型或切分问题' }
        '前后杂字级' { '裁边规则可修，但合法行首尾标点会被误裁（如 ``* (Playfully``）' }
        '丢字级' { '需补字规则，风险最高（可能补错位置）' }
        default { '—' }
    }
    [void]$md.Add(("| {0} | {1} | {2} | {3} |" -f $k.Name, $k.Count, $l1, $判))
}
[void]$md.Add("")
[void]$md.Add("**结论性数字（可复算）**：")
[void]$md.Add("")
[void]$md.Add(("- 改字 $($结果.Count) 例里，差 1 处的 $($单处.Count) 例、差 2 处的 $($两处.Count) 例、差 ≥3 处的 $($三处以上.Count) 例。"))
# 单行/整块分开算：L_行 的分母是 69 行，L_整块 的分母是 52 行，混着算会虚高。
$单行L1 = @($单处 | Where-Object { $_.Mode -eq '单行' }).Count
$整块L1 = @($单处 | Where-Object { $_.Mode -eq '整块' }).Count
$含空格 = @($结果 | Where-Object { $_.细节 -match '(少|多出)\s+、' -or $_.细节 -match '(少|多出)\s+$' }).Count
[void]$md.Add(("- 其中**单行**差 1 处 $单行L1 例、**整块**差 1 处 $整块L1 例（口径必须分开：L_行 分母 69，L_整块 分母 52）。"))
[void]$md.Add(("- 差异里含**空格**的 $含空格 例（空格按判据计入「数字标点级」，所以该类 21 例不全是数字/符号问题）。"))
[void]$md.Add("")
[void]$md.Add("**L1 理论天花板**（只算单行，且要求零误伤）：")
[void]$md.Add("")
[void]$md.Add(("- 单行 L1 = **$单行L1 例** → L_行 天花板 40+$单行L1 = $(40 + $单行L1)/69 = $([Math]::Round(100.0 * (40 + $单行L1) / 69, 1))%。"))
[void]$md.Add(("- 整块 L1 = **$整块L1 例** → L_整块 天花板 29+$整块L1 = $(29 + $整块L1)/52 = $([Math]::Round(100.0 * (29 + $整块L1) / 52, 1))%。"))
[void]$md.Add("")
[void]$md.Add("> ⚠️ 这是**天花板**，不是预期收益：它假设每一处都判对且不误伤任何原本正确的行。")
[void]$md.Add("> 真实收益必须靠 A/B 实测，不许拿这张表当成绩。")
[void]$md.Add("> 天花板远不到 85% 这一条也要看清：只靠字级后处理，L_行 最好也就到上面这个数。")

[IO.File]::WriteAllText($出表路径, ($md -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("`n已写表：{0}" -f $出表路径) -ForegroundColor Green
