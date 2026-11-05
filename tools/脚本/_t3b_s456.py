# -*- coding: utf-8 -*-
"""第 16 轮收口打磨 S4/S5/S6 真机验收（v2.2）。

S4 详情行顺序 / S5 中文不闪「翻译中…」 / S6 删掉「可滚动」。

真机路径（不是 --回放）：--回放 不建卡片，量不到界面。
沿用 _t3b_s3.py 的做法：一个受控窗口常驻 + 500ms 定时器重读文件，换案例只改文件。
每步留整屏截图，事后必须自己打开看过再交。
"""
import io, json, os, subprocess, sys, time, threading

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v2.2", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
TMP = os.path.join(ROOT, "tools", "临时")
SRC_TXT = os.path.join(TMP, "s456_source.txt")
OUT = os.path.join(ROOT, "tools", "脚本", "_s456_result.txt")
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
T_HUGE = T_LONG + (
    " Before dawn the gatekeeper relented, but only after the merchant agreed to "
    "leave his youngest mule behind as a pledge, and to return within the month "
    "with the seal or the money to buy the animal twice over. The stranger walked "
    "through the gate at first light with his head down, and the village talked "
    "about him for a week, mostly about the mule. Nobody ever saw him again, and "
    "the gatekeeper kept the mule, which turned out to be a very good mule, and "
    "for years afterwards people told the story as if it had happened to them."
)
T_ZH = "这是一段中文测试文字，用来确认中文原文不会被翻译。"

SHOW_PS = r"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$txtPath = '{txt}'
$f = New-Object System.Windows.Forms.Form
$f.Text = 'S456Source'
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


def say(s=""):
    OUTL.append(str(s))
    try:
        print(str(s).encode("gbk", "replace").decode("gbk"))
    except Exception:
        print("[unprintable]")


OUTL = []


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
    io.open(SRC_TXT, "w", encoding="utf-8", newline="").write(text)
    time.sleep(1.2)


def kill_app():
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(1.2)


def start_app():
    return subprocess.Popen([EXE], cwd=ROOT)


def rows_since(base):
    return io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()[base:]


def show(rows, keys):
    for r in rows:
        if any(k in r for k in keys):
            say("  " + r.strip()[:170])


def wait_final(base, want_translate=True, delay=0.6, deadline=120):
    """等本案例自己的「提示框已更新」够数。

    【必须从 base 起数】第一版在整个日志里数 "提示框已更新"，前一个案例已经攒够 2 次，
    于是每个案例都瞬间返回，截到的是「还没出结果」的空卡片 —— 又是量具抢跑。
    """
    t0 = time.time()
    while time.time() - t0 < deadline:
        time.sleep(0.3)
        rows = rows_since(base)
        txt = "\n".join(rows)
        if "取词成功" not in txt and "取词失败" not in txt:
            continue
        n = txt.count("提示框已更新")
        if want_translate:
            done = ("翻译：成功" in txt or "翻译：放弃" in txt or "翻译：跳过" in txt)
            if n >= 2 and done:
                time.sleep(delay)
                return True
        else:
            if n >= 1:
                time.sleep(delay)
                return True
    return False


def frame(title, shot, base, keys):
    say("")
    say("== %s ==" % (title.encode("gbk", "replace").decode("gbk")))
    say("截图 %s" % shot)
    show(rows_since(base), keys)


