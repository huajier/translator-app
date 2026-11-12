# -*- coding: utf-8 -*-
r"""Gale-Church 对齐 v2：切句时把「换行」也算边界。

v1 的问题（真机样本暴露）：
  "Obtaining Spellbooks\nBesides finding the odd spellbook..."
  被切成了 1 句（146 字）—— 标题和正文粘在一起，高亮会连带。

v2 规则：
  句子边界 = 句末标点(.!?…)
           或 换行（且下一行不是小写字母开头 —— 小写开头说明是上一行的续句）

  这个「下一行小写 = 续句」的判据很关键：
    · 屏幕上自动换行时，下一行通常是小写开头（句子没说完）
    · 真正的段落/标题后，下一行通常是大写开头

跑法：python "tools\脚本\量-GaleChurch对齐2.py"
"""
import json
import math
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

ABBREV = {'dr', 'mr', 'mrs', 'ms', 'st', 'vs', 'etc', 'e.g', 'i.e', 'cf', 'al',
          'т.е', 'т.д', 'др', 'проф', 'г', 'гг', 'рис', 'см', 'напр'}


def is_cjk(c):
    return '\u4e00' <= c <= '\u9fff'


def split_foreign(text):
    """外文切句：句末标点 OR 换行（下一行非小写开头）。"""
    text = (text or '').replace('\r\n', '\n').strip()
    if not text:
        return []

    # 先按「换行 + 下一行非小写字母开头」把文本切成若干块
    lines = text.split('\n')
    blocks = []
    cur = ''
    for i, raw in enumerate(lines):
        line = raw.rstrip()
        if not line.strip():
            if cur.strip():
                blocks.append(cur.strip())
            cur = ''
            continue
        stripped = line.lstrip()
        starts_lower = bool(stripped) and stripped[0].islower()
        if cur and not starts_lower:
            # 新块开始（上一块收口）
            blocks.append(cur.strip())
            cur = line
        else:
            # 续行
            cur = (cur + ' ' + line).strip() if cur else line
    if cur.strip():
        blocks.append(cur.strip())

    # 每块内部再按标点切句
    out = []
    for block in blocks:
        out.extend(_split_by_punct(block))
    return out


def _split_by_punct(text):
    out, buf, i, n = [], [], 0, len(text)
    while i < n:
        ch = text[i]
        buf.append(ch)
        if ch in '.!?…':
            j = i + 1
            while j < n and text[j] in '.!?…':
                buf.append(text[j])
                j += 1
            is_end = True
            if ch == '.':
                k = i - 1
                while k >= 0 and text[k].isdigit():
                    k -= 1
                if k < i - 1 and j < n and text[j].isdigit():
                    is_end = False
                m = re.search(r'([A-Za-zА-Яа-яЁё.]+)\.?$', ''.join(buf[:-1]))
                if m and is_end:
                    w = m.group(1).lower().rstrip('.')
                    if w in ABBREV:
                        is_end = False
                if is_end and j < n and text[j].isalnum():
                    tail = ''.join(buf[:-1])
                    if tail and not tail[-1].isspace() and '.' in tail.split()[-1]:
                        is_end = False
            if is_end and j < n and not text[j].isspace() and text[j] not in '。！？；':
                is_end = False
            if is_end:
                s = ''.join(buf).strip()
                if s:
                    out.append(s)
                buf = []
                while j < n and text[j] in ' \t':
                    j += 1
                i = j
                continue
            i = j
            continue
        i += 1
    tail = ''.join(buf).strip()
    if tail:
        out.append(tail)
    return out


def split_chinese(text):
    """中文切句：句末标点 + 换行（下一行非小写——中文没有大小写，用标点主导）。"""
    text = (text or '').replace('\r\n', '\n').strip()
    if not text:
        return []
    parts = re.split(r'(?<=[。！？；…])', text)
    out = []
    for part in parts:
        for line in part.split('\n'):
            s = line.strip()
            if s:
                out.append(s)
    return out


def align(src_sents, dst_sents, max_group=2):
    ns, nd = len(src_sents), len(dst_sents)
    if ns == 0 or nd == 0:
        return []
    src_len = [max(1, len(s)) for s in src_sents]
    dst_len = [max(1, len(s)) for s in dst_sents]
    ratio = (sum(dst_len) / nd) / (sum(src_len) / ns)
    ratio = max(0.15, min(ratio, 8.0))

    shapes = [(1, 1), (2, 1), (1, 2)]
    if max_group >= 3:
        shapes += [(3, 1), (1, 3)]

    INF = float('inf')
    cost = [[INF] * (nd + 1) for _ in range(ns + 1)]
    back = [[None] * (nd + 1) for _ in range(ns + 1)]
    cost[0][0] = 0.0

    def seg(arr, i, k):
        return sum(arr[i:i + k])

    for i in range(ns + 1):
        for j in range(nd + 1):
            if cost[i][j] == INF:
                continue
            for a, b in shapes:
                ni, nj = i + a, j + b
                if ni > ns or nj > nd:
                    continue
                ls, ld = seg(src_len, i, a), seg(dst_len, j, b)
                c = (math.log(ld / (ls * ratio)) ** 2) if ld > 0 and ls > 0 else 100.0
                if a != b:
                    c += 0.4
                nc = cost[i][j] + c
                if nc < cost[ni][nj]:
                    cost[ni][nj] = nc
                    back[ni][nj] = (i, j, a, b)

    pairs, i, j = [], ns, nd
    while i > 0 or j > 0:
        bk = back[i][j]
        if bk is None:
            if i > 0 and j > 0:
                pairs.append((list(range(i)), list(range(j))))
            break
        pi, pj, a, b = bk
        pairs.append((list(range(pi, i)), list(range(pj, j))))
        i, j = pi, pj
    pairs.reverse()
    return pairs


