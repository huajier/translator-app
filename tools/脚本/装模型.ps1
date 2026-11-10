# 装模型.ps1 —— 把 en 识别模型拷进发布目录（模型是二进制，不进 git）
#
# 用法：
#   .\tools\脚本\装模型.ps1 -目标目录 release\v0.3.4.3-Rapid-en
#   .\tools\脚本\装模型.ps1 -目标目录 release\v0.3.4.3-Rapid-en -核对
#
# 做的事：
#   1. 从 tools\模型\en_v5\ 拷 en_PP-OCRv5_rec_mobile.onnx + ppocrv5_en_dict.txt
#      到 <目标目录>\models\v5\
#
# 【2026-11-07 改源目录：tools\临时\models\en_v5 → tools\模型\en_v5】
#   旧目录在 tools\临时\ 下，被清过，模型全机只剩 release\v0.3.4.3-1c\models\v5\ 一份。
#   构建流程从来没调过本脚本（grep "装模型" 在 构建.ps1 里是 0 命中），
#   于是 v1.9~v0.4.7 全都没有 en 模型，程序每次启动都落回 latin，
#   日志里「英文识别模型缺失」在阶段四出现 183 次。
#   现在源目录固定在 tools\模型\en_v5\（不在 tools\临时\ 下，不会被当临时文件清走），
#   并且由 构建.ps1 第 2.5 步强制调用 + -核对：缺一个文件就构不出来。
#   2. 打印体积、SHA256，并与已知值对比（-核对 时不一致就报错退出）
#
# 为什么不在 git 里：.onnx 是二进制，用户规定模型不进仓库（见 .gitignore 的 *.onnx 兜底）。

param(
    [Parameter(Mandatory = $true)][string]$目标目录,
    [string]$源目录 = 'tools\模型\en_v5',
    [switch]$核对,
    # 【2026-11-10 阶段三 P1-西里尔】选哪一组模型。
    #   en       = 原来的英文模型（默认，向后兼容）
    #   cyrillic = 西里尔模型（与 en 双跑择优，见 RapidOcrEngine.TryRecognizeBoth）
    [ValidateSet('en', 'cyrillic')][string]$组 = 'en'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

# 已知的 en 模型指纹（2026-10-27 从 modelscope v3.10.0 下载）
$期望en = @{
    'en_PP-OCRv5_rec_mobile.onnx' = @{ 大小 = 7872351; SHA256 = 'C3461ADD59BB4323ECBA96A492AB75E06DDA42467C9E3D0C18DB5D1D21924BE8' }
    'ppocrv5_en_dict.txt'         = @{ 大小 = 1416;    SHA256 = $null }
}

# 【2026-11-10 阶段三 P1-西里尔】西里尔模型指纹
#   来源 itextresearch/itext-cyrillic_PP-OCRv5_mobile_rec（架构师已下好，本机备份 tools\模型\cyrillic_v5\）
#   字典 850 行（西里尔 242 + 拉丁 52），由 itext_cyr.yml 的 character_dict 抽出。
$期望西里尔 = @{
    'cyrillic_PP-OCRv5_rec_mobile.onnx' = @{ 大小 = 8074890; SHA256 = '1B248A527759307854FA8BADAB0DBE3C0DAB7296322FE30579E0B92C50A0A41B' }
    'ppocrv5_cyrillic_dict.txt'         = @{ 大小 = 2780;    SHA256 = '2DE016B31EDD687ACFC1BF72170F4FEFF9F0C4893EDAC87CEF30B2C5A82443EC' }
}

$期望 = if ($组 -eq 'cyrillic') { $期望西里尔 } else { $期望en }
if ($组 -eq 'cyrillic' -and -not $PSBoundParameters.ContainsKey('源目录')) {
    $源目录 = 'tools\模型\cyrillic_v5'
}

$目标模型目录 = Join-Path $目标目录 'models\v5'
if (-not (Test-Path $目标目录)) { throw "目标目录不存在：$目标目录（先 dotnet publish 出来）" }
New-Item -ItemType Directory -Force -Path $目标模型目录 | Out-Null

$失败 = 0
foreach ($名 in $期望.Keys) {
    $源 = Join-Path $源目录 $名
    if (-not (Test-Path $源)) { throw "源文件不存在：$源（模型不进 git；本机固定备份在 $源目录，指纹见本脚本顶部核对表）" }
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
