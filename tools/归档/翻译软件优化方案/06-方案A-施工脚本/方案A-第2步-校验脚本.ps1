$ErrorActionPreference = 'Stop'
$orig = 'tools\脚本\_step1.tmp.cs'            # 第1步之后的原文（2218 行）
$mainF = 'src\Core\Capture\OcrTextCapture.cs'
$newF = 'src\Core\Capture\OcrTextCapture.FirstPass.cs'
$gen = 'tools\脚本\临时-拆分第2步v4.ps1'      # 生成脚本本身

$O = [System.IO.File]::ReadAllLines($orig)
$M = [System.IO.File]::ReadAllLines($mainF)
$N = [System.IO.File]::ReadAllLines($newF)
Write-Host "原文 $($O.Count) 行 / 主文件 $($M.Count) 行 / 新文件 $($N.Count) 行"
if ($O.Count -ne 2218) { throw "原文基线不是 2218 行" }

function Dedent([string]$s) { if ($s.StartsWith('    ')) { return $s.Substring(4) } return $s }
$fail = 0
function Cmp([string]$label, [string]$want, [string]$got, [int]$ln) {
    if ($want -cne $got) {
        Write-Host "  X $label 第 $ln 行"
        Write-Host "      期望[$want]"
        Write-Host "      实际[$got]"
        $script:fail++
    }
}

# ============ 一、主文件逐行 = 原文拼回来的？ ============
Write-Host "`n===== 一、主文件逐行核对 ====="
$expect = New-Object System.Collections.Generic.List[string]
for ($i = 1; $i -le 156; $i++) { $expect.Add($O[$i-1]) }
$seam = @(
    '            FirstPassOutcome firstPass = await RunFirstPassAsync(plain, raw, engine, cancellationToken).ConfigureAwait(false);',
    '            PassResult first = firstPass.First;',
    '            string text = firstPass.Text;',
    '            int lineCount = firstPass.LineCount;',
    '            long ocrMs = firstPass.OcrMs;',
    '            string note = firstPass.Note;',
    '            Bitmap? upscaled = firstPass.Upscaled;'
)
foreach ($s in $seam) { $expect.Add($s) }
for ($i = 190; $i -le 191; $i++) { $expect.Add($O[$i-1]) }
for ($i = 288; $i -le 2218; $i++) { $expect.Add($O[$i-1]) }
Write-Host "期望 $($expect.Count) 行 / 实际 $($M.Count) 行"
if ($expect.Count -ne $M.Count) { throw "主文件行数不符" }
for ($k = 0; $k -lt $M.Count; $k++) { Cmp '主文件' $expect[$k] $M[$k] ($k+1) }
if ($fail -eq 0) { Write-Host "OK 主文件 = 原文 L1..L156 + 7 行接缝 + 原文 L190..L191 + 原文 L288..L2218" }

# ============ 二、新文件方法体逐行 = 原文 L157..L287 左移 4 格？ ============
Write-Host "`n===== 二、新文件方法体逐行核对 ====="
$startIdx = -1; $retIdx = -1
for ($k = 0; $k -lt $N.Count; $k++) {
    if ($startIdx -lt 0 -and $N[$k].Trim() -ceq 'long preprocessStart;') { $startIdx = $k }
    if ($retIdx -lt 0 -and $N[$k].Trim().StartsWith('return new FirstPassOutcome(')) { $retIdx = $k }
}
if ($startIdx -lt 0 -or $retIdx -lt 0) { throw "找不到方法体起止" }
Write-Host "方法体第 $($startIdx+1) 行起 / return 在第 $($retIdx+1) 行"

$expectBody = New-Object System.Collections.Generic.List[string]
$expectBody.Add('        long preprocessStart;')
for ($i = 157; $i -le 287; $i++) { $expectBody.Add((Dedent $O[$i-1])) }
$expectBody.Add('')
$actualBody = @($N[$startIdx..($retIdx-1)])
Write-Host "期望 $($expectBody.Count) 行 / 实际 $($actualBody.Count) 行"
if ($expectBody.Count -ne $actualBody.Count) {
    Write-Host "  首行: 期望[$($expectBody[0])] 实际[$($actualBody[0])]"
    Write-Host "  末行: 期望[$($expectBody[-1])] 实际[$($actualBody[-1])]"
    throw "方法体行数不符"
}
$badBody = 0
for ($k = 0; $k -lt $expectBody.Count; $k++) {
    if ($expectBody[$k] -cne $actualBody[$k]) {
        Write-Host "  X 方法体第 $($k+1) 行"
        Write-Host "      期望[$($expectBody[$k])]"
        Write-Host "      实际[$($actualBody[$k])]"
        $badBody++
        if ($badBody -gt 6) { break }
    }
}
if ($badBody -eq 0) { Write-Host "OK 方法体 = 原文 L157..L287 整体左移 4 格 + 1 行 preprocessStart 声明" }
if ($badBody -gt 0) { $fail++ }

# ============ 三、新写的行逐条列出（只有这些不是搬家） ============
Write-Host "`n===== 三、非搬运的新增行（人工审） ====="
Write-Host "--- 新文件头 ---"
for ($k = 0; $k -lt $startIdx; $k++) { Write-Host ("  N{0,-4} {1}" -f ($k+1), $N[$k]) }
Write-Host "--- 新文件尾 ---"
for ($k = $retIdx; $k -lt $N.Count; $k++) { Write-Host ("  N{0,-4} {1}" -f ($k+1), $N[$k]) }
Write-Host "--- 主文件接缝 ---"
for ($k = 156; $k -lt 163; $k++) { Write-Host ("  M{0,-4} {1}" -f ($k+1), $M[$k]) }

# ============ 四、搬走的字符总量对照（逐行已证，这里只是总量差） ============
Write-Host "`n===== 四、行数账 ====="
Write-Host ("  主文件 {0} = 原文 2218 - 搬走 132(156..287) + 接缝 7 + 保留空行 3 ?" -f $M.Count)
$segLen = 287 - 156 + 1
Write-Host "  实际: 原文段落 L156..L287 共 $segLen 行"
Write-Host ("  主文件 2218 - {0} + 7 = {1}（期望 {2}）" -f $segLen, (2218 - $segLen + 7), $M.Count)
Write-Host ("  但主文件第156行那句注释留下了，所以段落少搬1行 → 2218 - {0} + 7 = {1}" -f ($segLen-1), (2218 - ($segLen-1) + 7))

if ($fail -eq 0) { Write-Host "`n===== 全部通过：纯搬家，零改写 =====" } else { throw "有 $fail 处不一致" }
