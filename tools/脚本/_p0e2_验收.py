# -*- coding: utf-8 -*-
"""P0-E2 卡片高度 A/B：受控长文本 + 先热身再正式框选 + 常驻采样器。"""
import json, os, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(ROOT, "release", "v1.4", "TranslatorApp.exe")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
SAMPLER = os.path.join(ROOT, "tools", "临时", "_p0e2_sampler.ps1")
MOCK = os.path.join(ROOT, "tools", "脚本", "模拟操作.ps1")
LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")


# 常驻采样器（内联，原来叫 tools/临时/_p0e2_sampler.ps1，误删后内嵌以保证可复现）
# 为什么要常驻：卡片只活 3 秒，每 0.5 秒起一次 PowerShell 要 1.5 秒，必然漏拍。
# 为什么要按 PID 枚举：按标题找"框选坐标提示"时，Add-Type 的 C# 里中文关键字
# 会被 ANSI 破坏，一次都匹配不到。
SAMPLER_PY = True  # 采样器改成 Python ctypes 直调 user32，不再起 PowerShell 子进程

def peak_height(pid, seconds=9.0, interval=0.06):
    """常驻轮询卡片高度，返回（峰值, 采样序列）。

    为什么不用 PowerShell 采样器：
      1) 卡片只活 3 秒，每 0.5 秒起一次 PowerShell（启动 1.5 秒）必然漏拍，必须常驻；
      2) 常驻 .ps1 必须带 BOM，write/edit 写出的一律没有，反复踩坑；
      3) .ps1 里 EnumWindows 的回调委托作用域容易出岔子（$script:keep 抓不到）。
    Python 本来就是常驻进程，ctypes 直接调 user32 最省事。
    """
    import ctypes
    from ctypes import wintypes

    u32 = ctypes.WinDLL("user32", use_last_error=True)

    class RECT(ctypes.Structure):
        _fields_ = [("L", ctypes.c_long), ("T", ctypes.c_long),
                    ("R", ctypes.c_long), ("B", ctypes.c_long)]

    WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    u32.IsWindowVisible.argtypes = [wintypes.HWND]
    u32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(RECT)]

    series, last = [], None
    t0 = time.time()
    while time.time() - t0 < seconds:
        found = []

        def cb(hwnd, _lp):
            wpid = wintypes.DWORD()
            u32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
            if wpid.value == pid and u32.IsWindowVisible(hwnd):
                r = RECT()
                if u32.GetWindowRect(hwnd, ctypes.byref(r)):
                    w, h = r.R - r.L, r.B - r.T
                    # 排掉全屏遮罩(1920x1080)、主窗口(480x300)、过小的窗口
                    if 300 < w < 900 and 0 < h < 900 and not (w == 480 and h == 300):
                        found.append("%dx%d" % (w, h))
            return True

        u32.EnumWindows(WNDENUMPROC(cb), 0)
        tok = ",".join(found)
        if tok != last:
            series.append("%.2fs|%s" % (time.time() - t0, tok))
            last = tok
        time.sleep(interval)

    peak = 0
    for s in series:
        for tok in s.split("|")[1].split(","):
            if "x" in tok:
                try:
                    peak = max(peak, int(tok.split("x")[1]))
                except ValueError:
                    pass
    return peak, series

SHOW_PS = r'''
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.Text = 'P0E2Source'
$f.StartPosition = 'Manual'
$f.Left = 100; $f.Top = 100; $f.Width = 900; $f.Height = 420
$f.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$tb = New-Object System.Windows.Forms.TextBox
$tb.Multiline = $true; $tb.ReadOnly = $true; $tb.BorderStyle = 'None'
$tb.Dock = 'Fill'
$tb.Font = New-Object System.Drawing.Font('Consolas', 13)
$tb.ForeColor = [System.Drawing.Color]::White
$tb.BackColor = [System.Drawing.Color]::FromArgb(20,20,26)
$tb.Text = [IO.File]::ReadAllText('{txt}', [Text.Encoding]::UTF8)
$f.Controls.Add($tb)
$f.TopMost = $true
$f.Show()
$f.Activate()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Seconds 200
'''

