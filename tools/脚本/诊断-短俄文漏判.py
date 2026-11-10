# -*- coding: utf-8 -*-
r"""诊断：用户反馈的「明显英文/短俄文被识别成英文然后乱码」。

针对用户发来的截图（Старые хиты），看两个模型各读出什么、
以及 cyr_long 判据为什么选错了。

跑法：python "tools\脚本\诊断-短俄文漏判.py"
"""
import os
import sys

import numpy as np
import onnxruntime as ort
from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TMP = os.path.join(ROOT, 'tools', '临时', '_eslav')
REL = os.path.join(ROOT, 'release', 'v0.4.13', 'models', 'v5')
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


def judge(fe, fc):
    """当前线上判据 cyr_long：返回 ('cyr'|'en', 原因)"""
    if fc['n'] == 0:
        return 'en', 'cyr 空'
    if fc['cyr'] < 0.50:
        return 'en', 'cyr 西里尔 %.2f < 0.50' % fc['cyr']
    if fc['junk'] >= 0.02:
        return 'en', 'cyr 脏字符 %.2f >= 0.02' % fc['junk']
    if fe['mixed'] >= 0.15:
        return 'cyr', 'en 混排 %.2f >= 0.15' % fe['mixed']
    if fe['n'] < fc['n'] * 0.8:
        return 'cyr', 'en 太短 %d < %d*0.8' % (fe['n'], fc['n'])
    return 'en', '两条触发条件都没满足（en 混排 %.2f < 0.15，en 长度 %d 不短于 cyr %d*0.8）' % (
        fe['mixed'], fe['n'], fc['n'])


# ============ 用户发来的那张图 ============
p('===== ① 用户截图（Старые хиты）=====')
IMG = os.path.join(ROOT, '.dsh-vision-router', 'artifacts', '.runs',
                   '.vision-run-handoff', 'materialized', 'a1bd38e8a8b1a545df72.png')
if os.path.exists(IMG):
    img = Image.open(IMG)
    p('图尺寸：%s' % (img.size,))
    te, ce = model_en.run(img)
    tc, cc = model_cy.run(img)
    fe, fc = feats(te, ce, model_en.vocab), feats(tc, cc, model_cy.vocab)
    p('  en : %r  分 %.3f  混排 %.2f  拉丁 %.2f' % (te, ce, fe['mixed'], fe['lat']))
    p('  cyr: %r  分 %.3f  西里尔 %.2f  脏 %.2f' % (tc, cc, fc['cyr'], fc['junk']))
    who, why = judge(fe, fc)
    p('  当前判据选 → %s（%s）' % (who, why))
    p('  真值：Старые хиты')
    p('  %s' % ('✅ 判对了' if who == 'cyr' else '❌ 选了 en = 用户看到的乱码'))
else:
    p('图不存在：%s' % IMG)

# ============ 复现：短俄文 + 明显英文的混合 ============
p()
p('===== ② 复现用户说的「有很明显的英文就整个变英文乱码」=====')
FONTS = [(r'C:\Windows\Fonts\arial.ttf', 'Arial'),
         (r'C:\Windows\Fonts\segoeui.ttf', 'Segoe UI')]


def render(text, fp, size=26):
    f = ImageFont.truetype(fp, size)
    b = f.getbbox(text)
    tw, th = b[2] - b[0], b[3] - b[1]
    img = Image.new('RGB', (tw + 24, th + 24), (26, 26, 26))
    ImageDraw.Draw(img).text((12 - b[0], 12 - b[1]), text, font=f, fill=(240, 240, 240))
    return img


CASES = [
    ('Старые хиты', '纯短俄文'),
    ('Старые хиты FPS', '俄文 + 明显英文'),
    ('Главное меню', '纯短俄文'),
    ('Главное меню SETTINGS', '俄文 + 英文'),
    ('Играть', '单个俄文词'),
    ('Выход Exit', '俄文 + 英文词'),
    ('Новая игра New Game', '俄文 + 英文'),
    ('Ошибка сервера', '短俄文句'),
    ('Ошибка сервера Error', '短俄文 + 英文'),
]

for fp, fname in FONTS:
    if not os.path.exists(fp):
        continue
    p()
    p('--- %s（深底浅字，模拟游戏 UI）---' % fname)
    for text, label in CASES:
        img = render(text, fp)
        te, ce = model_en.run(img)
        tc, cc = model_cy.run(img)
        fe, fc = feats(te, ce, model_en.vocab), feats(tc, cc, model_cy.vocab)
        who, why = judge(fe, fc)
        mark = '✅' if who == 'cyr' else '❌'
        p('  %s %-28s [%-14s] 选=%s' % (mark, text, label, who))
        p('      en =%r' % te)
        p('      cyr=%r' % tc)
        p('      → %s' % why)

txt = '\n'.join(OUT) + '\n'
open(os.path.join(TMP, '诊断-短俄文漏判.txt'), 'w', encoding='utf-8').write(txt)
print()
print('已写入 tools\\临时\\_eslav\\诊断-短俄文漏判.txt')
