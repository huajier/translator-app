# -*- coding: utf-8 -*-
"""第 16 轮 S3 验收：详情行格式 / 译文行 / 缓存 0ms / 断网 / 300 字可滚动 / 中文不翻。

真机路径（不是 --回放）：--回放 不建卡片，量不到界面。

【为什么只有一个受控窗口】第一版每个案例都新起一个 WinForms 窗口，第 3 个案例起
    窗口没出来（TopMost 抢不过、或者 PowerShell 起得慢），框到的是屏幕底下那个
    247 字的窗口 —— 三个案例读到同一段文字，等于没测。现在改成：**一个窗口常驻，
    里面装一个 500ms 定时器每拍重读一次文本文件**，换案例只改文件内容。

每一步都留整屏截图，事后必须自己打开看过再交。
"""
import io, json, os, subprocess, sys, time, threading

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v2.1", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
TMP = os.path.join(ROOT, "tools", "临时")
SRC_TXT = os.path.join(TMP, "s3_source.txt")
BOX = ("160", "160", "940", "480")

T_GAME = "THIEF\r\nDagger\r\nLight Armor"
T_LONG = (
    "The caravan reached the northern pass three days late, and the gatekeeper "
    "refused to open the gate until the merchant produced a seal he had lost in "
    "the river. They argued for hours in the rain while the mules stood shivering, "
    "and by evening the whole village had gathered to watch a stranger explain, "
    "over and over, why a man with no seal and no papers and no letter from the "
    "capital still deserved a warm bed and a bowl of soup for the night."
)
T_ZH = "这是一段中文测试文字，用来确认中文原文不会被翻译。"

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
# 每 500ms 重读一次文件：换案例只改文件，窗口不动，避免「窗口没出来框到别处」
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 500
$timer.Add_Tick({
    try { $new = [IO.File]::ReadAllText($txtPath, [Text.Encoding]::UTF8) } catch { $new = $tb.Text }
    if ($new -ne $tb.Text) { $tb.Text = $new }
    if (-not $f.TopMost) { $f.TopMost = $true }
})
$timer.Start()
$f.Show()
$f.Activate()
$f.TopMost = $true
while ($f.Visible) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 100
}
"""

LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")
OUT = []


def say(s=""):
    OUT.append(str(s))
    # 【不要直接 print】PS 5.1 控制台是 GBK，日志里出现「✓」「→」这类字符会
    # UnicodeEncodeError 把整轮验收打断（第一版就是崩在中文案例那一步）。
    try:
        print(str(s).encode("gbk", "replace").decode("gbk"))
    except Exception:
        print("[unprintable]")


def nlines():
    if not os.path.exists(LOG):
        return 0
    return len(io.open(LOG, encoding="utf-8-sig", errors="replace").readlines())


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


def shot_later(path, delay):
    th = threading.Thread(target=lambda: (time.sleep(delay), screenshot(path)))
    th.daemon = True
    th.start()
    return th


def drag():
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MOCK,
                    "-场景", "正常框选", "-x1", BOX[0], "-y1", BOX[1],
                    "-x2", BOX[2], "-y2", BOX[3]],
                   cwd=ROOT, capture_output=True, timeout=120)


def set_source(text):
    """换受控窗口里的文字（窗口本身不动）。"""
    io.open(SRC_TXT, "w", encoding="utf-8", newline="").write(text)
    time.sleep(1.2)          # 等那个 500ms 定时器至少拍两下


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.2)


def start_app():
    return subprocess.Popen([EXE], cwd=ROOT)


def grab_rows(base):
    return io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()[base:]


def show(rows, keys):
    for r in rows:
        if any(k in r for k in keys):
            say("  " + r.strip()[:170])


def main():
    original = io.open(CFG, encoding="utf-8-sig").read()
    os.makedirs(SHOT_DIR, exist_ok=True)
    src = None
    try:
        setcfg(True, "")
        kill_app()

        # 受控源窗口：全程只起这一个
        set_source(T_GAME)
        ps1 = os.path.join(TMP, "s3_show.ps1")
        io.open(ps1, "w", encoding="utf-8-sig", newline="").write(SHOW_PS.replace("{txt}", SRC_TXT))
        src = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                                "-File", ps1],
                               cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(5.0)
        screenshot(os.path.join(SHOT_DIR, "t16_s3_sourcecheck.png"))

        # ---------- 案例 1：THIEF / Dagger / Light Armor（冷启动第一次框选）----------
        base = nlines()
        start_app()
        time.sleep(5.0)
        s1 = os.path.join(SHOT_DIR, "t16_s3_game.png")
        th = shot_later(s1, 5.2)
        drag()
        th.join(timeout=120)
        time.sleep(2.0)
        say("== S3-1/2 第一次框选（真 Bing 冷启动） ==")
        say("截图 %s" % s1)
        show(grab_rows(base), ("最终原文", "取词成功", "翻译：", "提示框已更新"))

        # ---------- 案例 2：同一句再框一次（同一进程 → 命中缓存）----------
        base2 = nlines()
        s2 = os.path.join(SHOT_DIR, "t16_s3_cache.png")
        th2 = shot_later(s2, 3.0)
        drag()
        th2.join(timeout=120)
        time.sleep(1.5)
        say("")
        say("== S3-3 再框同一句（应命中缓存 0ms） ==")
        say("截图 %s" % s2)
        show(grab_rows(base2), ("取词成功", "翻译：", "提示框已更新"))
        kill_app()

        # ---------- 案例 3：300 字以上 ----------
        set_source(T_LONG)
        base3 = nlines()
        start_app()
        time.sleep(5.0)
        s3 = os.path.join(SHOT_DIR, "t16_s3_long.png")
        th3 = shot_later(s3, 6.0)
        drag()
        th3.join(timeout=120)
        time.sleep(2.0)
        say("")
        say("== S3-5 300 字以上（原文行应有可滚动） ==")
        say("截图 %s" % s3)
        show(grab_rows(base3), ("最终原文", "取词成功", "翻译：", "提示框已更新"))
        kill_app()

        # ---------- 案例 4：中文原文 ----------
        set_source(T_ZH)
        base4 = nlines()
        start_app()
        time.sleep(5.0)
        s4 = os.path.join(SHOT_DIR, "t16_s3_zh.png")
        th4 = shot_later(s4, 3.5)
        drag()
        th4.join(timeout=120)
        time.sleep(1.5)
        say("")
        say("== S3-6 中文原文（不应翻译） ==")
        say("截图 %s" % s4)
        show(grab_rows(base4), ("最终原文", "取词成功", "翻译", "提示框已更新"))
        kill_app()

        # ---------- 案例 5：断网 ----------
        set_source(T_GAME)
        setcfg(True, "http://127.0.0.1:5199")     # 不可达地址，模拟断网
        base5 = nlines()
        start_app()
        time.sleep(5.0)
        s5 = os.path.join(SHOT_DIR, "t16_s3_offline.png")
        th5 = shot_later(s5, 4.5)
        drag()
        th5.join(timeout=120)
        time.sleep(2.0)
        say("")
        say("== S3-4 断网（译文行不显示、原文完整） ==")
        say("截图 %s" % s5)
        show(grab_rows(base5), ("最终原文", "取词成功", "翻译：", "提示框已更新"))
        kill_app()
    finally:
        io.open(CFG, "w", encoding="utf-8", newline="").write(original)
        kill_app()
        if src is not None:
            try:
                src.terminate()
            except Exception:
                pass

    io.open(os.path.join(ROOT, "tools", "脚本", "_s3_result.txt"),
            "w", encoding="utf-8", newline="").write("\n".join(OUT) + "\n")


if __name__ == "__main__":
    main()