# 受控长文本：1527 字符英文，译成中文约 1349 字，在 420px 宽下约 19 行 = 340px+，
# 足以超出 TransScroll 的 200px 限高，这样才量得准「限高有没有生效」。
# 内联而不是读 tools/临时/p0e2-long.txt：那个目录被 .gitignore 忽略，脚本单独 checkout 后跑不起来。
LONG_TEXT = (
'The ancient gate stood silent at the edge of the valley, its runes long faded beneath a century of rain. Nobody in the village remembered who had carved them, or why the stone still hummed whenever the moon rose above the northern ridge. Children were warned to keep their distance, and hunters took the long road around the hill even when the snow made it dangerous. Yet on the first night of every winter, a single lantern could be seen moving slowly along the wall, and the old woman who kept the shrine would say nothing at all. Travelers who asked about it were told to mind their own business, that the road was long and the night was cold, and that some questions cost more than they were worth. But a few of them, the curious ones, stayed until morning and swore afterwards that they had heard singing, low and slow, coming from somewhere beneath the stones. The village never confirmed it and never denied it either, which in that part of the country amounts to the same thing. Years passed, the road was widened, the shrine was repaired twice, and still nobody touched the gate. When the last of the old women died her granddaughter found a box of letters written in a hand nobody recognized, all of them addressed to someone who had left before the war and never come back, and every single one of them was dated on the first night of winter. The box was put in the attic with the rest of the things nobody knew what to do with, and there it stayed, gathering dust, waiting for someone with more patience than sense.'
)

def setcfg(on, endpoint):
    d = json.load(open(CFG, encoding="utf-8-sig"))
    d["translateEnabled"] = on
    # v2.0 起有一次性迁移：不带这个标记，程序启动会把翻译强制开回来，
    # 于是「测关掉翻译」这一条永远测不出东西。
    d["translateDefaultOnMigrated"] = True
    d["translateNoticeShown"] = True
    d["translateTargetLang"] = "zh-Hans"
    d["translateEndpoint"] = endpoint
    open(CFG, "w", encoding="utf-8", newline="").write(json.dumps(d, ensure_ascii=False, indent=2))

def select(warm=False):
    """框选。warm=True 时只框上半截，用来热身 OCR 引擎。

    为什么热身要框不同区域：缓存键是「规范化原文」，框同一块的话正式那次会命中缓存，
    详情行多出「这段字刚翻过…」约 14px，污染「译文块撑多高」的测量。
    """
    if warm:
        args = ["150", "150", "950", "260"]
    else:
        args = ["150", "150", "950", "450"]
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MOCK,
                    "-场景", "正常框选", "-x1", args[0], "-y1", args[1],
                    "-x2", args[2], "-y2", args[3]],
                   cwd=ROOT, capture_output=True, timeout=90)

def nlines():
    return len(open(LOG, encoding="utf-8-sig", errors="replace").readlines()) if os.path.exists(LOG) else 0

_baseline = [0]

def newlines():
    """本轮（case 开跑之后）新增的日志行。"""
    if not os.path.exists(LOG):
        return []
    return open(LOG, encoding="utf-8-sig", errors="replace").readlines()[_baseline[0]:]

