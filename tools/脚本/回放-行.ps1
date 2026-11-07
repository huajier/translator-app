# 行级回放（S2）：把 行真值.json 的 80 张裁样单独回放一遍，判 L_行 / L_整块，
# 并从 app 日志里算耗时（平均/中位/最慢段），最后按 游戏 / 档 / 极性 汇总。
# 用法：
#   & ".\tools\脚本\回放-行.ps1"
#   & ".\tools\脚本\回放-行.ps1" -Exe "release\v0.3.4.2\TranslatorApp.exe"
# 注意：它用自己的一份回放清单（id = 行-NN），不会覆盖 331 张那份 回放-清单.txt。

param(
    [string]$Exe = 'release\v0.3.4.3\TranslatorApp.exe',
    [string]$行真值 = 'tools\素材\行真值.json',
    [string]$清单 = 'tools\语料\回放-清单-行.txt',
    [string]$结果 = 'tools\临时\回放-行结果.txt'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function 全([string]$p) { if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $root $p } }

$exePath = 全 $Exe
if (-not (Test-Path $exePath)) { throw "找不到 exe：$exePath" }
$truthPath = 全 $行真值
$listPath = 全 $清单
$resultPath = 全 $结果

# ---------- 1. 造回放清单 ----------
$doc = [IO.File]::ReadAllText($truthPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
$corpus = Join-Path $root 'tools\语料'
$listLines = New-Object System.Collections.ArrayList
foreach ($e in @($doc.images)) {
    $full = [IO.Path]::GetFullPath((Join-Path $corpus ([string]$e.file)))
    if (-not (Test-Path $full)) { throw "裁样不存在：$full（真值 $($e.编号) 号）" }
    [void]$listLines.Add(("{0}|{1}" -f $e.id, $full))
}
[IO.File]::WriteAllLines($listPath, [string[]]$listLines.ToArray(), (New-Object System.Text.UTF8Encoding($true)))
Write-Host ("回放清单：{0}（{1} 张）" -f $listPath, $listLines.Count) -ForegroundColor Cyan

# ---------- 2. 跑 exe ----------
if (Test-Path $resultPath) { Remove-Item $resultPath -Force }
Get-Process TranslatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$logPath = Join-Path $root ("tools\日志\app-{0}.log" -f (Get-Date -Format 'yyyyMMdd'))
$logBefore = if (Test-Path $logPath) { ([IO.File]::ReadAllLines($logPath, [Text.Encoding]::UTF8)).Count } else { 0 }

Write-Host ("开始回放：{0}" -f $exePath) -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
& $exePath --回放 $listPath $resultPath

# exe 是 WPF 程序，& 调用会立刻返回、实际在后台跑；所以等结果文件写稳了再往下走。
$limit = 600
$lastLen = -1
while ($sw.Elapsed.TotalSeconds -lt $limit) {
    if (Test-Path $resultPath) {
        $len = (Get-Item $resultPath).Length
        if ($len -gt 0 -and $len -eq $lastLen) { break }
        $lastLen = $len
    }
    Start-Sleep -Milliseconds 500
}
$sw.Stop()
Write-Host ("回放结束，墙钟 {0:N1} 秒" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Cyan
if (-not (Test-Path $resultPath)) { throw "没有产出结果文件：$resultPath" }

# ---------- 3. 耗时（只解析本次新增的日志行）----------
$times = [ordered]@{}
if (Test-Path $logPath) {
    $all = [IO.File]::ReadAllLines($logPath, [Text.Encoding]::UTF8)
    $new = if ($all.Count -gt $logBefore) { $all[$logBefore..($all.Count - 1)] } else { @() }
    foreach ($l in $new) {
        # 【2026-11-07 改正则】第 11 轮（T3a-补）把回放日志从「…耗时 780 ms」改成
        #   「回放 行-01：成功，31 字，认字 780 ms / 翻译 1002 ms / 合计 1782 ms，翻译 = …」，
        #   老正则 `耗时 (\d+) ms` 再也匹配不上 → 这一段从此静默输出「日志里没抓到」，
        #   连续几轮报耗时都是空的，没人发现。现在按新格式抓「合计」，老格式作兜底。
        if ($l -match '回放\s+(\S+?)：.*?合计\s+(\d+)\s*ms') { $times[$Matches[1]] = [int]$Matches[2] }
        elseif ($l -match '回放\s+(\S+?)：.*?耗时\s+(\d+)\s*ms') { $times[$Matches[1]] = [int]$Matches[2] }
    }
}
$msList = @($times.Values)
Write-Host ""
Write-Host "=== 耗时 ===" -ForegroundColor Cyan
if ($msList.Count -eq 0) {
    Write-Host "  日志里没抓到本次回放的耗时行（看 $logPath）" -ForegroundColor Yellow
} else {
    $sorted = $msList | Sort-Object
    $avg = ($msList | Measure-Object -Average).Average
    $med = if ($sorted.Count % 2) { $sorted[[int](($sorted.Count - 1) / 2)] } else { [int](($sorted[[int]($sorted.Count / 2) - 1] + $sorted[[int]($sorted.Count / 2)]) / 2) }
    $slow = $times.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
    Write-Host ("  平均 {0:N1} ms / 中位 {1} ms / 最慢段 {2} = {3} ms（共 {4} 张）" -f $avg, $med, $slow.Key, $slow.Value, $msList.Count)
    $top = $times.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 5
    Write-Host ("  最慢 5 段：" + (($top | ForEach-Object { "$($_.Key) $($_.Value)ms" }) -join '、'))
}

# ---------- 4. 判分（复用 S0 量具）----------
$cmp = Join-Path $root 'tools\脚本\比对回放.ps1'
& $cmp -按行 -Result $resultPath *>&1 | Out-String -Width 200 | Write-Host

# ---------- 5. 分游戏 / 分档 / 分极性 ----------
$tsvPath = "$resultPath.行明细.tsv"
if (Test-Path $tsvPath) {
    $stat = @{}
    $lines = [IO.File]::ReadAllLines($tsvPath, [Text.Encoding]::UTF8)
    foreach ($l in ($lines | Select-Object -Skip 1)) {
        if (-not $l.Trim()) { continue }
        $c = $l -split "`t"
        $stat[$c[0]] = [pscustomobject]@{ 总 = [int]$c[2]; 对 = [int]$c[3]; 改 = [int]$c[4]; 丢 = [int]$c[5]; 多 = [int]$c[6] }
    }
    foreach ($维 in @('游戏', '档', '极性', 'mode')) {
        $agg = [ordered]@{}
        foreach ($e in @($doc.images)) {
            $k = [string]$e.$维
            if (-not $k) { $k = '未标' }
            if (-not $agg.Contains($k)) { $agg[$k] = [pscustomobject]@{ 总 = 0; 对 = 0; 改 = 0; 丢 = 0; 多 = 0 } }
            $s = $stat[[string]$e.id]
            if (-not $s) { continue }
            $agg[$k].总 += $s.总; $agg[$k].对 += $s.对; $agg[$k].改 += $s.改; $agg[$k].丢 += $s.丢; $agg[$k].多 += $s.多
        }
        Write-Host ""
        Write-Host ("=== 分{0} ===" -f $维) -ForegroundColor Cyan
        foreach ($k in ($agg.Keys | Sort-Object)) {
            $a = $agg[$k]
            $p = if ($a.总 -gt 0) { 100.0 * $a.对 / $a.总 } else { 0 }
            Write-Host ("  {0,-22} 行 {1,3}  对 {2,3} / 错 {3,3}（改 {4}/丢 {5}/多 {6}）→ {7:N1}%" -f `
                $k, $a.总, $a.对, ($a.总 - $a.对), $a.改, $a.丢, $a.多, $p)
        }
    }
}
