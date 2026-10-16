$ErrorActionPreference = 'Stop'
$src = 'src\Core\Capture\OcrTextCapture.cs'
$out = 'src\Core\Capture\OcrTextCapture.FirstPass.cs'

# 先把主文件恢复到第1步后的状态，保证本脚本可重复跑
$base = [System.IO.File]::ReadAllLines('tools\脚本\_step1.tmp.cs')
$enc = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($src, $base, $enc)

$L = [System.IO.File]::ReadAllLines($src)
Write-Host "原文行数: $($L.Count)"
if ($L.Count -ne 2218) { throw "行数不是 2218，停下" }

function ChkEq([int]$i1, [string]$want) {
    if ($L[$i1-1] -cne $want) { throw "L$i1 不符`n  期望[$want]`n  实际[$($L[$i1-1])]" }
}
function ChkHas([int]$i1, [string]$want) {
    if (-not $L[$i1-1].Contains($want)) { throw "L$i1 不含[$want]`n  实际[$($L[$i1-1])]" }
}

# ---- 边界断言：断错一行就停，绝不猜 ----
ChkEq 154 '        try'
ChkEq 155 '        {'
ChkEq 156 '            // 4) 第一遍识别（原生大小）'
ChkHas 157 'var first = await RecognizeOnce(engine, plain, cancellationToken)'
ChkEq 189 '            Bitmap? upscaled = null;'
ChkEq 190 '            try'
ChkEq 191 '            {'
ChkHas 192 'string text = first.Text;'
ChkEq 287 '                AppLogger.Info($"OCR 最终采用：{note}，两遍合计识别耗时 {ocrMs} 毫秒");'
ChkEq 288 ''
ChkHas 289 '// 【下面两个变量必须在跑备用引擎之前就声明好】'
ChkEq 292 '                string channelUsed = ChannelName;'
ChkEq 293 '                string scoreNote = "";'
ChkEq 2197 '            }'
ChkEq 2198 '            finally'
ChkEq 2199 '            {'
ChkEq 2200 '                upscaled?.Dispose();'
ChkEq 2201 '            }'
ChkEq 2202 '        }'
ChkEq 2203 '        catch (OperationCanceledException)'
ChkEq 2212 '        finally'
ChkEq 2214 '            // 每一张 GDI 位图都必须释放：连续识别几十次内存不许涨（T3-8 的判定标准）'
ChkEq 2215 '            plain.Dispose();'
ChkEq 2217 '    }'
ChkEq 2218 '}'
Write-Host "边界断言全部通过"

for ($i = 157; $i -le 287; $i++) {
    if ($L[$i-1] -match '\b(return|goto)\b') { throw "L$i 出现跳转语句，须人工判断：$($L[$i-1])" }
}
Write-Host "搬家段内无 return/goto ✅"

function Dedent([string]$s) { if ($s.StartsWith('    ')) { return $s.Substring(4) } return $s }

# ---- 新文件 ----
# 方法体 = 原 L157..L287 整体左移 4 格（L156 那句步骤注释留在调用点）
# 前置 `long preprocessStart;`：原来它在主文件 L125 声明，L202/L210 用；
#   搬走后 L202/L210 进了方法，得在方法里自己声明一个（同名同类型，行为不变）。
$body = New-Object System.Collections.Generic.List[string]
$body.Add('        long preprocessStart;')
for ($i = 157; $i -le 287; $i++) { $body.Add((Dedent $L[$i-1])) }
$body.Add('')
$body.Add('            return new FirstPassOutcome(first, text, lineCount, ocrMs, note, upscaled);')
$body.Add('        }')
$body.Add('        catch')
$body.Add('        {')
$body.Add('            upscaled?.Dispose();')
$body.Add('            throw;')
$body.Add('        }')
$body.Add('    }')
$body.Add('}')

$head = @(
    'using System;',
    'using System.Drawing;',
    'using System.Threading;',
    'using System.Threading.Tasks;',
    'using TranslatorApp.Core.Abstractions;',
    'using TranslatorApp.Core.Logging;',
    'using Windows.Media.Ocr;',
    '',
    'namespace TranslatorApp.Core.Capture;',
    '',
    'internal sealed partial class OcrTextCapture : ITextCapture',
    '{',
    '    private readonly record struct FirstPassOutcome(',
    '        PassResult First,',
    '        string Text,',
    '        int LineCount,',
    '        long OcrMs,',
    '        string Note,',
    '        Bitmap? Upscaled);',
    '',
    '    private async Task<FirstPassOutcome> RunFirstPassAsync(',
    '        Bitmap plain, Bitmap raw, OcrEngine engine, CancellationToken cancellationToken)',
    '    {'
)

$newFile = New-Object System.Collections.Generic.List[string]
foreach ($x in $head) { $newFile.Add([string]$x) }
foreach ($x in $body) { $newFile.Add([string]$x) }

# ---- 主文件 = 原 L1..L156 + 接缝(7行) + 原 L190..L191 + 原 L288..L2218 ----
# L190/L191 那两行（try / {）必须留着：它们开的是原来那个「内层 try」，
# 它的 finally（原 L2200 `upscaled?.Dispose();`）还在下面，不是新写的结构。
$seam = @(
    '            FirstPassOutcome firstPass = await RunFirstPassAsync(plain, raw, engine, cancellationToken).ConfigureAwait(false);',
    '            PassResult first = firstPass.First;',
    '            string text = firstPass.Text;',
    '            int lineCount = firstPass.LineCount;',
    '            long ocrMs = firstPass.OcrMs;',
    '            string note = firstPass.Note;',
    '            Bitmap? upscaled = firstPass.Upscaled;'
)

$main = New-Object System.Collections.Generic.List[string]
for ($i = 1; $i -le 156; $i++) { $main.Add($L[$i-1]) }
foreach ($s in $seam) { $main.Add($s) }
for ($i = 190; $i -le 191; $i++) { $main.Add($L[$i-1]) }
for ($i = 288; $i -le 2218; $i++) { $main.Add($L[$i-1]) }

[System.IO.File]::WriteAllLines($out, $newFile.ToArray(), $enc)
[System.IO.File]::WriteAllLines($src, $main.ToArray(), $enc)

Write-Host "新文件: $out  $($newFile.Count) 行"
Write-Host "主文件: $src  $($main.Count) 行"
Write-Host "`n主文件接缝（L154..L168）："
154..168 | ForEach-Object { Write-Host ("  L{0,-4} {1}" -f $_, $main[$_-1]) }
