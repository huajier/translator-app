<#
真机回归-框选.ps1 -- reproduce the selection-gesture input bugs

Reported symptoms (2026-10-29):
  1) hold Ctrl+Alt + left-drag, release Ctrl or Alt BEFORE the mouse button -> a hitch
  2) toggle Ctrl+Alt rapidly -> machine stalls for a while
  3) after releasing modifiers the selection box stays on screen
  4) idle CPU ~10%, during recognition ~40%

Cases:
  A  Ctrl+Alt down -> left down -> move 200px -> release Ctrl -> release left -> release Alt
  B  same but release Alt first
  C  release left first, then release both modifiers
  D  drag distance 0 (click, no move)
  E  toggle Ctrl+Alt 20 times within 2 seconds

Assertions (from logs + screen pixels only, target app untouched):
  * box still painted on screen -> scan the top edge midpoint for #38BDF8
  * capture triggered -> "开始取词" in the log
  * mask rebuilds -> "遮罩已铺满" occurrences
  * overlay count -> windows of the target process titled "框选遮罩"
  * OCR queue depth -> captures started minus captures finished
  * CPU -> process CPU time delta / wall clock / cores

Usage:
  powershell -ExecutionPolicy Bypass -File "tools\脚本\真机回归-框选.ps1"
  powershell ... -场景 "A,B"
  powershell ... -保留程序
#>

param(
    [string]$Exe = "release\v0.3.4.3-nl\TranslatorApp.exe",
    [string]$场景 = "A,B,C,D,E",
    [int]$基线秒 = 3,
    [switch]$保留程序
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$项目根 = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $项目根 $Exe }
if (-not (Test-Path $exePath)) { throw "cannot find exe: $exePath" }

$日志 = Join-Path $项目根 ("tools\日志\app-" + (Get-Date -Format "yyyyMMdd") + ".log")
$OVERLAY_TITLE = [string]([char]0x6846 + [char]0x9009 + [char]0x906E + [char]0x7F69)
$WORD_CAPTURE = [string]([char]0x5F00 + [char]0x59CB + [char]0x53D6 + [char]0x8BCD)
$WORD_DONE = [string]([char]0x53D6 + [char]0x8BCD + [char]0x6210 + [char]0x529F) + '|' + [string]([char]0x53D6 + [char]0x8BCD + [char]0x5931 + [char]0x8D25)
$WORD_COMPLETED = [string]([char]0x6846 + [char]0x9009 + [char]0x5B8C + [char]0x6210)
$WORD_CANCELLED = [string]([char]0x6846 + [char]0x9009 + [char]0x53D6 + [char]0x6D88)
$WORD_MASK_BEGIN = [string]([char]0x906E + [char]0x7F69 + [char]0x5DF2 + [char]0x94FA + [char]0x6EE1)
$WORD_MASK_GRAB = [string]([char]0x906E + [char]0x7F69 + [char]0x5DF2 + [char]0x63A5 + [char]0x7BA1)
$WORD_MASK_RELEASE = [string]([char]0x906E + [char]0x7F69 + [char]0x5DF2 + [char]0x91CA + [char]0x653E)
$WORD_TOO_SMALL = [string]([char]0x5FFD + [char]0x7565 + [char]0x4E00 + [char]0x6B21 + [char]0x592A + [char]0x5C0F)
$APP_TITLE_PART = [string]([char]0x7FFB + [char]0x8BD1)

