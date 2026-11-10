# -*- coding: utf-8 -*-
r"""诊断第 2 步：找一个能同时覆盖「短俄文」和「英文」的判据。

用户实测反馈（2026-11-10）：
  ① Старые хиты 这种短俄文 → 被识别成英文 CTapble xitbl（乱码）
  ② 有明显的英文时整个变英文

根因（已复现）：en 模型把俄文读成「形似俄文的拉丁乱码」时
  词内混排比 = 0.00（没有数字）、长度和 cyr 基本一致
  → cyr_long 的两条触发条件（混排≥0.15 / en明显更短）全都命中不了
  → 判据选 en → 用户看到乱码

新思路：**比较两个模型的自信度**，而不是猜 en 的结果对不对。
  但要先证明「英文图上 en 分数总是压过 cyr」不成立得太多。

跑法：python "tools\脚本\诊断-判据3.py"
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


def parse_yml(path):
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
model_cy = Rec(os.path.join(TMP, 'cyr_rec.onnx'), parse_yml(os.path.join(TMP, 'itext_cyr.yml')))


def split_lines(img, min_h=6):
    g = img.convert('L')
    w, h = g.size
    a = np.array(g)
    if a.mean() < 128:
        a = 255 - a
    ink = (a < 160).sum(axis=1)
    rows = ink > max(1, int(w * 0.005))
    bands, start = [], None
    for y, v in enumerate(rows):
        if v and start is None:
            start = y
        elif not v and start is not None:
            bands.append((start, y))
            start = None
    if start is not None:
        bands.append((start, h))
    merged, gap_min = [], max(2, int(h * 0.02))
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


def feats(text, conf, vocab):
    if not text:
        return dict(junk=1.0, mixed=0.0, cyr=0.0, lat=0.0, n=0, conf=conf)
    body = [c for c in text if not c.isspace()]
    n = len(body)
    if n == 0:
        return dict(junk=1.0, mixed=0.0, cyr=0.0, lat=0.0, n=0, conf=conf)
    junk = sum(1 for c in body if c not in vocab) / n
    cyr = sum(1 for c in body if 0x0400 <= ord(c) <= 0x04FF) / n
    lat = sum(1 for c in body if c.isascii() and c.isalpha()) / n
    ws = [w for w in text.split() if any(c.isalpha() for c in w)]
    mixed = (sum(1 for w in ws if any(c.isdigit() for c in w)) / len(ws)) if ws else 0.0
    return dict(junk=junk, mixed=mixed, cyr=cyr, lat=lat, n=n, conf=conf)


# ############ 判据候选 ############
def judge_old(fe, fc):
    """当前线上：cyr_long"""
    if fc['n'] == 0 or fc['cyr'] < 0.50 or fc['junk'] >= 0.02:
        return 'en'
    if fe['mixed'] >= 0.15 or fe['n'] < fc['n'] * 0.8:
        return 'cyr'
    return 'en'


def judge_conf(margin):
    """新：cyr 自信度比 en 高 margin 就用 cyr（前提 cyr 是干净俄文）"""
    def f(fe, fc):
        if fc['n'] == 0 or fc['cyr'] < 0.50 or fc['junk'] >= 0.02:
            return 'en'
        if fc['conf'] - fe['conf'] >= margin:
            return 'cyr'
        return 'en'
    return f


def judge_conf_or_old(margin):
    """新 + 旧条件并集"""
    def f(fe, fc):
        if fc['n'] == 0 or fc['cyr'] < 0.50 or fc['junk'] >= 0.02:
            return 'en'
        if fc['conf'] - fe['conf'] >= margin:
            return 'cyr'
        if fe['mixed'] >= 0.15 or fe['n'] < fc['n'] * 0.8:
            return 'cyr'
        return 'en'
    return f


# ############ 语料 ############
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
    ru.append((seg, truth[seg], feats(te, ce, model_en.vocab), feats(tc, cc, model_cy.vocab), te, tc))

data = json.load(open(os.path.join(ROOT, 'tools', '素材', '行真值.json'), encoding='utf-8-sig'))
en = []
for im in data['images']:
    fp = os.path.join(CORPUS, im['file'])
    if not os.path.exists(fp):
        continue
    img = Image.open(fp)
    te, ce = model_en.run(img)
    tc, cc = model_cy.run(img)
    en.append((im['id'], '\n'.join(im['lines']), feats(te, ce, model_en.vocab),
               feats(tc, cc, model_cy.vocab), te, tc))

# 短俄文合成语料（用户截图那一类）
from PIL import ImageDraw, ImageFont
SHORT_RU = ['Старые хиты', 'Главное меню', 'Играть', 'Выход', 'Настройки',
            'Новая игра', 'Ошибка сервера', 'Загрузка', 'Продолжить']
short = []
fp_arial = r'C:\Windows\Fonts\arial.ttf'
for t in SHORT_RU:
    f = ImageFont.truetype(fp_arial, 26)
    b = f.getbbox(t)
    img = Image.new('RGB', (b[2] - b[0] + 24, b[3] - b[1] + 24), (26, 26, 26))
    ImageDraw.Draw(img).text((12 - b[0], 12 - b[1]), t, font=f, fill=(240, 240, 240))
    te, ce = model_en.run(img)
    tc, cc = model_cy.run(img)
    short.append((t, feats(te, ce, model_en.vocab), feats(tc, cc, model_cy.vocab), te, tc))

p('语料：俄文段 %d，行级 %d，短俄文合成 %d' % (len(ru), len(en), len(short)))
p('裸基线：俄文 en %d/%d cyr %d/%d ｜ 行级 en %d/%d cyr %d/%d'
  % (sum(1 for r in ru if lcs_ratio(r[1], r[4]) >= 0.90), len(ru),
     sum(1 for r in ru if lcs_ratio(r[1], r[5]) >= 0.90), len(ru),
     sum(1 for r in en if lcs_ratio(r[1], r[4]) >= 0.90), len(en),
     sum(1 for r in en if lcs_ratio(r[1], r[5]) >= 0.90), len(en)))

CANDS = [('旧判据 cyr_long', judge_old)]
for m in (0.00, 0.02, 0.05, 0.08, 0.10, 0.15):
    CANDS.append(('conf差>=%.2f' % m, judge_conf(m)))
for m in (0.05, 0.08, 0.10, 0.15):
    CANDS.append(('conf差>=%.2f+旧' % m, judge_conf_or_old(m)))

p()
p('%-22s %-10s %-10s %-10s %s' % ('判据', '俄文段', '短俄文', '行级', '行级丢失'))
p('-' * 74)
results = []
for label, jf in CANDS:
    ru_ok = sum(1 for r in ru if lcs_ratio(r[1], r[5] if jf(r[2], r[3]) == 'cyr' else r[4]) >= 0.90)
    sh_ok = sum(1 for r in short if jf(r[1], r[2]) == 'cyr')
    line_ok, lost = 0, []
    for rid, want, fe, fc, te, tc in en:
        use = tc if jf(fe, fc) == 'cyr' else te
        ok = lcs_ratio(want, use) >= 0.90
        line_ok += ok
        if lcs_ratio(want, te) >= 0.90 and not ok:
            lost.append(rid)
    base = sum(1 for r in en if lcs_ratio(r[1], r[4]) >= 0.90)
    verdict = 'PASS' if (ru_ok >= 9 and sh_ok >= 8 and line_ok >= base and not lost) else 'fail'
    p('%-22s %-10s %-10s %-10s %s  %s'
      % (label, '%d/%d' % (ru_ok, len(ru)), '%d/%d' % (sh_ok, len(short)),
         '%d/%d' % (line_ok, len(en)), ','.join(lost) if lost else '无', verdict))
    results.append((verdict, ru_ok, sh_ok, line_ok, len(lost), label, jf))

p()
ok = [r for r in results if r[0] == 'PASS']
if ok:
    ok.sort(key=lambda x: (-x[2], -x[1], x[4]))
    p('✅ 通过：%s' % ok[0][5])
    jf = ok[0][6]
    p()
    p('短俄文逐条：')
    for t, fe, fc, te, tc in short:
        who = jf(fe, fc)
        p('  %-16s 选=%-3s  en=%-22r cyr=%r' % (t, who, te, tc))
    p()
    p('俄文段逐条：')
    for seg, want, fe, fc, te, tc in ru:
        who = jf(fe, fc)
        p('  %-10s 选=%-3s %s' % (seg, who, 'OK' if lcs_ratio(want, tc if who == 'cyr' else te) >= 0.90 else '差'))
else:
    p('❌ 全部不通过')

txt = '\n'.join(OUT) + '\n'
open(os.path.join(TMP, '诊断-判据3.txt'), 'w', encoding='utf-8').write(txt)
print()
print('已写入 tools\\临时\\_eslav\\诊断-判据3.txt')
