# -*- coding: utf-8 -*-
"""第 22 轮 S4/S5 真机验收：长文本分段的失败隔离与整块缓存。

S4 假服务器第 2 个请求返 403 →
   期望：第 1、3 段成功，第 2 段失败；日志「翻译：段 2/3 …没翻出来」；
        整块仍是「翻译：成功」（不是「翻译：放弃」）；
        详情行末尾「（第 2 段没翻出来）」。
S5 同一段 900+ 字连框两次 →
   期望：第二次「翻译：命中缓存（整块 N 段都命中…）」且卡片立刻出全文。

只用假服务器，因为 S4 必须精确控制「哪一段挂」，真 Bing 做不到。
S1~S3 已经用真 Bing 验过一遍，这里验的是失败路径与缓存路径。
"""
import io, json, os, re, socket, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v0.4.5", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
SERVER = os.path.join(ROOT, "tools", "脚本", "假译服务器.ps1")
SHOT_DIR = os.path.join(ROOT, "tools", "日志", "取证截图")
TMP = os.path.join(ROOT, "tools", "临时")
SRC_TXT = os.path.join(TMP, "l22_source.txt")
OUT = os.path.join(ROOT, "tools", "脚本", "_l22_s45_result.txt")
BOX = ("160", "160", "940", "480")
PORTS = range(5099, 5107)
LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")

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


def kill_all_srv():
    ps = ("Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | "
          "Where-Object { $_.CommandLine -like '*假译服务器.ps1*' } | "
          "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }")
    subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                   capture_output=True, timeout=60)
    time.sleep(0.6)


def start_srv(extra=()):
    kill_all_srv()
    subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                      "-File", SERVER] + list(extra),
                     cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(60):
        time.sleep(0.25)
        for p in PORTS:
            try:
                s = socket.create_connection(("127.0.0.1", p), timeout=0.3)
                s.close()
                return p
            except OSError:
                pass
    raise RuntimeError("假译服务器没起来")


def wait_block(base, deadline=120):
    """等本案例自己的整块结果（成功或放弃）。"""
    t0 = time.time()
    while time.time() - t0 < deadline:
        time.sleep(0.25)
        txt = "\n".join(rows_since(base))
        if "翻译：成功" in txt or "翻译：放弃" in txt:
            return True
    return False


