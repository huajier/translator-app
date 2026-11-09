# -*- coding: utf-8 -*-
r"""P1（俄文乱码）证据复核脚本 —— 架构师交付物，只读，不碰 src/ 与 release/。

跑法：python "tools\脚本\P1-俄文闸门-证据.py"
输出：tools\日志\阶段三-P1-俄文闸门-证据.txt（同时打印到屏幕）

它复现「阶段三-P1-俄文闸门-提示词.md」里的每一个数字。数据源：
  ① release\v0.4.13\models\v5\*.txt + 模型/语言包（存在性、条目统计）
  ② tools\日志\app-20261107.log（RapidOCR 是否吐过西里尔 + 各通道耗时）
  ③ tools\日志\app-20261109.log（调 RapidOCR「之前」手上有什么文本）
  ④ tools\临时\AB-实验\开/关-*结果.txt + tools\素材\行真值.json + 三份标准答案
     （闸门方案的段级/行级得失）
"""
import json
import os
import re
import statistics
import sys

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = []


def p(s=''):
    OUT.append(s)
    print(s)


def load_kv(path):
    d = {}
    for line in open(path, 'rb').read().decode('utf-8', errors='replace').splitlines():
        if '|' in line:
            k, v = line.split('|', 1)
            d[k.strip()] = v
    return d


def norm(s):
    return re.sub(r'\s+', '', s or '').lower()


def ok(ans, got):
    A, G = norm(ans), norm(got)
    return bool(A) and (A == G or A in G)


HOMO = set('АВЕКМНОРСТУХаеорсух')


def cyr_ratio(s):
    L = [c for c in (s or '') if c.isalpha()]
    return 0.0 if not L else sum(1 for c in L if 0x0400 <= ord(c) <= 0x04FF) / len(L)


def homo_ratio(s):
    cy = [c for c in (s or '') if 0x0400 <= ord(c) <= 0x04FF]
    return 0.0 if not cy else sum(1 for c in cy if c in HOMO) / len(cy)


def mixed_ratio(t):
    """词内数字字母混排占比：含字母的词里，同时含数字的占多少。"""
    toks = [x for x in re.findall(r'\S+', t or '') if any(c.isalpha() for c in x)]
    if not toks:
        return 0.0
    return sum(1 for x in toks
               if any(c.isdigit() for c in x) and any(c.isalpha() for c in x)) / len(toks)


# ============ ① 字典与模型文件 ============
p('===== ① release\\v0.4.13 模型与字典 =====')
base = os.path.join(ROOT, 'release', 'v0.4.13')
for name in ('ppocrv5_en_dict.txt', 'ppocrv5_latin_dict.txt'):
    path = os.path.join(base, 'models', 'v5', name)
    raw = open(path, 'rb').read()
    lines = raw.decode('utf-8', errors='replace').splitlines()
    cyr_lines = [(i + 1, l) for i, l in enumerate(lines)
                 if any(0x0400 <= ord(c) <= 0x04FF for c in l)]
    nonascii = sum(1 for l in lines if any(ord(c) > 127 for c in l))
    p('%s' % name)
    p('  行数=%d  含西里尔(U+0400~U+04FF)条目=%d  含非ASCII条目=%d  字节=%d'
      % (len(lines), len(cyr_lines), nonascii, len(raw)))
    for ln, txt in cyr_lines:
        p('  ← 第 %d 行含西里尔：%r（UTF-8 字节=%s，码点=%s），该行长度=%d 字符'
          % (ln, txt, txt.encode('utf-8'),
             [hex(ord(c)) for c in txt if 0x0400 <= ord(c) <= 0x04FF], len(txt)))
    cps = sorted({hex(ord(c)) for l in lines for c in l if ord(c) > 127})
    p('  非ASCII 码点种类=%d，样例=%s' % (len(cps), ','.join(cps[:14])))

for name, sub in (('en_PP-OCRv5_rec_mobile.onnx', ('models', 'v5')),
                  ('rus.traineddata', ('tessdata',)),
                  ('eng.traineddata', ('tessdata',)),
                  ('chi_sim.traineddata', ('tessdata',))):
    path = os.path.join(base, *sub, name)
    p('  %-32s 存在=%s 大小=%s' % (name, os.path.exists(path),
                                   os.path.getsize(path) if os.path.exists(path) else 'NA'))
p('  models\\v5 下的文件：%s' % sorted(os.listdir(os.path.join(base, 'models', 'v5'))))
p('  tessdata 下的文件：%s' % sorted(os.listdir(os.path.join(base, 'tessdata'))))

