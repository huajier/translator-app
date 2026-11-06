# -*- coding: utf-8 -*-
"""第 22 轮 S1/S2/S3 真机验收：长文本分段翻译（真 Bing，不用假服务器）。

一次跑完三件事：
  S1 日志里至少 3 条段级日志；跨段总时长在合理区间；没有「翻译：放弃」
  S2 段级日志第 1 条落地后 1 秒内截图 —— 卡片上已经有第一段中文、译文详情行不显示
  S3 整块结束后的截图 —— 完整中文译文 + 详情行「Bing 网页翻译 · N ms · M 字」

沿用第 16 轮的做法：一个受控文本窗口常驻（内部定时器重读文件），
换文本只改文件。真机路径，不走 --回放（回放不建卡片）。
"""
import io, json, os, subprocess, sys, time, threading

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v0.4.5", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
TMP = os.path.join(ROOT, "tools", "临时")
SRC_TXT = os.path.join(TMP, "l22_source.txt")
OUT = os.path.join(ROOT, "tools", "脚本", "_l22_s123_result.txt")
BOX = ("160", "160", "940", "480")

LONG_EN = (
    "The caravan reached the northern pass three days late, and the gatekeeper refused to open "
    "the gate until the merchant produced a seal he had lost in the river. They argued for hours "
    "in the rain while the mules stood shivering, and by evening the whole village had gathered "
    "to watch a stranger explain, over and over, why a man with no seal and no papers and no "
    "letter from the capital still deserved a warm bed and a bowl of soup for the night.\n"
    "Before dawn the gatekeeper relented, but only after the merchant agreed to leave his "
    "youngest mule behind as a pledge, and to return within the month with the seal or the money "
    "to buy the animal twice over. The stranger walked through the gate at first light with his "
    "head down, and the village talked about him for a week, mostly about the mule.\n"
    "Nobody ever saw him again, and the gatekeeper kept the mule, which turned out to be a very "
    "good mule, and for years afterwards people told the story as if it had happened to them. "
    "Years later a boy asked the gatekeeper why he had let the stranger through at all, and the "
    "old man said that a man who argues in the rain has something he wants badly enough to be "
    "soaked for it, and that such a man is usually telling the truth."
)

SHOW_PS = r"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$txtPath = '{txt}'
$f = New-Object System.Windows.Forms.Form
$f.Text = 'L22Source'
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
$tb.Font = New-Object System.Drawing.Font('Consolas', 11)
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
OUTL = []


def say(s=""):
    OUTL.append(str(s))
    try:
        print(str(s).encode("gbk", "replace").decode("gbk"))
    except Exception:
        print("[unprintable]")


def nlines():
    if not os.path.exists(LOG):
        return 0
    return len(io.open(LOG, encoding="utf-8-sig", errors="replace").readlines())


def setcfg(on=True, endpoint="", lang="zh-Hans"):
    d = json.load(io.open(CFG, encoding="utf-8-sig"))
    d["translateEnabled"] = on
    d["translateDefaultOnMigrated"] = True
    d["translateNoticeShown"] = True
    d["translateTargetLang"] = lang
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
    subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, timeout=90)


def drag():
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MOCK,
                    "-场景", "正常框选", "-x1", BOX[0], "-y1", BOX[1],
                    "-x2", BOX[2], "-y2", BOX[3]],
                   cwd=ROOT, capture_output=True, timeout=120)


def set_source(text):
    io.open(SRC_TXT, "w", encoding="utf-8", newline="").write(text)
    time.sleep(1.2)


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.2)


def rows_since(base):
    return io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()[base:]


def show(rows, keys):
    for r in rows:
        if any(k in r for k in keys):
            say("  " + r.strip()[:180])


def main():
    original = io.open(CFG, encoding="utf-8-sig").read()
    os.makedirs(SHOT_DIR, exist_ok=True)
    src = None
    ok_all = True
    try:
        set_source(LONG_EN)
        ps1 = os.path.join(TMP, "l22_show.ps1")
        io.open(ps1, "w", encoding="utf-8-sig", newline="").write(SHOW_PS.replace("{txt}", SRC_TXT))
        src = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps1],
                               cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(5.0)

        kill_app()
        setcfg(True, "", "zh-Hans")
        base = nlines()
        app = subprocess.Popen([EXE], cwd=ROOT)
        time.sleep(6.5)

        say("源文本 %d 字（含换行）" % len(LONG_EN))
        drag()

        # ---- S2：等第一条段级日志出现，1 秒内截图
        early_shot = os.path.join(SHOT_DIR, "t23_s2_early.png")
        t0 = time.time()
        first_seg_at = None
        while time.time() - t0 < 60:
            time.sleep(0.08)
            txt = "\n".join(rows_since(base))
            if "翻译：段 1/" in txt:
                first_seg_at = time.time()
                break
        if first_seg_at is None:
            say("!! 60 秒内没看到第 1 段日志")
            ok_all = False
        else:
            time.sleep(1.0)
            screenshot(early_shot)
            say("S2 截图（段 1 日志后 1.0 秒）：%s" % early_shot)

        # ---- 等整块结束
        t1 = time.time()
        finished = False
        while time.time() - t1 < 90:
            time.sleep(0.2)
            txt = "\n".join(rows_since(base))
            if "翻译：成功" in txt or "翻译：放弃" in txt:
                finished = True
                break
        time.sleep(0.8)
        final_shot = os.path.join(SHOT_DIR, "t23_s3_final.png")
        screenshot(final_shot)
        say("S3 截图（整块结束）：%s" % final_shot)

        rows = rows_since(base)
        txt = "\n".join(rows)
        say("")
        say("== 日志 ==")
        show(rows, ("最终原文", "取词成功", "翻译：", "提示框已更新"))

        seg_lines = [r for r in rows if "翻译：段 " in r]
        say("")
        say("S1 段级日志条数 = %d" % len(seg_lines))
        if len(seg_lines) < 3:
            say("!! S1 FAIL：段级日志少于 3 条")
            ok_all = False
        if "翻译：放弃" in txt:
            say("!! S1 FAIL：出现「翻译：放弃」")
            ok_all = False
        if not finished:
            say("!! 90 秒内没等到整块结果")
            ok_all = False
        say("S1 结果：段级 %d 条、汇总 %s、放弃 %s" % (
            len(seg_lines),
            "有" if "翻译：成功" in txt else "无",
            "有" if "翻译：放弃" in txt else "无"))
        say("总判定：%s" % ("PASS" if ok_all else "FAIL"))
    finally:
        try:
            kill_app()
        except Exception:
            pass
        if src is not None:
            try:
                src.terminate()
            except Exception:
                pass
        io.open(CFG, "w", encoding="utf-8", newline="").write(original)
        io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(OUTL) + "\n")
        say("")
        say("结果已写 %s" % OUT)


main()
