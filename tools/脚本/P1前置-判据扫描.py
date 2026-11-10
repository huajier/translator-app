# -*- coding: utf-8 -*-
r"""第 1 批前置：离线扫「该用哪个模型」的判据阈值。

三批语料：
  A. 11 段俄文整段图（先切行，逐行识别）
  B. 80 张真实游戏行级图（有真值）
  C. 合成文本行（俄文 + 英文，3 种字体）

对每张图/每行，两个模型各出一份结果。然后扫判据：
  判据的活儿是回答「en 的结果可疑吗？可疑才换 cyrillic」。
  要求：俄文段拿到 cyrillic 的结果、英文一格不掉、行-20/行-28 必须被拦住。

跑法：python "tools\脚本\P1前置-判据扫描.py"
输出：tools\临时\_eslav\P1前置-判据扫描.txt
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
    """PaddleOCR rec：48 高、BGR、归一化到 [-1,1]。"""

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


# ================= 图像工具 =================
def split_lines(img, min_h=6, gap_ratio=0.12):
    """按水平投影切行。返回切出来的行图列表（保持阅读顺序）。"""
    g = img.convert('L')
    w, h = g.size
    a = np.array(g)
    # 浅字深底 or 深字浅底：用中位数判断
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
    # 合并过窄的带（同一行被切碎）
    merged = []
    gap_min = max(2, int(h * gap_ratio * 0.2))
    for b in bands:
        if merged and b[0] - merged[-1][1] < gap_min:
            merged[-1] = (merged[-1][0], b[1])
        else:
            merged.append(list(b))
    out = []
    for y0, y1 in merged:
        if y1 - y0 < min_h:
            continue
        crop = img.crop((0, y0, w, y1))
        # 横向也裁掉空白边
        ga = np.array(crop.convert('L'))
        col_ink = (ga < 160).sum(axis=0) if ga.mean() >= 128 else (255 - ga < 160).sum(axis=0)
        nz = np.nonzero(col_ink > 0)[0]
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


# ================= 判据特征 =================
def feats(text, conf, model):
    """给一份识别结果算「有多可疑」的几组特征。"""
    if not text:
        return dict(conf=0.0, mixed=0.0, junk=0.0, cyr=0.0, n=0)
    ws = [w for w in text.split() if any(c.isalpha() for c in w)]
    mixed = (sum(1 for w in ws if any(c.isdigit() for c in w) and any(c.isalpha() for c in w))
             / len(ws)) if ws else 0.0
    # junk：不在字典里的非空白字符占比（假西里尔常带 $ _ 等脏字符）
    body = [c for c in text if not c.isspace()]
    junk = (sum(1 for c in body if c not in model.vocab) / len(body)) if body else 0.0
    cyr = (sum(1 for c in body if 0x0400 <= ord(c) <= 0x04FF) / len(body)) if body else 0.0
    return dict(conf=conf, mixed=mixed, junk=junk, cyr=cyr, n=len(body))


# ================= 语料 A：俄文 11 段 =================
p('===== 语料 A：俄文 11 段（切行后逐行识别）=====')
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
CORPUS = os.path.join(ROOT, 'tools', '语料')

ru_rows = []
for seg in RU:
    mat, code = seg.split('-', 1)
    d = os.path.join(CORPUS, mat, code)
    if not os.path.isdir(d):
        p('  %s → 目录不存在，跳过' % seg)
        continue
    imgs = sorted(f for f in os.listdir(d) if f.lower().endswith(('.png', '.jpg')))
    if not imgs:
        continue
    fp = os.path.join(d, imgs[0])   # 第 1 张代表该段
    img = Image.open(fp)
    lines = split_lines(img)
    got_en = ' '.join(model_en.run(l)[0] for l in lines)
    got_cy = ' '.join(model_cy.run(l)[0] for l in lines)
    want = truth[seg]
    ru_rows.append((seg, want, got_en, got_cy, fp, len(lines)))
    mark_cy = 'OK' if lcs_ratio(want, got_cy) >= 0.90 else '差'
    mark_en = 'OK' if lcs_ratio(want, got_en) >= 0.90 else '差'
    p('%-10s 切%d行 | en %s | cyr %s' % (seg, len(lines), mark_en, mark_cy))
    p('    真: %s' % want.replace('\n', ' ')[:66])
    p('    en: %s' % got_en[:66])
    p('   cyr: %s' % got_cy[:66])

p()
p('俄文段小计（裸模型，无任何闸门）：en 对 %d/%d，cyr 对 %d/%d'
  % (sum(1 for r in ru_rows if lcs_ratio(r[1], r[2]) >= 0.90), len(ru_rows),
     sum(1 for r in ru_rows if lcs_ratio(r[1], r[3]) >= 0.90), len(ru_rows)))

# ================= 语料 B：80 张行图 =================
p()
p('===== 语料 B：真实游戏行级图 80 张 =====')
data = json.load(open(os.path.join(ROOT, 'tools', '素材', '行真值.json'), encoding='utf-8-sig'))
en_rows = []
for im in data['images']:
    fp = os.path.join(CORPUS, im['file'])
    if not os.path.exists(fp):
        continue
    img = Image.open(fp)
    t_en, c_en = model_en.run(img)
    t_cy, c_cy = model_cy.run(img)
    en_rows.append(dict(id=im['id'], want='\n'.join(im['lines']), en=t_en, cy=t_cy,
                        fen=feats(t_en, c_en, model_en), fcy=feats(t_cy, c_cy, model_cy)))
p('可测 %d 张' % len(en_rows))
p('裸模型：en 对 %d/%d，cyr 对 %d/%d'
  % (sum(1 for r in en_rows if lcs_ratio(r['want'], r['en']) >= 0.90), len(en_rows),
     sum(1 for r in en_rows if lcs_ratio(r['want'], r['cy']) >= 0.90), len(en_rows)))

# ================= 扫判据 =================
p()
p('===== 扫判据：en 结果可疑吗？可疑才换成 cyr =====')
p('每个候选 = (用什么特征, 阈值, 方向)。换之后 cyr 那份还要过一次验收门。')

CANDS = []
for th in (0.10, 0.15, 0.20, 0.25, 0.30):
    CANDS.append(('mixed', th, '>='))
for th in (0.90, 0.93, 0.95, 0.97):
    CANDS.append(('conf', th, '<'))
for th in (0.02, 0.05, 0.10):
    CANDS.append(('junk', th, '>='))
# 组合
for m in (0.15, 0.25):
    for c in (0.93, 0.95):
        CANDS.append(('mixed|conf', (m, c), 'or'))


def suspect_en(f, key, th):
    if key == 'mixed':
        return f['mixed'] >= th
    if key == 'conf':
        return f['conf'] < th
    if key == 'junk':
        return f['junk'] >= th
    if key == 'mixed|conf':
        m, c = th
        return f['mixed'] >= m or f['conf'] < c
    return False


def cyr_acceptable(f_cy, f_en, mode):
    """cyr 那份够不够格顶掉 en 那份。"""
    if mode == 'none':
        return True
    if mode == 'junk':
        return f_cy['junk'] < 0.02
    if mode == 'junk+n':
        return f_cy['junk'] < 0.02 and f_cy['n'] >= 2
    if mode == 'better':
        return f_cy['junk'] <= f_en['junk'] and f_cy['conf'] >= f_en['conf'] * 0.85
    return True


ACCEPT_MODES = ['none', 'junk']
p()
hdr = '%-16s %-8s %-10s %-10s %-8s' % ('判据', '验收门', '俄文段', '行级', '行-20/28')
p(hdr)
p('-' * len(hdr))
best = []
for key, th, _dir in CANDS:
    for mode in ACCEPT_MODES:
        # 俄文段
        ru_ok = 0
        for seg, want, got_en, got_cy, fp, nl in ru_rows:
            fe = feats(got_en, 0.0, model_en)
            fc = feats(got_cy, 0.0, model_cy)
            use = got_cy if (suspect_en(fe, key, th) and cyr_acceptable(fc, fe, mode)) else got_en
            if lcs_ratio(want, use) >= 0.90:
                ru_ok += 1
        # 行级
        line_ok = 0
        bad = []
        for r in en_rows:
            use = r['cy'] if (suspect_en(r['fen'], key, th) and cyr_acceptable(r['fcy'], r['fen'], mode)) else r['en']
            ok = lcs_ratio(r['want'], use) >= 0.90
            line_ok += ok
            if not ok and r['id'] in ('行-20', '行-28'):
                bad.append(r['id'])
        mark = '拦住' if not bad else ','.join(bad)
        label = '%s%s%s' % (key, _dir, th if not isinstance(th, tuple) else str(th))
        p('%-16s %-8s %2d/%-7d %2d/%-7d %-8s'
          % (label, mode, ru_ok, len(ru_rows), line_ok, len(en_rows), mark))
        best.append((ru_ok, line_ok, key, th, mode, len(bad)))

p()
base_line = sum(1 for r in en_rows if lcs_ratio(r['want'], r['en']) >= 0.90)
p('基线（纯 en，不换）：行级 %d/%d' % (base_line, len(en_rows)))
good = [b for b in best if b[1] >= base_line and b[5] == 0]
good.sort(key=lambda x: (-x[0], -x[1]))
p()
if good:
    p('===== 满足「行级不掉 + 行-20/28 拦住」的候选 =====')
    for ru_ok, line_ok, key, th, mode, _ in good[:10]:
        p('  俄文 %2d/%d  行级 %2d/%d   判据=%s %s %s  门=%s'
          % (ru_ok, len(ru_rows), line_ok, len(en_rows), key, th, '', mode))
else:
    p('❌ 没有任何候选同时满足两个条件。')

txt = '\n'.join(OUT) + '\n'
open(os.path.join(TMP, 'P1前置-判据扫描.txt'), 'w', encoding='utf-8').write(txt)
print()
print('已写入 tools\\临时\\_eslav\\P1前置-判据扫描.txt')