# ============ ② 11-07 回放：RapidOCR 产出过西里尔吗 ============
p()
p('===== ② app-20261107.log 最后一次回放（14:35 起）=====')
log7 = os.path.join(ROOT, 'tools', '日志', 'app-20261107.log')
lines7 = open(log7, 'rb').read().decode('utf-8', errors='replace').splitlines()
anchor = [i for i, l in enumerate(lines7) if '回放 ' in l]
last = [i for i in anchor if lines7[i][:18] >= '[2026-11-07 14:35']
seg = None
chan, ms, score, final = {}, {}, {}, {}
for l in lines7[last[0]:]:
    m = re.search(r'回放 (\S+?)[：:]', l)
    if m:
        seg = m.group(1)
        mt = re.search(r'认字 (\d+) ms', l)
        if mt:
            ms[seg] = int(mt.group(1))
    m2 = re.search(r'【RapidOCR 主通道】认到 (\d+) 个有效字符，平均分 ([\d.]+)', l)
    if m2 and seg:
        score[seg] = float(m2.group(2))
    c = re.search(r'通道=(\S+)', l)
    if c and seg:
        chan[seg] = c.group(1)
    m3 = re.search(r'最终原文：(.*)$', l)
    if m3 and seg:
        final[seg] = m3.group(1)

p('回放段数=%d，有通道记录的=%d' % (len(set(chan)), len(chan)))
for c in ('RapidOCR', 'Tesseract', 'WindowsOcr'):
    ids = [s for s in chan if chan[s] == c]
    withcyr = [s for s in ids if cyr_ratio(final.get(s, '')) > 0]
    v = sorted(ms[s] for s in ids if s in ms)
    p('  通道=%-10s n=%-4d 最终原文含西里尔=%-3d 认字ms median=%s p95=%s'
      % (c, len(ids), len(withcyr),
         statistics.median(v) if v else '-',
         v[min(len(v) - 1, int(0.95 * len(v)))] if v else '-'))

RU_SEG = ('材料1-B2', '材料2-R', '材料3-M3')
ru_ids = [s for s in chan if any(s.startswith(k) for k in RU_SEG)]
ru_rap = [s for s in ru_ids if chan[s] == 'RapidOCR']
p('俄文回放图=%d 张：走 RapidOCR %d 张、走 Tesseract %d 张'
  % (len(ru_ids), len(ru_rap), sum(1 for s in ru_ids if chan[s] == 'Tesseract')))
p('  走 RapidOCR 的 %d 张里，最终原文含西里尔字母的：%d 张'
  % (len(ru_rap), sum(1 for s in ru_rap if cyr_ratio(final.get(s, '')) > 0)))
p('  （回放图 55 张 ≠ 标准答案 11 段：R1~R9 每段回放 5~6 张，段级成绩按 11 段算）')

# ============ ③ 11-09：调 RapidOCR「之前」手上有什么 ============
p()
p('===== ③ app-20261109.log：调 RapidOCR 之前（Windows 第一遍）的文本 =====')
log9 = os.path.join(ROOT, 'tools', '日志', 'app-20261109.log')
lines9 = open(log9, 'rb').read().decode('utf-8', errors='replace').splitlines()
cur = None
caps = []
for l in lines9:
    m = re.search(r'第一遍原文：(.*?)（人话分', l)
    if m:
        cur = m.group(1).strip()
    if '取词成功：' in l or '取词失败' in l:
        mm = re.search(r'语言=([^ （)]+)', l)
        caps.append((cur or '', mm.group(1) if mm else '?'))
ru_caps = [c for c in caps if c[1] == '俄文']
p('取词总次数=%d，其中判为俄文=%d' % (len(caps), len(ru_caps)))
p('这 %d 次俄文里，第一遍文本含西里尔字母的次数=%d'
  % (len(ru_caps), sum(1 for t, _ in ru_caps if cyr_ratio(t) > 0)))
near_empty = [t for t, _ in ru_caps if sum(1 for c in t if c.isalnum()) < 2]
p('第一遍文本几乎为空（有效字符<2）的次数=%d (%.1f%%)'
  % (len(near_empty), 100.0 * len(near_empty) / max(1, len(ru_caps))))
samples = [t for t, _ in ru_caps if t and t != '（空）'][:3]
for s in samples:
    p('  非空样本：%r' % s[:80])

# ============ ④ 闸门方案得失 ============
p()
p('===== ④ 闸门方案：段级/行级得失（同一批回放）=====')
AB = os.path.join(ROOT, 'tools', '临时', 'AB-实验')
MAT = os.path.join(ROOT, 'tools', '素材')
kaiF = load_kv(os.path.join(AB, '开-全量结果.txt'))
guanF = load_kv(os.path.join(AB, '关-全量结果.txt'))
kaiL = load_kv(os.path.join(AB, '开-行级结果.txt'))
guanL = load_kv(os.path.join(AB, '关-行级结果.txt'))
truthL = {im['id']: '\n'.join(im['lines']) for im in
          json.load(open(os.path.join(MAT, '行真值.json'), encoding='utf-8-sig'))['images']}
ans = {}
for i, n in enumerate(('验收标准答案.txt', '验收标准答案2.txt', '验收标准答案3.txt'), 1):
    for line in open(os.path.join(MAT, n), encoding='utf-8-sig'):
        line = line.strip()
        if line and not line.startswith('#') and '|' in line:
            k, v = line.split('|', 1)
            ans['材料%d-%s' % (i, k.strip())] = v.strip()

