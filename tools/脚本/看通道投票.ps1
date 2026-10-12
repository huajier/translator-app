param([string]$LogFile = "tools\日志\app-20261012.log", [string]$Since = "13:3")

$ErrorActionPreference = 'Stop'
$names = @("红通道","绿通道","蓝通道","灰度","局部阈值","红通道不二值化")
$all = Get-Content $LogFile -Encoding UTF8 | Where-Object { $_ -match "^\[2026-10-12 $Since" }

$groups = New-Object System.Collections.ArrayList
$cur = $null
foreach ($l in $all) {
    if ($l -match "补跑几路（只看不换）") {
        if ($cur) { [void]$groups.Add($cur) }
        $cur = @{ __t = [regex]::Match($l,"(\d\d:\d\d:\d\d\.\d\d\d)").Groups[1].Value }
        continue
    }
    if ($l -match "补跑·(.+?)：分数 ([\d.]+)，最低词级分 ([\d.]+)，内容【(.*?)】") {
        if ($cur) { $cur[$matches[1]] = @{ C=[double]$matches[2]; M=[double]$matches[3]; T=$matches[4] } }
        continue
    }
    if ($l -match "取词成功：通道=(\S+) 字数=(\d+).*置信度 ([\d.]+)，(\d+) 个有效字符") {
        if ($cur) { $cur.__d = "置信度 $($matches[3]) 字数 $($matches[2])"; [void]$groups.Add($cur); $cur = $null }
    }
}
if ($cur) { [void]$groups.Add($cur) }

"补跑组数 = $($groups.Count)"
$idx = 0
foreach ($g in $groups) {
    $idx++
    "—— 组$idx [$($g.__t)] 交付：$($g.__d) ——"
    foreach ($n in $names) {
        if (-not $g.ContainsKey($n)) { continue }
        $p = $g[$n]
        $mark = if ($p.C -gt 0) { "" } else { "  ← 空的" }
        "    {0,-12} {1:F2} 最低{2,6:F2}{3}  【{4}】" -f $n, $p.C, $p.M, $mark, $p.T
    }
}