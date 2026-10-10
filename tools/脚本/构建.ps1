<#
构建.ps1 —— 一键编译并发布「翻译软件」（阶段 1 起通用，现在到 v0.2）

这个脚本干嘛的：
  把 src\ 下的源代码编译成一个能双击运行的 exe，放进 release\v<版本号>\，
  并顺手处理掉两个会让「托盘图标挂不上 / 变成白图标」的坑（见下面第 3、3.5 步）。

怎么用（在项目根目录打开 PowerShell）：
  powershell -ExecutionPolicy Bypass -File "tools\脚本\构建.ps1"                  # 默认 v0.2
  powershell -ExecutionPolicy Bypass -File "tools\脚本\构建.ps1" -Version v0.1     # 指定版本
  （加 -ExecutionPolicy Bypass 是因为本机 PowerShell 默认禁止直接运行 .ps1 脚本）

它按顺序做 5 件事：
  1. 关掉正在运行的 TranslatorApp —— 不关的话 exe 被占用，新文件写不进去（报"拒绝访问"）
  2. dotnet publish → 输出到 release\v<版本号>\
  3. 检查并去掉新文件可能带上的「强制完整性标签」
  3.5 确保托盘图标 assets\tray.ico 真的在发布目录里（否则退回白图标）
  4. 确认 exe 存在，并核对编译输出里确实 0 错误 0 警告

【第 3 步为什么重要 —— 阶段 1 踩过的真坑】
  某些受限环境写文件时会给文件打上「低(Low)完整性」标签。文件一旦带标签，
  从它启动的程序就被钉在那个低权限上，而 Windows 有条规则（UIPI）：
  权限低的一方给权限高的一方（任务栏 Explorer）发托盘消息会被直接拒绝，
  结果就是「程序在跑、托盘里没图标」，日志里报 错误码 5（拒绝访问）。
  去掉标签后程序跟随启动者权限，托盘图标正常出现。

【为什么第 2 步敢在「有 warning」时就 exit 1】
  宁可停下来报错，也不要把一个带隐患的产物交出去。
  已知且无害的警告（WFO0003 这类分析器建议）应该在 csproj 里用 NoWarn 处理掉，
  而不是靠这里放宽标准 —— 否则真出问题时没人分得清。
#>

param(
    [string]$Version = 'v0.2'
)

$ErrorActionPreference = 'Stop'

# ---- 路径：这个脚本在 tools\脚本\ 下，往上两层就是项目根目录 ----
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$proj = Join-Path $root 'src\TranslatorApp\TranslatorApp.csproj'
$outDir = Join-Path $root "release\$Version"

Write-Host "项目根目录：$root"
Write-Host "目标版本目录：$outDir"

# ---- 第 1 步：关掉正在运行的程序 ----
$running = Get-Process TranslatorApp -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "第 1 步：先关掉正在运行的 TranslatorApp（PID=$($running.Id)）……"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 600
} else {
    Write-Host "第 1 步：没有正在运行的 TranslatorApp，跳过。"
}

# ---- 第 2 步：编译发布 ----
Write-Host "第 2 步：dotnet publish ……（首次会花几十秒）"
# 【2026-10-08 改：不再用 PublishSingleFile】
#   原因：Tesseract 的 .NET 封装靠「程序集所在目录」找它的原生 DLL，
#   而单文件打包下程序集 Location 是空字符串，拼路径直接抛 ArgumentNullException。
#   我试过用绝对路径预加载原生库绕过——**没用**：
#   封装是在拼路径那一步就崩了，压根没走到 LoadLibrary，所以预加载救不了。
#
#   另外单文件本来也换不来什么：tesseract50.dll / leptonica 这两个原生库
#   本来就必须以散文件形式躺在 exe 旁边，「一个 exe 走天下」在加了 Tesseract
#   之后已经不成立了。所以这里直接把单文件去掉，产物是一个文件夹。
$buildOutput = & dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o $outDir 2>&1
$buildOutput | ForEach-Object { Write-Host $_ }

$problems = $buildOutput | Where-Object { $_ -match 'error|warning' }
if ($problems) {
    Write-Host ""
    Write-Host "编译出问题了（上面有 error/warning）：" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "编译完成：0 错误，0 警告。" -ForegroundColor Green

# ---- 第 3 步：去掉文件上的强制完整性标签 ----
Write-Host "第 3 步：检查并清理 $outDir 下的文件标签……"
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class LabelStripper {
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint SetNamedSecurityInfoW(string pObjectName, int ObjectType, uint SecurityInfo,
        IntPtr pOwner, IntPtr pGroup, IntPtr pDacl, IntPtr pSacl);
    public const int SE_FILE_OBJECT = 1;
    public const uint LABEL_SECURITY_INFORMATION = 0x10;
}
'@

function Remove-IntegrityLabel([string]$path) {
    $full = [System.IO.Path]::GetFullPath($path)
    $r = [LabelStripper]::SetNamedSecurityInfoW($full, [LabelStripper]::SE_FILE_OBJECT,
        [LabelStripper]::LABEL_SECURITY_INFORMATION, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
    if ($r -ne 0) { return "错误码 $r" }
    return "OK"
}

Get-ChildItem $outDir -File -ErrorAction SilentlyContinue | ForEach-Object {
    $result = Remove-IntegrityLabel $_.FullName
    if ($result -ne 'OK') { Write-Host "  $($_.Name)：$result" }
}
$exeResult = Remove-IntegrityLabel (Join-Path $outDir 'TranslatorApp.exe')
Write-Host "  TranslatorApp.exe 标签处理结果：$exeResult" -ForegroundColor Green

# ---- 第 3.5 步：确保托盘图标真的被复制到发布目录 ----
# 踩过的坑：dotnet publish 的增量逻辑第二次起不再复制 tray.ico，release 里 assets\ 会变成空目录，
# 程序只好退回系统默认白图标（日志会写「托盘图标文件不存在」）。所以这里强制补一次并当场校验。
$icoSrc = Join-Path $root 'assets\tray.ico'
$icoDst = Join-Path $outDir 'assets\tray.ico'
New-Item -ItemType Directory -Path (Split-Path $icoDst) -Force | Out-Null
if (-not (Test-Path $icoDst)) {
    Copy-Item $icoSrc $icoDst -Force
    Write-Host "发现发布目录缺 assets\tray.ico，已补上（否则程序会显示成系统白图标）。"
}
if (-not (Test-Path $icoDst)) {
    Write-Host "致命：$icoDst 仍然不存在，托盘会显示成系统默认图标。" -ForegroundColor Red
    exit 1
}
$icoSize = (Get-Item $icoDst).Length
Write-Host "托盘图标已就位：$icoDst （$icoSize 字节）" -ForegroundColor Green

# ---- 第 4 步：确认 exe 存在 ----
$exe = Join-Path $outDir 'TranslatorApp.exe'
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
    Write-Host ""
    Write-Host "全部完成 ✅  $exe （$size MB）" -ForegroundColor Green
    Write-Host "双击它就能运行。"
} else {
    Write-Host "没找到 $exe —— 编译可能失败了，看上面的输出。" -ForegroundColor Red
    exit 1
}