def case(tag, on, port, baseline):
    _baseline[0] = baseline
    setcfg(on, "http://127.0.0.1:%d" % port)
    app = subprocess.Popen([EXE], cwd=ROOT)
    time.sleep(4.0)
    select(warm=True); time.sleep(4.5)   # 热身 1：OCR 引擎初始化 + 取票
    select(warm=True); time.sleep(3.5)   # 热身 2：线程池就位（只热身一次实测偶发预算不够）
    # 采样要跟框选同时开始，所以先起采样器线程再点框选
    import threading
    box = {}
    th = threading.Thread(target=lambda: box.update(zip(("peak", "series"),
                                                        peak_height(app.pid, 9.0))))
    th.start()
    time.sleep(1.3)
    select()
    th.join(timeout=30)
    peak, series = box.get("peak", 0), box.get("series", [])
    time.sleep(0.5)
    subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
    time.sleep(0.8)

    tr = ""
    if os.path.exists(LOG):
        for ln in open(LOG, encoding="utf-8-sig", errors="replace").readlines()[baseline:]:
            if "翻译：" in ln and ("成功" in ln or "命中缓存" in ln or "放弃" in ln):
                tr = ln.strip()
    print("  %-10s 峰值高度=%d" % (tag, peak))
    for s in series: print("       %s" % s)
    if tr: print("       翻译日志：%s" % tr[-95:])
    return peak

def main():
    # 【别再犯】跑验收会把用户配置改成指向假服务器。开跑前备份、finally 还原，
    # 并逐键校验终态——只 Copy-Item 不校验的话，还原失败也没人知道。
    # （第九轮 P0-B2 就是这条：验收脚本把用户的 translateEnabled/translateEndpoint 改脏了。）
    import shutil
    bak = CFG + ".p0e2-bak"
    had = os.path.exists(CFG)
    original = None
    if had:
        with open(CFG, encoding="utf-8-sig") as f:
            original = f.read()
        shutil.copy2(CFG, bak)

    show = os.path.join(ROOT, "tools", "临时", "_p0e2_show.ps1")
    os.makedirs(os.path.dirname(show), exist_ok=True)
    long_txt = os.path.join(ROOT, "tools", "临时", "p0e2-long.txt")
    open(long_txt, "w", encoding="utf-8", newline="").write(LONG_TEXT)
    open(show, "w", encoding="utf-8-sig", newline="").write(
        SHOW_PS.replace("{txt}", long_txt))
    src = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", show],
                           cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    srv = subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                            os.path.join(ROOT, "tools", "脚本", "假译服务器.ps1")],
                           cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(3.5)
    try:
        print("== P0-E2 卡片高度 A/B（受控 532 字英文，先热身再正式）==")
        a = case("A 翻译=关", False, 5099, nlines())
        # B 组重试：预算偶发不够时卡片上只有「正在翻译…」，量到的是中间态不是译文态。
        # 判据 = 本轮日志里真的出现「翻译：成功」或「命中缓存」，否则重扫一次。
        b = 0
        for attempt in range(1, 4):
            b = case("B 翻译=开(第%d次)" % attempt, True, 5099, nlines())
            got = any(("翻译：" in ln and "成功" in ln) for ln in newlines())
            if got:
                break
            print("       （本轮没拿到译文，重扫）")
        print("")
        print("峰值高度 关=%d 开=%d 增 %d px" % (a, b, b - a))
        print("断言1 译文真画上去(开>关)：%s" % ("PASS" if b > a else "FAIL"))
        # 限高口径：译文块最多贡献 200(MaxHeight) + 1(分隔线) + 12(上下 margin) = 213px。
        # 不加限高的话，1349 字译文在 420px 宽下约 19 行 × 18px = 340px+，卡片会到 900 以上。
        delta = b - a
        print("断言2 限高生效(增量 <= 220px)：%s（增量 %d）"
              % ("PASS" if 0 < delta <= 220 else "FAIL", delta))
    finally:
        subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
        src.kill(); srv.kill()
        # 还原 + 逐键校验
        if had and original is not None:
            with open(CFG, "w", encoding="utf-8", newline="") as f:
                f.write(original)
            if os.path.exists(bak):
                os.remove(bak)
            now = json.load(open(CFG, encoding="utf-8-sig"))
            want = json.loads(original)
            if now == want:
                print("RESTORED-OK 配置与开跑前逐键一致（%d 键）" % len(now))
            else:
                diff = {k: (want.get(k), now.get(k)) for k in set(want) | set(now)
                        if want.get(k) != now.get(k)}
                print("FAIL 配置没还原：%s" % diff)
                sys.exit(1)

if __name__ == "__main__":
    main()