def show(rows, keys):
    for r in rows:
        if any(k in r for k in keys):
            say("  " + r.strip()[:200])


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
        say("源文本 %d 字" % len(LONG_EN))

        # ============================== S4：第 2 段挂
        say("")
        say("===== S4 假服务器第 2 个请求返 403 =====")
        port = start_srv(["-FailAt", "2"])
        say("假服务器端口 = %d" % port)
        kill_app()
        setcfg(True, "http://127.0.0.1:%d" % port, "zh-Hans")
        base = nlines()
        app = subprocess.Popen([EXE], cwd=ROOT)
        time.sleep(6.5)
        drag()
        finished = wait_block(base)
        time.sleep(0.8)
        s4_shot = os.path.join(SHOT_DIR, "t23_s4_partial.png")
        screenshot(s4_shot)
        rows = rows_since(base)
        txt = "\n".join(rows)
        say("日志：")
        show(rows, ("取词成功", "翻译：段", "翻译：成功", "翻译：放弃", "连续"))
        seg3 = [r for r in rows if "翻译：段 3/" in r]
        say("")
        c1 = "翻译：段 2/3" in txt and "没翻出来" in txt
        c2 = "翻译：成功" in txt and "翻译：放弃" not in txt
        c3 = "第 2 段没翻出来" in txt
        c4 = len(seg3) == 1
        say("S4-① 第 2 段有「没翻出来」：%s" % ("PASS" if c1 else "FAIL"))
        say("S4-② 整块仍是成功、没有「翻译：放弃」：%s" % ("PASS" if c2 else "FAIL"))
        say("S4-③ 汇总里写明「第 2 段没翻出来」：%s" % ("PASS" if c3 else "FAIL"))
        say("S4-④ 第 3 段仍继续翻了（连续失败没停手）：%s" % ("PASS" if c4 else "FAIL"))
        if not (c1 and c2 and c3 and c4):
            ok_all = False
        say("S4 截图：%s ｜ 整块结束=%s" % (s4_shot, finished))
        kill_app()
        kill_all_srv()

        # ============================== S5：整块缓存
        say("")
        say("===== S5 同一段连框两次（第二次应整块命中缓存）=====")
        port = start_srv([])
        say("假服务器端口 = %d" % port)
        kill_app()
        setcfg(True, "http://127.0.0.1:%d" % port, "zh-Hans")
        base = nlines()
        app = subprocess.Popen([EXE], cwd=ROOT)
        time.sleep(6.5)
        drag()
        wait_block(base)
        time.sleep(1.0)
        rows1 = rows_since(base)
        txt1 = "\n".join(rows1)
        say("第一次：%s" % ("成功" if "翻译：成功" in txt1 else "!! 没成功"))
        if "翻译：成功" not in txt1:
            ok_all = False

        # 第二次框同一块：卡片要重开（第一次的 3 秒计时器早过了），重新拖一遍
        base2 = nlines()
        t0 = time.time()
        drag()
        finished2 = wait_block(base2)
        elapsed2 = time.time() - t0
        time.sleep(0.6)
        s5_shot = os.path.join(SHOT_DIR, "t23_s5_cache.png")
        screenshot(s5_shot)
        rows2 = rows_since(base2)
        txt2 = "\n".join(rows2)
        say("第二次日志：")
        show(rows2, ("取词成功", "翻译：段", "翻译：成功", "翻译：命中缓存"))
        hit = "整块" in txt2 and "段都命中" in txt2
        # 【量具修正，两次才写对】
        #   第一版断言「没有『翻译：段』行」——错的，段级日志记录的是「这段翻完了」，
        #   缓存命中照样打一条，它不代表发了网络请求。
        #   第二版断言「每段用时 ≤5ms」——也错的：命中时 r.ElapsedMs 是**当初那次联网**
        #   的耗时（TranslationService 命中分支就是这么填的），不是这次。于是 316ms 看着
        #   像又发了请求。产品代码已改成命中时在日志里点明这个数是谁的时间。
        #   真正可判的判据只有一个：整块汇总那条的总用时。命中时它是 0~几 ms，
        #   真联网时它是几百到几千 ms（真 Bing 三次 7683ms、假服务器 300ms 量级）。
        all_wall = [int(m.group(1)) for m in re.finditer(r"翻译：成功，\d+ 字 → 译 \d+ 字，用时 (\d+) ms", txt2)]
        cached_marked = "（缓存命中，这个数是当初那次联网的耗时）" in txt2
        all_wall_ms = max(all_wall) if all_wall else -1
        fast = all_wall_ms >= 0 and all_wall_ms <= 50
        say("")
        say("S5-① 第二次走缓存（日志有「整块 N 段都命中」）：%s" % ("PASS" if hit else "FAIL"))
        say("S5-② 第二次整块用时 %d ms（阈值 ≤50，命中时它才是「这次花了多久」）：%s"
            % (all_wall_ms, "PASS" if fast else "FAIL"))
        say("S5-③ 段级日志已标明命中时那个数是旧耗时：%s" % ("PASS" if cached_marked else "FAIL"))
        say("S5-④ 第二次墙钟 %.2f 秒（从拖拽到整块结束，大头是认字）" % elapsed2)
        if not (hit and fast and cached_marked):
            ok_all = False
        say("S5 截图：%s ｜ 整块结束=%s" % (s5_shot, finished2))

        say("")
        say("总判定：%s" % ("PASS" if ok_all else "FAIL"))
    finally:
        try:
            kill_app()
        except Exception:
            pass
        try:
            kill_all_srv()
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
