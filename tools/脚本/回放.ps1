# ============================================================
# 一句话跑完图库回放：
#   1) 从 tools\语料\清单.json 生成清单
#   2) 让程序用 --回放 模式吃这些图（跳过框选和截屏，其余流水线完全一样）
#   3) 比对成绩
#
# 用法：
#   & ".\tools\脚本\回放.ps1"
#   & ".\tools\脚本\回放.ps1" -Exe "release\v0.3.4.2\TranslatorApp.exe"
#
# 注意：默认跑的是 release\ 下最新那个版本目录里的 exe，**不是** src\TranslatorApp\bin\Release\。
#       原因（2026-10-23 踩到）：构建.ps1 只往 release\v<版本>\ 里 publish，
#       从不更新 bin\Release\net10.0-windows10.0.19041.0\ 根目录 ——
#       那个目录会一直停在很久以前那次构建（连引擎还是旧的 5.0.0）。
#       默认值指向它，等于「改完代码跑回放，验的是旧程序」，白跑一轮。
#       要用别处的程序，-Exe 显式给。
# ============================================================
param(
    [string]$Exe = '',
    [string]$Result = 'tools\语料\回放-结果.txt',
    [string]$List = 'tools\语料\回放-清单.txt',
    [switch]$Approve,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$corpus = Join-Path $root 'tools\语料'
$manifest = Join-Path $corpus '清单.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not $Exe) {
    # 取 release\ 下版本号最大的那个目录（v0.3.4.3 这种），没有 release\ 才退回 bin\Release。
    $relRoot = Join-Path $root 'release'
    $newest = $null
    if (Test-Path $relRoot) {
        $newest = Get-ChildItem $relRoot -Directory |
            # -cmatch 区分大小写：release\ 里混着 V0.3.2.12 这种大写 V 的旧目录，
            # [version] 转不动它，一个不匹配的目录就能把整个脚本炸掉。
            Where-Object { $_.Name -cmatch '^v[0-9]+(\.[0-9]+)+$' -and (Test-Path (Join-Path $_.FullName 'TranslatorApp.exe')) } |
            Sort-Object { [version]($_.Name.TrimStart('v')) } -Descending |
            Select-Object -First 1
    }
    $Exe = if ($newest) {
        Join-Path $newest.FullName 'TranslatorApp.exe'
    } else {
        Write-Host "警告：release\ 下没有可用产物，退回 src\TranslatorApp\bin\Release\（可能是旧程序，小心）。" -ForegroundColor Yellow
        Join-Path $root 'src\TranslatorApp\bin\Release\net10.0-windows10.0.19041.0\TranslatorApp.exe'
    }
}
elseif (-not [IO.Path]::IsPathRooted($Exe)) { $Exe = Join-Path $root $Exe }
if (-not (Test-Path $Exe)) { throw "找不到程序：$Exe（先构建）" }
Write-Host "用这个程序回放：$Exe" -ForegroundColor DarkGray

if (-not (Test-Path $manifest)) { throw "找不到图库清单：$manifest" }
$doc = [System.IO.File]::ReadAllText($manifest, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$items = @($doc.images | Sort-Object mat, id, file)

$lines = foreach ($it in $items) {
    $p = Join-Path $corpus $it.file
    if (-not (Test-Path $p)) { continue }
    $n = [int]([IO.Path]::GetFileNameWithoutExtension($it.file))
    # 材料 0 = 实战图，段名前面不带「材料0」，直接用「实战-名字」
    $id = if ($it.mat -eq 0) { "实战-$($it.id)" } else { "材料$($it.mat)-$($it.id)" }
    if ($n -gt 1) { $id = "$id-$n" }
    "$id|$p"
}

$listPath = if ([IO.Path]::IsPathRooted($List)) { $List } else { Join-Path $root $List }
$resultPath = if ([IO.Path]::IsPathRooted($Result)) { $Result } else { Join-Path $root $Result }
foreach ($p in @($listPath, $resultPath)) {
    $d = Split-Path -Parent $p
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}
[System.IO.File]::WriteAllText($listPath, (($lines -join "`n") + "`n"), $utf8)
if (Test-Path $resultPath) { Remove-Item -LiteralPath $resultPath -Force }

Write-Host ("回放 {0} 张图……" -f @($lines).Count) -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Exe -ArgumentList @('--回放', $listPath, $resultPath) -Wait -PassThru -NoNewWindow
$sw.Stop()
Write-Host ("程序跑完，用时 {0:N1} 秒" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Cyan

if (-not (Test-Path $resultPath)) { throw "没有生成回放结果：$resultPath（程序可能没构建 --回放 模式）" }

# 调比对脚本并带出退出码。比对脚本里「有退步」会 exit 1。
#
# 【为什么要先 $LASTEXITCODE = 0】$LASTEXITCODE 只在「外部程序退出」或「被调脚本里 exit」时被设置。
#   「没有基线」「-Approve 建基线」这两条路上，比对脚本是 return、不会设置它，
#   那时 $LASTEXITCODE 还留着上一条外部命令的值（调用方可能刚跑过 dotnet），
#   不先清零就会把不相干的旧退出码当成本次结果。
$LASTEXITCODE = 0
& (Join-Path $PSScriptRoot '比对回放.ps1') -Result $resultPath -Approve:$Approve -Force:$Force
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
