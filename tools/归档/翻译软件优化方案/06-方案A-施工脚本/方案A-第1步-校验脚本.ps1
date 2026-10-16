# 第1步「只搬家」的硬校验：
#  A. 主文件 = 原文的「严格递增子序列」（顺序没乱、只少了搬走的块）
#  B. 三个新文件去掉样板行后，各自也是原文的严格递增子序列
#  C. 原生源文件本身就有重复行，所以子序列用线性扫描（标准算法）
$ErrorActionPreference = 'Stop'
$orig = [System.IO.File]::ReadAllLines('tools\脚本\_orig.tmp.cs')
"原文行数：$($orig.Count)"

$isBoiler = {
    param($l)
    $l -eq '' -or $l -eq '{' -or $l -eq '}' -or
    $l -like 'using *' -or $l -like 'namespace *' -or $l -like 'internal sealed *'
}

function CheckSub($name, $path) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $body = @($lines | Where-Object { -not (& $isBoiler $_) })
    # 主文件那行 partial 是唯一允许的改写，校验时还原成原文形态
    $body = @($body | ForEach-Object { $_ -replace '^internal sealed partial class', 'internal sealed class' })
    $i = 0; $bad = $null; $n = 0
    foreach ($l in $body) {
        while ($i -lt $orig.Count -and $orig[$i] -cne $l) { $i++ }
        if ($i -ge $orig.Count) { $bad = $l; break }
        $i++; $n++
    }
    if ($bad -ne $null) {
        $t = if ($bad.Length -gt 60) { $bad.Substring(0, 60) } else { $bad }
        "❌ {0,-42} 顺序对不上，卡在该行：{1}" -f $name, $t
    } else {
        "✅ {0,-42} {1} 行按原序命中原文（未乱序）" -f $name, $n
    }
}

CheckSub '主文件 OcrTextCapture.cs'        'src\Core\Capture\OcrTextCapture.cs'
CheckSub 'Utils'                            'src\Core\Capture\OcrTextCapture.Utils.cs'
CheckSub 'Rules'                            'src\Core\Capture\OcrTextCapture.Rules.cs'
CheckSub 'Tuning'                           'src\Core\Capture\OcrTextCapture.Tuning.cs'
