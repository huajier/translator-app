# -*- coding: utf-8 -*-
r"""最后一次判据扫描：两个模型都跑，按「干净度」择优。

和上一轮的区别（这是重点）：
  上一轮结构 = "en 结果可疑吗？可疑才换 cyr"  ← 已证明走不通
  这一轮结构 = "两份都算好，谁更干净用谁"        ← 每张图多花 8.5ms，换掉猜错的风险

跑法：python "tools\脚本\P1前置-判据扫描2.py"
输出：tools\临时\_eslav\P1前置-判据扫描2.txt
"""
import json
import os
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


class Rec:
    def __init__(self, onnx, dict_items):
        self.s = ort.InferenceSession(onnx, providers=['CPUExecutionProvider'])
        self.inp = self.s.get_inputs()[0]
        self.decode = [''] + dict_items + [' ']
        self.vocab = set(dict_items)

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
        idx = prob.argmax(axis=1)
        sc = prob.max(axis=1)
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
model_cy = Rec(os.path.join(TMP, 'cyr_rec.onnx'),
               parse_dict_from_yml(os.path.join(TMP, 'itext_cyr.yml')))


def split_lines(img, min_h=6):
    g = img.convert('L')
    w, h = g.size
    a = np.array(g)
    if a.mean() < 128:
        a = 255 - a
    ink = (a < 160).sum(axis=1)
    thr = max(1, int(w * 0.005))
    rows = ink > thr
    bands, start = [], None
    for y, v in enumerate(rows):
        if v and start is None:
            start = y
        elif not v and start is not None:
            bands.append((start, y))
            start = None
    if start is not None:
        bands.append((start, h))
    merged = []
    gap_min = max(2, int(h * 0.02))
    for b in bands:
        if merged and b[0] - merged[-1][1] < gap_min:
            merged[-1] = [merged[-1][0], b[1]]
        else:
            merged.append(list(b))
    out = []
    for y0, y1 in merged:
        if y1 - y0 < min_h:
            continue
        crop = img.crop((0, y0, w, y1))
        ga = np.array(crop.convert('L'))
        col = (ga < 160).sum(axis=0) if ga.mean() >= 128 else ((255 - ga) < 160).sum(axis=0)
        nz = np.nonzero(col > 0)[0]
        if len(nz):
            crop = crop.crop((max(0, nz[0] - 2), 0, min(w, nz[-1] + 3), y1 - y0))
        out.append(crop)
    return out if out else [img]


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


# ================= 「干净度」指标 =================
def quality(text, conf, vocab):
    """越高越好。设计原则：只看结果本身，不信分数（分数低≠读错，这是上一轮的教训）。"""
    if not text:
        return dict(q=0.0, junk=1.0, cyr=0.0, latin=0.0, n=0, conf=conf)
    body = [c for c in text if not c.isspace()]
    n = len(body)
    if n == 0:
        return dict(q=0.0, junk=1.0, cyr=0.0, latin=0.0, n=0, conf=conf)
    junk = sum(1 for c in body if c not in vocab) / n
    cyr = sum(1 for c in body if 0x0400 <= ord(c) <= 0x04FF) / n
    latin = sum(1 for c in body if c.isascii() and c.isalpha()) / n
    # 干净度：字典内比例为主，字符数为辅（太短的结果不可信），分数只占很小权重
    q = (1.0 - junk) * 0.75 + min(n, 40) / 40.0 * 0.15 + conf * 0.10
    return dict(q=q, junk=junk, cyr=cyr, latin=latin, n=n, conf=conf)


def pick(t_en, c_en, t_cy, c_cy):
    """两个结果都算好，选更干净的那个。"""
    fe = quality(t_en, c_en, model_en.vocab)
    fc = quality(t_cy, c_cy, model_cy.vocab)
    # 平手或极接近时，**保 en**（现状优先，避免无谓改变）
    if fc['q'] > fe['q'] + 0.02:
        return t_cy, fc, fe, 'cyr'
    return t_en, fe, fc, 'en'


# ================= 语料 A：俄文 =================
p('===== 语料 A：俄文 11 段 =====')
truth = {}
for i, n in enumerate(('验收标准答案.txt', '验收标准答案2.txt', '验收标准答案3.txt'), 1):
    fp = os.path.join(ROOT, 'tools', '素材', n)
    if not os.path.exists(fp):
        continue
    for line in open(fp, encoding='utf-8-sig'):
        line = line.strip()
        if line and not line.startswith('#') and '|' in line:
            k, v = line.split('|', 1)
            truth['材料%d-%s' % (i, k.strip())] = v.strip()

