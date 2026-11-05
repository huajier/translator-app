# -*- coding: utf-8 -*-
"""第 17 轮 S4/S5 真机验收（v0.4.0）。

S4 卡片滚动条改深色：900 字以上英文（原文区滚动条真出现）+ 中文长文（译文区滚动条）。
S5 回归：目标语言选「日语」→ 重启仍是日语；勾选翻译 → 托盘勾选态跟着变。

真机路径（不用 --回放：回放不建卡片，量不到界面）。
沿用 _t3b_s456.py：一个受控窗口常驻 + 500ms 定时器重读文件，换案例只改文件内容。
"""
import io, json, os, subprocess, time, threading

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v0.4.0", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
UIA = os.path.join(ROOT, "tools", "脚本", "_t3b_uia.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
TMP = os.path.join(ROOT, "tools", "临时")
SRC_TXT = os.path.join(TMP, "t18_source.txt")
OUT = os.path.join(ROOT, "tools", "脚本", "_t18_result.txt")
BOX = ("160", "160", "940", "480")
LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")

# 900 字以上英文（S4 要求），段落之间空一行，读起来像真文本。
T_HUGE_EN = (
    "The caravan reached the northern pass three days late, and the gatekeeper "
    "refused to open the gate until the merchant produced a seal he had lost in "
    "the river. They argued for hours in the rain while the mules stood shivering, "
    "and by evening the whole village had gathered to watch a stranger explain, "
    "over and over, why a man with no seal and no papers and no letter from the "
    "capital still deserved a warm bed and a bowl of soup for the night.\n\n"
    "Before dawn the gatekeeper relented, but only after the merchant agreed to "
    "leave his youngest mule behind as a pledge, and to return within the month "
    "with the seal or the money to buy the animal twice over. The stranger walked "
    "through the gate at first light with his head down, and the village talked "
    "about him for a week, mostly about the mule. Nobody ever saw him again, and "
    "the gatekeeper kept the mule, which turned out to be a very good mule, and "
    "for years afterwards people told the story as if it had happened to them.\n\n"
    "Years passed. The pass road grew wider, then narrower again, and the inn at "
    "the bottom of the hill changed hands four times. In the year the old bridge "
    "finally fell, a woman with a ledgers case came asking after the gatekeeper, "
    "and was told that he had died in his bed at a great age, still holding the "
    "mule's halter, and that the mule had outlived him by a full winter and had "
    "been buried, at his written request, in the orchard where the light fell "
    "longest in the afternoon."
)

# S4-2 要验的是「译文区那条滚动条也是深色」。两个坑都踩过了：
#   ① 目标语言是 zh-Hans，中文原文会被母语判据跳过 —— 界面上根本没有译文区；
#   ② 拿 1070 字英文去翻中文，真 Bing 要 4894 ms，撞 5 秒硬闸，译文区同样不出现。
# 解法：**把目标语言临时改成 en，框中文长文**。
#   · 中文约 600 字 → 英文译文约 1000 字符 → 420px 宽每行约 64 字符 → 16 行 × 14px ≈ 224px，
#     超过 TransScroll 的 MaxHeight=200，滚动条必然出现；
#   · 中文→英文比英文→中文快（源文本字符数少），5 秒硬闸够用。
T_LONG_ZH = (
    "商队晚到了三天，守门人不开门，除非商人拿出一枚丢在河里的印章。"
    "他们在大雨里争了几个钟头，骡子站在旁边发抖，到傍晚全村的人都围过来看一个陌生人"
    "一遍又一遍地解释：没有印章、没有文书、京城也没有信来的人，凭什么还能讨到一张暖床和一碗热汤。"
    "天没亮的时候守门人让步了，条件是商人把最小的那头骡子留下来作抵押，一个月之内带着印章"
    "或者双倍的钱回来赎。陌生人低着头走过城门，村里人议论了他一个星期，议论的大多是那头骡子。"
    "再没有人见过他。守门人留下了骡子，那确实是一头好骡子；此后很多年，人们讲起这件事，"
    "讲得像是自己亲身经历过一样。后来路过的人总要问起城门口那块没字的石头，老人们就指一指"
    "山坡上的果园，说那边埋着一头骡子，别的什么也不肯多讲。"
)

SHOW_PS = r"""
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$txtPath = '{txt}'
$f = New-Object System.Windows.Forms.Form
$f.Text = 'T18Source'
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


def setcfg(on=True, endpoint="", lang="zh-Hans", notice=True):
    d = json.load(io.open(CFG, encoding="utf-8-sig"))
    d["translateEnabled"] = on
    d["translateDefaultOnMigrated"] = True
    d["translateNoticeShown"] = notice
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
            say("  " + r.strip()[:190])


def wait_final(base, want_translate=True, delay=0.9, deadline=150):
    """等本案例自己的结果落定（必须从 base 起数，否则前面案例的日志会骗过它）。"""
    t0 = time.time()
    while time.time() - t0 < deadline:
        time.sleep(0.3)
        txt = "\n".join(rows_since(base))
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
    say("== %s ==" % title)
    say("截图 %s" % shot)
    show(rows_since(base), keys)


def uia(step, val=""):
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-File", UIA, "-Step", step, "-Value", val],
                       cwd=ROOT, capture_output=True, text=True, timeout=180,
                       errors="replace")
    return ((r.stdout or "") + (r.stderr or "")).strip().replace("\n", " | ")


def main():
    original = io.open(CFG, encoding="utf-8-sig").read()
    os.makedirs(SHOT_DIR, exist_ok=True)
    src = None
    try:
        # ================= S4-1：900 字以上英文（原文区滚动条） =================
        set_source(T_HUGE_EN)
        ps1 = os.path.join(TMP, "t18_show.ps1")
        io.open(ps1, "w", encoding="utf-8-sig", newline="").write(
            SHOW_PS.replace("{txt}", SRC_TXT))
        src = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                                "-File", ps1],
                               cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(5.0)

        kill_app()
        setcfg(True, "")
        base = nlines()
        start_app()
        time.sleep(6.5)
        s = os.path.join(SHOT_DIR, "t18_s4_en_scroll.png")
        drag()
        wait_final(base)
        screenshot(s)
        frame("S4-1 900+ 字英文（原文区滚动条必须出现）", s, base,
              ("最终原文", "取词成功", "翻译：", "提示框已更新"))

        # ================= S4-2：中文长文 + 目标 en（译文区滚动条） =================
        set_source(T_LONG_ZH)
        kill_app()
        setcfg(True, "", "en")
        time.sleep(0.5)
        base2 = nlines()
        start_app()
        time.sleep(6.5)
        s2 = os.path.join(SHOT_DIR, "t18_s4_zh_scroll.png")
        drag()
        wait_final(base2)
        screenshot(s2)
        frame("S4-2 中文长文 + 目标 en（译文区滚动条必须出现）", s2, base2,
              ("最终原文", "取词成功", "翻译：", "提示框已更新"))

        # ================= S5-1：目标语言选日语 → 重启仍是日语 =================
        kill_app()
        setcfg(True, "", "zh-Hans")
        start_app()
        time.sleep(6.5)
        say("")
        say("== S5-1 目标语言选日语 ==")
        say("  " + uia("lang-select", "日语")[:150])
        d = json.load(io.open(CFG, encoding="utf-8-sig"))
        say("  配置 translateTargetLang = %r" % d.get("translateTargetLang"))
        kill_app()
        start_app()
        time.sleep(6.5)
        say("  重启后 " + uia("read-main")[:150])

        # ================= S5-2：勾选翻译 → 托盘勾选态跟着变 =================
        say("")
        say("== S5-2 托盘勾选态 ==")
        say("  " + uia("tray-shot", os.path.join(SHOT_DIR, "t18_s5_tray.png"))[:150])
        time.sleep(0.8)
        screenshot(os.path.join(SHOT_DIR, "t18_s5_tray.png"))
        d = json.load(io.open(CFG, encoding="utf-8-sig"))
        say("  配置 translateEnabled = %r" % d.get("translateEnabled"))
        uia("tray-close")
    finally:
        if src is not None:
            try:
                src.kill()
            except Exception:
                pass
        kill_app()
        io.open(CFG, "w", encoding="utf-8", newline="").write(original)
        say("")
        say("配置已还原")
        io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(OUTL) + "\n")


if __name__ == "__main__":
    main()
