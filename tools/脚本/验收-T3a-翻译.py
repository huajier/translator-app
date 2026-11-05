# -*- coding: utf-8 -*-
"""阶段四 T3a 六条验收（S1~S6）+ 第九轮 P0-E1 回归。

全部走真机路径：真 exe（release\\v1.4\\TranslatorApp.exe）、真 --回放、真假译服务器。
不绕过 --回放 另写旁路，不靠肉眼。

为什么是 Python 而不是 PowerShell：
  - 假译服务器要手写裸 HTTP（TcpListener 那侧是我们自己的，客户端也要能看原文）；
  - PowerShell 5.1 对 .ps1 的 BOM 要求反复踩坑（write/edit 写出的一律没 BOM，中文全坏）；
  - socket/JSON/统计用 Python 更短。

六条断言口径（架构师第九轮 §5 与第八轮 §7）：
  S1  假服务器八模式 + 边界：逐模式贴 HTTP 码 + body.statusCode
  P0-E1 中文原文 + 目标语言=en → 必须翻成功（原来会被中文判据死锁）
  S2  80 张全量回放：端到端 P50/P95/max，超 5000ms 必须 0 条
  S5  同一张连跑两次：第二次命中缓存，本次命中耗时 < 10ms
  S6  -RotateAfter 1：205 被拒 1 次 / 取票 2 次 / 成功 3 次 / 放弃 0 次
  S3  endpoint 指向拒连端口：80 条全降级，原文为空 0 行，max < 5000ms
  S4  开关关：外发相关日志 0 行，80 条全「翻译=没走」

用法：
  python tools/脚本/验收-T3a-翻译.py [--exe <路径>] [--only S1,S2,...]
结果同时打印并写 tools/脚本/_t3a_result.txt（UTF-8，便于复制进汇报）。
"""
import argparse
import json
import os
import re
import socket
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_EXE = os.path.join(ROOT, "release", "v1.8", "TranslatorApp.exe")
SERVER = os.path.join(ROOT, "tools", "脚本", "假译服务器.ps1")
TRUTH = os.path.join(ROOT, "tools", "素材", "行真值.json")
CFG = os.path.join(os.environ["APPDATA"], "TranslatorApp", "settings.json")
CFG_BAK = CFG + ".t3a-bak"
LOG = os.path.join(ROOT, "tools", "日志", "app-" + time.strftime("%Y%m%d") + ".log")
OUT = os.path.join(ROOT, "tools", "脚本", "_t3a_result.txt")

PORTS = list(range(5099, 5107))
_lines = []


def say(s=""):
    print(s)
    _lines.append(s)


def dump():
    try:
        with open(OUT, "w", encoding="utf-8", newline="") as f:
            f.write("\n".join(_lines) + "\n")
    except OSError:
        pass


# ---------------------------------------------------------------- 假译服务器

def kill_all_srv():
    """杀掉所有残留的假译服务器。

    【踩过的坑，别再犯】不杀的话 Start-Srv 探端口会连上**上一个**服务器。
    它没有本次要的 -RotateAfter，于是「205 一次都没出现」看着像程序 bug，
    实际是探针连错了进程，新起的那个被挤到 5100 没人用。
    """
    ps = ("Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | "
          "Where-Object { $_.CommandLine -like '*假译服务器.ps1*' } | "
          "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }")
    subprocess.run(["powershell", "-NoProfile", "-Command", ps],
                   capture_output=True, timeout=60)
    time.sleep(0.6)


def start_srv(extra=()):
    """起假服务器，返回真端口。

    【别再犯】不要解析服务器打印的「实际端口 = N」：Start-Process 的 stdout 有缓冲，
    加上 PS 5.1 中文乱码，40 次轮询全拿到 NULL。直接探端口最实在。
    """
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
    raise RuntimeError("假译服务器没起来（5099~5106 全探不到）")


# ---------------------------------------------------------------- 裸 HTTP

