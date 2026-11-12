# -*- coding: utf-8 -*-
r"""切段规则验证（离线 + 真 Bing）。

方案定了：段落级对照 + [S1][S2] 标记。
现在要验证的是**切段规则**本身对不对。

切段要处理的真实情况（从用户实测日志里来的）：
  · 屏幕自动换行 → 不是段落边界（一段可能被折成 3~4 行）
  · 空行 → 段落边界
  · 标题行（短、无句末标点、下一行是正文）→ 单独成段
  · 冒号结尾 → 后面通常同段
  · 中文原文 vs 外文原文，规则不同

跑法：python "tools\脚本\量-切段器验证.py"
输出：tools\临时\_bt\切段器验证.txt
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


# ============ 切段规则 ============
def looks_like_heading(line):
    """短、没有句末标点、首字母大写或全大写 → 像标题。"""
    s = line.strip()
    if not s:
        return False
    if len(s) > 48:                     # 太长了不像标题
        return False
    if s[-1] in '.!?。！？:：,，;；':   # 以标点结尾 → 不是标题
        return False
    # 至少要有字母（排除纯符号行）
    if not any(c.isalpha() for c in s):
        return False
    # 单词数：标题一般 <= 6 个词
    if len(s.split()) > 6:
        return False
    return True


def split_paragraphs(text, is_chinese=False):
    """把文本切成段落，返回 list[str]（已 strip）。

    规则：
      1) 空行 = 硬边界
      2) 标题行单独成段（短、无句末标点、下一个非空行看起来是正文）
      3) 其余连续行合并成一段
      4) 以冒号结尾的行，与下一行同段（冒号后面通常是同一句的延续）
    """
    if not text:
        return []

    raw_lines = [l.rstrip() for l in text.replace('\r\n', '\n').split('\n')]
    # 标记哪些行是「空行」，用来切硬边界
    blocks = []
    cur = []

    for line in raw_lines:
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
        # 块内再按「标题」细分
        buf = []
        for idx, line in enumerate(block):
            s = line.strip()
            if idx > 0 and looks_like_heading(s):
                # 这是一个新标题：先把之前的收掉
                if buf:
                    out.append(' '.join(x.strip() for x in buf))
                    buf = []
            buf.append(s)

            # 冒号结尾 → 强迫与下一行同段（不在这里收）
            if s.endswith((':', '：')) and idx < len(block) - 1:
                continue

        if buf:
            out.append(' '.join(x.strip() for x in buf))

    # 清理：合并只有标点的碎片
    cleaned = []
    for x in out:
        x = x.strip()
        if not x:
            continue
        if cleaned and len(x) <= 2 and x in '.!?。！？':
            cleaned[-1] = cleaned[-1] + x
        else:
            cleaned.append(x)
    return cleaned


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


# ============ 样本 ============
# 用户截图那段（屏幕自动换行，无空行）
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

# 有空行的
S_BLANK = (
    "Section One\n\n"
    "First paragraph here. It has two sentences.\n\n"
    "Second paragraph starts. Also two sentences here."
)

# 单段落（没有任何明确边界）
S_ONE = (
    "This is a single paragraph that happens to wrap across several lines "
    "because the window is narrow. It keeps going and going."
)

# 冒号结尾 + 列表
S_COLON = (
    "Choose an option:\n"
    "- First option\n"
    "- Second option\n"
    "- Third option"
)

# 俄文
S_RU = (
    "Машинный перевод\n"
    "Машинный перевод — это раздел компьютерной лингвистики, в котором предложения переводятся с\n"
    "одного языка на другой с помощью компьютерной программы. Программы используют разные подходы.\n"
    "Качество зависит от данных.\n"
    "Заключение\n"
    "Современные системы достигают высокого уровня точности."
)

# 中文原文
S_ZH = (
    "获取魔法书\n"
    "除了在地牢中偶尔找到魔法书之外，这些书籍还有两个主要来源：\n"
    "商店和神秘图书馆。商店有几种类型，其中之一是书店。\n"
    "魔法学派与等级\n"
    "魔法被分为不同的学派和递增的等级。"
)

SAMPLES = [
    ('用户截图那段（无空行，屏幕换行）', S_USER),
    ('有空行的分段', S_BLANK),
    ('单段落（无边界）', S_ONE),
    ('冒号 + 列表', S_COLON),
    ('俄文（含标题）', S_RU),
    ('中文原文（含标题）', S_ZH),
]

b = Bing()
p('===== 取票 =====')
if not b.ticket():
    p('❌ 取票失败')
    open(r'tools\临时\_bt\切段器验证.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)
p('取票成功')

p()
p('=' * 72)
p('切段 + 标记 + 翻译 + 还原 全流程验证')
p('=' * 72)

all_ok = 0
for name, src in SAMPLES:
    is_zh = any('\u4e00' <= c <= '\u9fff' for c in src[:200])
    paras = split_paragraphs(src, is_zh)
    p()
    p('--- %s ---' % name)
    p('切成 %d 段：' % len(paras))
    for i, s in enumerate(paras):
        p('  [%d] %s' % (i + 1, s[:74] + ('…' if len(s) > 74 else '')))

    # 加标记发送
    payload = '\n'.join('[S%d] %s' % (i + 1, s) for i, s in enumerate(paras))
    try:
        got = b.translate(payload)
        time.sleep(0.5)
    except Exception as ex:
        p('  ❌ 翻译失败：%s' % ex)
        continue

    # 解析标记
    pat = r'\[\s*[Ss]\s*(\d+)\s*\]'
    marks = re.findall(pat, got)
    ok = len(marks) == len(paras) and [int(x) for x in marks] == list(range(1, len(paras) + 1))

    p()
    p('  译文标记：找到 %d 个（应为 %d）→ %s' % (len(marks), len(paras), marks))
    if ok:
        parts = re.split(pat, got)
        p('  ✅ 还原成功，段段对应：')
        for k in range(1, len(parts) - 1, 2):
            idx = parts[k]
            body = parts[k + 1].strip()
            p('     [%s] %s' % (idx, body[:66]))
        all_ok += 1
    else:
        p('  ❌ 标记不全，本样本会降级为整段显示')
        p('  原始返回：%s' % repr(got[:200]))

p()
p('=' * 72)
p('标记法成功率：%d/%d' % (all_ok, len(SAMPLES)))
p()
p('另外检查切段合理性（人工看）：')
p('  · 用户那段应该切成 4~5 段（2 个标题 + 2~3 段正文）')
p('  · 单段落样本应该只有 1 段')
p('  · 冒号+列表应该切成 2 段（引导语 + 列表整体）')

open(r'tools\临时\_bt\切段器验证.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\切段器验证.txt')
