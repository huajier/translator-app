# 装模型.ps1 —— 把 en 识别模型拷进发布目录（模型是二进制，不进 git）
#
# 用法：
#   .\tools\脚本\装模型.ps1 -目标目录 release\v0.3.4.3-Rapid-en
#   .\tools\脚本\装模型.ps1 -目标目录 release\v0.3.4.3-Rapid-en -核对
#
# 做的事：
#   1. 从 tools\临时\models\en_v5\ 拷 en_PP-OCRv5_rec_mobile.onnx + ppocrv5_en_dict.txt
#      到 <目标目录>\models\v5\
#   2. 打印体积、SHA256，并与已知值对比（-核对 时不一致就报错退出）
#
# 为什么不在 git 里：.onnx 是二进制，用户规定模型不进仓库（见 .gitignore 的 *.onnx 兜底）。

param(
    [Parameter(Mandatory = $true)][string]$目标目录,
    [string]$源目录 = 'tools\临时\models\en_v5',
    [switch]$核对
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

# 已知的 en 模型指纹（2026-10-27 从 modelscope v3.10.0 下载）
$期望 = @{
    'en_PP-OCRv5_rec_mobile.onnx' = @{ 大小 = 7872351; SHA256 = 'C3461ADD59BB4323ECBA96A492AB75E06DDA42467C9E3D0C18DB5D1D21924BE8' }
    'ppocrv5_en_dict.txt'         = @{ 大小 = 1416;    SHA256 = $null }
}

$目标模型目录 = Join-Path $目标目录 'models\v5'
if (-not (Test-Path $目标目录)) { throw "目标目录不存在：$目标目录（先 dotnet publish 出来）" }
New-Item -ItemType Directory -Force -Path $目标模型目录 | Out-Null

$失败 = 0
foreach ($名 in $期望.Keys) {
    $源 = Join-Path $源目录 $名
    if (-not (Test-Path $源)) { throw "源文件不存在：$源（先下载模型，见 tools\日志\阶段3-S3-换模型.md）" }
    $目标 = Join-Path $目标模型目录 $名
    Copy-Item -Path $源 -Destination $目标 -Force

    $fi = Get-Item $目标
    $sha = (Get-FileHash $目标 -Algorithm SHA256).Hash
    $期望项 = $期望[$名]
    $大小对 = ($fi.Length -eq $期望项.大小)
    $sha对 = ($null -eq $期望项.SHA256) -or ($sha -eq $期望项.SHA256)
    $标记 = if ($大小对 -and $sha对) { 'OK' } else { '不一致'; $失败++ }

    "{0,-30} {1,10:N0} 字节  SHA256 {2}  [{3}]" -f $名, $fi.Length, $sha.Substring(0, 16), $标记
}

"=== 目标 models\v5 现有文件 ==="
Get-ChildItem $目标模型目录 -File | ForEach-Object { "{0,-52} {1,10:N0}" -f $_.Name, $_.Length }

$总 = (Get-ChildItem $目标模型目录 -File | Measure-Object -Sum Length).Sum
"models\v5 合计 {0:N2} MB" -f ($总 / 1MB)

if ($核对 -and $失败 -gt 0) { throw "有 $失败 个文件指纹不一致" }
"完成。"
