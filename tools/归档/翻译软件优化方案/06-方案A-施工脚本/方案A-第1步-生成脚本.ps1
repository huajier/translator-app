# 方案A 第1步：纯搬家（Utils / Rules / Tuning）。只剪贴，不改字。
# 源：tools\脚本\_orig.tmp.cs（= 搬家前的 HEAD 原文，2842 行）
# 区间一律「扁平 int 数组，两两成对」，区间含前导空行 —— 避开 PowerShell 嵌套数组拍平的坑。
$ErrorActionPreference = 'Stop'
$origPath = 'tools\脚本\_orig.tmp.cs'
$lines = [System.IO.File]::ReadAllLines($origPath)
if ($lines.Count -ne 2842) { throw "源行数不对：$($lines.Count)（预期 2842），停下" }
$orig = @($lines)   # 留一份原文全量，供末尾自检

function Slice([int]$a, [int]$b) { $lines[($a - 1)..($b - 1)] }

$header = @()
$header += $lines[0..9]                                        # using 1-10
$header += @('', 'namespace TranslatorApp.Core.Capture;', '',
             'internal sealed partial class OcrTextCapture : ITextCapture', '{')
# Tuning 里没有用到任何 using 里的类型，但类声明里的 ITextCapture 需要 Abstractions
$headerNoUsing = @('using TranslatorApp.Core.Abstractions;', '',
                   'namespace TranslatorApp.Core.Capture;', '',
                   'internal sealed partial class OcrTextCapture : ITextCapture', '{')

function Compose($headerLines, [int[]]$pairs) {
    $out = [System.Collections.Generic.List[string]]::new()
    foreach ($l in $headerLines) { $out.Add($l) }
    for ($k = 0; $k -lt $pairs.Count; $k += 2) {
        foreach ($l in (Slice $pairs[$k] $pairs[$k + 1])) { $out.Add($l) }
    }
    $out.Add('}')
    return $out
}

# 每个区间都自带前导空行（第一块除外，由 header 收尾）
$utilsPairs  = @(2338,2388, 2389,2428, 2429,2439, 2440,2458, 2459,2467,
                 2584,2586, 2587,2595, 2596,2614, 2615,2627, 2628,2654, 2827,2829)
$rulesPairs  = @(2468,2510, 2511,2583, 2655,2739, 2740,2752,
                 2753,2764, 2765,2790, 2791,2826, 2830,2841)
$tuningPairs = @(29,148)   # 148 是空行，一起带走，主文件 { 后直接接 149

$utils  = Compose $header        $utilsPairs
$rules  = Compose $header        $rulesPairs
$tuning = Compose $headerNoUsing $tuningPairs

# ---- 主文件 = 原文 1..28 + 149..2337 + 结尾 } ----
$main = [System.Collections.Generic.List[string]]::new()
foreach ($l in (Slice 1 28))     { $main.Add($l) }
foreach ($l in (Slice 149 2337)) { $main.Add($l) }
$main.Add('}')
$idx = -1
for ($i = 0; $i -lt $main.Count; $i++) {
    if ($main[$i] -match '^internal sealed class OcrTextCapture') { $idx = $i; break }
}
if ($idx -lt 0) { throw '没找到类声明行' }
$main[$idx] = $main[$idx].Replace('internal sealed class', 'internal sealed partial class')

# ---- 写入（UTF-8 无 BOM，CRLF）----
$enc = [System.Text.UTF8Encoding]::new($false)
function Save($path, $arr) {
    [System.IO.File]::WriteAllText($path, (($arr -join "`r`n") + "`r`n"), $enc)
    "写出 {0} ：{1} 行" -f $path, $arr.Count
}
Save 'src\Core\Capture\OcrTextCapture.cs'        $main
Save 'src\Core\Capture\OcrTextCapture.Utils.cs'  $utils
Save 'src\Core\Capture\OcrTextCapture.Rules.cs'  $rules
Save 'src\Core\Capture\OcrTextCapture.Tuning.cs' $tuning

# ---- 自检：搬家前后「内容行多重集」必须逐字一致 ----
function Content($arr) {
    @($arr | Where-Object {
        $_ -ne '' -and $_ -notmatch '^\s*(using |namespace |internal sealed )' -and $_ -ne '{' -and $_ -ne '}'
    })
}
$all = @()
$all += $main; $all += $utils; $all += $rules; $all += $tuning
$a = @(Content $orig); $b = @(Content $all)
"内容行：原 $($a.Count) → 新 $($b.Count)"
$d = Compare-Object (@($a | Group-Object | Sort-Object Name)) (@($b | Group-Object | Sort-Object Name)) -Property Name, Count
if ($d) {
    "❌ 多重集有差异 $($d.Count) 条："
    foreach ($x in $d) { $t = $x.Name; if ($t.Length -gt 70) { $t = $t.Substring(0, 70) }; "  {0} x{1} [{2}]" -f $x.SideIndicator, $x.Count, $t }
} else {
    '✅ 内容行多重集逐字一致（无丢行、无重复、无改写）'
}
