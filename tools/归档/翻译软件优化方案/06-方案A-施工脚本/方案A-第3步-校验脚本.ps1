$ErrorActionPreference = 'Stop'
$origPath = 'tools\脚本\_step2.tmp.cs'
$mainPath = 'src\Core\Capture\OcrTextCapture.cs'
$newPath  = 'src\Core\Capture\OcrTextCapture.Foreign.cs'

$O = [System.IO.File]::ReadAllLines($origPath)
$M = [System.IO.File]::ReadAllLines($mainPath)
$F = [System.IO.File]::ReadAllLines($newPath)
Write-Host "原文 $($O.Count) 行 / 主文件 $($M.Count) 行 / 新文件 $($F.Count) 行`n"

$seam = @(
    '                    ForeignOutcome foreign = RunForeignPath(',
    '                        raw, first, text, lineCount, note,',
    '                        channelUsed, scoreNote, rejectedAsGarbage,',
    '                        windowsSaysChinese, windowsCharsForCompare, windowsScoreForCompare);',
    '                    text = foreign.Text;',
    '                    lineCount = foreign.LineCount;',
    '                    channelUsed = foreign.ChannelUsed;',
    '                    scoreNote = foreign.ScoreNote;',
    '                    rejectedAsGarbage = foreign.RejectedAsGarbage;'
)

Write-Host "===== 一、主文件逐行核对 ====="
$exp = New-Object System.Collections.Generic.List[string]
for ($i = 1; $i -le 269; $i++) { $exp.Add($O[$i-1]) }
foreach ($s in $seam) { $exp.Add($s) }
for ($i = 1527; $i -le $O.Count; $i++) { $exp.Add($O[$i-1]) }
Write-Host "期望 $($exp.Count) 行 / 实际 $($M.Count) 行"
if ($exp.Count -ne $M.Count) { throw "行数不符" }
$bad = 0
for ($k = 0; $k -lt $exp.Count; $k++) {
    if ($exp[$k] -cne $M[$k]) {
        if ($bad -lt 5) { Write-Host "  ❌ 第 $($k+1) 行`n     期望[$($exp[$k])]`n     实际[$($M[$k])]" }
        $bad++
    }
}
if ($bad -gt 0) { throw "主文件有 $bad 行不符" }
Write-Host "OK 主文件 = 原文 L1..L269 + 9 行接缝 + 原文 L1527..L$($O.Count)"

Write-Host "`n===== 二、新文件方法体逐行核对 ====="
# 新文件头 21 行；方法体从第 22 行起，长度 = 原 L270..L1526 = 1257 行
$head = 21
$bodyLen = 1526 - 270 + 1
Write-Host "方法体第 $($head+1) 行起 / 期望 $bodyLen 行"
$bad2 = 0
for ($k = 0; $k -lt $bodyLen; $k++) {
    $src = $O[269 + $k]
    $want = if ($src.Length -eq 0) { $src } else { $src.Substring(12) }
    $got = $F[$head + $k]
    if ($want -cne $got) {
        if ($bad2 -lt 5) { Write-Host "  ❌ 方法体第 $($k+1) 行`n     期望[$want]`n     实际[$got]" }
        $bad2++
    }
}
if ($bad2 -gt 0) { throw "方法体有 $bad2 行不符" }
Write-Host "OK 方法体 = 原文 L270..L1526 整体左移 12 格"

Write-Host "`n===== 三、非搬运的新增行（人工审） ====="
Write-Host "--- 新文件头 ---"
1..$head | ForEach-Object { Write-Host ("  N{0,-5} {1}" -f $_, $F[$_-1]) }
Write-Host "--- 新文件尾 ---"
($head + $bodyLen)..$F.Count | ForEach-Object { Write-Host ("  N{0,-5} {1}" -f $_, $F[$_-1]) }
Write-Host "--- 主文件接缝 ---"
270..278 | ForEach-Object { Write-Host ("  M{0,-5} {1}" -f $_, $M[$_-1]) }

Write-Host "`n===== 四、行数账 ====="
Write-Host "  主文件 $($M.Count) = 原文 269 + 接缝 9 + 尾段 $($O.Count - 1527 + 1) = $(269 + 9 + ($O.Count - 1527 + 1))"
Write-Host "  新文件 $($F.Count) = 头 $head + 方法体 $bodyLen + 尾 4 = $(($head + $bodyLen + 4))"
Write-Host "`n===== 全部通过：纯搬家，零改写 ====="
