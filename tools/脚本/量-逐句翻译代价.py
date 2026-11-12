# -*- coding: utf-8 -*-
r"""量：需求二（句句对照）的代价 —— 整段翻译 vs 逐句翻译。

用户场景（2026-11-11 确认）：以「甲」为主 —— 一段三五句的短段
（如魔法书说明 4 句）。乙（几十句长文）用得少。

本脚本用真实 Bing 接口量三件事：
  ① 整段一次性翻译：耗时、译文
  ② 逐句翻译（每句一次请求）：总耗时、各句译文
  ③ 译文质量差异（人工可读的对比输出）

**只读**：不碰 src\、不碰 release\。纯 HTTP 调 Bing 网页接口。

跑法：python "tools\脚本\量-逐句翻译代价.py"
输出：tools\临时\_bt\量-逐句翻译代价.txt
"""
import json
import re
import sys
import time
import urllib.parse
import urllib.request

sys.stdout.reconfigure(encoding='utf-8')

OUT = []


def p(s=''):
    OUT.append(s)
    print(s)


UA = ('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 '
      '(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36')


class Bing:
    """最小可用的 Bing 网页翻译客户端（和项目里 BingFreeProvider 同一条路）。"""

    def __init__(self):
        self.ig = None
        self.iid = None
        self.token = None
        self.key = None
        self.ms = 0.0

    def _get(self, url):
        req = urllib.request.Request(url, headers={
            'User-Agent': UA,
            'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
        })
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.read().decode('utf-8', 'replace')

    def ticket(self):
        t0 = time.perf_counter()
        html = self._get('https://cn.bing.com/translator')
        self.ms = (time.perf_counter() - t0) * 1000

        m = re.search(r'IG:"([0-9A-Fa-f]+)"', html)
        self.ig = m.group(1) if m else ''
        m = re.search(r'data-iid="([^"]+)"', html)
        self.iid = m.group(1) if m else 'translator.5028'
        # params: [时间戳作 key, token]
        m = re.search(r'params_AbusePreventionHelper\s*=\s*\[(\d+),\s*"([^"]+)"', html)
        if m:
            self.key, self.token = m.group(1), m.group(2)
        else:
            self.key, self.token = str(int(time.time() * 1000)), ''
        return bool(self.ig and self.token)

    def translate(self, text, to='zh-Hans'):
        t0 = time.perf_counter()
        url = (f'https://cn.bing.com/ttranslatev3?isVertical=1'
               f'&IG={self.ig}&IID={self.iid}')
        body = urllib.parse.urlencode({
            'fromLang': 'auto-detect',
            'text': text,
            'to': to,
            'token': self.token,
            'key': self.key,
        }).encode('utf-8')
        req = urllib.request.Request(url, data=body, headers={
            'User-Agent': UA,
            'Content-Type': 'application/x-www-form-urlencoded',
            'Referer': 'https://cn.bing.com/translator',
        })
        try:
            with urllib.request.urlopen(req, timeout=10) as r:
                raw = r.read().decode('utf-8', 'replace')
        except Exception as ex:
            return None, (time.perf_counter() - t0) * 1000, f'{type(ex).__name__}: {ex}'

        ms = (time.perf_counter() - t0) * 1000
        try:
            data = json.loads(raw)
        except Exception:
            return None, ms, f'非 JSON: {raw[:120]}'
        if isinstance(data, list) and data:
            d = data[0]
            if 'translations' in d:
                return d['translations'][0].get('text', ''), ms, None
            return None, ms, f'无 translations: {raw[:120]}'
        if isinstance(data, dict) and data.get('statusCode'):
            return None, ms, f"statusCode={data.get('statusCode')}"
        return None, ms, f'意外返回: {raw[:120]}'


def split_sentences(text):
    """按句号/问号/叹号切句，保留标点。俄文英文通用。"""
    parts = re.split(r'(?<=[.!?])\s+', text.strip())
    return [x for x in parts if x.strip()]


