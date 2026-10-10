<#
    发布前归档用户的实测记录。

    【为什么必须有这个脚本】
      每次发布新版，`release\<版本>\logs` 会被整个清掉重建，
      **用户的取词记录也跟着没了**——那是我们唯一的实测数据来源。
      之前已经这样丢过至少两轮，每次都要重新测，非常浪费用户的时间。

    【现在做什么】
      把 logs 目录里的记录**复制**到 `tools\日志\用户实测\`，
      文件名用「第N轮-日期.txt」，同一份文件不重复归档。

    用法：powershell -ExecutionPolicy Bypass -File "tools\脚本\归档取词记录.ps1" -Version v0.3.1
#>

param(
    [string]$Version = 'v0.3.1'
)

$ErrorActionPreference = 'Stop'

$repo   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$logDir = Join-Path $repo "release\$Version\logs"
$destDir = Join-Path $repo "tools\日志\用户实测"

if (-not (Test-Path $logDir)) {
    Write-Host "没有记录可归档：$logDir" -ForegroundColor DarkGray
    exit 0
}

New-Item -ItemType Directory -Path $destDir -Force | Out-Null

# 算下一个轮次号：看目录里已有的「第N轮-*.txt」最大是多少
$existing = @(Get-ChildItem $destDir -Filter '第*轮-*.txt' -EA SilentlyContinue)
$maxRound = 0
foreach ($f in $existing) {
    if ($f.Name -match '^第(\d+)轮-') { $maxRound = [Math]::Max($maxRound, [int]$Matches[1]) }
}

$moved = 0
foreach ($f in @(Get-ChildItem $logDir -Filter '取词记录-*.txt' -EA SilentlyContinue)) {

    # 指纹 = 这份记录前 20 行的内容。
    # 同一份记录已经归档过就跳过，避免每发布一次就多出一份重复文件。
    $newPrint = ((Get-Content $f.FullName -Encoding UTF8 -TotalCount 20) -join '|')

    $already = $false
    foreach ($e in $existing) {
        $oldPrint = ((Get-Content $e.FullName -Encoding UTF8 -TotalCount 20) -join '|')
        if ($oldPrint -eq $newPrint) { $already = $true; break }
    }
    if ($already) { continue }

    $round  = $maxRound + 1 + $moved
    $stamp   = $f.BaseName -replace '^取词记录-', ''
    $target  = Join-Path $destDir ("第{0}轮-{1}.txt" -f $round, $stamp)
    Copy-Item $f.FullName $target -Force
    $moved++
    Write-Host "已归档：$(Split-Path -Leaf $target)" -ForegroundColor Green
}

if ($moved -eq 0) {
    Write-Host "记录已经归档过了，没有新增。" -ForegroundColor DarkGray
}
