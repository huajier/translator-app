# -*- coding: utf-8 -*-
r"""第 0 批·第 2 步：用真实游戏截图验证西里尔 rec 模型。

对 80 张真实行级图（单行、有真值）直接喂 rec，不需要 det。
对照：现有 en 模型 vs 新 cyrillic 模型，同一批图谁更准。

跑法：python "tools\脚本\P0-西里尔模型裸测-真图.py"
输出：tools\临时\_eslav\P0-真图结果.txt
"""
import json
import os
import sys
import time

import numpy as np
import onnxruntime as ort
from PIL import Image

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TMP = os.path.join(ROOT, 'tools', '临时', '_eslav')
OUT = []


def p(s=''):
    OUT.append(s)
    print(s)


def parse_dict_from_yml(path):
    items, started = [], False
    for line in open(path, encoding='utf-8', errors='replace'):
        st = line.strip()
        if st.startswith('character_dict'):
            started = True
            continue
        if not started:
            continue
        if st.startswith('- '):
            raw = st[2:].strip()
            if len(raw) >= 2 and raw[0] == raw[-1] and raw[0] in '\'"':
                raw = raw[1:-1]
            items.append(raw)
        elif st:
            break
    return items


def load_plain_dict(path):
    return [l for l in open(path, 'rb').read().decode('utf-8', 'replace').splitlines()]


class Rec:
    def __init__(self, onnx_path, dict_items):
        self.s = ort.InferenceSession(onnx_path, providers=['CPUExecutionProvider'])
        self.inp = self.s.get_inputs()[0]
        self.decode = [''] + dict_items + [' ']
        self.ms = []

    def run(self, img):
        w, h = img.size
        if h == 0:
            return '', 0.0
        rw = max(1, min(int(np.ceil(48 * w / float(h))), 3200))
        im = img.convert('RGB').resize((rw, 48), Image.BICUBIC)
        a = np.array(im, dtype=np.float32) / 255.0
        a = ((a - 0.5) / 0.5).transpose(2, 0, 1)[None, ...]
        t0 = time.perf_counter()
        o = self.s.run(None, {self.inp.name: a})[0]
        self.ms.append((time.perf_counter() - t0) * 1000)
        prob = o[0]
        idx = prob.argmax(axis=1)
        sc = prob.max(axis=1)
        cs, cf, prev = [], [], -1
        for i, k in enumerate(idx):
            if k != 0 and k != prev and k < len(self.decode):
                cs.append(self.decode[k])
                cf.append(float(sc[i]))
            prev = k
        return ''.join(cs), (sum(cf) / len(cf) if cf else 0.0)


def norm(s):
    return ''.join(c.lower() for c in (s or '') if c.isalnum())


def lcs_ratio(a, b):
    a, b = norm(a), norm(b)
    if not a:
        return 1.0 if not b else 0.0
    m, n = len(a), len(b)
    if m * n > 500000:
        return 1.0 if a in b else 0.0
    prev = [0] * (n + 1)
    for i in range(1, m + 1):
        cur = [0] * (n + 1)
        ai = a[i - 1]
        for j in range(1, n + 1):
            cur[j] = prev[j - 1] + 1 if ai == b[j - 1] else max(prev[j], cur[j - 1])
        prev = cur
    return prev[n] / m


# ============ 加载两个模型 ============
p('===== 模型 =====')
cyr_chars = parse_dict_from_yml(os.path.join(TMP, 'itext_cyr.yml'))
en_chars = load_plain_dict(os.path.join(ROOT, 'release', 'v0.4.13', 'models', 'v5', 'ppocrv5_en_dict.txt'))

model_cyr = Rec(os.path.join(TMP, 'cyr_rec.onnx'), cyr_chars)
model_en = Rec(os.path.join(ROOT, 'release', 'v0.4.13', 'models', 'v5',
                            'en_PP-OCRv5_rec_mobile.onnx'), en_chars)
p('cyrillic 字典 %d 条 / en 字典 %d 条' % (len(cyr_chars), len(en_chars)))

# ============ 真实行级图 ============
p()
p('===== 真实游戏行级图（80 张，有真值）=====')
truth_file = os.path.join(ROOT, 'tools', '素材', '行真值.json')
data = json.load(open(truth_file, encoding='utf-8-sig'))
IMG_ROOT = os.path.join(ROOT, 'tools', '语料')

rows = []
missing = 0
for im in data['images']:
    fp = os.path.join(IMG_ROOT, im['file'])
    if not os.path.exists(fp):
        missing += 1
        continue
    want = '\n'.join(im['lines'])
    rows.append((im['id'], fp, want))

p('可测 %d 张（缺 %d 张）' % (len(rows), missing))

ok_en = ok_cyr = 0
both = []
for rid, fp, want in rows:
    img = Image.open(fp)
    t_en, c_en = model_en.run(img)
    t_cyr, c_cyr = model_cyr.run(img)
    s_en = lcs_ratio(want, t_en) >= 0.90
    s_cyr = lcs_ratio(want, t_cyr) >= 0.90
    ok_en += s_en
    ok_cyr += s_cyr
    if s_en != s_cyr:
        both.append((rid, want, t_en, t_cyr, s_en, s_cyr))

n = len(rows)
p()
p('%-6s %-10s %-10s' % ('', 'en模型', 'cyrillic'))
p('判对   %-10s %-10s' % ('%d/%d' % (ok_en, n), '%d/%d' % (ok_cyr, n)))
p('判对率 %-10s %-10s' % ('%.1f%%' % (100.0 * ok_en / n), '%.1f%%' % (100.0 * ok_cyr / n)))

if model_en.ms:
    import statistics
    p('中位耗时 en=%.1fms  cyr=%.1fms' % (statistics.median(model_en.ms), statistics.median(model_cyr.ms)))

if both:
    p()
    p('===== 有差异的 %d 张 =====' % len(both))
    for rid, want, t_en, t_cyr, s_en, s_cyr in both:
        p('%-6s %s' % (rid, '新赢' if s_cyr else '新输'))
        p('   真值: %s' % want[:60])
        p('   en  : %s' % t_en[:60])
        p('   cyr : %s' % t_cyr[:60])

txt = '\n'.join(OUT) + '\n'
open(os.path.join(TMP, 'P0-真图结果.txt'), 'w', encoding='utf-8').write(txt)
print()
print('已写入 tools\\临时\\_eslav\\P0-真图结果.txt')
