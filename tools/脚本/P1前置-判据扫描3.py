# -*- coding: utf-8 -*-
r"""判据第 2 版：系统扫描特征组合，找出能同时满足全部约束的判据。

上一版的问题（R8 漏选）：
  真值 Обновите ПО до версии 2.0 и перезапустите config.txt
  en  = O6Hobnte [iO Ao Bepcnn 2.0 n nepe3anyctnte config.txt   ← 全是字典内拉丁字符，junk=0
  cyr = Обновите ПO до версии 2.0 и перезапустите соnfіg.txt     ← 基本正确
  「干净度」判据把 en 当成干净的，因为它没有 $ _ 这类脏字符。

本版新增特征：
  mixed  词内数字字母混排比（长文本才用，短数字行会误伤）
  lenr   两份结果长度比（短得多的那份可能是读残了）
  cyrdom 西里尔占比
  junk   字典外字符占比

严格要求（三条同时满足才算候选）：
  A. 俄文段 ≥ 9/11
  B. 行级 ≥ 65/80（一格不掉）
  C. 行-20、行-28 不许丢

跑法：python "tools\脚本\P1前置-判据扫描3.py"
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


# ================= 特征 =================
def feats(text, conf, vocab):
    if not text:
        return dict(junk=1.0, mixed=0.0, cyr=0.0, lat=0.0, n=0, conf=conf, words=0)
    body = [c for c in text if not c.isspace()]
    n = len(body)
    if n == 0:
        return dict(junk=1.0, mixed=0.0, cyr=0.0, lat=0.0, n=0, conf=conf, words=0)
    junk = sum(1 for c in body if c not in vocab) / n
    cyr = sum(1 for c in body if 0x0400 <= ord(c) <= 0x04FF) / n
    lat = sum(1 for c in body if c.isascii() and c.isalpha()) / n
    ws = [w for w in text.split() if any(c.isalpha() for c in w)]
    mixed = (sum(1 for w in ws if any(c.isdigit() for c in w))
             / len(ws)) if ws else 0.0
    return dict(junk=junk, mixed=mixed, cyr=cyr, lat=lat, n=n, conf=conf, words=len(ws))


def pick(fe, fc, rule):
    """返回 'en' 或 'cyr'。rule 是判据名。"""
    # 硬闸：junk 超标的先淘汰
    if rule in ('junk_first', 'junk_len', 'full'):
        e_ok = fe['junk'] < 0.02
        c_ok = fc['junk'] < 0.02
        if c_ok and not e_ok:
            return 'cyr'
        if e_ok and not c_ok:
            return 'en'

    if rule == 'junk_first':
        # 都干净 → 保 en（现状优先）
        return 'en'

    if rule == 'junk_len':
        # 都干净 → 谁更长用谁（读残的往往短）
        if fc['n'] > fe['n'] * 1.15 and fc['n'] >= 8:
            return 'cyr'
        return 'en'

    if rule == 'full':
        # 都干净 → 综合：长文本看混排，一切看长度与西里尔完整度
        score_e, score_c = 0.0, 0.0
        # 长文本（≥15 字符）里，混排比高 = 像乱码，扣分
        if fe['n'] >= 15:
            score_e -= fe['mixed'] * 2.0
        if fc['n'] >= 15:
            score_c -= fc['mixed'] * 2.0
        # 长度：短得多的可能读残
        score_e += min(fe['n'], 60) / 60.0 * 0.5
        score_c += min(fc['n'], 60) / 60.0 * 0.5
        # 西里尔/拉丁要「纯」：一半一半往往是假货
        score_e -= abs(fe['cyr']) * 0.1
        score_c += fc['cyr'] * 0.8 if fc['cyr'] > 0.5 else 0.0
        score_e += fe['cyr'] * 0.8 if fe['cyr'] > 0.5 else 0.0
        if score_c > score_e + 0.02:
            return 'cyr'
        return 'en'

    if rule == 'cyr_long':
        # 俄文候选：cyr 结果西里尔占比高 且（en 结果混排高 或 en 结果短）
        if fc['cyr'] >= 0.50 and fc['junk'] < 0.02:
            if fe['mixed'] >= 0.15 or fe['n'] < fc['n'] * 0.8:
                return 'cyr'
        return 'en'

    return 'en'


# ================= 语料 =================
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
ru_cache = []
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
    ru_cache.append((seg, truth[seg], feats(te, ce, model_en.vocab),
                     feats(tc, cc, model_cy.vocab), te, tc))

data = json.load(open(os.path.join(ROOT, 'tools', '素材', '行真值.json'), encoding='utf-8-sig'))
en_cache = []
for im in data['images']:
    fp = os.path.join(CORPUS, im['file'])
    if not os.path.exists(fp):
        continue
    img = Image.open(fp)
    te, ce = model_en.run(img)
    tc, cc = model_cy.run(img)
    en_cache.append((im['id'], '\n'.join(im['lines']), feats(te, ce, model_en.vocab),
                     feats(tc, cc, model_cy.vocab), te, tc))

p('语料：俄文段 %d，行级 %d' % (len(ru_cache), len(en_cache)))
p('裸模型基线：俄 en %d/%d cyr %d/%d ｜ 行 en %d/%d cyr %d/%d'
  % (sum(1 for r in ru_cache if lcs_ratio(r[1], r[4]) >= 0.90), len(ru_cache),
     sum(1 for r in ru_cache if lcs_ratio(r[1], r[5]) >= 0.90), len(ru_cache),
     sum(1 for r in en_cache if lcs_ratio(r[1], r[4]) >= 0.90), len(en_cache),
     sum(1 for r in en_cache if lcs_ratio(r[1], r[5]) >= 0.90), len(en_cache)))

RULES = ['junk_first', 'junk_len', 'full', 'cyr_long']
p()
p('%-12s %-12s %-12s %-14s %s' % ('判据', '俄文段', '行级', '丢的格', '结果'))
p('-' * 72)
best = []
for rule in RULES:
    ru_ok = 0
    for seg, want, fe, fc, te, tc in ru_cache:
        use = tc if pick(fe, fc, rule) == 'cyr' else te
        if lcs_ratio(want, use) >= 0.90:
            ru_ok += 1
    line_ok, lost, got = 0, [], []
    for rid, want, fe, fc, te, tc in en_cache:
        use = tc if pick(fe, fc, rule) == 'cyr' else te
        ok = lcs_ratio(want, use) >= 0.90
        line_ok += ok
        base_ok = lcs_ratio(want, te) >= 0.90
        if base_ok and not ok:
            lost.append(rid)
        if not base_ok and ok:
            got.append(rid)
    base = sum(1 for r in en_cache if lcs_ratio(r[1], r[4]) >= 0.90)
    verdict = 'PASS' if (ru_ok >= 9 and line_ok >= base and '行-20' not in lost and '行-28' not in lost) else 'fail'
    p('%-12s %2d/%-9d %2d/%-9d %-14s %s%s'
      % (rule, ru_ok, len(ru_cache), line_ok, len(en_cache),
         ','.join(lost) if lost else '无', verdict,
         ('  新赢:' + ','.join(got)) if got else ''))
    if verdict == 'PASS':
        best.append((ru_ok, line_ok, rule))

p()
if best:
    best.sort(key=lambda x: (-x[0], -x[1]))
    p('===== 通过：%s =====' % best[0][2])
    rule = best[0][2]
    p()
    p('俄文逐段：')
    for seg, want, fe, fc, te, tc in ru_cache:
        who = pick(fe, fc, rule)
        use = tc if who == 'cyr' else te
        ok = lcs_ratio(want, use) >= 0.90
        p('  %-10s %-3s %s' % (seg, who, 'OK' if ok else '差'))
        if not ok:
            p('      真: %s' % want.replace('\n', ' ')[:60])
            p('      得: %s' % use[:60])
else:
    p('❌ 四条判据全部不通过。')

txt = '\n'.join(OUT) + '\n'
open(os.path.join(TMP, 'P1前置-判据扫描3.txt'), 'w', encoding='utf-8').write(txt)
print()
print('已写入 tools\\临时\\_eslav\\P1前置-判据扫描3.txt')
