# -*- coding: utf-8 -*-
r"""验证一个关键假设：整段翻译后，原文句数 == 译文句数？

为什么这条决定方案：
  百度能做逐句高亮，是因为它的翻译接口**直接返回句子数组**（带 sentenceId）。
  我们的 Bing 网页接口只返回整段文本，没有句子边界。
  如果整段翻译后「原文句数 == 译文句数」，就能两边各自切句、按序号 1:1 配对，
  **完全不需要逐句翻译** —— 速度不变（2.6 秒），高亮照样做出来。

  如果句数经常不等，就得做 DP 对齐，或者保守地不高亮。

跑法：python "tools\脚本\量-句数一致性.py"
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
        raise RuntimeError(raw[:120])


# 切句：英文/俄文用 .!? 后跟空格；中文用 。！？；
def split_src(text):
    parts = re.split(r'(?<=[.!?…])\s+', text.strip())
    return [x.strip() for x in parts if x.strip()]


def split_dst(text):
    parts = re.split(r'(?<=[。！？；…])', text.strip())
    return [x.strip() for x in parts if x.strip()]


SAMPLES = [
    ('魔法书（英→中，5 句）',
     "If the player reads the cursed magic book, it will cause the player to forget a spell, "
     "instantly removing a previously learned spell from the player's memory. "
     "At the same time, the magic book itself will be instantly destroyed. "
     "This includes spells learned through reading, class talent spells, and racial talent spells. "
     "If the player does not have any spells to forget, the magic book will still be destroyed, "
     "and a chat message will appear indicating that the player is temporarily experiencing confusion."),

    ('太阳系（俄→中，4~5 句）',
     "Солнце — всего лишь очень обычная звезда во Вселенной, но оно является центральным небесным телом "
     "Солнечной системы. В Солнечной системе восемь планет, включая нашу Землю, некоторые карликовые планеты, "
     "кометы и бесчисленное множество других небесных тел движутся вокруг Солнца под действием его мощной гравитации. "
     "Область Солнечной системы огромна: только на примере Плутона, его орбита находится на расстоянии почти 40 "
     "астрономических единиц от Солнца, что составляет 6 миллиардов километров, а на самом деле границы Солнечной "
     "системы в десятки раз больше этого. Однако такой огромный Солнечная система в галактике — лишь капля в море."),

    ('短英文（2 句）',
     "The server is not responding. Please try again later."),

    ('单句英文',
     "Click OK to continue."),

    ('俄文短句（2 句）',
     "Ошибка 404: страница не найдена. Попробуйте обновить страницу позже."),

    ('英文 UI（3 句）',
     "Configuration saved successfully. The application must be restarted. Would you like to restart now?"),

    ('GitHub 菜单（无句号，逗号分隔）',
     "Set status Profile Repositories Stars Gists Organizations Enterprise Sponsors Settings Copilot "
     "Feature preview Appearance Accessibility"),

    ('带缩写和数字（易切错）',
     "The file config.txt is missing. Error code 404 was returned by the server. Contact Dr. Smith for help."),

    ('长俄文（5 句）',
     "Машинный перевод — это раздел компьютерной лингвистики, в котором предложения переводятся с одного языка "
     "на другой с помощью компьютерной программы. Программы машинного перевода используют различные подходы, "
     "включая статистические методы и нейронные сети. Качество перевода зависит от объёма обучающих данных. "
     "Современные системы достигают высокого уровня точности. Однако полностью автоматический перевод "
     "по-прежнему остаётся сложной задачей."),
]

b = Bing()
p('===== 取票 =====')
if not b.ticket():
    p('❌ 取票失败')
    open(r'tools\临时\_bt\量-句数一致性.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)
p('取票成功')

p()
p('===== 句数一致性验证 =====')
p('%-28s %-8s %-8s %s' % ('样本', '原文句数', '译文句数', '是否相等'))
p('-' * 64)

same = total = 0
detail = []

for name, src in SAMPLES:
    src_sents = split_src(src)
    try:
        dst = b.translate(src)
    except Exception as ex:
        p('%-28s ❌ %s' % (name, str(ex)[:40]))
        continue
    dst_sents = split_dst(dst)
    ok = len(src_sents) == len(dst_sents)
    same += ok
    total += 1
    p('%-28s %-8d %-8d %s' % (name, len(src_sents), len(dst_sents), '✅' if ok else '❌ 不等'))
    detail.append((name, src_sents, dst_sents, dst))
    time.sleep(0.4)

p()
p('句数相等：%d/%d（%.0f%%）' % (same, total, 100.0 * same / max(1, total)))

p()
p('===== 逐样本明细 =====')
for name, s_sents, d_sents, dst in detail:
    p()
    p('--- %s ---' % name)
    p('原文 %d 句｜译文 %d 句' % (len(s_sents), len(d_sents)))
    for i in range(max(len(s_sents), len(d_sents))):
        s = s_sents[i][:58] if i < len(s_sents) else '（无）'
        d = d_sents[i][:34] if i < len(d_sents) else '（无）'
        p('  [%d] %-58s │ %s' % (i + 1, s, d))

p()
p('=' * 64)
p('结论怎么读：')
p('  · 相等率高（>=80%）→ 整段翻译 + 两边切句 + 序号 1:1 配对即可，')
p('    **不需要逐句翻译**，速度完全不变。')
p('  · 相等率低 → 需要 DP 对齐，或对不齐的段落不显示高亮。')

open(r'tools\临时\_bt\量-句数一致性.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\量-句数一致性.txt')
