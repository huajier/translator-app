# -*- coding: utf-8 -*-
"""第 24 轮 S3 单独重跑：长文本分段期间，底部那行必须是蓝色「翻译中」且在跳。

【为什么要单独一个脚本】第一次跑 _l24.py 时连拍区域是写死的 (940,150,500,760)，
  但长文本卡片会被 PlaceBesideSelection 摆到别处 —— 拍到的根本不是卡片，
  连拍 8 帧像素一模一样（块数恒 10），断言「在跳」自然失败。
  正确做法：先整屏抓一张，用蓝色边框定位卡片矩形，再按这个矩形连拍。
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
OUT = os.path.join(ROOT, "tools", "脚本", "_l24_s3_result.txt")
LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")
BOX = ("160", "160", "940", "480")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _grab import grab_to_png
from _crop import load_png, crop_zoom

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


def is_blue(px):
    return abs(px[0] - 56) < 30 and abs(px[1] - 189) < 30 and abs(px[2] - 248) < 30


def find_card(path):
    """用蓝色边框定位卡片矩形。

    【2026-11-06 踩的坑】第一版把「蓝色像素最多的那一行」当成左右边框所在行，
      然后取 count>85%*max 的行当上下边框 —— 但卡片边框只有 430px 宽，
      整屏 1920px 下那一行的计数就是最大值本身，没有任何其他行能到 85%，
      于是 bottom == top，「卡片」高度被算成 1 像素，连拍全是 430×1。
      正确做法：先用最宽的那一行定出卡片的 x 跨度（那就是上边框），
      再在这个 x 跨度里往下找最后一条「几乎铺满整个跨度」的行 = 下边框。
    """
    w, h, bpp, rows = load_png(path)
    rowcnt = [sum(1 for x in range(w) if is_blue(rows[y][x * bpp:x * bpp + 3])) for y in range(h)]
    mx = max(rowcnt) if rowcnt else 0
    if mx < 200:
        return None
    top = rowcnt.index(mx)
    xs = [x for x in range(w) if is_blue(rows[top][x * bpp:x * bpp + 3])]
    left, right = xs[0], xs[-1]
    span = right - left + 1
    bottom = top
    for y in range(top, h):
        c = sum(1 for x in range(left, right + 1) if is_blue(rows[y][x * bpp:x * bpp + 3]))
        if c > span * 0.8:
            bottom = y
    return (left, top, span, bottom - top + 1)


def grab_card(full_path, card, dst):
    """从整屏图里裁出卡片区域再存一张（卡片可能超出屏幕底边，裁到屏幕底为止）。

    【为什么不直接 grab() 一个矩形】BitBlt 抓超屏区域会得到黑边；
      直接从已抓的整屏图裁更准，也不用再拍一次（再拍一次就不是同一帧了）。
    """
    crop_zoom(full_path, dst, card, 1)


def hint_stats(path):
    """返回 (块数, 蓝色像素数, 提示行 y 范围)。

    【2026-11-06 踩的坑：卡片会超出屏幕底部】
      长文本卡片高度能到 900+，顶边 y=160，底边直接跑出屏幕（1080）。
      所以「找下边框」这条路根本走不通 —— 根本看不见。
      改成：在卡片正文的 x 范围内，从下往上找**最后一行有蓝字的**，
      那就是「翻译中.」那一行（它永远是卡片最底下的一行字）。
    """
    w, h, bpp, rows = load_png(path)

    # 上边框（横贯全宽）定出卡片左右
    rowcnt = [sum(1 for x in range(w) if is_blue(rows[y][x * bpp:x * bpp + 3])) for y in range(h)]
    mx = max(rowcnt) if rowcnt else 0
    if mx < 100:
        return 0, 0, (0, 0)
    top = rowcnt.index(mx)
    xs = [x for x in range(w) if is_blue(rows[top][x * bpp:x * bpp + 3])]
    left, right = xs[0] + 6, xs[-1] - 6

    last = -1
    for y in range(h - 1, top + 20, -1):
        r = rows[y]
        c = sum(1 for x in range(left, right) if is_blue(r[x * bpp:x * bpp + 3]))
        if c >= 3:          # 至少三个蓝像素才算一行字，边框残影挡掉
            last = y
            break
    if last < 0:
        return 0, 0, (0, 0)
    y0, y1 = max(0, last - 16), min(h, last + 3)
    cols = set()
    for y in range(y0, y1):
        r = rows[y]
        for x in range(left, right):
            if is_blue(r[x * bpp:x * bpp + 3]):
                cols.add(x)
    if not cols:
        return 0, 0, (y0, y1)
    ss = sorted(cols)
    blocks, lastx = 1, ss[0]
    for x in ss[1:]:
        if x - lastx > 3:
            blocks += 1
        lastx = x
    return blocks, len(cols), (y0, y1)


def nlines():
    return len(io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()) if os.path.exists(LOG) else 0


def rows_since(base):
    return io.open(LOG, encoding="utf-8-sig", errors="replace").readlines()[base:]


def setcfg(on, endpoint, lang="zh-Hans"):
    d = json.load(io.open(CFG, encoding="utf-8-sig"))
    d["translateEnabled"] = on
    d["translateDefaultOnMigrated"] = True
    d["translateNoticeShown"] = True
    d["translateTargetLang"] = lang
    d["translateEndpoint"] = endpoint
    io.open(CFG, "w", encoding="utf-8", newline="").write(json.dumps(d, ensure_ascii=False, indent=2) + "\n")


def wait_for(base, keys, deadline=120):
    t0 = time.time()
    while time.time() - t0 < deadline:
        time.sleep(0.06)
        txt = "\n".join(rows_since(base))
        for k in keys:
            if k in txt:
                return k, time.time() - t0
    return None, time.time() - t0


def main():
    io.open(SRC_TXT, "w", encoding="utf-8", newline="").write(LONG_EN)
    if not os.path.exists(SHOW_PS):
        say("缺 %s，先跑一次 _l24.py" % SHOW_PS)
        return
    subprocess.Popen(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", SHOW_PS],
                     cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(3.0)
    backup = io.open(CFG, encoding="utf-8-sig").read()
    try:
        subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
        time.sleep(1.2)
        setcfg(True, "", "zh-Hans")
        subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(6.0)
        base = nlines()
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", MOCK,
                        "-场景", "正常框选", "-x1", BOX[0], "-y1", BOX[1],
                        "-x2", BOX[2], "-y2", BOX[3]], cwd=ROOT, capture_output=True, timeout=120)
        hit, dt = wait_for(base, ["翻译：段 1/"], deadline=90)
        say("段 1 落地：%s（%.2f 秒）" % (hit, dt))
        time.sleep(0.2)
        full = os.path.join(TMP, "_s3_full.png")
        grab_to_png(full)
        card = find_card(full)
        say("卡片矩形：%s" % (card,))
        if card is None:
            say("S3：FAIL（没定位到卡片）")
            return
        seq = []
        paths = []
        # 卡片会超出屏幕底边 → 每帧抓「卡片左边界到屏幕底」这一条，别用卡片高度
        band = (card[0], card[1], card[2], 1080 - card[1])
        say("连拍区域：%s（卡片顶到底边，含超出屏幕的下半截）" % (band,))
        for i in range(8):
            p = os.path.join(SHOT_DIR, "t25_s3_frame%d.png" % i)
            grab_to_png(p, left=band[0], top=band[1], width=band[2], height=band[3])
            paths.append(p)
            time.sleep(0.05)
        for i, p in enumerate(paths):
            n, cnt, yband = hint_stats(p)
            seq.append((i, n, cnt, yband))
        say("连拍 8 帧（块数/蓝色像素数/扫描带）：")
        for row in seq:
            say("  #%d 块数=%d 像素=%d 带=%s" % row)
        vals = sorted(set(x[1] for x in seq))
        say("块数取值集合：%s" % vals)
        txt = "\n".join(rows_since(base))
        say("期间出现「正在翻译剩余段落」：%s" % ("是" if "正在翻译剩余段落" in txt else "否"))
        hit2, dt2 = wait_for(base, ["翻译：成功", "翻译：放弃"], deadline=180)
        say("整块结果：%s（%.2f 秒）" % (hit2, dt2))
        time.sleep(0.7)
        grab_to_png(os.path.join(SHOT_DIR, "t25_s3_done.png"),
                    left=band[0], top=band[1], width=band[2], height=band[3])
        say("已截 t25_s3_done.png")
        # 放大中间帧，供人/识图看
        mid = paths[len(paths) // 2]
        crop_zoom(mid, os.path.join(TMP, "_s3_mid_x3.png"), (0, 0, band[2], band[3]), 3)
        ok = (hit2 == "翻译：成功" and "正在翻译剩余段落" not in txt and len(vals) >= 2)
        say("S3：%s" % ("PASS" if ok else "FAIL"))
    finally:
        io.open(CFG, "w", encoding="utf-8", newline="").write(backup)
        subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-Process powershell -ErrorAction SilentlyContinue | "
                        "Where-Object { $_.MainWindowTitle -eq 'L24Source' } | "
                        "Stop-Process -Force -ErrorAction SilentlyContinue"],
                       capture_output=True, timeout=60)
        say("配置已还原（%d 键）" % len(json.load(io.open(CFG, encoding="utf-8-sig"))))
        io.open(OUT, "w", encoding="utf-8", newline="").write("\n".join(OUTL) + "\n")


main()
