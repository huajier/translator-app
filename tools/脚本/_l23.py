# -*- coding: utf-8 -*-
"""第 23 轮真机验收：翻译中提示改成蓝色小字带跳点。

S1 短文本（真 Bing）：先看到蓝色「翻译中.」，翻完变成灰色详情行 + 白色译文。
S2 长文本约 1200 字：第 1 段落地后蓝色「正在翻译剩余段落」，全部完成后变灰色详情行。
S3 黑洞 endpoint：蓝色提示不残留，失败原因写在译文区，详情行不可见。
S4 中文原文：整个译文区（横线 + 滚动区 + 详情行）都不出现。

为什么 S1/S2 用真 Bing 不用假服务器：假服务器 2ms 就返回，根本抓不到
「翻译中」那一瞬间 —— 这个测试条件会消灭它要测的东西（第 12 轮踩过）。
"""
import io, json, os, re, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
VER = os.environ.get("VER", "v0.4.6")
EXE = os.path.join(ROOT, "release", VER, "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
TMP = os.path.join(ROOT, "tools", "临时")
SRC_TXT = os.path.join(TMP, "l23_source.txt")
SHOW_PS = os.path.join(TMP, "l23_show.ps1")
OUT = os.path.join(ROOT, "tools", "脚本", "_l23_result.txt")
LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")
BOX = ("160", "160", "940", "480")

SHORT_EN = "THIEF / Dagger / Light Armor"

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
    "soaked for it, and that such a man is usually telling the truth.\n"
    "The boy thought about this for the rest of his life, and when he was old he told it to his "
    "own children, adding a detail here and there that he could not possibly have known, so that "
    "in the end the story belonged to everyone in the village and to no one in particular. That "
    "is how these things go: a stranger passes through, and a hundred years later the whole "
    "valley remembers him wrong and loves him anyway, because the story is all that is left."
)

ZH = "这是一段中文测试文字，用来确认整块译文区都不会出现，包括横线、滚动区和详情行。"

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


def rows_since(base):
    return io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()[base:]


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
    """超快抓屏（ctypes BitBlt，约 0.03~0.5 秒）。

    【2026-11-06 第23轮踩的坑】原来用 PowerShell CopyFromScreen：
      起一次 PowerShell 要 1.5 秒，而「翻译中」只存在 1.46 秒 ——
      拍下去的时候翻译已经完成了，截到的全是完成态。
      量具比被测现象还慢，就永远拍不到那一瞬间。
    """
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from _grab import grab_to_png
    grab_to_png(path)


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


def start_app():
    subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(40):
        time.sleep(0.5)
        r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq TranslatorApp.exe"],
                           capture_output=True)
        if b"TranslatorApp" in r.stdout:
            time.sleep(3.5)   # 等 RapidOCR 引擎与主窗口就绪
            return True
    return False


def wait_for(base, keys, deadline=90):
    """等本次案例自己的日志里出现任一关键字。返回 (命中关键字, 耗时秒) 或 (None, 超时)。"""
    t0 = time.time()
    while time.time() - t0 < deadline:
        time.sleep(0.12)
        txt = "\n".join(rows_since(base))
        for k in keys:
            if k in txt:
                return k, time.time() - t0
    return None, time.time() - t0