RU = sorted(k for k in truth if k.startswith(('材料1-B2', '材料2-R', '材料3-M3')))
ru = []
for seg in RU:
    mat, code = seg.split('-', 1)
    d = os.path.join(CORPUS, mat, code)
    if not os.path.isdir(d):
        continue
    imgs = sorted(f for f in os.listdir(d) if f.lower().endswith(('.png', '.jpg')))
    if not imgs:
        continue
    img = Image.open(os.path.join(d, imgs[0]))
    lines = split_lines(img)
    te = ' '.join(model_en.run(l)[0] for l in lines)
    tc = ' '.join(model_cy.run(l)[0] for l in lines)
    ce = sum(model_en.run(l)[1] for l in lines) / max(1, len(lines))
    cc = sum(model_cy.run(l)[1] for l in lines) / max(1, len(lines))
    use, f_use, f_oth, who = pick(te, ce, tc, cc)
    ok = lcs_ratio(truth[seg], use) >= 0.90
    ru.append((seg, truth[seg], te, tc, who, ok))
    p('%-10s 选 %-3s %s' % (seg, who, 'OK' if ok else '差'))
    if not ok:
        p('    真: %s' % truth[seg].replace('\n', ' ')[:66])
        p('    得: %s' % use[:66])

ru_ok = sum(1 for r in ru if r[5])
p()
p('俄文段：择优后 %d/%d（en 单独 %d/%d，cyr 单独 %d/%d）'
  % (ru_ok, len(ru),
     sum(1 for r in ru if lcs_ratio(r[1], r[2]) >= 0.90), len(ru),
     sum(1 for r in ru if lcs_ratio(r[1], r[3]) >= 0.90), len(ru)))

# ================= 语料 B：80 张行图 =================
p()
p('===== 语料 B：真实游戏行级图 =====')
data = json.load(open(os.path.join(ROOT, 'tools', '素材', '行真值.json'), encoding='utf-8-sig'))
en = []
for im in data['images']:
    fp = os.path.join(CORPUS, im['file'])
    if not os.path.exists(fp):
        continue
    img = Image.open(fp)
    te, ce = model_en.run(img)
    tc, cc = model_cy.run(img)
    use, f_use, f_oth, who = pick(te, ce, tc, cc)
    en.append(dict(id=im['id'], want='\n'.join(im['lines']), te=te, tc=tc,
                   use=use, who=who, ok=lcs_ratio('\n'.join(im['lines']), use) >= 0.90))

base = sum(1 for r in en if lcs_ratio(r['want'], r['te']) >= 0.90)
now = sum(1 for r in en if r['ok'])
p('行级：择优后 %d/%d ｜ en 单独 %d/%d ｜ cyr 单独 %d/%d'
  % (now, len(en), base, len(en),
     sum(1 for r in en if lcs_ratio(r['want'], r['tc']) >= 0.90), len(en)))
p('选 cyr 的图：%d/%d（%.0f%%）'
  % (sum(1 for r in en if r['who'] == 'cyr'), len(en),
     100.0 * sum(1 for r in en if r['who'] == 'cyr') / len(en)))

lost = [r for r in en if lcs_ratio(r['want'], r['te']) >= 0.90 and not r['ok']]
gain = [r for r in en if lcs_ratio(r['want'], r['te']) < 0.90 and r['ok']]
p()
if lost:
    p('❌ 丢格 %d：' % len(lost))
    for r in lost:
        p('   %-6s 真=%s' % (r['id'], r['want'][:40]))
        p('          en =%s' % r['te'][:40])
        p('          cyr=%s' % r['tc'][:40])
else:
    p('✅ 一格没丢')
if gain:
    p('🟢 新赢 %d：%s' % (len(gain), ','.join(r['id'] for r in gain)))

txt = '\n'.join(OUT) + '\n'
open(os.path.join(TMP, 'P1前置-判据扫描2.txt'), 'w', encoding='utf-8').write(txt)
print()
print('已写入 tools\\临时\\_eslav\\P1前置-判据扫描2.txt')
