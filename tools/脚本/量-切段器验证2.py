# -*- coding: utf-8 -*-
r"""切段规则 v2：修掉两个已知问题。

v1 的问题（用户实机样本暴露的）：
  ① 标题没被切出来 —— block 第 0 行是标题时，因为 `idx > 0` 条件不满足被漏判，
     「Obtaining Spellbooks」和正文粘成一段，整段只剩 2 段（应 4~5 段）。
  ② 冒号+列表被切碎 —— 「Choose an option:」和后面 3 个列表项切成 4 段（应 1~2 段）。

v2 判断顺序（一条一条往下过，先命中的算）：
  1) 空行 → 硬边界
  2) 上一行以冒号结尾 → 强制同段（列表项粘在引导语后面）
  3) 本行像标题（短 + 无句末标点 + 下一行是长正文）→ 单独成段
  4) 其余连续行合并

跑法：python "tools\脚本\量-切段器验证2.py"
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

SENT_END = '.!?。！？'
COLON_END = ':：'


def word_count(s):
    """中英俄通用：中文按字数/2 估，外文按空格分词。"""
    if any('\u4e00' <= c <= '\u9fff' for c in s):
        return max(1, len(s) // 2)
    return len(s.split())


def looks_like_heading(line, next_line=None):
    """短 + 不以句末标点结尾 + 字数少 + （有下一行时）下一行明显更长。"""
    s = line.strip()
    if not s or len(s) > 48:
        return False
    if s[-1] in SENT_END + COLON_END + ',，;；':
        return False
    if not any(c.isalpha() for c in s):
        return False
    w = word_count(s)
    if w > 7:
        return False
    # 有下一行时：下一行必须明显更长（正文），否则不像标题
    if next_line is not None:
        nw = word_count(next_line)
        if nw <= max(w * 1.5, w + 3):
            return False
    return True


def split_paragraphs(text):
    """把文本切成段落。返回 list[str]。"""
    if not text:
        return []

    lines = [l.rstrip() for l in text.replace('\r\n', '\n').split('\n')]

    # 先按空行切硬边界
    blocks, cur = [], []
    for line in lines:
        if not line.strip():
            if cur:
                blocks.append(cur)
                cur = []
            continue
        cur.append(line)
    if cur:
        blocks.append(cur)

    out = []

    for block in blocks:
        i = 0
        n = len(block)
        while i < n:
            line = block[i].strip()
            nxt = block[i + 1].strip() if i + 1 < n else None

            # 规则 3：像标题 → 单独成段
            if looks_like_heading(line, nxt):
                out.append(line)
                i += 1
                continue

            # 其余：从这一行开始累积成一段，直到遇到标题或块结束
            buf = [line]
            # 规则 2：本行以冒号结尾 → 把后面的行全吞进来（列表粘引导语）
            forced_tail = line.endswith((':', '：'))
            i += 1
            while i < n:
                curline = block[i].strip()
                nextline = block[i + 1].strip() if i + 1 < n else None
                if forced_tail:
                    # 冒号后面：一直吞到块结束
                    buf.append(curline)
                    forced_tail = curline.endswith((':', '：'))
                    i += 1
                    continue
                if looks_like_heading(curline, nextline):
                    break
                buf.append(curline)
                i += 1

            out.append(' '.join(buf))

    return [x.strip() for x in out if x.strip()]


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

S_BLANK = (
    "Section One\n\n"
    "First paragraph here. It has two sentences.\n\n"
    "Second paragraph starts. Also two sentences here."
)

S_ONE = (
    "This is a single paragraph that happens to wrap across several lines "
    "because the window is narrow. It keeps going and going."
)

S_COLON = (
    "Choose an option:\n"
    "- First option\n"
    "- Second option\n"
    "- Third option"
)

S_RU = (
    "Машинный перевод\n"
    "Машинный перевод — это раздел компьютерной лингвистики, в котором предложения переводятся с\n"
    "одного языка на другой с помощью компьютерной программы. Программы используют разные подходы.\n"
    "Качество зависит от данных.\n"
    "Заключение\n"
    "Современные системы достигают высокого уровня точности."
)

S_ZH = (
    "获取魔法书\n"
    "除了在地牢中偶尔找到魔法书之外，这些书籍还有两个主要来源：\n"
    "商店和神秘图书馆。商店有几种类型，其中之一是书店。\n"
    "魔法学派与等级\n"
    "魔法被分为不同的学派和递增的等级。"
)

# 每个样本的期望段数（人工判断）
SAMPLES = [
    ('用户截图那段（无空行）', S_USER, 4),
    ('有空行的分段', S_BLANK, 3),
    ('单段落（无边界）', S_ONE, 1),
    ('冒号 + 列表', S_COLON, 2),
    ('俄文（含标题）', S_RU, 3),
    ('中文原文（含标题）', S_ZH, 3),
]

b = Bing()
p('===== 取票 =====')
if not b.ticket():
    p('❌ 取票失败')
    open(r'tools\临时\_bt\切段器验证2.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)
p('取票成功')

p()
p('=' * 72)
p('切段 v2 验证')
p('=' * 72)

mark_ok = 0
seg_ok = 0

for name, src, expect in SAMPLES:
    paras = split_paragraphs(src)
    p()
    p('--- %s（期望 %d 段，实得 %d 段）---' % (name, expect, len(paras)))
    for i, s in enumerate(paras):
        p('  [%d] %s' % (i + 1, s[:76] + ('…' if len(s) > 76 else '')))
    if len(paras) == expect:
        seg_ok += 1
        p('  切段 ✅')
    else:
        p('  切段 ⚠️ 段数与期望不同（不一定错，看上面内容判断合理不合理）')

    payload = '\n'.join('[S%d] %s' % (i + 1, s) for i, s in enumerate(paras))
    try:
        got = b.translate(payload)
        time.sleep(0.5)
    except Exception as ex:
        p('  ❌ 翻译失败：%s' % ex)
        continue

    pat = r'\[\s*[Ss]\s*(\d+)\s*\]'
    marks = re.findall(pat, got)
    ok = len(marks) == len(paras) and [int(x) for x in marks] == list(range(1, len(paras) + 1))
    if ok:
        mark_ok += 1
        parts = re.split(pat, got)
        p('  标记 ✅ 还原：')
        for k in range(1, len(parts) - 1, 2):
            p('     [%s] %s' % (parts[k], parts[k + 1].strip()[:64]))
    else:
        p('  标记 ❌ 找到 %d 个，应为 %d 个：%s' % (len(marks), len(paras), marks))
        p('  原始：%s' % repr(got[:180]))

p()
p('=' * 72)
p('切段符合期望：%d/%d' % (seg_ok, len(SAMPLES)))
p('标记法成功：  %d/%d' % (mark_ok, len(SAMPLES)))

open(r'tools\临时\_bt\切段器验证2.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\切段器验证2.txt')
