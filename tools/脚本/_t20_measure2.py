# -*- coding: utf-8 -*-
"""第 19 轮 S1（第二版）：量窗口高度 / 最后一行文字底边 / 差值。

第一版失败原因：截了整屏，但主窗口被别的窗口盖着，
在 RECT 范围内扫到的全是深色背景（21,21,23），算出「差值 5px」是假的。

这版：起程序 → SetForegroundWindow 抢到前台 → 只截窗口 RECT 那一块。
"""
import io, os, subprocess, time

BASE = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(BASE, "release", "v0.4.2", "TranslatorApp.exe")
SHOT = os.path.join(BASE, "tools", "日志", "取证截图", "t20_s1_main.png")
OUT = os.path.join(BASE, "tools", "脚本", "_t20_result.txt")
OUTL = []


def say(s=""):
    OUTL.append(str(s))
    print(str(s).encode("gbk", "replace").decode("gbk"))


def ps_file(path):
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-File", path], capture_output=True)
    return (r.stdout + r.stderr).decode("gbk", "replace").strip()


def main():
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.2)
    subprocess.Popen([EXE], cwd=BASE)
    time.sleep(6.5)

    script = r'''
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace W4 -Name M -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
'@
$pr = Get-Process TranslatorApp
[void][W4.M]::SetForegroundWindow($pr.MainWindowHandle)
Start-Sleep -Milliseconds 700
$r = New-Object W4.M+RECT
[void][W4.M]::GetWindowRect($pr.MainWindowHandle, [ref]$r)
$w = $r.R - $r.L
$h = $r.B - $r.T
Write-Output ("TITLE=" + $pr.MainWindowTitle)
Write-Output ("RECT=" + $r.L + "," + $r.T + "," + $w + "x" + $h)
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$bmp.Save('__SHOT__')
$g.Dispose(); $bmp.Dispose()
'''
    script = script.replace("__SHOT__", SHOT)
    p = os.path.join(BASE, "tools", "临时", "_t20_cap.ps1")
    io.open(p, "w", encoding="utf-8-sig", newline="").write(script)
    say(ps_file(p))
    say("截图 %s（只截窗口本身）" % SHOT)
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(OUTL) + "\n")


if __name__ == "__main__":
    main()