# ============ 样本：用户截图里那段魔法书说明（4 句） ============
# 用户截图右侧英文原文（Baldur's Gate 3 风格的游戏说明）
SAMPLE_SHORT = (
    "If the player reads the cursed magic book, it will cause the player to forget a spell, "
    "instantly removing a previously learned spell from the player's memory. "
    "At the same time, the magic book itself will be instantly destroyed. "
    "This includes spells learned through reading, class talent spells, and racial talent spells. "
    "If the player does not have any spells to forget, the magic book will still be destroyed, "
    "and a chat message will appear indicating that the player is temporarily experiencing confusion."
)

# 长文样本（用户之前框的太阳系那段，取其中一段俄文）
SAMPLE_LONG = (
    "Солнце — всего лишь очень обычная звезда во Вселенной, но оно является центральным небесным телом "
    "Солнечной системы. В Солнечной системе восемь планет, включая нашу Землю, некоторые карликовые планеты, "
    "кометы и бесчисленное множество других небесных тел движутся вокруг Солнца под действием его мощной гравитации. "
    "Область Солнечной системы огромна: только на примере Плутона, его орбита находится на расстоянии почти 40 "
    "астрономических единиц от Солнца, что составляет 6 миллиардов километров, а на самом деле границы Солнечной "
    "системы в десятки раз больше этого. Однако такой огромный Солнечная система в галактике — лишь капля в море."
)

b = Bing()
p('===== ① 取票 =====')
ok = b.ticket()
p('取票成功=%s  耗时=%.0f ms  IG=%s  有token=%s' % (ok, b.ms, b.ig[:8] if b.ig else '(空)', bool(b.token)))
if not ok:
    p('❌ 取票失败，后面的量都没意义。可能被反爬/网络问题。')
    open(r'tools\临时\_bt\量-逐句翻译代价.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)

for name, sample in (('短段（魔法书 5 句）', SAMPLE_SHORT), ('长段（太阳系 5 句）', SAMPLE_LONG)):
    sents = split_sentences(sample)
    p()
    p('=' * 66)
    p('样本：%s' % name)
    p('原文 %d 字，切出 %d 句：' % (len(sample), len(sents)))
    for i, s in enumerate(sents):
        p('  [%d] %s' % (i + 1, s[:72] + ('…' if len(s) > 72 else '')))

    # ---- A. 整段一次 ----
    p()
    p('--- A. 整段一次翻译 ---')
    t0 = time.perf_counter()
    whole, ms_a, err = b.translate(sample)
    wall_a = (time.perf_counter() - t0) * 1000
    if err:
        p('  ❌ 失败：%s' % err)
    else:
        p('  墙钟 %.0f ms（接口内计 %.0f ms），译文 %d 字' % (wall_a, ms_a, len(whole)))
        p('  译文：%s' % whole[:200].replace('\n', ' '))

    # ---- B. 逐句 ----
    p()
    p('--- B. 逐句翻译（每句一次请求）---')
    total = 0.0
    got = []
    for i, s in enumerate(sents):
        t0 = time.perf_counter()
        tr, ms, err = b.translate(s)
        w = (time.perf_counter() - t0) * 1000
        total += w
        if err:
            p('  [%d] ❌ %.0f ms  %s' % (i + 1, w, err))
            got.append(None)
        else:
            p('  [%d] %.0f ms  %s' % (i + 1, w, tr[:70]))
            got.append(tr)

    p()
    ok_n = sum(1 for x in got if x)
    p('  逐句合计墙钟 %.0f ms（%d/%d 句成功）' % (total, ok_n, len(sents)))
    if not err if 'err' in dir() else False:
        pass
    try:
        p('  相对整段：%.1f 倍' % (total / max(1, wall_a)))
    except Exception:
        pass

    # ---- C. 质量对比 ----
    p()
    p('--- C. 译文对比（人工看差别）---')
    if whole:
        p('  【整段】%s' % whole.replace('\n', ' ')[:400])
    for i, x in enumerate(got):
        if x:
            p('  【逐句%d】%s' % (i + 1, x[:200]))

p()
p('=' * 66)
p('说明：本脚本只调 Bing 公开网页接口，不碰 src\\ 和 release\\。')
p('注意：连续请求可能被限流，样本量只有 2 段，看量级即可。')

open(r'tools\临时\_bt\量-逐句翻译代价.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\量-逐句翻译代价.txt')