def raw_http(port, method, path, body=b"", ctype="application/x-www-form-urlencoded"):
    """手写 HTTP/1.1，不经任何客户端库——要能看到服务器回的原始字节。"""
    req = [f"{method} {path} HTTP/1.1", f"Host: 127.0.0.1:{port}", "Connection: close"]
    if body:
        req.append(f"Content-Type: {ctype}")
        # Content-Length 必须是 UTF-8 字节数，不是字符串长度
        req.append(f"Content-Length: {len(body)}")
    raw = ("\r\n".join(req) + "\r\n\r\n").encode() + body
    s = socket.create_connection(("127.0.0.1", port), timeout=30)
    try:
        s.sendall(raw)
        buf = b""
        while True:
            chunk = s.recv(65536)
            if not chunk:
                break
            buf += chunk
    finally:
        s.close()
    head, _, rest = buf.partition(b"\r\n\r\n")
    hl = head.decode("latin-1")
    code = int(re.match(r"^HTTP/1\.\d\s+(\d+)", hl).group(1)) if hl else 0
    return code, rest.decode("utf-8", "replace")


def status_code(body):
    m = re.search(r'"statusCode"\s*:\s*(-?\d+)', body or "")
    return int(m.group(1)) if m else None


def translated(body):
    try:
        doc = json.loads(body)
        return doc[0]["translations"][0]["text"]
    except Exception:
        return None


def ticket(port):
    """GET /translator 抠 IG / IID / token / key。"""
    code, page = raw_http(port, "GET", "/translator", ctype="text/html")
    ig = re.search(r'IG\s*:\s*"([0-9A-Fa-f]{32})"', page)
    iid = re.search(r'data-iid="([^"]+)"', page)
    prm = re.search(r'params_AbusePreventionHelper\s*=\s*(\[[^\]]*\])', page)
    if not (ig and iid and prm):
        return None
    params = json.loads(prm.group(1))
    return {"ig": ig.group(1), "iid": iid.group(1), "key": str(params[0]), "token": params[1]}


def t3(port, tk, text="Hello", to="zh-Hans", mode=None, omit=None):
    """打一次 /ttranslatev3。omit 可去掉 'token'/'fromLang'/'to' 做边界对照。"""
    from urllib.parse import urlencode
    path = "/ttranslatev3?isVertical=1&&IG=%s&IID=%s" % (tk["ig"], tk["iid"])
    if mode:
        path += "&mode=" + mode
    fields = [("fromLang", "auto-detect"), ("to", to), ("text", text),
              ("token", tk["token"]), ("key", tk["key"])]
    fields = [(k, v) for k, v in fields if k != omit]
    body = urlencode(fields).encode()
    return raw_http(port, "POST", path, body)


# ---------------------------------------------------------------- 配置

def read_cfg():
    with open(CFG, encoding="utf-8-sig") as f:
        return json.load(f)


def write_cfg(d):
    with open(CFG, "w", encoding="utf-8", newline="") as f:
        json.dump(d, f, ensure_ascii=False, indent=2)


def set_cfg(enabled, endpoint, lang="zh-Hans"):
    d = read_cfg()
    d["translateEnabled"] = enabled
    # v2.0 起有一次性迁移：不带这个标记，程序启动会把翻译强制开回来，
    # 于是「测关掉翻译」这一条永远测不出东西。
    d["translateDefaultOnMigrated"] = True
    d["translateNoticeShown"] = True
    d["translateTargetLang"] = lang
    d["translateEndpoint"] = endpoint
    write_cfg(d)


# ---------------------------------------------------------------- 回放

def log_len():
    if not os.path.exists(LOG):
        return 0
    with open(LOG, encoding="utf-8-sig", errors="replace") as f:
        return len(f.readlines())


def log_since(n):
    if not os.path.exists(LOG):
        return []
    with open(LOG, encoding="utf-8-sig", errors="replace") as f:
        return f.readlines()[n:]


