# -*- coding: utf-8 -*-
"""第 16 轮 S3-5 补测：300 字以上，等「翻译：成功」真的落地再截图。

【为什么单独重跑】上一轮 `_t3b_s3.py` 用固定 6.0 秒延时截图，结果截图那一刻
    日志里还停在「译文区已切到『翻译中…』」，画面上是「翻译中...」不是译文。
    固定延时测真 Bing 不稳（2927ms 起步、偶发 4000ms+），改成**轮询日志**：
    等出现「翻译：成功」或「翻译：放弃」再截，截到的一定是终态。
"""
import io, json, os, subprocess, time, threading

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v2.1", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
TMP = os.path.join(ROOT, "tools", "临时")
SRC_TXT = os.path.join(TMP, "s3_long_src.txt")
BOX = ("160", "160", "940", "480")

T_LONG = (
    "The caravan reached the northern pass three days late, and the gatekeeper "
    "refused to open the gate until the merchant produced a seal he had lost in "
    "the river. They argued for hours in the rain while the mules stood shivering, "
    "and by evening the whole village had gathered to watch a stranger explain, "
    "over and over, why a man with no seal and no papers and no letter from the "
    "capital still deserved a warm bed and a bowl of soup for the night."
)

SHOW_PS = r"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$txtPath = '{txt}'
$f = New-Object System.Windows.Forms.Form
$f.Text = 'S3Source'
$f.StartPosition = 'Manual'
$f.Left = 100; $f.Top = 100; $f.Width = 900; $f.Height = 420
$f.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$panel = New-Object System.Windows.Forms.Panel
$panel.Dock = 'Fill'
$panel.Padding = New-Object System.Windows.Forms.Padding(60, 60, 60, 60)
$panel.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.ReadOnly = $true; $tb.BorderStyle = 'None'
$tb.Dock = 'Fill'
$tb.Font = New-Object System.Drawing.Font('Consolas', 13)
$tb.ForeColor = [System.Drawing.Color]::White
$tb.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$tb.Text = [IO.File]::ReadAllText($txtPath, [Text.Encoding]::UTF8)
$panel.Controls.Add($tb)
$f.Controls.Add($panel)
$f.TopMost = $true
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 500
$timer.Add_Tick({
    try { $new = [IO.File]::ReadAllText($txtPath, [Text.Encoding]::UTF8) } catch { $new = $tb.Text }
    if ($new -ne $tb.Text) { $tb.Text = $new }
    $f.TopMost = $true
})
$timer.Start()
$f.Show()
$f.Activate()
while ($f.Visible) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 100
}
"""

LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")
OUT = []


def say(s=""):
    OUT.append(str(s))
    try:
        print(str(s).encode("gbk", "replace").decode("gbk"))
    except Exception:
        print("[unprintable]")


def nlines():
    if not os.path.exists(LOG):
        return 0
    return len(io.open(LOG, encoding="utf-8-sig", errors="replace").readlines())


def read_rows(base):
    return io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()[base:]


def setcfg(on=True, endpoint=""):
    d = json.load(io.open(CFG, encoding="utf-8-sig"))
    d["translateEnabled"] = on
    d["translateDefaultOnMigrated"] = True
    d["translateNoticeShown"] = True
    d["translateTargetLang"] = "zh-Hans"
    d["translateEndpoint"] = endpoint
    io.open(CFG, "w", encoding="utf-8", newline="").write(
        json.dumps(d, ensure_ascii=False, indent=2) + "\n")


def screenshot(path):
    ps = ("Add-Type -AssemblyName System.Windows.Forms,System.Drawing;"
          "$b=[System.Windows.Forms.SystemInformation]::VirtualScreen;"
          "$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height;"
          "$g=[System.Drawing.Graphics]::FromImage($bmp);"
          "$g.CopyFromScreen($b.Left,$b.Top,0,0,$bmp.Size);"
          "$bmp.Save('%s');" % path)
    subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                   capture_output=True, timeout=90)


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.2)


def main():
    original = io.open(CFG, encoding="utf-8-sig").read()
    os.makedirs(SHOT_DIR, exist_ok=True)
    src = None
    try:
        setcfg(True, "")
        kill_app()

        io.open(SRC_TXT, "w", encoding="utf-8", newline="").write(T_LONG)
        ps1 = os.path.join(TMP, "s3long_show.ps1")
        io.open(ps1, "w", encoding="utf-8-sig", newline="").write(SHOW_PS.replace("{txt}", SRC_TXT))
        src = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                                "-File", ps1],
                               cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(5.0)

        base = nlines()
        subprocess.Popen([EXE], cwd=ROOT)
        time.sleep(5.0)

        # 先把拖拽跑起来（它会占住 ~1 秒），然后轮询日志等终态
        drag = threading.Thread(target=lambda: subprocess.run(
            ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MOCK,
             "-场景", "正常框选", "-x1", BOX[0], "-y1", BOX[1], "-x2", BOX[2], "-y2", BOX[3]],
            cwd=ROOT, capture_output=True, timeout=120))
        drag.start()

        shot = os.path.join(SHOT_DIR, "t16_s3_long_final.png")
        deadline = time.time() + 90
        done = False
        while time.time() < deadline:
            rows = read_rows(base)
            if any("翻译：成功" in r or "翻译：放弃" in r for r in rows) and \
               len([r for r in rows if "提示框已更新" in r]) >= 2:
                time.sleep(0.6)          # 等一次 WPF 重排
                screenshot(shot)
                done = True
                break
            time.sleep(0.2)
        drag.join(timeout=120)
        if not done:
            screenshot(shot)
            say("!! 90 秒内没等到终态，截的是中途状态")

        rows = read_rows(base)
        say("== S3-5 300 字以上（终态截图） ==")
        say("截图 %s" % shot)
        for r in rows:
            if any(k in r for k in ("最终原文", "取词成功", "翻译：", "提示框已更新")):
                say("  " + r.strip()[:170])
        kill_app()
    finally:
        io.open(CFG, "w", encoding="utf-8", newline="").write(original)
        kill_app()
        if src is not None:
            try:
                src.terminate()
            except Exception:
                pass

    io.open(os.path.join(ROOT, "tools", "脚本", "_s3_long_result.txt"),
            "w", encoding="utf-8", newline="").write("\n".join(OUT) + "\n")


if __name__ == "__main__":
    main()