ru_ans = sorted(k for k in ans if any(k.startswith(x) for x in ('材料1-B2', '材料2-R', '材料3-M3')))
p('段级：共 %d 段，其中俄文 %d 段 → %s' % (len(ans), len(ru_ans), '、'.join(ru_ans)))

p('基线：纯 RapidOCR 段级 %d/%d（俄文 %d/%d）、行级 %d/80'
  % (sum(1 for k in ans if ok(ans[k], kaiF.get(k, ''))), len(ans),
     sum(1 for k in ru_ans if ok(ans[k], kaiF.get(k, ''))), len(ru_ans),
     sum(1 for k in kaiL if ok(truthL.get(k), kaiL[k]))))
p('参照：纯 Tesseract 段级 %d/%d（俄文 %d/%d）、行级 %d/80'
  % (sum(1 for k in ans if ok(ans[k], guanF.get(k, ''))), len(ans),
     sum(1 for k in ru_ans if ok(ans[k], guanF.get(k, ''))), len(ru_ans),
     sum(1 for k in kaiL if ok(truthL.get(k), guanL[k]))))


def accept(t):
    return cyr_ratio(t) >= 0.50 and homo_ratio(t) < 0.50


cands = [
    ('① 只 mixed>=0.15', lambda t, s: mixed_ratio(t) >= 0.15),
    ('② 只 score<0.95', lambda t, s: s is not None and s < 0.95),
    ('③ mixed>=0.15 或 score<0.95（推荐）', lambda t, s: mixed_ratio(t) >= 0.15 or (s is not None and s < 0.95)),
    ('④ 只 score<0.90', lambda t, s: s is not None and s < 0.90),
    ('⑤ 同形字护栏放到 0.75（错的做法）', None),
]
p()
p('%-34s %-9s %-9s %-9s %-12s %s' % ('闸门', '段级', '俄文段', '行级', '英文行图触发', '行级丢失'))
for label, gate in cands:
    homo_cap = 0.75 if gate is None else 0.50
    g = (lambda t, s: (mixed_ratio(t) >= 0.15 or (s is not None and s < 0.95))) if gate is None else gate

    def acc(t, cap=homo_cap):
        return cyr_ratio(t) >= 0.50 and homo_ratio(t) < cap

    seg_ok = ru_ok = 0
    for k in ans:
        R, T = kaiF.get(k, ''), guanF.get(k, '')
        use_t = g(R, score.get(k)) and acc(T)
        txt = T if use_t else R
        if ok(ans[k], txt):
            seg_ok += 1
            if k in ru_ans:
                ru_ok += 1
    line_ok = fire = 0
    lost = []
    for k in kaiL:
        R, T = kaiL[k], guanL.get(k, '')
        f = g(R, score.get(k))
        if f:
            fire += 1
        txt = T if (f and acc(T)) else R
        if ok(truthL.get(k), txt):
            line_ok += 1
        if ok(truthL.get(k), R) and not ok(truthL.get(k), txt):
            lost.append(k)
    p('%-34s %2d/%-6d %2d/%-6d %2d/%-6d %2d/80(%2.0f%%)    %s'
      % (label, seg_ok, len(ans), ru_ok, len(ru_ans), line_ok, len(kaiL),
         fire, 100.0 * fire / len(kaiL), ','.join(lost) or '无'))

p()
p('===== ⑤ 词内数字字母混排：分布（说明为什么会分得开）=====')
ru_vals = sorted(mixed_ratio(kaiF[k]) for k in ru_ans if k in kaiF)
other_vals = sorted(mixed_ratio(kaiF[k]) for k in ans if k not in ru_ans and k in kaiF)


def q(v, x):
    return v[min(len(v) - 1, int(x * len(v)))] if v else float('nan')


p('俄文段  P50=%.3f P90=%.3f   非俄文段 P50=%.3f P90=%.3f'
  % (q(ru_vals, .5), q(ru_vals, .9), q(other_vals, .5), q(other_vals, .9)))

p()
p('===== ⑥ RapidOCR 采纳样本的分数分布（§3.2 的依据）=====')
ru_sc = sorted(score[k] for k in ru_ans if k in score)
ot_sc = sorted(score[k] for k in ans if k not in ru_ans and k in score)
p('俄文段  n=%d  P10=%.2f P50=%.2f P90=%.2f'
  % (len(ru_sc), q(ru_sc, .1), q(ru_sc, .5), q(ru_sc, .9)))
p('非俄文段 n=%d  P10=%.2f P50=%.2f P90=%.2f'
  % (len(ot_sc), q(ot_sc, .1), q(ot_sc, .5), q(ot_sc, .9)))
p('没进 RapidOCR 通道的俄文段（连分数都没有）：%s'
  % '、'.join(k for k in ru_ans if k not in score))

txt = '\n'.join(OUT) + '\n'
dest = os.path.join(ROOT, 'tools', '日志', '阶段三-P1-俄文闸门-证据.txt')
open(dest, 'w', encoding='utf-8').write(txt)
print()
print('已写入：%s' % dest)
