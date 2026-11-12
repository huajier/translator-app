# -*- coding: utf-8 -*-
r"""验证方案丙：给原文插段落标记，让 Bing 翻译后还对得上。

思路：
  发给 Bing 的原文里插入 [S1] [S2] ... 这样的标记，
  译文回来后按标记切分 → 得到「段对段」的对应关系。
  只要标记在译文里还能认出来，就是一次请求拿到段落级对齐（最优解）。

要验三个问题：
  ① 标记会不会被翻译掉（[S1] → 【S1】/ [S1] / 消失）
  ② 标记位置会不会乱（跑进句子中间、被合并）
  ③ 标记本身会不会污染译文质量

跑法：python "tools\脚本\量-标记法.py"
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


# 用户截图那段，按语义分成 6 段
PARAS = [
    "Obtaining Spellbooks",
    "Besides finding the odd spellbook in the dungeon, there are two notable sources of these books: "
    "shops and the Mystic Library. Shops can come in several varietied, one of which is the bookstore, "
    "which often sell books and spellbooks. General stores can also be found selling spellbooks, but "
    "much less likely due to their larger pool of items that are not spellbooks.",
    "The Mystic Library can be found late into the game at the first floor of the Ruins, requiring caution "
    "around traps and levitation to access. Once inside, the many chests there can provide a vast "
    "increase in spellbooks, as well as Magic Staffs.",
    "Magic School and Tiers",
    "Spells are segmented into different schools and escalating tiers, where lower tier spells are easier "
    "to learn, requiring lower levels of the associated school.",
]

# 几种标记写法，逐一试，看哪种 Bing 最不容易动
MARKERS = [
    ('[S{n}]',   '方括号+字母数字'),
    ('<<S{n}>>', '双尖括号'),
    ('###{n}###', '井号包裹'),
    ('[[{n}]]',  '双方括号'),
]

b = Bing()
p('===== 取票 =====')
if not b.ticket():
    p('❌ 取票失败')
    open(r'tools\临时\_bt\量-标记法.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)
p('取票成功')

p()
p('原文共 %d 段：' % len(PARAS))
for i, s in enumerate(PARAS):
    p('  [%d] %s' % (i + 1, s[:70] + ('…' if len(s) > 70 else '')))

for fmt, desc in MARKERS:
    p()
    p('=' * 70)
    p('标记写法：%s（%s）' % (fmt, desc))

    # 拼接：标记 + 段落，标记之间用换行分隔
    pieces = []
    for i, s in enumerate(PARAS):
        pieces.append(fmt.format(n=i + 1) + ' ' + s)
    payload = '\n'.join(pieces)

    p('--- 发给 Bing 的文本（前 300 字）---')
    p(payload[:300].replace('\n', '⏎'))

    try:
        got = b.translate(payload)
    except Exception as ex:
        p('❌ 翻译失败：%s' % ex)
        time.sleep(0.5)
        continue

    p()
    p('--- 译文原始返回 ---')
    p(repr(got[:400]))

    # 检查标记是否还在
    p()
    p('--- 标记识别情况 ---')
    import re as _re
    # 按标记对应的正则找
    pat_map = {
        '[S{n}]': r'\[\s*[Ss]\s*(\d+)\s*\]',
        '<<S{n}>>': r'<<\s*[Ss]\s*(\d+)\s*>>',
        '###{n}###': r'#{2,}\s*(\d+)\s*#{2,}',
        '[[{n}]]': r'\[\s*\[\s*(\d+)\s*\]\s*\]',
    }
    pat = pat_map[fmt]
    found = _re.findall(pat, got)
    p('找到标记 %d 个（应为 %d 个）：%s' % (len(found), len(PARAS), found))

    # 宽松检查：有没有数字+括号的残留
    loose = _re.findall(r'[\[\]【】<>#]{1,3}\s*[Ss]?\s*\d+\s*[\[\]【】<>#]{1,3}', got)
    p('宽松匹配到的候选标记：%s' % loose[:12])

    ok = len(found) == len(PARAS) and [int(x) for x in found] == list(range(1, len(PARAS) + 1))
    p('判定：%s' % ('✅ 标记完整且顺序正确 —— 方案可行' if ok else '❌ 标记丢失或顺序乱'))

    if ok:
        # 按标记切分，验证能还原成段
        p()
        p('--- 按标记切分结果 ---')
        parts = _re.split(pat, got)
        # parts = [前置文本, '1', 内容, '2', 内容, ...]
        for k in range(1, len(parts) - 1, 2):
            idx = parts[k]
            body = parts[k + 1].strip()
            p('  [%s] %s' % (idx, body[:80]))

    time.sleep(0.6)

p()
p('=' * 70)
p('怎么读：')
p('  · 有某一种标记能完整保留、顺序正确、切分干净 → 用它，一次请求搞定段落对齐')
p('  · 全都不行 → 只能退回「逐段翻译」（多几次请求，慢）')

open(r'tools\临时\_bt\量-标记法.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\量-标记法.txt')
