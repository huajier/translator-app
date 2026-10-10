<#
托盘检查.ps1 —— 数一数 Windows 右下角通知区域里到底有几个图标（阶段 1 验证工具）

这个脚本干嘛的：
  托盘图标有没有真的挂上，不能靠"看一眼"（图标可能被系统折叠进 ∧ 面板里），
  要靠数。这个脚本直接问系统的任务栏工具条要按钮数量，数字变了就是真加上了。
  以前这版用 UI Automation 去读按钮，在本机读出来是 0（不可信），已改为直接发消息问。

怎么用（在项目根目录打开 PowerShell，必须加 -ExecutionPolicy Bypass）：
  powershell -ExecutionPolicy Bypass -File "tools\脚本\托盘检查.ps1"
      → 只数当前图标数量
  powershell -ExecutionPolicy Bypass -File "tools\脚本\托盘检查.ps1" -Launch
      → 先关掉旧实例，再启动 release\v0.1\TranslatorApp.exe，数启动前后差值

原理（一句话）：
  通知区域其实是个"工具条控件"，对它发 0x0418（TB_BUTTONCOUNT）这条消息，
  它就返回当前图标个数。这是 Windows 自带能力，不需要管理员权限。

退出码：0 = 正常；1 = 找不到任务栏（异常环境）
#>
param(
    [switch]$Launch,
    [string]$ExePath = ""
)

$ErrorActionPreference = 'Stop'

# ---- 直接问任务栏要数量 ----
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class TrayCounter {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string className, string windowName);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const int TB_BUTTONCOUNT = 0x0418;

    public static int Count() {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return -1;                          // -1 = 找不到任务栏
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr pager  = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
        IntPtr bar    = FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null);
        if (bar == IntPtr.Zero) return -2;                           // -2 = 找不到工具条
        return (int)SendMessage(bar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
    }
}
'@

$before = [TrayCounter]::Count()
if ($before -lt 0) {
    Write-Host "找不到通知区域的工具条（返回 $before），这个环境测不了。" -ForegroundColor Red
    exit 1
}
Write-Host "当前通知区域图标数量：$before"

if (-not $Launch) {
    Write-Host ""
    Write-Host "（加 -Launch 参数可以自动启动程序再数一次）"
    exit 0
}

# ---- 启动程序再数一次 ----
if ($ExePath -eq "") {
    $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $ExePath = Join-Path $root 'release\v0.1\TranslatorApp.exe'
}
if (-not (Test-Path $ExePath)) {
    Write-Host "找不到程序：$ExePath" -ForegroundColor Red
    exit 1
}

# 先关掉可能已在运行的实例，保证从干净状态开始测
$running = Get-Process TranslatorApp -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "先关掉正在运行的 TranslatorApp……"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $before = [TrayCounter]::Count()
    Write-Host "关闭后重新计数：$before"
}

Write-Host "启动 $ExePath ……"
Start-Process $ExePath
Start-Sleep -Seconds 6

$after = [TrayCounter]::Count()
$delta = $after - $before

Write-Host ""
Write-Host "启动前：$before    启动后：$after    差值：$delta"
if ($delta -eq 1) {
    Write-Host "✅ 通过：托盘图标真的多了一个" -ForegroundColor Green
} else {
    Write-Host "❌ 不通过：期望多 1 个，实际差 $delta" -ForegroundColor Red
    $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $log = Join-Path $root ("tools\日志\app-" + (Get-Date -Format 'yyyyMMdd') + ".log")
    if (Test-Path $log) {
        Write-Host ""
        Write-Host "程序日志最后几行（看有没有『NIM_ADD=失败』和权限诊断）："
        Get-Content $log -Encoding UTF8 | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
    }
}
exit 0
