$ErrorActionPreference = 'Stop'
$src = 'src\Core\Capture\OcrTextCapture.cs'
$out = 'src\Core\Capture\OcrTextCapture.Foreign.cs'
$enc = New-Object System.Text.UTF8Encoding($false)

# 先把主文件复位到第 2 步后的状态，保证本脚本可重复跑
$base = [System.IO.File]::ReadAllLines('tools\脚本\_step2.tmp.cs')
[System.IO.File]::WriteAllLines($src, $base, $enc)

$L = [System.IO.File]::ReadAllLines($src)
Write-Host "原文行数: $($L.Count)"
if ($L.Count -ne 2096) { throw "行数不是 2096，停下" }

function ChkEq([int]$i1, [string]$want) {
    if ($L[$i1-1] -cne $want) { throw "L$i1 不符`n  期望[$want]`n  实际[$($L[$i1-1])]" }
}

# ---- 边界断言：断错一行就停，绝不猜 ----
ChkEq 266 '                bool rejectedAsGarbage = false;'
ChkEq 267 ''
ChkEq 268 '                if (!windowsSaysChinese || colorful)'
ChkEq 269 '                {'
ChkEq 270 '                    TesseractOcr.EnsureInitialized();'
ChkEq 272 '                    if (TesseractOcr.IsAvailable)'
ChkEq 317 '                        using Bitmap foreignImage = ImagePreprocessor.UpscaleForForeign(raw);'
ChkEq 618 '                            Bitmap[] planes = ImagePreprocessor.PrepareAllPlanes(foreignImage);'
ChkEq 1171 '                            finally'
ChkEq 1173 '                                foreach (Bitmap b in planes) b?.Dispose();'
ChkEq 1196 '                        string chiForeignText = "";'
ChkEq 1210 '                        string? tessText = best.Text;'
ChkEq 1287 '                            using Bitmap redChineseImage = ImagePreprocessor.PreprocessRedChannel(raw, 2.0);'
ChkEq 1315 '                        if (!string.IsNullOrWhiteSpace(tessText))'
ChkEq 1500 '                                text = cleanedTess;'
ChkEq 1501 '                                lineCount = TextNormalizer.CountLines(cleanedTess);'
ChkEq 1502 '                                channelUsed = "Tesseract";'
ChkEq 1526 '                    }'
ChkEq 1527 '                }'
ChkEq 1528 '                else'
ChkEq 1529 '                {'
Write-Host "边界断言全部通过"

# ---- 段内禁止跳转语句（搬进方法不能改变控制流）----
for ($i = 270; $i -le 1526; $i++) {
    if ($L[$i-1] -match '\b(return|goto|break|continue|yield)\b') {
        throw "L$i 出现跳转语句，须人工判断：$($L[$i-1])"
    }
}
Write-Host "段内无 return/goto/break/continue ✅"

# ---- 段内实际用到的类（决定新文件需要哪些 using）----
$cls = @{}
for ($i = 270; $i -le 1526; $i++) {
    $t = $L[$i-1]
    if ($t -match '^\s*//') { continue }
    foreach ($m in [regex]::Matches($t, '\b([A-Z][A-Za-z0-9_]*)\.')) { $cls[$m.Groups[1].Value] = 1 }
}
Write-Host ("段内用到的类: " + (($cls.Keys | Sort-Object) -join ', '))

# ---- 整体左移 12 格（原缩进最小 20 → 方法体内最小 8）----
foreach ($i in 270..1526) {
    $t = $L[$i-1]
    if ($t.Length -eq 0) { continue }
    $ind = ($t -replace '^(\s*).*', '$1').Length
    if ($ind -lt 20) { throw "L$i 缩进只有 $ind（预期 ≥20），停下：$t" }
}
function Dedent12([string]$s) {
    if ($s.Length -eq 0) { return $s }
    return $s.Substring(12)
}

$head = @(
    'using System;',
    'using System.Drawing;',
    'using TranslatorApp.Core.Abstractions;',
    'using TranslatorApp.Core.Logging;',
    '',
    'namespace TranslatorApp.Core.Capture;',
    '',
    'internal sealed partial class OcrTextCapture : ITextCapture',
    '{',
    '    private readonly record struct ForeignOutcome(',
    '        string Text,',
    '        int LineCount,',
    '        string ChannelUsed,',
    '        string ScoreNote,',
    '        bool RejectedAsGarbage);',
    '',
    '    private ForeignOutcome RunForeignPath(',
    '        Bitmap raw, PassResult first, string text, int lineCount, string note,',
    '        string channelUsed, string scoreNote, bool rejectedAsGarbage,',
    '        bool windowsSaysChinese, int windowsCharsForCompare, int windowsScoreForCompare)',
    '    {'
)

$newFile = New-Object System.Collections.Generic.List[string]
foreach ($x in $head) { $newFile.Add([string]$x) }
foreach ($i in 270..1526) { $newFile.Add((Dedent12 $L[$i-1])) }
$newFile.Add('')
$newFile.Add('        return new ForeignOutcome(text, lineCount, channelUsed, scoreNote, rejectedAsGarbage);')
$newFile.Add('    }')
$newFile.Add('}')

# ---- 主文件 = 原 L1..L269 + 接缝(8行) + 原 L1527..L2096 ----
# if 判断（L268）与它的花括号留在主文件不变，只把分支体换成一句调用 + 5 行回写。
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

$main = New-Object System.Collections.Generic.List[string]
for ($i = 1; $i -le 269; $i++) { $main.Add($L[$i-1]) }
foreach ($s in $seam) { $main.Add($s) }
for ($i = 1527; $i -le 2096; $i++) { $main.Add($L[$i-1]) }

[System.IO.File]::WriteAllLines($out, $newFile.ToArray(), $enc)
[System.IO.File]::WriteAllLines($src, $main.ToArray(), $enc)

Write-Host "新文件: $out  $($newFile.Count) 行"
Write-Host "主文件: $src  $($main.Count) 行"
Write-Host "`n主文件接缝（L266..L282）："
266..282 | ForEach-Object { Write-Host ("  L{0,-5} {1}" -f $_, $main[$_-1]) }
