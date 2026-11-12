# -*- coding: utf-8 -*-
r"""路线 1 验证：Gale-Church 句长比例对齐。

原理（Gale & Church 1993）：
  一段原文和它的译文，句子长度（字符数）之间近似成正比。
  英文 100 字符 ≈ 中文 40~50 字符。
  用这个比例做动态规划，找出最优的句子配对，
  允许 1:1 / 1:2 / 2:1（一句长句被拆成两句，或两句合成一句）。

为什么用这个：
  之前两版都栽在「原文 N 句 / 译文 N+1 句」上。
  这个算法天生就能处理句数不等 —— 它不要求相等，而是找最像的配对。

跑法：python "tools\脚本\量-GaleChurch对齐.py"
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

# ============ 切句 ============
ABBREV = {'dr', 'mr', 'mrs', 'ms', 'st', 'vs', 'etc', 'e.g', 'i.e', 'cf', 'al',
          'т.е', 'т.д', 'др', 'проф', 'г', 'гг', 'рис', 'см', 'напр'}


def split_foreign(text):
    """外文切句：. ! ? … 结尾，跳过缩写/小数点/文件名。"""
    text = (text or '').replace('\r\n', '\n').strip()
    if not text:
        return []
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
    text = (text or '').replace('\r\n', '\n').strip()
    if not text:
        return []
    out, buf = [], []
    for ch in text:
        buf.append(ch)
        if ch in '。！？；…':
            s = ''.join(buf).strip()
            if s:
                out.append(s)
            buf = []
    tail = ''.join(buf).strip()
    if tail:
        out.append(tail)
    return out


# ============ Gale-Church 对齐 ============
def align(src_sents, dst_sents, ratio=None, max_group=2):
    """返回 [(src_indices, dst_indices), ...]

    用 Gale-Church 的对数长度差 + DP。
    """
    ns, nd = len(src_sents), len(dst_sents)
    if ns == 0 or nd == 0:
        return []

    src_len = [max(1, len(s)) for s in src_sents]
    dst_len = [max(1, len(s)) for s in dst_sents]

    # 长度比：目的地平均字符 / 源平均字符
    if ratio is None:
        ratio = (sum(dst_len) / nd) / (sum(src_len) / ns)
        ratio = max(0.15, min(ratio, 8.0))

    # 允许的配对形状：(源句数, 目标句数)
    shapes = [(1, 1), (2, 1), (1, 2)]
    if max_group >= 3:
        shapes += [(3, 1), (1, 3)]

    INF = float('inf')
    # cost[i][j] = 前 i 个源句、前 j 个目标句的最小代价
    cost = [[INF] * (nd + 1) for _ in range(ns + 1)]
    back = [[None] * (nd + 1) for _ in range(ns + 1)]
    cost[0][0] = 0.0

    def seg_len(arr, i, k):
        return sum(arr[i:i + k])

    for i in range(ns + 1):
        for j in range(nd + 1):
            if cost[i][j] == INF:
                continue
            for a, b in shapes:
                ni, nj = i + a, j + b
                if ni > ns or nj > nd:
                    continue
                ls = seg_len(src_len, i, a)
                ld = seg_len(dst_len, j, b)
                # 在源语言尺度下的期望长度
                expected_src = ls
                expected_dst_in_src = ld / ratio
                # 对数长度差的平方（Gale-Church 的惩罚项）
                import math
                c = (math.log(ld / (ls * ratio)) ** 2) if ld > 0 and ls > 0 else 100.0
                # 形状惩罚：非 1:1 略贵（避免为凑分数到处 2:1）
                if a != b:
                    c += 0.4
                nc = cost[i][j] + c
                if nc < cost[ni][nj]:
                    cost[ni][nj] = nc
                    back[ni][nj] = (i, j, a, b)

    # 回溯
    pairs = []
    i, j = ns, nd
    while i > 0 or j > 0:
        bk = back[i][j]
        if bk is None:
            # 走不到这里，兜底：剩余全当一组
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


SAMPLES = [
    ('魔法书那段（英，含标题）',
     "Obtaining Spellbooks\n"
     "Besides finding the odd spellbook in the dungeon, there are two notable sources of these books: "
     "shops and the Mystic Library. Shops can come in several varieties, one of which is the bookstore, "
     "which often sell books and spellbooks.\n"
     "The Mystic Library can be found late into the game at the first floor of the Ruins, requiring caution "
     "around traps and levitation to access. Once inside, the many chests there can provide a vast "
     "increase in spellbooks.\n"
     "Magic School and Tiers\n"
     "Spells are segmented into different schools and escalating tiers, where lower tier spells are easier "
     "to learn."),

    ('短文本（3 句）',
     "The server is not responding. Please try again later. Contact support if the problem persists."),

    ('俄文（含标题）',
     "Машинный перевод\n"
     "Машинный перевод — это раздел компьютерной лингвистики, в котором предложения переводятся с "
     "одного языка на другой. Программы используют разные подходы.\n"
     "Заключение\n"
     "Современные системы достигают высокого уровня точности."),
]

b = Bing()
p('===== 取票 =====')
if not b.ticket():
    p('❌ 取票失败')
    open(r'tools\临时\_bt\量-GaleChurch对齐.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)
p('取票成功')

p()
p('=' * 74)
p('Gale-Church 句长比例对齐验证')
p('=' * 74)

for name, src in SAMPLES:
    p()
    p('--- %s ---' % name)
    src_sents = split_foreign(src)
    p('原文 %d 句：' % len(src_sents))
    for i, s in enumerate(src_sents):
        p('  S[%d] (%d字) %s' % (i + 1, len(s), s[:62] + ('…' if len(s) > 62 else '')))

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
        p('  T[%d] (%d字) %s' % (i + 1, len(s), s[:62] + ('…' if len(s) > 62 else '')))

    ratio = (sum(len(x) for x in dst_sents) / max(1, len(dst_sents))) / \
            (sum(len(x) for x in src_sents) / max(1, len(src_sents)))
    p()
    p('句数：原文 %d ／ 译文 %d ／ 长度比 %.2f' % (len(src_sents), len(dst_sents), ratio))

    pairs = align(src_sents, dst_sents)
    p('对齐结果 %d 组：' % len(pairs))
    all_src = []
    all_dst = []
    for si, di in pairs:
        all_src += si
        all_dst += di
        s_text = ' '.join(src_sents[k] for k in si)
        d_text = ' '.join(dst_sents[k] for k in di)
        tag = '1:1' if len(si) == 1 and len(di) == 1 else '%d:%d' % (len(si), len(di))
        p('  [%s] %s' % (tag, s_text[:52]))
        p('        → %s' % d_text[:52])

    # 完整性检查：每个句子都必须被用到一次
    ok_src = sorted(all_src) == list(range(len(src_sents)))
    ok_dst = sorted(all_dst) == list(range(len(dst_sents)))
    p()
    p('覆盖完整性：原文 %s ／ 译文 %s'
      % ('✅ 全覆盖' if ok_src else '❌ 有漏/重复',
         '✅ 全覆盖' if ok_dst else '❌ 有漏/重复'))

p()
p('=' * 74)
p('要点：')
p('  · 句数不等也能对齐（这是它比前两版强的地方）')
p('  · 检查「覆盖完整性」：每组句子必须用且仅用一次')
p('  · 人工看几组对齐是否合理')

open(r'tools\临时\_bt\量-GaleChurch对齐.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\量-GaleChurch对齐.txt')
