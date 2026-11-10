# -*- coding: utf-8 -*-
r"""第 0 批：西里尔 rec 模型离线裸测（架构师验证脚本，只读 src/release）

不碰 src\、不碰 release\。只回答：这个模型到底会不会读俄文。

跑法：python "tools\脚本\P0-西里尔模型裸测.py"
输出：tools\临时\_eslav\P0-结果.txt
"""
import os
import sys

import numpy as np
import onnxruntime as ort
from PIL import Image, ImageDraw, ImageFont

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TMP = os.path.join(ROOT, 'tools', '临时', '_eslav')
os.makedirs(TMP, exist_ok=True)
OUT = []


def p(s=''):
    OUT.append(s)
    print(s)


# ============ 字典 ============
def parse_dict_from_yml(path):
    items = []
    started = False
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


chars = parse_dict_from_yml(os.path.join(TMP, 'itext_cyr.yml'))
decode = [''] + chars + [' ']

p('===== ① 字典与维度 =====')
cyr = [c for c in chars if len(c) == 1 and 0x0400 <= ord(c) <= 0x04FF]
lat = [c for c in chars if len(c) == 1 and c.isascii() and c.isalpha()]
p('character_dict = %d 条（西里尔 %d，拉丁 %d）' % (len(chars), len(cyr), len(lat)))

sess = ort.InferenceSession(os.path.join(TMP, 'cyr_rec.onnx'),
                            providers=['CPUExecutionProvider'])
inp = sess.get_inputs()[0]
out = sess.get_outputs()[0]
p('输入 %s，输出末维 %d vs %d+2=%d → %s'
  % (inp.shape, out.shape[-1], len(chars), len(chars) + 2,
     '对齐 ✓' if out.shape[-1] == len(chars) + 2 else '不对齐 ✗'))


# ============ 预处理与解码（PaddleOCR rec 标准流程）============
def preprocess_img(img, img_h=48, max_w=3200):
    w, h = img.size
    resized_w = min(int(np.ceil(img_h * w / float(h))), max_w)
    img = img.convert('RGB').resize((resized_w, img_h), Image.BICUBIC)
    arr = np.array(img, dtype=np.float32) / 255.0
    arr = (arr - 0.5) / 0.5
    return arr.transpose(2, 0, 1)[None, ...]


def decode_ctc(prob):
    idx = prob.argmax(axis=1)
    score = prob.max(axis=1)
    out_chars, confs = [], []
    prev = -1
    for i, k in enumerate(idx):
        if k != 0 and k != prev and k < len(decode):
            out_chars.append(decode[k])
            confs.append(float(score[i]))
        prev = k
    return ''.join(out_chars), (sum(confs) / len(confs) if confs else 0.0)


def recognize(img):
    o = sess.run(None, {inp.name: preprocess_img(img)})[0]
    return decode_ctc(o[0])


# ============ 合成文本行：俄文 + 英文对照 ============
p()
p('===== ② 合成文本行识别（直接验「会不会读俄文」）=====')

FONTS = [
    (r'C:\Windows\Fonts\arial.ttf', 'Arial'),
    (r'C:\Windows\Fonts\segoeui.ttf', 'Segoe UI'),
    (r'C:\Windows\Fonts\times.ttf', 'Times'),
]


def render(text, font_path, size=28, w=None, bg=(255, 255, 255), fg=(0, 0, 0)):
    f = ImageFont.truetype(font_path, size)
    bbox = f.getbbox(text)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    pad = 12
    W = w or (tw + pad * 2)
    H = th + pad * 2
    img = Image.new('RGB', (max(W, 40), max(H, 20)), bg)
    ImageDraw.Draw(img).text((pad - bbox[0], pad - bbox[1]), text, font=f, fill=fg)
    return img


RU_CASES = [
    'Съешь же ещё этих мягких французских булок да выпей чаю',
    'Ошибка 404 (страница не найдена): файл не найден',
    'Ошибка сервера. Попробуйте обновить страницу позже.',
    'Машинный перевод — это раздел компьютерной лингвистики',
    'Обновите ПО до версии 2.0 и перезапустите',
    'Версия ПО: ошибка 500, попробуйте позже',
]
EN_CASES = [
    'Increases ranged and thrown ATK',
    'Improves movement speed while carrying small weights',
    'WALL BUILDING TOOL',
    'SG-225 Breaker',
    'Use the Block command without holding a shield to',
    'Standard Bicycle 3d 8h',
]


def norm(s):
    return ''.join(c.lower() for c in s if c.isalnum())


def score(want, got):
    """字符级：got 里有多少比例的字能和 want 对上（顺序敏感的最长公共子序列近似）。"""
    a, b = norm(want), norm(got)
    if not a:
        return 0.0
    # 简单 LCS
    m, n = len(a), len(b)
    if m * n > 400000:
        return 1.0 if a in b else 0.0
    prev = [0] * (n + 1)
    for i in range(1, m + 1):
        cur = [0] * (n + 1)
        ai = a[i - 1]
        for j in range(1, n + 1):
            cur[j] = prev[j - 1] + 1 if ai == b[j - 1] else max(prev[j], cur[j - 1])
        prev = cur
    return prev[n] / m


for fp, fname in FONTS:
    if not os.path.exists(fp):
        continue
    p()
    p('--- 字体 %s ---' % fname)
    ru_ok = en_ok = 0
    for t in RU_CASES:
        got, conf = recognize(render(t, fp))
        s = score(t, got)
        ok = s >= 0.90
        ru_ok += ok
        p('  [俄] %.2f 分 相似 %.2f  %s' % (conf, s, 'OK' if ok else '差'))
        p('       要: %s' % t[:50])
        p('       得: %s' % got[:50])
    for t in EN_CASES:
        got, conf = recognize(render(t, fp))
        s = score(t, got)
        ok = s >= 0.90
        en_ok += ok
        p('  [英] %.2f 分 相似 %.2f  %s  「%s」→「%s」' % (conf, s, 'OK' if ok else '差', t[:32], got[:40]))
    p('  → %s：俄文 %d/%d，英文 %d/%d' % (fname, ru_ok, len(RU_CASES), en_ok, len(EN_CASES)))

txt = '\n'.join(OUT) + '\n'
dest = os.path.join(TMP, 'P0-结果.txt')
open(dest, 'w', encoding='utf-8').write(txt)
print()
print('已写入：%s' % dest)