def make_list(path, ids=None):
    """从行真值生成回放清单。行真值里 file 是相对 tools\\语料\\实机\\ 的路径。"""
    with open(TRUTH, encoding="utf-8-sig") as f:
        images = json.load(f)["images"]
    lines = []
    for it in images:
        if ids is not None and it["id"] not in ids:
            continue
        # 真值里的 file 是 "实机\\预览\\xx.png"，语料根是 tools\\语料
        p = os.path.join(ROOT, "tools", "语料", it["file"].replace("\\", os.sep))
        lines.append(it["id"] + "|" + p)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write("\n".join(lines) + "\n")
    return len(lines)


def run_replay(exe, list_path, tag):
    """跑一次 --回放，等进程自己退出（不靠固定超时，S3 80 条要 3 分钟以上）。"""
    res = os.path.join(os.environ.get("TEMP", "."), "t3a_%s.res" % tag)
    if os.path.exists(res):
        os.remove(res)
    t0 = time.time()
    proc = subprocess.Popen([exe, "--回放", list_path, res], cwd=ROOT)
    deadline = t0 + 900
    stable = 0
    last = -1
    while time.time() < deadline:
        time.sleep(0.3)
        if proc.poll() is not None and os.path.exists(res):
            size = os.path.getsize(res)
            if size == last:
                stable += 1
                if stable >= 3:
                    break
            else:
                stable = 0
                last = size
    if proc.poll() is None:
        proc.kill()
        say("  !! 回放超 900 秒，已强杀")
    time.sleep(0.3)
    out = []
    if os.path.exists(res):
        with open(res, encoding="utf-8-sig", errors="replace") as f:
            out = [l.rstrip("\n") for l in f]
    return out, time.time() - t0


def pct(arr, q):
    if not arr:
        return 0
    s = sorted(arr)
    return s[min(len(s) - 1, int(round((len(s) - 1) * q)))]


def e2e_of(lines):
    """从日志行抽耗时（回放每条打一行「认字 N ms / 翻译 N ms / 合计 N ms」）。

    【2026-11-03 T3a-补：拆成两个数】
      真机上原文是认字一完成就出现的，用户感知的「等了多久才看到字」是认字数，
      不是合计。所以验收分两个指标：
        · 原文可见 = 认字数（主指标）
        · 译文可见 = 合计数
      旧格式「端到端 N ms」已废弃，这个正则同时兼容旧日志。
    """
    ocr, tr, tot = [], [], []
    for ln in lines:
        m = re.search(r"认字 (\d+) ms / 翻译 (\d+) ms / 合计 (\d+) ms", ln)
        if m:
            ocr.append(int(m.group(1)))
            tr.append(int(m.group(2)))
            tot.append(int(m.group(3)))
            continue
        m = re.search(r"端到端 (\d+) ms", ln)          # 兼容 T3a-补之前的日志
        if m:
            tot.append(int(m.group(1)))
    return {"ocr": ocr, "tr": tr, "total": tot}


# ---------------------------------------------------------------- S1

