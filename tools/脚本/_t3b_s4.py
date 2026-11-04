# -*- coding: utf-8 -*-
"""T3b S4：确认主窗口改造没破坏已有的框选翻译。
  A 翻译开 → 真机框选一次，出原文 + 译文（**真 Bing，不许用假服务器**）
  B 翻译关 → 真机框选一次，只有原文，日志里没有任何「翻译：成功 / 发到」

真机路径（不是 --回放）：--回放 不建卡片，量不到界面。
"""
import io, json, os, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v1.9", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
BOX = ("160", "160", "940", "480")
TEXT = (
    'The ancient gate stood silent at the edge of the valley. Nobody in the village '
    'remembered who had carved the runes, or why the stone still hummed whenever the '
    'moon rose. Children were warned to keep their distance, and hunters took the '
    'long road around the hill even in snow.'
)

SHOW_PS = r"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.Text = 'S4Source'
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
$tb.Text = [IO.File]::ReadAllText('{txt}', [Text.Encoding]::UTF8)
$panel.Controls.Add($tb)
$f.Controls.Add($panel)
$f.TopMost = $true
$f.Show()
$f.Activate()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Seconds 300
"""

LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")
OUT = []


def say(s=""):
    OUT.append(str(s))


def nlines():
    if not os.path.exists(LOG):
        return 0
    return len(io.open(LOG, encoding="utf-8-sig", errors="replace").readlines())


def setcfg(on):
    d = json.load(io.open(CFG, encoding="utf-8-sig"))
    d["translateEnabled"] = on
    d["translateNoticeShown"] = True
    d["translateTargetLang"] = "zh-Hans"
    d["translateEndpoint"] = ""          # 空 = 走默认 cn.bing.com，真 Bing
    io.open(CFG, "w", encoding="utf-8", newline="").write(
        json.dumps(d, ensure_ascii=False, indent=2) + "\n")


def screenshot(path):
    ps = ("Add-Type -AssemblyName System.Windows.Forms,System.Drawing;"
          "$b=[System.Windows.Forms.SystemInformation]::VirtualScreen;"
          "$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height;"
          "$g=[System.Drawing.Graphics]::FromImage($bmp);"
          "$g.CopyFromScreen($b.Left,$b.Top,0,0,$bmp.Size);"
          "$bmp.Save('%s');" % path.replace("\\", "\\\\"))
    subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                   capture_output=True, timeout=90)


def run_case(tag, on, shot_delay):
    setcfg(on)
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.0)
    base = nlines()
    app = subprocess.Popen([EXE], cwd=ROOT)
    time.sleep(5.0)                      # 冷启动，不热身

    shot = os.path.join(SHOT_DIR, "t3b_s4_%s.png" % tag)
    os.makedirs(SHOT_DIR, exist_ok=True)
    import threading
    th = threading.Thread(target=lambda: (time.sleep(shot_delay), screenshot(shot)))
    th.start()
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MOCK,
                    "-场景", "正常框选", "-x1", BOX[0], "-y1", BOX[1],
                    "-x2", BOX[2], "-y2", BOX[3]],
                   cwd=ROOT, capture_output=True, timeout=120)
    th.join(timeout=120)
    time.sleep(9.0)
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.0)

    rows = io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()[base:]
    return shot, rows


def main():
    original = io.open(CFG, encoding="utf-8-sig").read()
    # 【必须先把文本窗口摆出来】否则框选框住的是屏幕上原来那个窗口，
    #   第一次跑就框到了 DSH 聊天窗口（中文原文 → 母语跳过 → 报「没翻译」的假 FAIL）。
    txt = os.path.join(ROOT, "tools", "临时", "f3-text.txt")
    showps = os.path.join(ROOT, "tools", "临时", "_f3_show.ps1")
    io.open(txt, "w", encoding="utf-8", newline="").write("\r\n" + TEXT)
    io.open(showps, "w", encoding="utf-8-sig", newline="").write(
        SHOW_PS.replace("{txt}", txt))
    show = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                             "-File", showps],
                            cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(3.5)

    try:
        # ---------- A 翻译开 ----------
        shot_a, rows_a = run_case("on", True, 4.5)
        ok_a = [r for r in rows_a if "翻译：成功" in r]
        card_a = [r for r in rows_a if "提示框已更新为取词结果" in r]
        say("== A 翻译=开（真 Bing） ==")
        say("截图 %s" % shot_a)
        say("翻译成功行 %d 条" % len(ok_a))
        for r in ok_a[:2]:
            say("  " + r.strip())
        say("卡片更新 %d 次" % len(card_a))
        for r in card_a:
            say("  " + r.strip()[:110])

        # ---------- B 翻译关 ----------
        shot_b, rows_b = run_case("off", False, 2.4)
        # 【只认「真的发出去过」的行】不能拿 "Bing" 当关键字：
        #   启动时那行「已读取设置：… 翻译 = 关（译成 zh-Hans，地址 = 默认 Bing …）」
        #   本来就含 Bing，会把它误判成外发（第一版就踩了这个，报了个假 FAIL）。
        BAD = ("翻译：成功", "翻译：放弃", "正在翻译", "提供方 = ", "发到微软翻译服务")
        ok_b = [r for r in rows_b if any(k in r for k in BAD) and "已读取设置" not in r]
        card_b = [r for r in rows_b if "提示框已更新为取词结果" in r]
        say("")
        say("== B 翻译=关 ==")
        say("截图 %s" % shot_b)
        say("任何翻译活动（应 0 条）：%d 条" % len(ok_b))
        for r in ok_b:
            say("  " + r.strip()[:110])
        say("卡片更新 %d 次（应 1 次：只有原文）" % len(card_b))
        for r in card_b:
            say("  " + r.strip()[:110])

        # ---------- 断言 ----------
        say("")
        a_ok = len(ok_a) >= 1 and len(card_a) == 2
        b_ok = len(ok_b) == 0 and len(card_b) == 1
        say("S4-A 真 Bing 出原文+译文：%s" % ("PASS" if a_ok else "FAIL"))
        say("S4-B 关开关只有原文、零外发：%s" % ("PASS" if b_ok else "FAIL"))
        say("总判定：%s" % ("PASS" if a_ok and b_ok else "FAIL"))
    finally:
        io.open(CFG, "w", encoding="utf-8", newline="").write(original)
        try:
            subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
        except Exception:
            pass

    io.open(os.path.join(ROOT, "tools", "脚本", "_s4_result.txt"),
            "w", encoding="utf-8", newline="").write("\n".join(OUT) + "\n")


if __name__ == "__main__":
    main()
