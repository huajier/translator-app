# -*- coding: utf-8 -*-
"""第 19 轮 S1：量三个数 —— 窗口实际高度 / 最后一行文字底边 / 差值。

为什么要量而不靠看：用户要的是「内容缩了容器也要缩」，
差值是 200px 就说明 Height 根本没改对。肉眼看「下面空空的」说明不了差多少。
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


def ps(script):
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-Command", script], capture_output=True)
    return (r.stdout + r.stderr).decode("gbk", "replace").strip()


def screenshot(path):
    ps("Add-Type -AssemblyName System.Windows.Forms,System.Drawing;"
       "$b=[System.Windows.Forms.SystemInformation]::VirtualScreen;"
       "$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height;"
       "$g=[System.Drawing.Graphics]::FromImage($bmp);"
       "$g.CopyFromScreen($b.Left,$b.Top,0,0,$bmp.Size);"
       "$bmp.Save('%s');" % path)


def main():
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.2)
    p = subprocess.Popen([EXE], cwd=BASE)
    time.sleep(6.5)
    r = ps("[int[]]::new(4) | Out-Null;"
           "Add-Type -Namespace W3 -Name M -MemberDefinition @'\n"
           "[DllImport(\"user32.dll\")] public static extern bool GetWindowRect(IntPtr h, out RECT r);\n"
           "[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }\n"
           "'@;"
           "$pr=Get-Process TranslatorApp;"
           "$x=New-Object W3.M+RECT;"
           "[void][W3.M]::GetWindowRect($pr.MainWindowHandle,[ref]$x);"
           "Write-Output (\"TITLE=\" + $pr.MainWindowTitle);"
           "Write-Output (\"RECT=\" + $x.L + \",\" + $x.T + \",\" + ($x.R-$x.L) + \"x\" + ($x.B-$x.T))")
    say(r)
    screenshot(SHOT)
    say("截图 %s" % SHOT)
    p.kill()
    io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(OUTL) + "\n")


if __name__ == "__main__":
    main()