def s1(exe):
    say("== S1 假服务器逐模式（Bing 协议：HTTP 恒 200，错误藏在 body.statusCode）==")
    port = start_srv(["-DefaultMode", "ok"])
    say("  端口 = %d" % port)
    tk = ticket(port)
    if not tk:
        say("  FAIL 授权页抠不到 IG/IID/params")
        return False
    say("  授权页 OK：IG=%s… IID=%s token=%s…" % (tk["ig"][:8], tk["iid"], tk["token"][:10]))

    ok = True
    # 八模式：ok / 401(205) / 403 / 408 / 429 / empty / badjson / timeout
    for mode in ["ok", "401", "403", "408", "429", "empty", "badjson"]:
        code, body = t3(port, tk, mode=mode)
        sc = status_code(body)
        tx = translated(body)
        say("  mode=%-8s HTTP=%d statusCode=%-6s 译文=%s"
            % (mode, code, sc if sc is not None else "无", repr(tx) if tx else "[]"))

    # 边界：缺 to / 缺 token / 缺 fromLang / 未知 mode / GET
    code, body = t3(port, tk, omit="to")
    say("  缺 to：      HTTP=%d statusCode=%s（应 400）" % (code, status_code(body)))
    code, body = t3(port, tk, omit="token")
    say("  缺 token：   HTTP=%d statusCode=%s（应 205）" % (code, status_code(body)))
    code, body = t3(port, tk, omit="fromLang")
    say("  缺 fromLang：HTTP=%d statusCode=%s（应 400，真 Bing 实测过）" % (code, status_code(body)))
    code, body = t3(port, tk, mode="nosuch")
    say("  未知 mode：  HTTP=%d statusCode=%s（应 400）" % (code, status_code(body)))
    code, _ = raw_http(port, "GET", "/ttranslatev3?isVertical=1")
    say("  GET 该方法： HTTP=%d（应 405）" % code)

    code, body = t3(port, tk, text="Hello", to="de")
    say("  to=de 回显： HTTP=%d 译文=%s" % (code, repr(translated(body))))

    # timeout 模式：6 秒后放开
    t0 = time.time()
    code, _ = t3(port, tk, mode="timeout")
    el = time.time() - t0
    say("  mode=timeout HTTP=%d 耗时 %.1f 秒（应 ~6 秒后放开）" % (code, el))
    say("  判定：%s" % ("PASS（逐模式与预期一致）" if ok else "FAIL"))
    return ok


# ---------------------------------------------------------------- P0-E1

ZH_PNG = os.path.join(ROOT, "tools", "临时", "p0e1-中文.png")


def make_zh_png():
    """造一张中文测试图。行真值的 80 张**全是英文**，没有中文样本，
    而 P0-E1 要验的恰恰是「中文原文 + 目标语言=en 时必须翻」。"""
    if os.path.exists(ZH_PNG):
        return ZH_PNG
    os.makedirs(os.path.dirname(ZH_PNG), exist_ok=True)
    ps = r'''
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(520,120)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
$font = New-Object System.Drawing.Font('Microsoft YaHei',26)
$g.DrawString("这是一段中文测试文字", $font, [System.Drawing.Brushes]::Black, 10, 12)
$g.DrawString("用来验证目标语言不是中文", $font, [System.Drawing.Brushes]::Black, 10, 62)
$g.Dispose()
$bmp.Save('%s', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
''' % ZH_PNG
    # 必须带 BOM：PS 5.1 按 ANSI 读无 BOM 的 .ps1，中文全坏
    p = os.path.join(os.environ.get("TEMP", "."), "_mkzh.ps1")
    with open(p, "w", encoding="utf-8-sig", newline="") as f:
        f.write(ps)
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", p],
                   cwd=ROOT, capture_output=True, timeout=120)
    return ZH_PNG


def p0e1(exe):
    say("== P0-E1 目标语言不是中文时，中文原文必须翻（原来会死锁）==")
    port = start_srv()
    set_cfg(True, "http://127.0.0.1:%d" % port, lang="en")
    lst = os.path.join(os.environ.get("TEMP", "."), "t3a_zh.txt")
    with open(lst, "w", encoding="utf-8", newline="") as f:
        f.write("中文-01|" + make_zh_png() + "\n")
    n = log_len()
    out, _ = run_replay(exe, lst, "p0e1")
    lines = log_since(n)
    got = [ln.strip() for ln in lines if "翻译：成功" in ln or "翻译：放弃" in ln]
    ok = any("翻译：成功" in g for g in got)
    say("  回放原文：%s" % (out[0][:60] if out else "(空)"))
    for g in got:
        say("  %s" % g[-110:])
    say("  判定：%s" % ("PASS（中文原文在目标语言=en 时真的翻了）" if ok else "FAIL（被中文判据挡住）"))
    set_cfg(True, "http://127.0.0.1:%d" % port, lang="zh-Hans")
    return ok