def main():
    for d in (SHOT_DIR, TMP):
        os.makedirs(d, exist_ok=True)
    io.open(SRC_TXT, "w", encoding="utf-8", newline="").write(SHORT_EN)
    # 源文本窗口：一个窗口常驻，内部定时器重读文件（第16轮 S3 的教训）
    io.open(SHOW_PS, "w", encoding="utf-8-sig", newline="").write(r"""
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
""" % SRC_TXT.replace("\\", "\\\\"))
    subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                      "-File", SHOW_PS],
                     cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(3.0)

    backup = io.open(CFG, encoding="utf-8-sig").read()

    ok = {}
    try:
        # ===== S1 短文本，真 Bing =====
        say("===== S1 短文本（真 Bing）=====")
        kill_app()
        set_source(SHORT_EN)
        setcfg(True, "", "zh-Hans")
        start_app()
        base = nlines()
        drag()
        hit, dt = wait_for(base, ["译文区已切到「翻译中」（蓝色小字带跳点）"], deadline=60)
        say("  S1 抓到翻译中日志：%s（%.2f 秒）" % (hit, dt))
        if hit:
            screenshot(os.path.join(SHOT_DIR, "t24_s1_translating.png"))
            say("  已截 t24_s1_translating.png")
        hit2, dt2 = wait_for(base, ["翻译：成功", "翻译：放弃"], deadline=60)
        say("  S1 整块结果：%s（%.2f 秒）" % (hit2, dt2))
        time.sleep(0.7)
        screenshot(os.path.join(SHOT_DIR, "t24_s1_done.png"))
        say("  已截 t24_s1_done.png")
        ok["S1"] = bool(hit) and hit2 == "翻译：成功"

        # ===== S2 长文本约 1200 字 =====
        say("")
        say("===== S2 长文本（真 Bing）=====")
        set_source(LONG_EN)
        base = nlines()
        drag()
        hit, dt = wait_for(base, ["翻译：段 1/"], deadline=90)
        say("  S2 第 1 段落地：%s（%.2f 秒）" % (hit, dt))
        time.sleep(0.5)
        screenshot(os.path.join(SHOT_DIR, "t24_s2_partial.png"))
        say("  已截 t24_s2_partial.png")
        hit2, dt2 = wait_for(base, ["翻译：成功", "翻译：放弃"], deadline=120)
        say("  S2 整块结果：%s（%.2f 秒）" % (hit2, dt2))
        time.sleep(0.7)
        screenshot(os.path.join(SHOT_DIR, "t24_s2_done.png"))
        say("  已截 t24_s2_done.png")
        ok["S2"] = hit2 == "翻译：成功"

        # ===== S3 黑洞 endpoint（连接被拒，不是真拔网线，等效且可复现）=====
        say("")
        say("===== S3 黑洞 endpoint =====")
        kill_app()
        set_source(SHORT_EN)
        setcfg(True, "http://127.0.0.1:5199", "zh-Hans")
        start_app()
        base = nlines()
        drag()
        hit, dt = wait_for(base, ["翻译：成功", "翻译：放弃"], deadline=60)
        say("  S3 整块结果：%s（%.2f 秒）" % (hit, dt))
        time.sleep(0.7)
        screenshot(os.path.join(SHOT_DIR, "t24_s3_fail.png"))
        say("  已截 t24_s3_fail.png")
        ok["S3"] = hit == "翻译：放弃"

        # ===== S4 中文原文 =====
        say("")
        say("===== S4 中文原文 =====")
        kill_app()
        set_source(ZH)
        setcfg(True, "", "zh-Hans")
        start_app()
        base = nlines()
        drag()
        hit, dt = wait_for(base, ["翻译：跳过", "翻译：成功", "翻译：放弃"], deadline=60)
        say("  S4 结果：%s（%.2f 秒）" % (hit, dt))
        time.sleep(0.7)
        screenshot(os.path.join(SHOT_DIR, "t24_s4_chinese.png"))
        say("  已截 t24_s4_chinese.png")
        ok["S4"] = hit == "翻译：跳过"
    finally:
        io.open(CFG, "w", encoding="utf-8", newline="").write(backup)
        kill_app()
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-Process powershell -ErrorAction SilentlyContinue | "
                        "Where-Object { $_.MainWindowTitle -eq 'L23Source' } | "
                        "Stop-Process -Force -ErrorAction SilentlyContinue"],
                       capture_output=True, timeout=60)
        say("")
        for k in ("S1", "S2", "S3", "S4"):
            say("%s：%s" % (k, "PASS" if ok.get(k) else "FAIL（或未跑到）"))
        say("配置已还原（%d 键）" % len(json.load(io.open(CFG, encoding="utf-8-sig"))))
        io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(OUTL) + "\n")


main()
