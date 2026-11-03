# -*- coding: utf-8 -*-
"""P0-E4：用真 Bing 打一次端到端，验证 fromLang 字段名。
四样：请求行+表单、响应状态+body、fromLang vs from 对照、单项耗时 P50/P95。
"""
import json
import re
import statistics
import time
import urllib.parse
import urllib.request

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0")
BASE = "https://cn.bing.com"

OUT = []


def w(s=""):
    print(s)
    OUT.append(str(s))


def fetch(url, data=None, headers=None, timeout=30):
    h = {"User-Agent": UA}
    if headers:
        h.update(headers)
    t0 = time.time()
    req = urllib.request.Request(url, data=data, headers=h)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            body = r.read().decode("utf-8", "replace")
            return r.status, body, (time.time() - t0) * 1000
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        return e.code, body, (time.time() - t0) * 1000
    except Exception as e:
        return None, "EXC " + repr(e), (time.time() - t0) * 1000


def get_ticket():
    st, html, ms = fetch(BASE + "/translator")
    w("== 授权页 GET %s/translator ==" % BASE)
    w("  状态 = %s  耗时 = %.0f ms  长度 = %d" % (st, ms, len(html)))
    ig = re.search(r'IG\s*:\s*"([0-9A-Fa-f]{32})"', html)
    iid = re.search(r'data-iid="([^"]+)"', html)
    par = re.search(r'params_AbusePreventionHelper\s*=\s*(\[[^\]]*\])', html)
    if not (ig and iid and par):
        w("  !! 抓取失败 ig=%s iid=%s par=%s" % (bool(ig), bool(iid), bool(par)))
        return None
    arr = json.loads(par.group(1))
    t = {"ig": ig.group(1), "iid": iid.group(1),
         "key": str(arr[0]), "token": arr[1]}
    w("  IG = %s" % t["ig"])
    w("  IID = %s" % t["iid"])
    w("  key(params[0]) = %s" % t["key"])
    w("  token(params[1]) = %s...(打码)" % t["token"][:8])
    return t


def post(t, fields_text, to="zh-Hans", label=""):
    """fields_text: 完整表单字符串（不含 to/text/token/key 之外的字段拼法由调用方给）"""
    url = ("%s/ttranslatev3?isVertical=1&&IG=%s&IID=%s"
           % (BASE, t["ig"], t["iid"]))
    body = fields_text.encode("utf-8")
    w("")
    w("---- %s ----" % label)
    w("请求行长 = %d 字节" % len(url))
    w("请求 URL  = %s" % url)
    # token/key 打码后再打印表单，字段名一个字不改
    shown = fields_text
    shown = shown.replace(urllib.parse.quote(t["token"], safe=""), "<TOKEN>")
    shown = shown.replace(t["token"], "<TOKEN>")
    w("表单 body = %s" % shown)
    st, resp, ms = fetch(url, data=body,
                         headers={"Content-Type": "application/x-www-form-urlencoded",
                                  "Referer": BASE + "/translator",
                                  "Origin": BASE})
    w("HTTP 状态 = %s  耗时 = %.0f ms" % (st, ms))
    w("响应 body = %s" % resp[:600])
    return st, resp, ms


def main():
    t = get_ticket()
    if not t:
        w("拿不到票据，停。")
        return
    tok = urllib.parse.quote(t["token"], safe="")
    key = urllib.parse.quote(t["key"], safe="")
    txt = "Hello world"

    # ① 成功：fromLang=auto-detect
    f1 = ("fromLang=auto-detect&to=zh-Hans&text=%s&token=%s&key=%s"
          % (urllib.parse.quote(txt, safe=""), tok, key))
    st1, r1, ms1 = post(t, f1, label="A. fromLang=auto-detect（代码现在用的）")

    # ② 对照：改成 from=auto-detect
    f2 = ("from=auto-detect&to=zh-Hans&text=%s&token=%s&key=%s"
          % (urllib.parse.quote(txt, safe=""), tok, key))
    st2, r2, ms2 = post(t, f2, label="B. from=auto-detect（T3-spike-2 汇报写的）")

    # ③ 对照：干脆不传源语言字段
    f3 = ("to=zh-Hans&text=%s&token=%s&key=%s"
          % (urllib.parse.quote(txt, safe=""), tok, key))
    st3, r3, ms3 = post(t, f3, label="C. 完全不传源语言字段")

    # ④ 单项耗时：成功路径打 6 次
    w("")
    w("== 单项耗时（fromLang 路径，各 6 次） ==")
    times = []
    for i in range(6):
        f = ("fromLang=auto-detect&to=zh-Hans&text=%s&token=%s&key=%s"
             % (urllib.parse.quote("line %d test" % i, safe=""), tok, key))
        st, resp, ms = post(t, f, label="第 %d 次" % (i + 1))
        times.append(ms)
    times_sorted = sorted(times)
    p50 = statistics.median(times)
    p95 = times_sorted[min(len(times_sorted) - 1, int(len(times_sorted) * 0.95))]
    w("")
    w("== 汇总 ==")
    w("fromLang 成功 = %s   from 结果 = %s   无源语言字段 = %s"
      % (st1, st2, st3))
    w("单项耗时 = %s" % ", ".join("%.0f" % x for x in times))
    w("P50 = %.0f ms   P95 = %.0f ms   min = %.0f   max = %.0f"
      % (p50, p95, min(times), max(times)))


if __name__ == "__main__":
    main()