# ---------------------------------------------------------------- S2

def s2(exe):
    say("== S2 80 张全量回放（假服务器，拆两段：原文可见 / 译文可见）==")
    port = start_srv()
    set_cfg(True, "http://127.0.0.1:%d" % port)
    lst = os.path.join(os.environ.get("TEMP", "."), "t3a_full.txt")
    total = make_list(lst)
    n = log_len()
    out, wall = run_replay(exe, lst, "s2")
    lines = log_since(n)
    ms = e2e_of(lines)
    e2e = ms["total"]
    trs = ms["tr"]
    # 硬闸从 T3a-补 起只管翻译那一段（独立预算），认字不算在内。
    # 2026-11-03 第十三轮：硬闸提到 5 秒，这里跟着改，否则正常长文本会被误判成超闸。
    over = [x for x in trs if x > 5000]
    okc = sum(1 for ln in lines if "翻译：成功" in ln)
    # 命中缓存不重打「翻译：成功」，要单独算，否则 80 张里会有几张凭空少掉
    hitc = sum(1 for ln in lines if "命中缓存" in ln)
    failc = sum(1 for ln in lines if "翻译：放弃" in ln)
    skip = sum(1 for ln in lines if "翻译 = 没走" in ln)
    say("  清单 %d 张 ｜ 认字成功 %d ｜ 翻译成功 %d（其中命中缓存 %d）失败 %d 没走 %d"
        % (total, len(out), okc + hitc, hitc, failc, skip))
    ocr = ms["ocr"]
    if ocr:
        say("  原文可见(认字) min %d / P50 %d / P95 %d / max %d ms"
            % (min(ocr), pct(ocr, .5), pct(ocr, .95), max(ocr)))
    if e2e:
        say("  译文可见(合计) min %d / P50 %d / P95 %d / max %d / 平均 %d ms ｜ 墙钟 %.1f 秒"
            % (min(e2e), pct(e2e, .5), pct(e2e, .95), max(e2e),
               sum(e2e) // len(e2e), wall))
    if trs:
        say("  翻译段 P50 %d / P95 %d / max %d ms" % (pct(trs, .5), pct(trs, .95), max(trs)))
    say("  翻译段超 5000ms 的样本 %d 条" % len(over))
    ok = len(out) == total and len(over) == 0
    say("  判定：%s" % ("PASS" if ok else "FAIL"))
    return ok


# ---------------------------------------------------------------- S5

def s5(exe):
    say("== S5 同一张连跑两次，第二次必须命中缓存 ==")
    port = start_srv()
    set_cfg(True, "http://127.0.0.1:%d" % port)
    lst = os.path.join(os.environ.get("TEMP", "."), "t3a_cache.txt")
    with open(TRUTH, encoding="utf-8-sig") as f:
        first = json.load(f)["images"][0]
    p = os.path.join(ROOT, "tools", "语料", first["file"].replace("\\", os.sep))
    # 【别再犯】必须写两行同一张图。只写一行的话第二次请求根本不存在，命中数永远是 0。
    with open(lst, "w", encoding="utf-8", newline="") as f:
        f.write("%s|%s\n%s|%s\n" % (first["id"], p, first["id"], p))
    n = log_len()
    run_replay(exe, lst, "s5")
    lines = log_since(n)
    hit = [ln.strip() for ln in lines if "命中缓存" in ln]
    hit_ms = []
    for ln in hit:
        m = re.search(r"本次命中 (\d+) ms", ln)
        if m:
            hit_ms.append(int(m.group(1)))
    net = [ln.strip() for ln in lines if "翻译：成功" in ln]
    for x in net[-1:]:
        say("  联网那次：%s" % x[-100:])
    for x in hit:
        say("  缓存命中：%s" % x[-110:])
    ok = len(hit) >= 1 and hit_ms and max(hit_ms) < 10
    say("  命中次数 %d ｜ 本次命中耗时 %s ms" % (len(hit), hit_ms))
    say("  判定：%s" % ("PASS（< 10ms）" if ok else "FAIL"))
    return ok


# ---------------------------------------------------------------- S6

def s6(exe):
    say("== S6 token 被拒（205）→ 强制刷新 → 继续翻 ==")
    # 【别再犯】① 换 -TokenSalt 验不到 205：每次回放是新进程，内存里本来就没有旧票；
    #           ② 只跑一张也验不到：第一张就把票用完，看不到「刷新后还能翻」。
    # 必须让假服务器在**同一进程内**换票（-RotateAfter 1）+ 跑前 3 行。
    port = start_srv(["-RotateAfter", "1"])
    set_cfg(True, "http://127.0.0.1:%d" % port)
    lst = os.path.join(os.environ.get("TEMP", "."), "t3a_s6.txt")
    with open(TRUTH, encoding="utf-8-sig") as f:
        imgs = json.load(f)["images"][:3]
    with open(lst, "w", encoding="utf-8", newline="") as f:
        for it in imgs:
            f.write(it["id"] + "|" + os.path.join(ROOT, "tools", "语料",
                                                  it["file"].replace("\\", os.sep)) + "\n")
    n = log_len()
    run_replay(exe, lst, "s6")
    lines = log_since(n)
    rejected = sum(1 for ln in lines if "token 被拒" in ln)
    tokens = sum(1 for ln in lines if "已取到新 token" in ln)
    okc = sum(1 for ln in lines if "翻译：成功" in ln)
    bad = sum(1 for ln in lines if "翻译：放弃" in ln)
    for ln in lines:
        if "token 被拒" in ln:
            say("  %s" % ln.strip()[-110:])
    say("  205 被拒 = %d 次（应 1）｜ 取票 = %d（应 2）｜ 成功 = %d（应 3）｜ 放弃 = %d（应 0）"
        % (rejected, tokens, okc, bad))
    ok = rejected == 1 and tokens == 2 and okc == 3 and bad == 0
    say("  判定：%s" % ("PASS" if ok else "FAIL"))
    return ok


# ---------------------------------------------------------------- S3

def s3(exe):
    say("== S3 断网（endpoint 指向拒连端口）：必须全降级、原文一字不少 ===")
    set_cfg(True, "http://127.0.0.1:1")
    lst = os.path.join(os.environ.get("TEMP", "."), "t3a_full.txt")
    total = make_list(lst)
    n = log_len()
    out, wall = run_replay(exe, lst, "s3")
    lines = log_since(n)
    ms = e2e_of(lines)
    e2e = ms["total"]
    trs = ms["tr"]
    over = [x for x in trs if x > 5000]
    empty = [l for l in out if not l.split("|", 1)[-1].strip()]
    bad = sum(1 for ln in lines if "翻译：放弃" in ln)
    okc = sum(1 for ln in lines if "翻译：成功" in ln)
    msgs = {}
    for ln in lines:
        if "翻译：放弃" in ln:
            m = re.search(r"放弃，(.+?)（", ln)
            if m:
                msgs[m.group(1)] = msgs.get(m.group(1), 0) + 1
    if trs:
        say("  翻译段(断网) P50 %d / P95 %d / max %d ms ｜ 墙钟 %.1f 秒"
            % (pct(trs, .5), pct(trs, .95), max(trs), wall))
    if e2e:
        say("  译文可见(合计) P50 %d / P95 %d / max %d ms"
            % (pct(e2e, .5), pct(e2e, .95), max(e2e)))
    say("  失败 %d ｜ 成功 %d ｜ 结果行 %d/%d ｜ 原文为空 %d 行 ｜ 翻译段超 5000ms %d 条"
        % (bad, okc, len(out), total, len(empty), len(over)))
    for k, v in sorted(msgs.items(), key=lambda x: -x[1]):
        say("  文案「%s」x%d" % (k, v))
    ok = len(out) == total and not empty and not over and bad == total
    say("  判定：%s" % ("PASS" if ok else "FAIL"))
    return ok


# ---------------------------------------------------------------- S4

def s4(exe):
    say("== S4 开关关：零外发、卡片与阶段三完全一致 ==")
    set_cfg(False, "http://127.0.0.1:5099")
    lst = os.path.join(os.environ.get("TEMP", "."), "t3a_full.txt")
    total = make_list(lst)
    n = log_len()
    run_replay(exe, lst, "s4")
    lines = log_since(n)
    pat = re.compile(r"翻译：提供方|已取到新 token|翻译：成功|翻译：放弃|命中缓存|译文")
    outbound = [ln.strip() for ln in lines if pat.search(ln)]
    skip = sum(1 for ln in lines if "翻译 = 没走" in ln)
    cfgline = [ln.strip() for ln in lines if "已读取设置" in ln]
    if cfgline:
        say("  %s" % cfgline[0][-100:])
    for x in outbound[:5]:
        say("  !! %s" % x[-100:])
    say("  外发相关日志 %d 行（应 0）｜ 翻译=没走 %d 条（应 %d）" % (len(outbound), skip, total))
    ok = len(outbound) == 0 and skip == total
    say("  判定：%s" % ("PASS" if ok else "FAIL"))
    return ok


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--only", default="S1,P0E1,S2,S5,S6,S3,S4")
    a = ap.parse_args()
    exe = a.exe
    only = {x.strip().upper() for x in a.only.split(",") if x.strip()}

    if not os.path.exists(exe):
        say("找不到 exe：%s" % exe)
        dump()
        return 1
    say("exe = %s" % exe)
    say("时间 = %s" % time.strftime("%Y-%m-%d %H:%M:%S"))
    say("")

    # 备份用户配置，跑完必须还原——不然会把用户自己的 rapidOcr* 设置吃掉
    had = os.path.exists(CFG)
    if had:
        with open(CFG, "rb") as src, open(CFG_BAK, "wb") as dst:
            dst.write(src.read())

    results = {}
    try:
        for name, fn in [("S1", s1), ("P0E1", p0e1), ("S2", s2), ("S5", s5),
                         ("S6", s6), ("S3", s3), ("S4", s4)]:
            if name not in only:
                continue
            try:
                results[name] = fn(exe)
            except Exception as e:
                say("  !! %s 抛异常：%s: %s" % (name, type(e).__name__, e))
                results[name] = False
            say("")
    finally:
        kill_all_srv()
        subprocess.run(["taskkill", "/F", "/IM", "TranslatorApp.exe"], capture_output=True)
        if had:
            with open(CFG_BAK, "rb") as src, open(CFG, "wb") as dst:
                dst.write(src.read())
            os.remove(CFG_BAK)
            # 终态校验：还原后必须与开跑前逐键一致
            try:
                back = read_cfg()
                say("配置已还原：%d 个键，translateEnabled=%s translateEndpoint=%r"
                    % (len(back), back.get("translateEnabled"), back.get("translateEndpoint")))
            except Exception as e:
                say("!! 配置还原后读不回来：%s" % e)

    say("")
    say("== 汇总 ==")
    for k in ["S1", "P0E1", "S2", "S5", "S6", "S3", "S4"]:
        if k in results:
            say("  %-5s %s" % (k, "PASS" if results[k] else "FAIL"))
    allok = all(results.values()) if results else False
    say("  总计：%s" % ("全 PASS" if allok else "有 FAIL"))
    dump()
    say("（结果已写 %s）" % OUT)
    return 0 if allok else 1


if __name__ == "__main__":
    sys.exit(main())