class Bing:
    def __init__(self):
        self.ig = self.token = self.key = None
        self.iid = 'translator.5028'

    def _get(self, url):
        req = urllib.request.Request(url, headers={'User-Agent': UA,
                                                  'Accept-Language': 'zh-CN,zh;q=0.9'})
        with urllib.request.urlopen(req, timeout=15) as r:
            return r.read().decode('utf-8', 'replace')

    def ticket(self):
        html = self._get('https://cn.bing.com/translator')
        m = re.search(r'IG:"([0-9A-Fa-f]+)"', html)
        self.ig = m.group(1) if m else ''
        m = re.search(r'data-iid="([^"]+)"', html)
        self.iid = m.group(1) if m else 'translator.5028'
        m = re.search(r'params_AbusePreventionHelper\s*=\s*\[(\d+),\s*"([^"]+)"', html)
        if m:
            self.key, self.token = m.group(1), m.group(2)
        return bool(self.ig and self.token)

    def translate(self, text, to='zh-Hans'):
        url = f'https://cn.bing.com/ttranslatev3?isVertical=1&IG={self.ig}&IID={self.iid}'
        body = urllib.parse.urlencode({'fromLang': 'auto-detect', 'text': text,
                                       'to': to, 'token': self.token,
                                       'key': self.key}).encode()
        req = urllib.request.Request(url, data=body, headers={
            'User-Agent': UA,
            'Content-Type': 'application/x-www-form-urlencoded',
            'Referer': 'https://cn.bing.com/translator',
        })
        with urllib.request.urlopen(req, timeout=10) as r:
            raw = r.read().decode('utf-8', 'replace')
        data = json.loads(raw)
        if isinstance(data, list) and data and 'translations' in data[0]:
            return data[0]['translations'][0].get('text', '')
        raise RuntimeError(raw[:150])


# 用用户真机的原文（含屏幕换行），这是最真实的测试
S_USER = (
    "Obtaining Spellbooks\n"
    "Besides finding the odd spellbook in the dungeon, there are two notable sources of these books:\n"
    "shops and the Mystic Library. Shops can come in several varietied, one of which is the bookstore,\n"
    "which often sell books and spellbooks. General stores can also be found selling spellbooks, but\n"
    "much less likely due to their larger pool of items that are not spellbooks.\n"
    "The Mystic Library can be found late into the game at the first floor of the Ruins, requiring caution\n"
    "around traps and levitation to access. Once inside, the many chests there can provide a vast\n"
    "increase in spellbooks, as well as Magic Staffs.\n"
    "Magic School and Tiers\n"
    "Spells are segmented into different schools and escalating tiers, where lower tier spells are easier\n"
    "to learn, requiring lower levels of the associated school."
)

S_SHORT = "The server is not responding. Please try again later. Contact support if the problem persists."

S_RU = (
    "Машинный перевод\n"
    "Машинный перевод — это раздел компьютерной лингвистики, в котором предложения переводятся с\n"
    "одного языка на другой. Программы используют разные подходы.\n"
    "Заключение\n"
    "Современные системы достигают высокого уровня точности."
)

SAMPLES = [
    ('用户截图那段（含屏幕换行）', S_USER),
    ('短文本', S_SHORT),
    ('俄文（含标题）', S_RU),
]

b = Bing()
p('===== 取票 =====')
if not b.ticket():
    p('❌ 取票失败')
    open(r'tools\临时\_bt\量-GaleChurch对齐2.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)
p('取票成功')

for name, src in SAMPLES:
    p()
    p('=' * 76)
    p('--- %s ---' % name)
    src_sents = split_foreign(src)
    p('原文 %d 句：' % len(src_sents))
    for i, s in enumerate(src_sents):
        p('  S[%d] (%3d字) %s' % (i + 1, len(s), s[:64] + ('…' if len(s) > 64 else '')))

    try:
        dst_full = b.translate(src)
        time.sleep(0.6)
    except Exception as ex:
        p('  ❌ 翻译失败：%s' % ex)
        continue

    dst_sents = split_chinese(dst_full)
    p()
    p('译文 %d 句：' % len(dst_sents))
    for i, s in enumerate(dst_sents):
        p('  T[%d] (%3d字) %s' % (i + 1, len(s), s[:64] + ('…' if len(s) > 64 else '')))

    pairs = align(src_sents, dst_sents)
    p()
    p('对齐 %d 组：' % len(pairs))
    all_src, all_dst = [], []
    for si, di in pairs:
        all_src += si
        all_dst += di
        st = ' '.join(src_sents[k] for k in si)
        dt = ' '.join(dst_sents[k] for k in di)
        tag = '1:1' if len(si) == 1 and len(di) == 1 else '%d:%d' % (len(si), len(di))
        p('  [%s] %s' % (tag, st[:50]))
        p('        → %s' % dt[:50])

    ok_s = sorted(all_src) == list(range(len(src_sents)))
    ok_d = sorted(all_dst) == list(range(len(dst_sents)))
    p('覆盖：原文 %s ／ 译文 %s'
      % ('✅' if ok_s else '❌', '✅' if ok_d else '❌'))

open(r'tools\临时\_bt\量-GaleChurch对齐2.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\量-GaleChurch对齐2.txt')
