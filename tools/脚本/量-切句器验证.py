# -*- coding: utf-8 -*-
r"""切句器规则验证（离线，不碰 src\）。

目标：给定原文和译文，各自切句，统计句数相等的比例。
句数相等 → 可按序号 1:1 配对做高亮；不等 → 该段不高亮。

规则要处理的坑（实测踩到的）：
  · 英文缩写 Dr. / Mr. / Mrs. / Ms. / St. / vs. / e.g. / i.e. / etc. 不算句末
  · 数字小数点 2.5 不算句末
  · URL / 文件名 config.txt 里的点不算句末
  · 俄文缩写 т.е. / т.д. / др. 同英文
  · 中文句号 。！？； 都是句末，且后面通常没有空格

跑法：python "tools\脚本\量-切句器验证.py"
输出：tools\临时\_bt\切句器验证.txt
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

# 不能当句末的缩写（小写比较，后面可跟空格）
ABBREVIATIONS = {
    'dr', 'mr', 'mrs', 'ms', 'st', 'vs', 'etc', 'e.g', 'i.e', 'cf', 'al',
    'т.е', 'т.д', 'др', 'проф', 'г', 'гг', 'рис', 'см', 'напр',
}


def split_english_russian(text):
    """按 . ! ? … 切句，跳过缩写、小数点、文件名里的点。"""
    text = text.replace('\r\n', '\n').strip()
    if not text:
        return []

    out = []
    buf = []
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        buf.append(ch)

        if ch in '.!?…':
            # 连续标点（?! ... 等）吞掉
            j = i + 1
            while j < n and text[j] in '.!?…':
                buf.append(text[j])
                j += 1

            # 判断这个标点是否真的是句末
            is_end = True

            if ch == '.':
                # 前面是数字，且再前面也是数字 → 小数点
                k = i - 1
                while k >= 0 and text[k].isdigit():
                    k -= 1
                if k < i - 1:  # 点前有数字
                    # 看后面是不是还有数字（2.5）
                    if j < n and text[j].isdigit():
                        is_end = False
                # 缩写检查：往前取一个词
                m = re.search(r'([A-Za-zА-Яа-яЁё.]+)\.?$', ''.join(buf[:-1]))
                if m and is_end:
                    word = m.group(1).lower().rstrip('.')
                    if word in ABBREVIATIONS:
                        is_end = False
                # 文件名/URL：点后紧跟字母，且前面没空格（config.txt）
                if is_end and j < n and (text[j].isalnum()):
                    # 前面紧挨着的是一段无空格的词 → 很可能是文件名
                    tail = ''.join(buf[:-1])
                    if tail and not tail[-1].isspace() and '.' in tail.split()[-1]:
                        is_end = False

            # 后面必须是空白、换行或结束才算句末
            if is_end and j < n and not text[j].isspace():
                # 中文里句号后可以直接接字，但这里处理的是外文
                if text[j] not in '。！？；':
                    is_end = False

            if is_end:
                s = ''.join(buf).strip()
                if s:
                    out.append(s)
                buf = []
                # 跳过标点后的空白
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
    """按 。！？；… 切句（句末标点本身保留在句子里）。"""
    text = text.replace('\r\n', '\n').strip()
    if not text:
        return []
    out = []
    buf = []
    for ch in text:
        buf.append(ch)
        if ch in '。！？；…':
            # 连续标点吞掉
            s = ''.join(buf).strip()
            if s:
                out.append(s)
            buf = []
    tail = ''.join(buf).strip()
    if tail:
        out.append(tail)
    return out


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


SAMPLES = [
    ('魔法书（英，5 句实为 4 句）',
     "If the player reads the cursed magic book, it will cause the player to forget a spell, "
     "instantly removing a previously learned spell from the player's memory. "
     "At the same time, the magic book itself will be instantly destroyed. "
     "This includes spells learned through reading, class talent spells, and racial talent spells. "
     "If the player does not have any spells to forget, the magic book will still be destroyed, "
     "and a chat message will appear indicating that the player is temporarily experiencing confusion."),

    ('缩写（Dr. Smith）',
     "The file config.txt is missing. Error code 404 was returned by the server. "
     "Contact Dr. Smith for help."),

    ('小数点（2.5）',
     "The file occupies 2.5 MB. Download takes 30 seconds at 60 FPS. Try again later."),

    ('e.g. 与 etc.',
     "Use a spell, e.g. Fireball. Some spells, etc. are destructive. Be careful."),

    ('太阳系（俄，4 句）',
     "Солнце — всего лишь очень обычная звезда во Вселенной, но оно является центральным небесным телом "
     "Солнечной системы. В Солнечной системе восемь планет, включая нашу Землю. "
     "Область Солнечной системы огромна: только на примере Плутона. "
     "Однако такой огромный Солнечная система в галактике — лишь капля в море."),

    ('俄文缩写 т.е.',
     "Это очень важно, т.е. критично. Проверьте настройки. Затем перезапустите."),

    ('单句',
     "Click OK to continue."),

    ('无句号（菜单）',
     "Set status Profile Repositories Stars Gists Organizations Enterprise Sponsors"),

    ('问号叹号混合',
     "Are you sure? This cannot be undone! Click cancel to go back."),

    ('换行分段',
     "First paragraph here.\nSecond paragraph starts here. It has two sentences."),
]

b = Bing()
p('===== 取票 =====')
if not b.ticket():
    p('❌ 取票失败')
    open(r'tools\临时\_bt\切句器验证.txt', 'w', encoding='utf-8').write('\n'.join(OUT))
    sys.exit(1)
p('取票成功')

p()
p('===== 切句 + 翻译对齐验证 =====')
p('%-26s %-10s %-10s %s' % ('样本', '原句数', '译句数', '结果'))
p('-' * 60)

same = total = 0
detail = []

for name, src in SAMPLES:
    src_sents = split_english_russian(src) if not any('\u4e00' <= c <= '\u9fff' for c in src) else split_chinese(src)
    try:
        dst = b.translate(src)
        time.sleep(0.35)
    except Exception as ex:
        p('%-26s ❌ %s' % (name, str(ex)[:30]))
        continue
    dst_sents = split_chinese(dst)
    ok = len(src_sents) == len(dst_sents)
    same += ok
    total += 1
    p('%-26s %-10d %-10d %s' % (name, len(src_sents), len(dst_sents),
                                '✅' if ok else '❌ 不等'))
    detail.append((name, src_sents, dst_sents))

p()
p('句数相等：%d/%d（%.0f%%）' % (same, total, 100.0 * same / max(1, total)))

p()
p('===== 明细（重点看不等的）=====')
for name, s, d in detail:
    if len(s) == len(d) and name not in ('缩写（Dr. Smith）', '小数点（2.5）', 'e.g. 与 etc.', '换行分段'):
        continue
    p()
    p('--- %s ---' % name)
    p('原 %d 句 / 译 %d 句' % (len(s), len(d)))
    for i in range(max(len(s), len(d))):
        p('  [%d] %-52s │ %s'
          % (i + 1,
             (s[i][:52] if i < len(s) else '（无）'),
             (d[i][:30] if i < len(d) else '（无）')))

open(r'tools\临时\_bt\切句器验证.txt', 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 tools\\临时\\_bt\\切句器验证.txt')
