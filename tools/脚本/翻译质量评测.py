# -*- coding: utf-8 -*-
"""翻译质量评测：把 tools\\素材\\翻译评测语料.json 里的条目真打一遍 Bing，出对照表。

为什么单独写一个而不是复用 _f2_真bing长文本.py：
  _f2 量的是「耗时」，样本是脚本自己拼的长段落；
  这里量的是「译得准不准」，样本是固定语料，逐条留痕，方便下轮原样重跑对比。
  取票/请求方式照抄 _p0e4_真bing.py，一个字没改协议。

判分：本脚本只出「原文 / 译文」原始对照，不自己判对错 ——
  对错由架构师按参考译文逐条判，判完写进 tools\\素材\\翻译评测结果.md。
  脚本自己判等于自己给自己打分。

用法：
  python -X utf8 tools\\脚本\\翻译质量评测.py
结果写 tools\\脚本\\_翻译评测_out.txt（UTF-8），同时打印。
"""
import io
import json
import os
import re
import time
import statistics
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CORPUS = os.path.join(ROOT, "tools", "素材", "翻译评测语料.json")
OUT_PATH = os.path.join(ROOT, "tools", "脚本", "_翻译评测_out.txt")

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
    """取 Bing 授权票据。params[0] 是时间戳当 key，params[1] 才是 token。"""
    st, html, ms = fetch(BASE + "/translator")
    ig = re.search(r'IG\s*:\s*"([0-9A-Fa-f]{32})"', html)
    iid = re.search(r'data-iid="([^"]+)"', html)
    par = re.search(r'params_AbusePreventionHelper\s*=\s*(\[[^\]]*\])', html)
    if not (ig and iid and par):
        w("!! 抓不到票据 status=%s len=%d" % (st, len(html)))
        return None
    arr = json.loads(par.group(1))
    w("授权页 → %s / %.0f ms" % (st, ms))
    return {"ig": ig.group(1), "iid": iid.group(1), "key": str(arr[0]), "token": arr[1]}


def translate(t, text):
    """照产品 BingFreeProvider 的字段名：fromLang（不是 from）、to=zh-Hans。"""
    url = "%s/ttranslatev3?isVertical=1&&IG=%s&IID=%s" % (BASE, t["ig"], t["iid"])
    body = ("fromLang=auto-detect&to=zh-Hans&text=%s&token=%s&key=%s"
            % (urllib.parse.quote(text, safe=""),
               urllib.parse.quote(t["token"], safe=""),
               urllib.parse.quote(t["key"], safe=""))).encode("utf-8")
    return fetch(url, data=body,
                 headers={"Content-Type": "application/x-www-form-urlencoded",
                          "Referer": BASE + "/translator", "Origin": BASE})


def parse(resp):
    """成功返回 (译文, None)；失败返回 (None, 原因)。"""
    try:
        obj = json.loads(resp)
    except Exception:
        return None, "非 JSON：" + resp[:60]
    if isinstance(obj, dict):
        return None, "statusCode=%s" % obj.get("statusCode")
    try:
        return obj[0]["translations"][0]["text"], None
    except Exception:
        return None, "结构不认识：" + resp[:60]


def main():
    data = json.load(io.open(CORPUS, encoding="utf-8-sig"))
    items = data["items"]
    w("== 翻译质量评测 · %d 条 ==" % len(items))
    w("语料 = tools\\素材\\翻译评测语料.json（全部取自真机取词记录）")
    w("")

    t = ticket()
    if not t:
        w("票据拿不到，停。")
        return

    times, fails, rows = [], 0, []
    for it in items:
        txt = it["text"]
        st, resp, ms = translate(t, txt)
        zh, why = parse(resp)
        if zh is None:
            fails += 1
            times.append(ms)
            w("%-4s [%s] %s" % (it["id"], it["类"], txt[:60]))
            w("      ✗ 失败 %s  (%.0f ms)" % (why, ms))
            rows.append((it, None, ms, why))
            continue
        times.append(ms)
        w("%-4s [%s] (%d 字 → %d 字, %.0f ms)" % (it["id"], it["类"], len(txt), len(zh), ms))
        w("      %s" % txt[:100])
        w("   →  %s" % zh)
        rows.append((it, zh, ms, None))
        w("")
        time.sleep(0.8)  # 别把免密钥通道打急了

    w("== 汇总 ==")
    w("成功 %d / %d，失败 %d" % (len(items) - fails, len(items), fails))
    if times:
        w("耗时 P50 = %.0f ms  min = %.0f  max = %.0f"
          % (statistics.median(times), min(times), max(times)))
    w("")
    w("下一步：人工按参考译文逐条判对错，写进 tools\\素材\\翻译评测结果.md")

    io.open(OUT_PATH, "w", encoding="utf-8", newline="").write("\n".join(OUT) + "\n")
    w("已写入 %s" % OUT_PATH)


if __name__ == "__main__":
    main()