def main():
    original = io.open(CFG, encoding="utf-8-sig").read()
    os.makedirs(SHOT_DIR, exist_ok=True)
    src = None
    try:
        set_source(T_GAME)
        ps1 = os.path.join(TMP, "s456_show.ps1")
        io.open(ps1, "w", encoding="utf-8-sig", newline="").write(SHOW_PS.replace("{txt}", SRC_TXT))
        src = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                                "-File", ps1],
                               cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(5.0)

        # ================= S4-1 / S6-3：三个词，翻译开 =================
        kill_app()
        setcfg(True, "")
        base = nlines()
        start_app()
        time.sleep(6.0)
        s = os.path.join(SHOT_DIR, "t17_s4_game.png")
        drag()
        wait_final(base)
        screenshot(s)
        frame("S4-1/S6-3 三个词，翻译开（四行顺序 + 详情行干净）", s, base,
              ("最终原文", "取词成功", "翻译：", "提示框已更新"))

        # ================= S5-2：英文原文 → 必须出现「翻译中…」 =================
        say("")
        say("S5-2 英文是否出现「翻译中…」判定：")
        eng_rows = [r for r in rows_since(base) if "译文区已切到" in r]
        say("  译文区已切到「翻译中…」出现 %d 次（英文应当 >=1）" % len(eng_rows))
        kill_app()

        # ================= S5-1：中文原文 → 全程不得出现「翻译中…」 =================
        set_source(T_ZH)
        base2 = nlines()
        start_app()
        time.sleep(6.0)
        s2 = os.path.join(SHOT_DIR, "t17_s5_zh.png")
        drag()
        wait_final(base2)
        screenshot(s2)
        zh_rows = rows_since(base2)
        zh_trans = [r for r in zh_rows if "译文区已切到" in r]
        zh_skip = [r for r in zh_rows if "翻译：跳过" in r]
        frame("S5-1 中文原文（不得有「翻译中…」，应有一条「翻译：跳过」）", s2, base2,
              ("最终原文", "取词成功", "翻译：", "提示框已更新", "译文区已切到"))
        say("  译文区已切到「翻译中…」出现 %d 次（中文必须 0）" % len(zh_trans))
        say("  翻译：跳过 出现 %d 次（中文应 >=1）" % len(zh_skip))
        kill_app()

        # ================= S5-3：目标语言改英语 → 中文必须翻译 =================
        setcfg(True, "", "en")
        base3 = nlines()
        start_app()
        time.sleep(6.0)
        s3 = os.path.join(SHOT_DIR, "t17_s5_zh_en.png")
        drag()
        wait_final(base3)
        screenshot(s3)
        frame("S5-3 目标语言=en，框中文（必须翻译）", s3, base3,
              ("最终原文", "取词成功", "翻译：", "提示框已更新"))
        kill_app()

        # ================= S4-2：翻译关 → 只有原文 + 原文详情 =================
        # 【必须换回英文】上一段是中文，中文会被母语判据跳过，测不出「翻译关」
        set_source(T_GAME)
        setcfg(False, "")
        base4 = nlines()
        start_app()
        time.sleep(6.0)
        s4 = os.path.join(SHOT_DIR, "t17_s4_off.png")
        drag()
        wait_final(base4, want_translate=False)
        screenshot(s4)
        frame("S4-2 翻译关（只有原文 + 原文详情）", s4, base4,
              ("取词成功", "提示框已更新", "翻译："))
        kill_app()

        # ================= S4-3：断网 → 译文位写失败原因，卡片不塌 =================
        # 【必须换回英文】中文会被母语判据跳过，根本不发请求，测不出断网
        set_source(T_GAME)
        setcfg(True, "http://127.0.0.1:5199")
        base5 = nlines()
        start_app()
        time.sleep(6.0)
        s5 = os.path.join(SHOT_DIR, "t17_s4_offline.png")
        drag()
        wait_final(base5)
        screenshot(s5)
        frame("S4-3 断网（译文位写失败原因，布局不漏）", s5, base5,
              ("取词成功", "翻译：", "提示框已更新"))
        kill_app()

        # ================= S4-4 / S6-1：444 字长文（无「可滚动」） =================
        set_source(T_LONG)
        setcfg(True, "")
        base6 = nlines()
        start_app()
        time.sleep(6.0)
        s6 = os.path.join(SHOT_DIR, "t17_s4_long.png")
        drag()
        wait_final(base6)
        screenshot(s6)
        frame("S4-4/S6-1 444 字长文（详情不被挤到底、不得有「可滚动」）", s6, base6,
              ("最终原文", "取词成功", "翻译：", "提示框已更新"))
        huge_rows = rows_since(base6)
        say("  「可滚动」出现 %d 次（必须 0）" % len([r for r in huge_rows if "可滚动" in r]))
        kill_app()

        # ================= S6-2：800 字以上（滚动条正常） =================
        set_source(T_HUGE)
        base7 = nlines()
        start_app()
        time.sleep(6.0)
        s7 = os.path.join(SHOT_DIR, "t17_s6_huge.png")
        drag()
        wait_final(base7)
        screenshot(s7)
        frame("S6-2 800 字以上（原文区滚动条应出现）", s7, base7,
              ("最终原文", "取词成功", "翻译：", "提示框已更新"))
        kill_app()

    finally:
        if src is not None:
            try:
                src.kill()
            except Exception:
                pass
        try:
            kill_app()
        except Exception:
            pass
        io.open(CFG, "w", encoding="utf-8", newline="").write(original)
        d = json.load(io.open(CFG, encoding="utf-8-sig"))
        say("")
        say("配置已还原：enabled=%s lang=%s endpoint=%r keys=%d"
            % (d.get("translateEnabled"), d.get("translateTargetLang"),
               d.get("translateEndpoint"), len(d)))
        io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(OUTL) + "\n")


if __name__ == "__main__":
    main()
