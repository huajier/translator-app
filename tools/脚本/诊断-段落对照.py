# -*- coding: utf-8 -*-
r"""诊断：拿用户真机的原文，走一遍切段 + 标记流程，看错在哪。

数据源：release\v0.4.13.sh3\logs\取词记录-20261112.txt 里的真实识别原文
目的：分清是「切段错」还是「标记解析错」。

跑法：python "tools\脚本\诊断-段落对照.py"
"""
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = []


def p(s=''):
    OUT.append(s)
    print(s)


# ============ 照抄 ParagraphSplitter.cs 的规则 ============
SENT_ENDS = '.!?。！？'
COLON_ENDS = ':：'
MAX_HEADING_LEN = 48
MAX_HEADING_WORDS = 7


def word_count(s):
    if not s:
        return 0
    for c in s:
        if '\u4e00' <= c <= '\u9fff':
            return max(1, len(s) // 2)
    return len(s.split())


def looks_like_heading(line, next_line=None):
    s = (line or '').strip()
    if not s or len(s) > MAX_HEADING_LEN:
        return False
    if s[-1] in SENT_ENDS + COLON_ENDS + ',，;；':
        return False
    if not any(c.isalpha() for c in s):
        return False
    w = word_count(s)
    if w > MAX_HEADING_WORDS:
        return False
    if next_line is not None:
        nw = word_count(next_line)
        if nw <= max(w * 1.5, w + 3):
            return False
    return True


def split_paragraphs(text):
    if not text:
        return []
    lines = [l.rstrip() for l in text.replace('\r\n', '\n').split('\n')]
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
        i, n = 0, len(block)
        while i < n:
            line = block[i].strip()
            nxt = block[i + 1].strip() if i + 1 < n else None
            if looks_like_heading(line, nxt):
                out.append(line)
                i += 1
                continue
            buf = [line]
            forced_tail = line.endswith((':', '：'))
            i += 1
            while i < n:
                cl = block[i].strip()
                nl = block[i + 1].strip() if i + 1 < n else None
                if forced_tail:
                    buf.append(cl)
                    forced_tail = cl.endswith((':', '：'))
                    i += 1
                    continue
                if looks_like_heading(cl, nl):
                    break
                buf.append(cl)
                i += 1
            out.append(' '.join(buf))
    return [x.strip() for x in out if x.strip()]


def build_payload(paras):
    return '\n'.join('[S%d] %s' % (i + 1, s) for i, s in enumerate(paras))


# ============ 读真实记录 ============
LOG = os.path.join(ROOT, 'release', 'v0.4.13.sh3', 'logs', '取词记录-20261112.txt')
if not os.path.exists(LOG):
    p('找不到日志：%s' % LOG)
    raise SystemExit(1)

raw = open(LOG, 'rb').read().decode('utf-8', 'replace')
recs = raw.split('##### 记录结束 #####')

items = []
for r in recs:
    m = re.search(r'字数:\s*(\d+)', r)
    tm = re.search(r'时间:\s*(\S+)', r)
    src = re.search(r'原文:\s*(.+)', r)
    if not (m and src):
        continue
    txt = src.group(1).replace('⏎', '\n').strip()
    if len(txt) < 40:
        continue
    items.append((tm.group(1) if tm else '?', int(m.group(1)), txt))

p('===== 取词记录里可分析的样本 %d 条 =====' % len(items))
for t, n, _ in items[-8:]:
    p('  %s  字数 %d' % (t, n))

# 挑三条：短（好的）、中、长
picks = []
for t, n, txt in items:
    if 300 <= n <= 700:
        picks.append(('中长 %d 字' % n, txt))
    elif n > 1000:
        picks.append(('长 %d 字' % n, txt))

picks = picks[-3:] if len(picks) > 3 else picks

for label, txt in picks:
    p()
    p('=' * 74)
    p('样本：%s' % label)
    p('--- 原文前 400 字（还原换行）---')
    for i, l in enumerate(txt.split('\n')[:12]):
        p('  [%d] %s' % (i + 1, l[:78]))

    paras = split_paragraphs(txt)
    p()
    p('--- 切段结果：%d 段 ---' % len(paras))
    for i, s in enumerate(paras):
        p('  [%d] (%d字) %s' % (i + 1, len(s), s[:70] + ('…' if len(s) > 70 else '')))

    payload = build_payload(paras)
    p()
    p('--- 发给翻译的文本（前 500 字，\\n 显示为 ⏎）---')
    p(payload[:500].replace('\n', '⏎'))

    # 检查：段落里有没有以 [ 开头的可疑内容（原文自带方括号会被误认成标记）
    p()
    p('--- 风险检查 ---')
    bracket = [s for s in paras if '[' in s or ']' in s]
    if bracket:
        p('  ⚠️ 有 %d 段包含方括号，可能和 [Sn] 标记混淆：' % len(bracket))
        for s in bracket[:3]:
            p('     %s' % s[:70])
    else:
        p('  ✅ 段落里没有方括号，不会和标记混淆')

    # 检查：切出来的段落是不是真的「段落」（太长的段落说明切粗了）
    too_long = [i for i, s in enumerate(paras) if len(s) > 400]
    if too_long:
        p('  ⚠️ 第 %s 段超过 400 字（切得太粗，高亮会是一大块）'
          % ','.join(str(i + 1) for i in too_long))
    short = [i for i, s in enumerate(paras) if len(s) < 20]
    if short:
        p('  ⚠️ 第 %s 段不足 20 字（可能是碎片）'
          % ','.join(str(i + 1) for i in short))

p()
p('=' * 74)
p('诊断要点：')
p('  1) 段数合不合理（标题应该单独成段）')
p('  2) 有没有超长段落（切太粗 → 高亮一大块）')
p('  3) 段落里有没有方括号（会和标记混淆）')

dest = os.path.join(ROOT, 'tools', '临时', '_bt', '诊断-段落对照.txt')
open(dest, 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 %s' % dest)