# ------------------------------------------------------------------ native
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class InputDriver {
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] p, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

    const uint T_MOUSE = 0, T_KEY = 1;
    const uint MOVE = 0x0001, LDOWN = 0x0002, LUP = 0x0004, ABS = 0x8000, VD = 0x4000, KEYUP = 0x0002;

    static void One(INPUT i) { INPUT[] a = new INPUT[1]; a[0] = i; SendInput(1, a, Marshal.SizeOf(typeof(INPUT))); }

    public static void Key(ushort vk, bool down) {
        INPUT i = new INPUT(); i.type = T_KEY;
        i.u.ki.wVk = vk; i.u.ki.wScan = 0; i.u.ki.dwFlags = down ? 0u : KEYUP;
        One(i);
    }

    public static void MoveAbs(int x, int y) {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        long nx = (long)(x - vx) * 65535 / Math.Max(1, vw - 1);
        long ny = (long)(y - vy) * 65535 / Math.Max(1, vh - 1);
        INPUT i = new INPUT(); i.type = T_MOUSE;
        i.u.mi.dx = (int)nx; i.u.mi.dy = (int)ny; i.u.mi.dwFlags = MOVE | ABS | VD;
        One(i);
    }

    public static void LeftDown() { INPUT i = new INPUT(); i.type = T_MOUSE; i.u.mi.dwFlags = LDOWN; One(i); }
    public static void LeftUp()   { INPUT i = new INPUT(); i.type = T_MOUSE; i.u.mi.dwFlags = LUP;   One(i); }
}

