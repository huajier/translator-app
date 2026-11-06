"""第23轮补验：确认「翻译中」后面的跳点真的在跳（不是只画了静止的三个字）。

做法：真 Bing 起一次翻译，然后以 ~120ms 间隔连拍 40 帧小区域
（只用 _grab.py 抓 240x120 那一小块，0.03 秒/帧），
逐帧统计「翻译中」三个字右侧的蓝色像素数，看点有几个、是否随时间变化。

判据：
  · 若点数在 0/1/2/3 之间变化 → 跳点生效
  · 若始终 0 → 跳点没画出来（StartTranslatingHint 没起或 ApplyHintText 没跑）
  · 若始终 3 → 定时器没跳
"""
import ctypes
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _crop import load_png  # noqa: E402
from _grab import grab_to_png  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
VER = os.environ.get("VER", "v0.4.6")
EXE = os.path.join(ROOT, "release", VER, "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
TMP = os.path.join(ROOT, "tools", "临时")
SHOT = os.path.join(ROOT, "tools", "日志", "取证截图")
SOURCE = os.path.join(TMP, "l23_source.txt")
SHOWER = os.path.join(TMP, "l23_show.ps1")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
OUT = os.path.join(ROOT, "tools", "脚本", "_l23_dots.txt")

REGION = (940, 150, 280, 130)   # 卡片左上角那块（含「翻译中」行）
# 「翻译中」三个字在整屏上的 x 约 964..994，卡片左边框 952，
# 所以区域内的相对位置是 964-940=24 .. 994-940=54，点从 56 往后算。
DOTS_X0 = 56
HINT_Y0, HINT_Y1 = 237 - 150 - 6, 246 - 150 + 6   # 只扫「翻译中」那一行横幅

lines = []


def say(s=""):
    print(str(s).encode("gbk", "replace").decode("gbk"))
    lines.append(s)


def setcfg(on, endpoint="", lang="zh-Hans"):
    import json
    d = json.load(open(CFG, encoding="utf-8"))
    d["translateEnabled"] = on
    d["translateEndpoint"] = endpoint
    d["translateTargetLang"] = lang
    d["translateNoticeShown"] = True
    d["translateDefaultOnMigrated"] = True
    open(CFG, "w", encoding="utf-8").write(json.dumps(d, ensure_ascii=False, indent=2))


def kill_app():
    subprocess.run(["powershell", "-NoProfile", "-Command",
                    "Get-Process TranslatorApp -ErrorAction SilentlyContinue | Stop-Process -Force"],
                   capture_output=True)


def start_app():
    subprocess.Popen([EXE], cwd=os.path.dirname(EXE))


def show_source():
    """把受控文本窗口摆到框选位置。

    内容与 `_l23.py` 里那份一致（一个窗口常驻 + 500ms 定时器重读文件），
    这里只保留最小版本：这个脚本只测一次短文本，不需要换内容。
    """
    if not os.path.exists(SHOWER):
        ps = r"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$txtPath = '%s'
$f = New-Object System.Windows.Forms.Form
$f.Text = 'L23Source'
$f.StartPosition = 'Manual'
$f.Left = 100; $f.Top = 100; $f.Width = 900; $f.Height = 420
$f.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$panel = New-Object System.Windows.Forms.Panel
$panel.Dock = 'Fill'
$panel.Padding = New-Object System.Windows.Forms.Padding(60,60,60,60)
$panel.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.ReadOnly = $true; $tb.BorderStyle = 'None'
$tb.Dock = 'Fill'
$tb.Font = New-Object System.Drawing.Font('Consolas', 11)
$tb.ForeColor = [System.Drawing.Color]::White
$tb.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$tb.Text = [IO.File]::ReadAllText($txtPath, [Text.Encoding]::UTF8)
$panel.Controls.Add($tb)
$f.Controls.Add($panel)
$f.TopMost = $true
$f.Show()
$f.Activate()
while ($f.Visible) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 100
}
""" % SOURCE.replace("\\", "\\\\")
        open(SHOWER, "w", encoding="utf-8-sig", newline="").write(ps)
    subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                      "-File", SHOWER], cwd=ROOT,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def drag():
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MOCK,
                    "-场景", "正常框选",
                    "-x1", "160", "-y1", "160", "-x2", "940", "-y2", "480"],
                   cwd=ROOT, capture_output=True)


def blue_count(path):
    """统计区域里「翻译中」右侧的蓝色像素，并按 x 分组成「点」的块数。

    只在「翻译中」那一行横幅里扫（HINT_Y0..HINT_Y1），
    否则会把上面那行蓝色原文 THIEF... 一起数进来。
    """
    w, h, bpp, rows = load_png(path)
    xs = set()
    for y in range(max(0, HINT_Y0), min(h, HINT_Y1)):
        row = rows[y]
        for x in range(DOTS_X0, w):
            r, g, b = row[x * bpp:x * bpp + 3]
            if b > 120 and b - r > 60 and g > 100:
                xs.add(x)
    if not xs:
        return 0, 0
    xs = sorted(xs)
    blocks, prev = 1, xs[0]
    for x in xs[1:]:
        if x - prev > 2:
            blocks += 1
        prev = x
    return len(xs), blocks


def main():
    backup = open(CFG, encoding="utf-8").read()
    try:
        kill_app()
        time.sleep(0.6)
        setcfg(True, "", "zh-Hans")
        open(SOURCE, "w", encoding="utf-8").write("THIEF / Dagger / Light Armor")
        show_source()
        time.sleep(1.6)
        start_app()
        time.sleep(5.5)
        drag()
        frames = []
        t0 = time.time()
        for i in range(40):
            p = os.path.join(TMP, "_dot_%02d.png" % i)
            grab_to_png(p, *REGION)
            frames.append((round(time.time() - t0, 2), p))
            time.sleep(0.09)
        say("帧 | 秒 | 蓝像素 | 点块数")
        counts = []
        for sec, p in frames:
            n, blocks = blue_count(p)
            counts.append(blocks)
            say("%s | %s | %s" % (os.path.basename(p), sec, "%d / %d" % (n, blocks)))
        uniq = sorted(set(counts))
        say("")
        say("出现过的点块数：%s" % uniq)
        if uniq == [0]:
            say("判定：FAIL —— 一直 0 个点，跳点没画出来")
        elif len(uniq) == 1:
            say("判定：PARTIAL —— 点数恒为 %d，没有在跳" % uniq[0])
        else:
            say("判定：PASS —— 点数在 %s 之间变化，跳点生效" % uniq)
        # 存一张「有点」的帧留证
        best = max(frames, key=lambda f: blue_count(f[1])[1])
        dst = os.path.join(SHOT, "t24_s1_dots.png")
        import shutil
        shutil.copyfile(best[1], dst)
        say("留证帧：%s（%d 个点）→ %s" % (os.path.basename(best[1]),
                                          blue_count(best[1])[1], dst))
    finally:
        kill_app()
        open(CFG, "w", encoding="utf-8").write(backup)
        import json
        d = json.load(open(CFG, encoding="utf-8"))
        say("配置已还原（%d 键）：enabled=%s endpoint=%r" % (
            len(d), d.get("translateEnabled"), d.get("translateEndpoint")))
        open(OUT, "w", encoding="utf-8").write("\n".join(lines) + "\n")
        say("结果写 %s" % OUT)


if __name__ == "__main__":
    main()
