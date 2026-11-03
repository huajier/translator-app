# -*- coding: utf-8 -*-
"""第十轮 P0-F2：真 Bing 长文本耗时（≥200 字），补上「只测过 Hello world」的窟窿。

样本 = 行真值里的真实游戏文本。单条最长只有 169 字，所以按「同一份真值里相邻的
几条拼到 ≥200 字」来造，拼接方式写在输出里，不假装是单条长句。

每段打 3 次，共 5 段；同时记 Hello world 作对照，看清「短串数字」和「真长文本数字」
差多少 —— 方案里的耗时表必须两列分家，就是靠这组数。
"""
import io
import json
import os
import re
import statistics
import time
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT_PATH = os.path.join(ROOT, "tools", "脚本", "_f2_长文本_out.txt")
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0")
BASE = "https://cn.bing.com"
OUT = []


def w(s=""):
    print(s)
    OUT.append(str(s))


def fetch(url, data=None, headers=None, timeout=40):
    h = {"User-Agent": UA}
    if headers:
        h.update(headers)
    t0 = time.time()
    req = urllib.request.Request(url, data=data, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read().decode("utf-8", "replace"), (time.time() - t0) * 1000
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace"), (time.time() - t0) * 1000
    except Exception as e:
        return None, "EXC " + repr(e), (time.time() - t0) * 1000


def ticket():
    st, html, ms = fetch(BASE + "/translator")
    ig = re.search(r'IG\s*:\s*"([0-9A-Fa-f]{32})"', html)
    iid = re.search(r'data-iid="([^"]+)"', html)
    par = re.search(r'params_AbusePreventionHelper\s*=\s*(\[[^\]]*\])', html)
    if not (ig and iid and par):
        w("!! 抓不到票据 status=%s len=%d" % (st, len(html)))
        return None
    arr = json.loads(par.group(1))
    w("授权页 %s/translator → %s / %.0f ms / %d 字节" % (BASE, st, ms, len(html)))
    return {"ig": ig.group(1), "iid": iid.group(1), "key": str(arr[0]), "token": arr[1]}


def translate(t, text):
    url = "%s/ttranslatev3?isVertical=1&&IG=%s&IID=%s" % (BASE, t["ig"], t["iid"])
    body = ("fromLang=auto-detect&to=zh-Hans&text=%s&token=%s&key=%s"
            % (urllib.parse.quote(text, safe=""),
               urllib.parse.quote(t["token"], safe=""),
               urllib.parse.quote(t["key"], safe=""))).encode("utf-8")
    st, resp, ms = fetch(url, data=body,
                         headers={"Content-Type": "application/x-www-form-urlencoded",
                                  "Referer": BASE + "/translator", "Origin": BASE})
    return st, resp, ms


def pct(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(len(xs) * p))]


def build_samples():
    """5 段 ≥200 字的真实游戏文本。"""
    d = json.load(io.open(os.path.join(ROOT, "tools", "素材", "行真值.json"),
                          encoding="utf-8-sig"))
    items = d["images"] if isinstance(d, dict) else d
    pool = []
    for it in items:
        t = " ".join(x.strip() for x in (it.get("lines") or []) if x.strip())
        if t:
            pool.append((it["id"], t))
    pool.sort(key=lambda x: -len(x[1]))

    samples, i = [], 0
    while len(samples) < 5 and i < len(pool):
        parts = [pool[i]]
        total = len(pool[i][1])
        j = i + 1
        while total < 200 and j < len(pool):
            parts.append(pool[j])
            total += len(pool[j][1]) + 1
            j += 1
        samples.append(parts)
        i = j
    return samples


def main():
    t = ticket()
    if not t:
        return

    w("")
    w("== 短串对照（之前唯一测过的量级） ==")
    st, resp, ms = translate(t, "Hello world")
    w("  Hello world 11 字 → %s / %.0f ms / %s" % (st, ms, resp[:90]))

    samples = build_samples()
    w("")
    w("== 长文本耗时（每段 3 次） ==")
    all_ms, rows = [], []
    for idx, parts in enumerate(samples, 1):
        text = " ".join(p[1] for p in parts)
        ids = "+".join(p[0] for p in parts)
        w("")
        w("样本 %d：%d 字，来源 %s" % (idx, len(text), ids))
        w("  原文：%s" % text[:70].replace("\n", " "))
        times, ok, tchars = [], 0, 0
        for k in range(3):
            st, resp, ms = translate(t, text)
            times.append(ms)
            all_ms.append(ms)
            good = st == 200 and '"statusCode"' not in resp
            if good:
                ok += 1
                try:
                    tchars = len(json.loads(resp)[0]["translations"][0]["text"])
                except Exception:
                    pass
            w("  第 %d 次：%s / %.0f ms / %s" % (k + 1, st, ms,
                                                (resp[:70] if not good else "译文 %d 字" % tchars)))
        rows.append((idx, len(text), ids, times, ok, tchars))

    w("")
    w("== 汇总 ==")
    for idx, n, ids, times, ok, tchars in rows:
        w("样本 %d：%d 字 → 译 %d 字，成功 %d/3，耗时 %s"
          % (idx, n, tchars, ok, ", ".join("%.0f" % x for x in times)))
    w("")
    w("全部 %d 次：P50 = %.0f ms  P95 = %.0f ms  min = %.0f  max = %.0f"
      % (len(all_ms), statistics.median(all_ms), pct(all_ms, 0.95),
         min(all_ms), max(all_ms)))
    io.open(OUT_PATH, "w", encoding="utf-8", newline="").write("\n".join(OUT) + "\n")
    w("")
    w("已写入 %s" % OUT_PATH)


if __name__ == "__main__":
    main()