public class WinProbe {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    public static string[] Titles(int pid) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            int p; GetWindowThreadProcessId(h, out p);
            if (p == pid) {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                list.Add(sb.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    public static IntPtr FindByTitlePart(int pid, string part) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            int p; GetWindowThreadProcessId(h, out p);
            if (p == pid) {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.ToString().Contains(part)) { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$VK_LCTRL = 0xA2
$VK_LALT  = 0xA4

# ------------------------------------------------------------------ probes
function Test-BoxVisible {
    # scan the whole region for the box colour #38BDF8; grow tiny regions to at
    # least 80x40 so the "drag distance 0" case cannot pass by accident (false green)
    param([int]$x1, [int]$y1, [int]$x2, [int]$y2)

    $sx = [Math]::Min($x1, $x2); $ex = [Math]::Max($x1, $x2)
    $sy = [Math]::Min($y1, $y2); $ey = [Math]::Max($y1, $y2)

    if (($ex - $sx) -lt 80) { $cx = [int](($sx + $ex) / 2); $sx = $cx - 40; $ex = $cx + 40 }
    if (($ey - $sy) -lt 40) { $cy = [int](($sy + $ey) / 2); $sy = $cy - 20; $ey = $cy + 20 }

    $sx = [Math]::Max(0, $sx); $sy = [Math]::Max(0, $sy)
    $w = $ex - $sx; $h = $ey - $sy
    if ($w -lt 4 -or $h -lt 4) { return $false }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($sx, $sy, 0, 0, $bmp.Size)
    $g.Dispose()

    $hit = 0
    for ($yy = 0; $yy -lt $bmp.Height; $yy += 2) {
        for ($xx = 0; $xx -lt $bmp.Width; $xx += 2) {
            $c = $bmp.GetPixel($xx, $yy)
            if ([Math]::Abs($c.R - 56) -lt 40 -and [Math]::Abs($c.G - 189) -lt 40 -and [Math]::Abs($c.B - 248) -lt 40) { $hit++ }
        }
    }
    $bmp.Dispose()
    return ($hit -gt 6)
}

function Get-CpuPercent {
    param($Proc, [int]$ms = 1000)
    if ($Proc.HasExited) { return -1 }
    $t0 = $Proc.TotalProcessorTime
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds $ms
    $Proc.Refresh()
    $t1 = $Proc.TotalProcessorTime
    $cores = [Environment]::ProcessorCount
    return [Math]::Round(($t1 - $t0).TotalMilliseconds / [Math]::Max(1, $sw.ElapsedMilliseconds) / $cores * 100, 1)
}

$script:日志偏移 = 0

function Read-NewLog {
    if (-not (Test-Path $日志)) { return @() }
    $all = @(Get-Content -LiteralPath $日志 -Encoding UTF8 -ErrorAction SilentlyContinue)
    if ($all.Count -le $script:日志偏移) { return @() }
    $fresh = @($all[$script:日志偏移..($all.Count - 1)])
    $script:日志偏移 = $all.Count
    return $fresh
}

function Measure-LogSlice {
    param([string[]]$Lines)
    $joined = ($Lines -join "`n")
    [pscustomobject]@{
        StartCapture = ([regex]::Matches($joined, $WORD_CAPTURE)).Count
        CaptureDone  = ([regex]::Matches($joined, $WORD_DONE)).Count
        Completed    = ([regex]::Matches($joined, $WORD_COMPLETED)).Count
        Cancelled    = ([regex]::Matches($joined, $WORD_CANCELLED)).Count
        MaskBegin    = ([regex]::Matches($joined, $WORD_MASK_BEGIN)).Count
        MaskGrab     = ([regex]::Matches($joined, $WORD_MASK_GRAB)).Count
        MaskRelease  = ([regex]::Matches($joined, $WORD_MASK_RELEASE)).Count
        TooSmall     = ([regex]::Matches($joined, $WORD_TOO_SMALL)).Count
    }
}

# ------------------------------------------------------------------ lifecycle
function Start-Target {
    Get-Process -Name TranslatorApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    $dir = Split-Path -Parent $exePath
    $p = Start-Process -FilePath $exePath -WorkingDirectory $dir -PassThru
    Start-Sleep -Milliseconds 2500
    if ($p.HasExited) { throw "target exited immediately (single instance?)" }

    $main = [WinProbe]::FindByTitlePart($p.Id, $APP_TITLE_PART)
    if ($main -ne [IntPtr]::Zero) {
        [void][WinProbe]::PostMessage($main, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 500
    }
    return $p
}

# ------------------------------------------------------------------ assertions
$results = New-Object System.Collections.ArrayList

function Add-Check {
    param($Case, $What, $Expect, $Actual, $Ok)
    [void]$results.Add([pscustomobject]@{
        Case = $Case; Check = $What; Expect = $Expect; Actual = $Actual
        Verdict = if ($Ok) { 'PASS' } else { 'FAIL' }
    })
    $tag = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}: expect {2} / actual {3}" -f $tag, $What, $Expect, $Actual)
}

# ------------------------------------------------------------------ input
function Send-Drag {
    param([int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$steps = 6, [int]$delay = 35)
    for ($i = 1; $i -le $steps; $i++) {
        $nx = $x1 + [int](($x2 - $x1) * $i / $steps)
        $ny = $y1 + [int](($y2 - $y1) * $i / $steps)
        [InputDriver]::MoveAbs($nx, $ny)
        Start-Sleep -Milliseconds $delay
    }
}

function Invoke-ScenarioAB {
    param([string]$Name, [int]$FirstUp, [int]$SecondUp)

    $x1 = 520; $y1 = 620; $x2 = 720; $y2 = 700
    [InputDriver]::MoveAbs($x1, $y1); Start-Sleep -Milliseconds 80
    [InputDriver]::Key($VK_LCTRL, $true); [InputDriver]::Key($VK_LALT, $true)
    Start-Sleep -Milliseconds 80
    [InputDriver]::LeftDown(); Start-Sleep -Milliseconds 90
    Send-Drag -x1 $x1 -y1 $y1 -x2 $x2 -y2 $y2 -steps 6 -delay 35

    $during = Test-BoxVisible -x1 $x1 -y1 $y1 -x2 $x2 -y2 $y2

    [InputDriver]::Key($FirstUp, $false)
    Start-Sleep -Milliseconds 250
    $afterRelease = Test-BoxVisible -x1 $x1 -y1 $y1 -x2 $x2 -y2 $y2

    Send-Drag -x1 $x2 -y1 $y2 -x2 ($x2 + 40) -y2 ($y2 + 20) -steps 4 -delay 40
    Start-Sleep -Milliseconds 200
    $afterMove = Test-BoxVisible -x1 $x2 -y1 $y2 -x2 ($x2 + 40) -y2 ($y2 + 20)

    [InputDriver]::LeftUp(); Start-Sleep -Milliseconds 120
    [InputDriver]::Key($SecondUp, $false)
    Start-Sleep -Milliseconds 1200

    $lines = Read-NewLog
    $m = Measure-LogSlice -Lines $lines
    $overlays = @([WinProbe]::Titles($script:targetPid) | Where-Object { $_ -eq $OVERLAY_TITLE })

    Write-Host ("Case {0}: during={1} afterRelease={2} afterMove={3} | maskBegin={4} grab={5} release={6} completed={7} cancelled={8} startCapture={9}" -f `
            $Name, $during, $afterRelease, $afterMove, $m.MaskBegin, $m.MaskGrab, $m.MaskRelease, $m.Completed, $m.Cancelled, $m.StartCapture)

    Add-Check $Name "box visible while dragging" $true $during $during
    Add-Check $Name "box disappears right after modifier release" $true (-not $afterRelease) (-not $afterRelease)
    Add-Check $Name "moving after release must not re-show mask" $true (-not $afterMove) (-not $afterMove)
    Add-Check $Name "releasing left button after modifier must not capture" "0" "$($m.StartCapture)" ($m.StartCapture -eq 0)
    Add-Check $Name "overlay window count <= 1" "1" "$($overlays.Count)" ($overlays.Count -le 1)
    Add-Check $Name "one drag builds the mask exactly once" "1" "$($m.MaskBegin)" ($m.MaskBegin -eq 1)
}

function Invoke-ScenarioC {
    $x1 = 520; $y1 = 620; $x2 = 720; $y2 = 700
    [InputDriver]::MoveAbs($x1, $y1); Start-Sleep -Milliseconds 80
    [InputDriver]::Key($VK_LCTRL, $true); [InputDriver]::Key($VK_LALT, $true)
    Start-Sleep -Milliseconds 80
    [InputDriver]::LeftDown(); Start-Sleep -Milliseconds 90
    Send-Drag -x1 $x1 -y1 $y1 -x2 $x2 -y2 $y2
    [InputDriver]::LeftUp(); Start-Sleep -Milliseconds 120
    [InputDriver]::Key($VK_LCTRL, $false); [InputDriver]::Key($VK_LALT, $false)
    Start-Sleep -Milliseconds 900
    $afterRelease = Test-BoxVisible -x1 $x1 -y1 $y1 -x2 $x2 -y2 $y2
    Start-Sleep -Milliseconds 1500

    $lines = Read-NewLog
    $m = Measure-LogSlice -Lines $lines
    $queued = $m.StartCapture - $m.CaptureDone

    Write-Host ("Case C: afterRelease={0} | completed={1} cancelled={2} startCapture={3} queue={4}" -f $afterRelease, $m.Completed, $m.Cancelled, $m.StartCapture, $queued)

    Add-Check 'C' "box disappears after normal release" $true (-not $afterRelease) (-not $afterRelease)
    Add-Check 'C' "normal release triggers exactly one capture" "1" "$($m.StartCapture)" ($m.StartCapture -eq 1)
    Add-Check 'C' "no unfinished capture job left" "0" "$queued" ($queued -le 0)
}

function Invoke-ScenarioD {
    $x1 = 560; $y1 = 640
    [InputDriver]::MoveAbs($x1, $y1); Start-Sleep -Milliseconds 80
    [InputDriver]::Key($VK_LCTRL, $true); [InputDriver]::Key($VK_LALT, $true)
    Start-Sleep -Milliseconds 80
    [InputDriver]::LeftDown(); Start-Sleep -Milliseconds 150
    $whileDown = Test-BoxVisible -x1 ($x1 - 40) -y1 ($y1 - 40) -x2 ($x1 + 40) -y2 ($y1 + 40)
    [InputDriver]::LeftUp(); Start-Sleep -Milliseconds 150
    [InputDriver]::Key($VK_LCTRL, $false); [InputDriver]::Key($VK_LALT, $false)
    Start-Sleep -Milliseconds 1000
    $afterRelease = Test-BoxVisible -x1 ($x1 - 40) -y1 ($y1 - 40) -x2 ($x1 + 40) -y2 ($y1 + 40)

    $lines = Read-NewLog
    $m = Measure-LogSlice -Lines $lines

    Write-Host ("Case D: whileDown={0} afterRelease={1} | tooSmall={2} startCapture={3}" -f $whileDown, $afterRelease, $m.TooSmall, $m.StartCapture)

    Add-Check 'D' "click without drag draws no box" $false $whileDown (-not $whileDown)
    Add-Check 'D' "click without drag triggers no capture" "0" "$($m.StartCapture)" ($m.StartCapture -eq 0)
    Add-Check 'D' "no leftover box after release" $true (-not $afterRelease) (-not $afterRelease)
}

function Invoke-ScenarioE {
    $idleBefore = Get-CpuPercent -Proc $script:proc -ms 1000

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 20; $i++) {
        [InputDriver]::Key($VK_LCTRL, $true); [InputDriver]::Key($VK_LALT, $true)
        Start-Sleep -Milliseconds 45
        [InputDriver]::Key($VK_LCTRL, $false); [InputDriver]::Key($VK_LALT, $false)
        Start-Sleep -Milliseconds 45
    }
    $sw.Stop()
    $burstMs = [Math]::Round($sw.Elapsed.TotalMilliseconds)
    $afterBurst = Get-CpuPercent -Proc $script:proc -ms 1000
    $settled = Get-CpuPercent -Proc $script:proc -ms 1000

    $lines = Read-NewLog
    $m = Measure-LogSlice -Lines $lines
    $overlays = @([WinProbe]::Titles($script:targetPid) | Where-Object { $_ -eq $OVERLAY_TITLE })
    $queued = $m.StartCapture - $m.CaptureDone

    Write-Host ("Case E: 20 cycles took {0}ms | cpu idle={1}% afterBurst={2}% settled={3}% | maskBegin={4} grab={5} release={6} startCapture={7}" -f `
            $burstMs, $idleBefore, $afterBurst, $settled, $m.MaskBegin, $m.MaskGrab, $m.MaskRelease, $m.StartCapture)

    Add-Check 'E' "overlay window count <= 1" "1" "$($overlays.Count)" ($overlays.Count -le 1)
    Add-Check 'E' "rapid toggling triggers no capture" "0" "$($m.StartCapture)" ($m.StartCapture -eq 0)
    Add-Check 'E' "CPU settles below 5% within 1s" "<5%" "$settled%" ($settled -lt 5.0)
    Add-Check 'E' "no unfinished capture job left" "0" "$queued" ($queued -le 0)
}

# ------------------------------------------------------------------ main
Write-Host "=== selection gesture regression ==="
Write-Host "exe : $exePath"
Write-Host "log : $日志"

$script:proc = Start-Target
$script:targetPid = $script:proc.Id
[void](Read-NewLog)

Write-Host ("`n--- idle baseline ({0}s) ---" -f $基线秒)
$idleCpu = Get-CpuPercent -Proc $script:proc -ms ($基线秒 * 1000)
Write-Host ("idle CPU: {0}%  (PID {1}, cores {2}, RAM {3:N0} MB)" -f `
        $idleCpu, $script:targetPid, [Environment]::ProcessorCount, ($script:proc.WorkingSet64 / 1MB))

$want = @{}
foreach ($s in ($场景 -split ',')) { $want[$s.Trim().ToUpper()] = $true }

Write-Host "`n--- cases ---"
if ($want['A']) { Write-Host "`n[A] release Ctrl first"; Invoke-ScenarioAB -Name 'A' -FirstUp $VK_LCTRL -SecondUp $VK_LALT; Start-Sleep -Milliseconds 3500 }
if ($want['B']) { Write-Host "`n[B] release Alt first";   Invoke-ScenarioAB -Name 'B' -FirstUp $VK_LALT  -SecondUp $VK_LCTRL; Start-Sleep -Milliseconds 3500 }
if ($want['C']) { Write-Host "`n[C] release mouse first, then keys"; Invoke-ScenarioC; Start-Sleep -Milliseconds 2000 }
if ($want['D']) { Write-Host "`n[D] press without drag"; Invoke-ScenarioD; Start-Sleep -Milliseconds 2000 }
if ($want['E']) { Write-Host "`n[E] toggle Ctrl+Alt 20x in 2s"; Invoke-ScenarioE }

[InputDriver]::MoveAbs(960, 540)

Write-Host "`n=== summary ==="
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object { $_.Verdict -eq 'FAIL' })
Write-Host ("checks {0}, FAIL {1}" -f $results.Count, $failed.Count)

if (-not $保留程序) {
    Get-Process -Name TranslatorApp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "target stopped"
}

if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
