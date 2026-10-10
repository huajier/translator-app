<#
测残影.ps1 —— 阶段 2 专用：抓「上一次框选留下的选框会不会闪一下」

这个脚本干嘛的：
  用户反馈：「框选完成后再次框选的时候上一次的框会短暂出现零点几秒」。
  这个 bug 只闪零点几秒，靠肉眼和普通截图都抓不住，所以要**连拍一堆**再逐张检查。

怎么判断「旧框闪了」：
  1) 先在区域 A 框一次（记下 A 的四条边在哪）；
  2) 等提示框消失；
  3) 再次按下 Ctrl+Alt（遮罩重新出现，此刻还没开始拖，理应只有一个干净的灰罩）；
  4) 紧接着连拍 N 张；
  5) 逐张检查 A 的边界上有没有「亮青色选框线」。
     亮青色 = 选框的颜色（#38BDF8），灰罩里不可能出现这种颜色。
     只要有一张出现 → bug 存在。

怎么用：
  powershell -ExecutionPolicy Bypass -File "tools\脚本\测残影.ps1"
#>

param(
    [int]$x1 = 400, [int]$y1 = 300, [int]$x2 = 900, [int]$y2 = 600,
    [int]$连拍张数 = 14
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# ---- 选框的亮青色（#38BDF8），灰罩里不可能出现这种颜色 ----
$BoxColor = [System.Drawing.Color]::FromArgb(255, 56, 189, 248)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class FlashProbe {
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct Union { [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public Union u; }
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] p, int size);
  static void Send(INPUT[] i){ SendInput((uint)i.Length, i, Marshal.SizeOf(typeof(INPUT))); }
  static INPUT Key(ushort vk, bool up){ var i=new INPUT(); i.type=1; i.u.ki=new KEYBDINPUT{wVk=vk, dwFlags=up?2u:0u}; return i; }

  /// 只按下 Ctrl+Alt 并**立刻返回**（不做任何等待），好让主脚本能马上开始连拍
  public static void PressComboOnly() {
    Send(new[]{ Key(0x11,false), Key(0x12,false) });
  }
  public static void ReleaseCombo() {
    Send(new[]{ Key(0x11,true), Key(0x12,true) });
  }
}
'@

function CaptureBitmap {
  $bmp = New-Object System.Drawing.Bitmap(1920,1080)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0,0,0,0,(New-Object System.Drawing.Size(1920,1080)))
  $g.Dispose()
  return $bmp
}

# 在给定的几条边上找有没有「亮青色」像素
# 刻意写成内联循环、不用带参数的函数：踩过一次坑 ——
# PowerShell 里带参函数 + 命令式传参，中途某个变量变成数组时报错的位置很难看懂。
# 这里所有坐标都强制 [int] 一下，多写两行换来一眼看得懂。
function FindBoxColor([System.Drawing.Bitmap]$bmp, [int]$ax1, [int]$ay1, [int]$ax2, [int]$ay2) {
  $hits = 0
  # 选框线颜色约 #38BDF8（亮青）。判断：偏蓝、偏亮、且绿通道明显高于红通道。
  $ys = @([int]$ay1, [int]($ay1 + 1), [int]($ay2 - 1), [int]$ay2)
  $xs = @([int]$ax1, [int]($ax1 + 1), [int]($ax2 - 1), [int]$ax2)
  for ($i = 0; $i -lt 4; $i++) {
    $yy = [int]$ys[$i]
    for ($x = [int]$ax1 - 3; $x -le [int]$ax2 + 3; $x += 2) {
      $c = $bmp.GetPixel($x, $yy)
      if (($c.G - $c.R) -gt 60 -and $c.B -gt 180 -and $c.R -lt 160) { $hits++ }
    }
  }
  for ($i = 0; $i -lt 4; $i++) {
    $xx = [int]$xs[$i]
    for ($y = [int]$ay1 - 3; $y -le [int]$ay2 + 3; $y += 2) {
      $c = $bmp.GetPixel($xx, $y)
      if (($c.G - $c.R) -gt 60 -and $c.B -gt 180 -and $c.R -lt 160) { $hits++ }
    }
  }
  return [int]$hits
}

Write-Host "先在 ($x1,$y1)-($x2,$y2) 框一次…" -ForegroundColor Cyan
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '模拟操作.ps1') -场景 正常框选 -x1 $x1 -y1 $y1 -x2 $x2 -y2 $y2 | Out-Null
Start-Sleep -Seconds 5   # 等提示框（3 秒）彻底消失

Write-Host "再次按下 Ctrl+Alt，立刻连拍 $连拍张数 张…" -ForegroundColor Cyan
[FlashProbe]::PressComboOnly()

$found = 0; $shotNo = 0
for ($i = 1; $i -le $连拍张数; $i++) {
  $bmp = CaptureBitmap
  $hits = FindBoxColor $bmp $x1 $y1 $x2 $y2
  if ($hits -gt 0) {
    $found++
    Write-Host ("  第 {0} 张：发现旧选框残留像素 {1} 个 ← 闪了" -f $i, $hits) -ForegroundColor Yellow
  }
  $bmp.Dispose()
  $shotNo++
}

[FlashProbe]::ReleaseCombo()

Write-Host ""
if ($found -gt 0) {
  Write-Host "结论：BUG 存在（$found/$shotNo 张拍到了旧框）" -ForegroundColor Red
} else {
  Write-Host "结论：没有残影，干净（0/$shotNo 张拍到旧框）" -ForegroundColor Green
}
