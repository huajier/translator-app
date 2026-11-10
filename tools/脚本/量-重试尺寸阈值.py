# -*- coding: utf-8 -*-
r"""量：小框 3 倍重试在多高的图上开始变成负收益。

背景（2026-11-10 用户实测长文本 704×474，日志逐段）：
  截图 0ms → Windows第一遍 285ms → 引擎初始化 388ms
  → 小框3倍重试 2578ms → en收尾 1042ms → cyr 156ms
  真正出结果的是 cyr，重试那 2.5 秒读得更烂（1x/3x 都没被采用）。

已在日志里看到的现象：
  小图 60×30 / 487×19，3x 把结果救回来了
  大图 704×474 / 737×69，3x 结果更烂

本脚本要给出「多高算大」的有依据的阈值。

数据源：
  A. 80 张行级图（有真值）—— 逐张跑 1x 和 3x，判谁更接近真值
  B. 331 张全量图 —— 只用尺寸分布，判断阈值会砍掉多少张

跑法：python "tools\脚本\量-重试尺寸阈值.py"
输出：tools\临时\_eslav\量-重试尺寸阈值.txt
"""
import json
import os
import statistics
import sys

import numpy as np
import onnxruntime as ort
from PIL import Image

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TMP = os.path.join(ROOT, 'tools', '临时', '_eslav')
REL = os.path.join(ROOT, 'release', 'v0.4.13', 'models', 'v5')
CORPUS = os.path.join(ROOT, 'tools', '语料')
OUT = []


def p(s=''):
    OUT.append(s)
    print(s)


class Rec:
    """只有 rec，没有 det —— 行级图本身就是一行文字，不需要检测。"""

    def __init__(self, onnx, dict_items):
        self.s = ort.InferenceSession(onnx, providers=['CPUExecutionProvider'])
        self.inp = self.s.get_inputs()[0]
        self.decode = [''] + dict_items + [' ']

    def run(self, img):
        w, h = img.size
        if h <= 0 or w <= 0:
            return '', 0.0
        rw = max(1, min(int(np.ceil(48 * w / float(h))), 3200))
        im = img.convert('RGB').resize((rw, 48), Image.BICUBIC)
        a = np.array(im, dtype=np.float32) / 255.0
        a = ((a - 0.5) / 0.5).transpose(2, 0, 1)[None, ...]
        o = self.s.run(None, {self.inp.name: a})[0]
        prob = o[0]
        idx, sc = prob.argmax(axis=1), prob.max(axis=1)
        cs, cf, prev = [], [], -1
        for i, k in enumerate(idx):
            if k != 0 and k != prev and k < len(self.decode):
                cs.append(self.decode[k])
                cf.append(float(sc[i]))
            prev = k
        return ''.join(cs), (sum(cf) / len(cf) if cf else 0.0)


model_en = Rec(os.path.join(REL, 'en_PP-OCRv5_rec_mobile.onnx'),
               [l for l in open(os.path.join(REL, 'ppocrv5_en_dict.txt'),
                                'rb').read().decode('utf-8', 'replace').splitlines()])


def norm(s):
    return ''.join(c.lower() for c in (s or '') if c.isalnum())


def lcs_ratio(want, got):
    a, b = norm(want), norm(got)
    if not a:
        return 1.0 if not b else 0.0
    m, n = len(a), len(b)
    if m * n > 600000:
        return 1.0 if a in b else 0.0
    prev = [0] * (n + 1)
    for i in range(1, m + 1):
        cur = [0] * (n + 1)
        ai = a[i - 1]
        for j in range(1, n + 1):
            cur[j] = prev[j - 1] + 1 if ai == b[j - 1] else max(prev[j], cur[j - 1])
        prev = cur
    return prev[n] / m


def upscale3(img, max_side=2000):
    """复刻 UpscaleForRetry：3 倍，长边超 2000 就压到 2000。"""
    w, h = img.size
    scale = 3.0
    longest = max(w, h)
    if longest * scale > max_side:
        scale = max_side / longest
    return img.resize((max(1, int(round(w * scale))), max(1, int(round(h * scale)))),
                      Image.BICUBIC)


# ================= A. 80 张行级图：1x vs 3x 谁更准 =================
p('===== A. 80 张行级图：1x vs 3x 谁更接近真值 =====')
data = json.load(open(os.path.join(ROOT, 'tools', '素材', '行真值.json'), encoding='utf-8-sig'))

rows = []
for im in data['images']:
    fp = os.path.join(CORPUS, im['file'])
    if not os.path.exists(fp):
        continue
    img = Image.open(fp)
    w, h = img.size
    want = '\n'.join(im['lines'])
    t1, _ = model_en.run(img)
    t3, _ = model_en.run(upscale3(img))
    s1 = lcs_ratio(want, t1)
    s3 = lcs_ratio(want, t3)
    rows.append(dict(id=im['id'], w=w, h=h, area=w * h,
                     s1=s1, s3=s3, t1=t1, t3=t3, want=want))

p('可测 %d 张' % len(rows))

# 分类：3x 更好 / 1x 更好 / 一样
better3 = [r for r in rows if r['s3'] > r['s1'] + 0.005]
better1 = [r for r in rows if r['s1'] > r['s3'] + 0.005]
same = [r for r in rows if abs(r['s3'] - r['s1']) <= 0.005]

