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
# 注意：--回放 模式只在 src\TranslatorApp\bin\Release\... 里构建得出，
#       release 目录的包如果没有重新发布，就没有这个模式。
# ============================================================
param(
    [string]$Exe = '',
    [string]$Result = 'tools\临时\回放-结果.txt',
    [string]$List = 'tools\临时\回放-清单.txt'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$corpus = Join-Path $root 'tools\语料'
$manifest = Join-Path $corpus '清单.json'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not $Exe) { $Exe = Join-Path $root 'src\TranslatorApp\bin\Release\net10.0-windows10.0.19041.0\TranslatorApp.exe' }
elseif (-not [IO.Path]::IsPathRooted($Exe)) { $Exe = Join-Path $root $Exe }
if (-not (Test-Path $Exe)) { throw "找不到程序：$Exe（先构建）" }

if (-not (Test-Path $manifest)) { throw "找不到图库清单：$manifest" }
$doc = [System.IO.File]::ReadAllText($manifest, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$items = @($doc.images | Sort-Object mat, id, file)

$lines = foreach ($it in $items) {
    $p = Join-Path $corpus $it.file
    if (-not (Test-Path $p)) { continue }
    $n = [int]([IO.Path]::GetFileNameWithoutExtension($it.file))
    $id = "材料$($it.mat)-$($it.id)"
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

& (Join-Path $PSScriptRoot '比对回放.ps1') -Result $resultPath
