# -*- coding: utf-8 -*-
r"""验证：Bing 翻译会不会保留原文的换行？

为什么这条决定方案：
  句数对齐（原文 N 句 ↔ 译文 N 句）实测经常差 1，
  根因是标题行/换行位置两边处理不一致。
  如果 Bing 保留换行，就能改成「段落级对照」—— 段落边界 = 换行，
  不靠猜标点，稳定得多。
  如果不保留换行，这条路断，只剩逐句翻译（慢 2.7 倍）。

跑法：python "tools\脚本\量-换行保留.py"
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


# 用户截图那段真实样本（含标题行 + 多行正文）
SAMPLE = (
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

SHORT = (
    "Line one has no period\n"
    "Line two ends with a period. Line three starts here.\n"
    "Line four is separate."
)

b = Bing()
p('===== 取票 =====')
if not b.ticket():
    p('❌ 取票失败')
    open(r'tools\临时\_bt\量-换行保留.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)
p('取票成功')

for name, src in (('用户截图那段（9 行）', SAMPLE), ('短样本（3 行）', SHORT)):
    p()
    p('=' * 70)
    p('样本：%s' % name)
    p('原文行数 = %d' % len(src.split('\n')))
    p('--- 原文 ---')
    for i, l in enumerate(src.split('\n')):
        p('  [%d] %s' % (i + 1, l[:80]))

    try:
        dst = b.translate(src)
    except Exception as ex:
        p('❌ 翻译失败：%s' % ex)
        continue

    p()
    p('--- 译文原始返回（repr，能看见 \\n 在哪）---')
    p(repr(dst[:400]))

    lines = dst.split('\n')
    p()
    p('--- 译文按 \\n 切 ---')
    p('译文行数 = %d' % len(lines))
    for i, l in enumerate(lines):
        p('  [%d] %s' % (i + 1, l[:80]))

    p()
    p('--- 判断 ---')
    src_lines = [x for x in src.split('\n') if x.strip()]
    dst_lines = [x for x in lines if x.strip()]
    p('原文非空行 %d ／ 译文非空行 %d ／ %s'
      % (len(src_lines), len(dst_lines),
         '✅ 行数相等' if len(src_lines) == len(dst_lines) else '❌ 行数不等'))
    p('译文里有没有 \\n：%s' % ('有' if '\n' in dst else '没有'))
    p('译文里有没有 \\r：%s' % ('有' if '\r' in dst else '没有'))

    time.sleep(0.5)

p()
p('=' * 70)
p('怎么读：')
p('  · 译文保留了 \\n 且行数与原文接近 → 段落级对照可行（稳、简单）')
p('  · 译文全挤成一行 → 段落对照不可行')

open(r'tools\临时\_bt\量-换行保留.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\量-换行保留.txt')