p('3x 更好 %d 张 ｜ 1x 更好 %d 张 ｜ 一样 %d 张' % (len(better3), len(better1), len(same)))

p()
p('--- 3x 更好的那些（重试真正救回来的）---')
for r in sorted(better3, key=lambda x: -x['w']):
    p('  %-6s %4d×%-4d  1x %.2f → 3x %.2f  真值=%s' %
      (r['id'], r['w'], r['h'], r['s1'], r['s3'], r['want'].replace('\n', ' ')[:34]))

p()
p('--- 1x 更好的那些（重试反而读坏）---')
for r in sorted(better1, key=lambda x: -x['w']):
    p('  %-6s %4d×%-4d  1x %.2f → 3x %.2f  真值=%s' %
      (r['id'], r['w'], r['h'], r['s1'], r['s3'], r['want'].replace('\n', ' ')[:34]))

# ================= 阈值扫描：按「片高 h」或「面积」限制重试 =================
p()
p('===== 阈值扫描：只在图不太大时才允许重试 =====')
p('评分口径：能判对（相似度 ≥0.90）的图数，越高越好')

base_ok = sum(1 for r in rows if r['s1'] >= 0.90)      # 完全不重试
now_ok = sum(1 for r in rows if max(r['s1'], r['s3']) >= 0.90)  # 现在的行为

p('  完全不重试（只 1x）：%d/%d' % (base_ok, len(rows)))
p('  现在的行为（1x/3x 择优）：%d/%d' % (now_ok, len(rows)))
p()

cands = []
for th in (40, 50, 60, 80, 100, 126, 150, 200, 300):
    cands.append(('高<=%d' % th, lambda r, t=th: r['h'] <= t))
for th in (20000, 40000, 60000, 100000, 200000, 400000):
    cands.append(('面积<=%d' % th, lambda r, t=th: r['area'] <= t))
for th in (600, 800, 1000, 1500):
    cands.append(('宽<=%d' % th, lambda r, t=th: r['w'] <= t))

p('%-18s %-10s %-10s %s' % ('阈值', '判对数', '相对现在', '被砍掉的重试张数'))
p('-' * 62)
res = []
for label, cond in cands:
    ok = 0
    cut = 0
    for r in rows:
        allow = cond(r)
        best = max(r['s1'], r['s3']) if allow else r['s1']
        if best >= 0.90:
            ok += 1
        # 现在会重试的图里，被这个阈值砍掉多少
        if r['s1'] < 0.90 and allow is False:
            cut += 1
    p('%-18s %2d/%-7d %+d         %d'
      % (label, ok, len(rows), ok - now_ok, cut))
    res.append((ok, label, cond))

p()
best = max(res, key=lambda x: x[0])
tie = [x for x in res if x[0] == best[0]]
p('最高判对数 = %d（现在是 %d）' % (best[0], now_ok))
p('达到该分数的候选：%s' % '、'.join(x[1] for x in tie))
if best[0] < now_ok:
    p('⚠️ 没有任何阈值能保住现在的成绩 —— 说明重试对部分大图仍有效，不能简单按尺寸砍')
elif best[0] == now_ok:
    # 在打平的候选里，选砍得最狠的（最省时间）
    def cut_count(cond):
        return sum(1 for r in rows if not cond(r))
    tie.sort(key=lambda x: -cut_count(x[2]))
    p('→ 打平时选「限制最严」的那个：%s（砍掉 %d 张图的重试）'
      % (tie[0][1], cut_count(tie[0][2])))

# ================= B. 331 张全量图的尺寸分布 =================
p()
p('===== B. 全量语料尺寸分布（判断阈值会影响多少张）=====')
sizes = []
for root, _, files in os.walk(CORPUS):
    for f in files:
        if not f.lower().endswith(('.png', '.jpg', '.jpeg')):
            continue
        try:
            w, h = Image.open(os.path.join(root, f)).size
        except Exception:
            continue
        sizes.append((w, h))

if sizes:
    hs = sorted(h for _, h in sizes)
    ws = sorted(w for w, _ in sizes)
    p('图片总数 %d' % len(sizes))
    p('高度分布：min=%d  P25=%d  P50=%d  P75=%d  P90=%d  max=%d'
      % (hs[0], hs[len(hs) // 4], hs[len(hs) // 2], hs[3 * len(hs) // 4],
         hs[int(0.9 * len(hs))], hs[-1]))
    p('宽度分布：min=%d  P25=%d  P50=%d  P75=%d  P90=%d  max=%d'
      % (ws[0], ws[len(ws) // 4], ws[len(ws) // 2], ws[3 * len(ws) // 4],
         ws[int(0.9 * len(ws))], ws[-1]))
    for th in (100, 126, 150, 200):
        n = sum(1 for _, h in sizes if h <= th)
        p('  高<=%d 的图：%d/%d（%.0f%%）' % (th, n, len(sizes), 100.0 * n / len(sizes)))

txt = '\n'.join(OUT) + '\n'
open(os.path.join(TMP, '量-重试尺寸阈值.txt'), 'w', encoding='utf-8').write(txt)
print()
print('已写入 tools\\临时\\_eslav\\量-重试尺寸阈值.txt')
