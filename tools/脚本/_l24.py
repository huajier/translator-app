# -*- coding: utf-8 -*-
"""第 24 轮真机验收：识别中跳点 / 翻译中不被顶掉 / 断网文案。

S1 识别中：框英文，趁识别没跑完截图，期望蓝色「识别中」+ 跳点。
S2 短文本：翻译中蓝色「翻译中.」，翻完变灰色详情行。
S3 长文本 1200 字：第一段译文出来时底下仍是蓝色「翻译中」+ 点在跳（连拍 ≥6 帧）。
S4 断网：endpoint 指 127.0.0.1:1，译文位写「网络连接失败」。

全部真 Bing / 真断网，不用假服务器（假服务器 2ms 返回，抓不到中间态）。
"""
import io, json, os, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
VER = os.environ.get("VER", "v0.4.7")
EXE = os.path.join(ROOT, "release", VER, "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
TMP = os.path.join(ROOT, "tools", "临时")
SRC_TXT = os.path.join(TMP, "l24_source.txt")
SHOW_PS = os.path.join(TMP, "l24_show.ps1")
OUT = os.path.join(ROOT, "tools", "脚本", "_l24_result.txt")
LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")
BOX = ("160", "160", "940", "480")
CARD_BOX = (940, 150, 300, 140)   # 卡片区域（识别中 / 短文本翻译中都在这一带）

SHORT_EN = "THIEF / Dagger / Light Armor"

# 【S2 必须换一句没翻过的】S1 已经翻过 SHORT_EN，S2 再框同一句会命中缓存，
#   缓存命中是 0ms 级的，「翻译中」那 0.1 秒根本抓不到（本轮实测拍到了完成态）。
#   这就是第 12 轮的老教训换个马甲：测试条件把要测的东西消灭了。
SHORT_EN2 = "Elixir of Greater Healing"

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


def grab(path, box=None):
    """超快抓屏（ctypes BitBlt，小块约 0.03 秒）。"""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from _grab import grab_to_png
    if box is None:
        grab_to_png(path)
    else:
        grab_to_png(path, left=box[0], top=box[1], width=box[2], height=box[3])


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
            time.sleep(3.5)
            return True
    return False


def wait_for(base, keys, deadline=90):
    t0 = time.time()
    while time.time() - t0 < deadline:
        time.sleep(0.06)
        txt = "\n".join(rows_since(base))
        for k in keys:
            if k in txt:
                return k, time.time() - t0
    return None, time.time() - t0


def dots_in(path):
    """数一张图里「最底那行提示」上的蓝色点块。

    【怎么找那行】卡片蓝边框是一条横贯整卡的长蓝线：
      取「蓝色像素最多的那些行」里最靠下的那一条 = 卡片下边框；
      往上 60px 之内就是「翻译中」那行小字。
      直接按固定 y 扫会踩坑：长文本卡片能到 750px 高，那行根本不在预估位置。
    返回值 (块数, 蓝色像素总数)。块数受字号影响会偏小，判「有没有在变」够用。
    """
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from _crop import load_png
    w, h, bpp, rows = load_png(path)

    def is_blue(px):
        return abs(px[0] - 56) < 26 and abs(px[1] - 189) < 26 and abs(px[2] - 248) < 26

    rowcnt = []
    for y in range(h):
        r = rows[y]
        c = sum(1 for x in range(w) if is_blue(r[x * bpp:x * bpp + 3]))
        rowcnt.append(c)
    widest = max(rowcnt) if rowcnt else 0
    if widest < 40:
        return 0, 0
    bottom = max(y for y in range(h) if rowcnt[y] > widest * 0.5)
    y0, y1 = max(0, bottom - 60), bottom - 2
    cols = set()
    for y in range(y0, y1):
        r = rows[y]
        for x in range(w):
            if is_blue(r[x * bpp:x * bpp + 3]):
                cols.add(x)
    if not cols:
        return 0, 0
    ss = sorted(cols)
    blocks, last = 1, ss[0]
    for x in ss[1:]:
        if x - last > 3:
            blocks += 1
        last = x
    return blocks, len(cols)


def main():
    for d in (SHOT_DIR, TMP):
        os.makedirs(d, exist_ok=True)
    io.open(SRC_TXT, "w", encoding="utf-8", newline="").write(SHORT_EN)
    io.open(SHOW_PS, "w", encoding="utf-8-sig", newline="").write(r"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$txtPath = '%s'
$f = New-Object System.Windows.Forms.Form
$f.Text = 'L24Source'
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
        # ===== S1 识别中 =====
        say("===== S1 识别中（真机）=====")
        kill_app()
        set_source(SHORT_EN)
        setcfg(True, "", "zh-Hans")
        start_app()
        base = nlines()
        drag()
        hit, dt = wait_for(base, ["原文区已切到「识别中」"], deadline=30)
        say("  S1 抓到识别中日志：%s（%.2f 秒）" % (hit, dt))
        if hit:
            for i in range(4):
                grab(os.path.join(SHOT_DIR, "t25_s1_recognizing%d.png" % i), CARD_BOX)
            say("  已连拍 t25_s1_recognizing0..3.png")
        hit2, dt2 = wait_for(base, ["提示框已更新"], deadline=60)
        say("  S1 识别完成：%s（%.2f 秒）" % (hit2, dt2))
        time.sleep(0.5)
        grab(os.path.join(SHOT_DIR, "t25_s1_done.png"), (940, 150, 320, 200))
        say("  已截 t25_s1_done.png")
        ok["S1"] = bool(hit) and bool(hit2)

        # ===== S2 短文本翻译中 =====
        say("")
        say("===== S2 短文本翻译中（真 Bing）=====")
        # 换一句 S1 没翻过的（否则命中缓存，0ms 就完事，「翻译中」根本抓不到）
        set_source(SHORT_EN2)
        base = nlines()
        drag()
        hit, dt = wait_for(base, ["译文区已切到「翻译中」（蓝色小字带跳点）"], deadline=60)
        say("  S2 抓到翻译中：%s（%.2f 秒）" % (hit, dt))
        if hit:
            grab(os.path.join(SHOT_DIR, "t25_s2_translating.png"), (940, 150, 320, 200))
            say("  已截 t25_s2_translating.png")
        hit2, dt2 = wait_for(base, ["翻译：成功", "翻译：放弃"], deadline=60)
        say("  S2 结果：%s（%.2f 秒）" % (hit2, dt2))
        time.sleep(0.7)
        grab(os.path.join(SHOT_DIR, "t25_s2_done.png"), (940, 150, 320, 200))
        say("  已截 t25_s2_done.png")
        ok["S2"] = bool(hit) and hit2 == "翻译：成功"

        # ===== S3 长文本分段 =====
        say("")
        say("===== S3 长文本分段（真 Bing）=====")
        set_source(LONG_EN)
        base = nlines()
        drag()
        hit, dt = wait_for(base, ["翻译：段 1/"], deadline=90)
        say("  S3 第 1 段落地：%s（%.2f 秒）" % (hit, dt))
        time.sleep(0.35)
        seq = []
        for i in range(8):
            p = os.path.join(SHOT_DIR, "t25_s3_burst%d.png" % i)
            grab(p, (940, 150, 500, 760))   # 整张卡片 + 底部那行提示
            time.sleep(0.12)
        txt = "\n".join(rows_since(base))
        # 连拍期间「翻译中」这行在卡片底部；按整块图高度估算行位置不可靠，
        # 直接扫整张图下三分之一的蓝色像素块数，看有没有变化。
        for i in range(8):
            p = os.path.join(SHOT_DIR, "t25_s3_burst%d.png" % i)
            n, cnt = dots_in(p)
            seq.append((i, n, cnt))
        say("  S3 连拍蓝点块数：" + " ".join("%d:%d/%d" % (a, b, c) for a, b, c in seq))
        vals = set(x[1] for x in seq)
        say("  S3 块数取值集合：%s" % sorted(vals))
        # 命中「翻译中」而不是「正在翻译剩余段落」
        say("  S3 期间出现「正在翻译剩余段落」：%s" % ("是" if "正在翻译剩余段落" in txt else "否"))
        hit2, dt2 = wait_for(base, ["翻译：成功", "翻译：放弃"], deadline=180)
        say("  S3 整块结果：%s（%.2f 秒）" % (hit2, dt2))
        time.sleep(0.7)
        grab(os.path.join(SHOT_DIR, "t25_s3_done.png"), (940, 150, 520, 800))
        say("  已截 t25_s3_done.png")
        ok["S3"] = (hit2 == "翻译：成功" and "正在翻译剩余段落" not in txt and len(vals) >= 2)
        io.open(os.path.join(SHOT_DIR, "t25_s3_burst0.png"), "rb")  # 保留首帧供看图

        # ===== S4 断网文案 =====
        say("")
        say("===== S4 断网文案 =====")
        kill_app()
        set_source(SHORT_EN)
        setcfg(True, "http://127.0.0.1:1", "zh-Hans")
        start_app()
        base = nlines()
        drag()
        hit, dt = wait_for(base, ["翻译：成功", "翻译：放弃"], deadline=90)
        say("  S4 结果：%s（%.2f 秒）" % (hit, dt))
        time.sleep(0.6)
        grab(os.path.join(SHOT_DIR, "t25_s4_fail.png"), (940, 150, 320, 200))
        say("  已截 t25_s4_fail.png")
        txt = "\n".join(rows_since(base))
        say("  S4 日志含「网络连接失败」：%s" % ("是" if "网络连接失败" in txt else "否"))
        ok["S4"] = hit == "翻译：放弃" and "网络连接失败" in txt
    finally:
        io.open(CFG, "w", encoding="utf-8", newline="").write(backup)
        kill_app()
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-Process powershell -ErrorAction SilentlyContinue | "
                        "Where-Object { $_.MainWindowTitle -eq 'L24Source' } | "
                        "Stop-Process -Force -ErrorAction SilentlyContinue"],
                       capture_output=True, timeout=60)
        say("")
        for k in ("S1", "S2", "S3", "S4"):
            say("%s：%s" % (k, "PASS" if ok.get(k) else "FAIL（或未跑到）"))
        say("配置已还原（%d 键）" % len(json.load(io.open(CFG, encoding="utf-8-sig"))))
        io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(OUTL) + "\n")


main()
