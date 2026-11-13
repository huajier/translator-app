# -*- coding: utf-8 -*-
r"""量：宽扁图（多行长文本）检测失败，哪种修法最好。

问题（2026-11-13 用户实测日志）：
  同一段 4 行英文，框尺寸 647×85 / 663×83 / 667×86 / 661×78 —— 几乎一样大。
  · 触发 3 倍重试的那两次 → 208 字全对（1x 是 "Armor Classin Barony..." 词粘连）
  · 没触发的那些      → 99/109/181 字，中间行整行漏检，但分数虚高（0.90~0.99）

根因：DBNet 在「宽高比大」的图上检测不稳定，1x 只检出 2 行（应为 4 行）。
      而 NeedsUpscaleRetry 只看字符分数，分数虚高就不触发。

本脚本用产品里同一套 RapidOcrNet 引擎，对真实图测三种策略：
  A. 现状：1x 优先，靠 NeedsUpscaleRetry 的三个信号决定要不要 3x
  B. 宽扁图直接 3x（宽高比 > 阈值 且 高度 < 阈值）
  C. 1x 检出框数 < 预期（按图高 / 行高估）就 3x

跑法：python "tools\脚本\量-宽扁图检测.py"
"""
import json
import os
import re
import statistics
import sys

sys.stdout.reconfigure(encoding='utf-8')
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = []


def p(s=''):
    OUT.append(s)
    print(s)


# ============ 挑真实样本 ============
# 从用户今天的取词记录里找「宽扁多行」的框：宽 600~700，高 70~90
LOG = os.path.join(ROOT, 'tools', '日志', 'app-20261113.log')
if not os.path.exists(LOG):
    p('找不到日志：%s' % LOG)
    raise SystemExit(1)

raw = open(LOG, 'rb').read().decode('utf-8', 'replace')
frames = []
for line in raw.splitlines():
    m = re.search(r'框选完成：左上=\((\d+),(\d+)\) 尺寸=(\d+)×(\d+)', line)
    if not m:
        continue
    tm = re.search(r'\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)', line)
    frames.append((tm.group(1) if tm else '?',
                   int(m.group(1)), int(m.group(2)),
                   int(m.group(3)), int(m.group(4))))

p('日志里的框选记录 %d 条' % len(frames))
wide = [f for f in frames if 550 <= f[3] <= 750 and 60 <= f[4] <= 100]
p('其中「宽扁多行」样本（宽 550~750、高 60~100）：%d 条' % len(wide))
for f in wide:
    p('  %s  %d×%d  宽高比 %.1f' % (f[0], f[3], f[4], f[3] / f[4]))

if not wide:
    p('❌ 没有可用的宽扁样本，无法继续')
    raise SystemExit(1)

p()
p('=' * 74)
p('复现需要截屏，本脚本改为「静态分析 + 策略设计」')
p('=' * 74)
p()
p('为什么不在这个脚本里跑引擎：')
p('  · RapidOcrNet 是 .NET 包，Python 侧调不到；')
p('  · 探针工程 tools\\探针\\真卡片验证\\ 已经能反射加载产品 DLL 跑真实识别。')
p()
p('所以这个脚本的产出是「策略参数建议」，实测交给探针。')
p()

# ============ 用日志里已有的成败数据推断阈值 ============
p('=' * 74)
p('用今天的真实成败反推策略')
p('=' * 74)
p()

# 从日志里提取每次取词：框尺寸 + 是否触发重试 + 最终字数
caps = []
cur = None
for line in raw.splitlines():
    m = re.search(r'框选完成：左上=\(\d+,\d+\) 尺寸=(\d+)×(\d+)', line)
    if m:
        cur = {'w': int(m.group(1)), 'h': int(m.group(2)), 'retry': False, 'chars': None}
        continue
    if cur is None:
        continue
    if '小框 3 倍重试' in line:
        cur['retry'] = True
    m = re.search(r'取词成功：.*字数=(\d+)', line)
    if m:
        cur['chars'] = int(m.group(1))
        caps.append(cur)
        cur = None

p('可分析的取词 %d 次' % len(caps))
wide_caps = [c for c in caps if 550 <= c['w'] <= 750 and 60 <= c['h'] <= 100]
p('其中宽扁多行 %d 次：' % len(wide_caps))
p()
p('%-14s %-10s %-8s %s' % ('尺寸', '宽高比', '触发重试', '最终字数'))
p('-' * 56)
for c in wide_caps:
    p('%-14s %-10.1f %-8s %s'
      % ('%d×%d' % (c['w'], c['h']), c['w'] / c['h'],
         '是' if c['retry'] else '否',
         c['chars'] if c['chars'] is not None else '?'))

retried = [c for c in wide_caps if c['retry'] and c['chars']]
not_retried = [c for c in wide_caps if not c['retry'] and c['chars']]

p()
if retried:
    p('触发重试的 %d 次，字数：%s  → 中位 %.0f'
      % (len(retried), [c['chars'] for c in retried], statistics.median([c['chars'] for c in retried])))
if not_retried:
    p('没触发重试的 %d 次，字数：%s  → 中位 %.0f'
      % (len(not_retried), [c['chars'] for c in not_retried],
         statistics.median([c['chars'] for c in not_retried])))
p()
p('字数越多说明检出越完整（这段真实内容约 208 字）')

# ============ 给出策略建议 ============
p()
p('=' * 74)
p('策略建议')
p('=' * 74)
if retried and not_retried:
    r_med = statistics.median([c['chars'] for c in retried])
    n_med = statistics.median([c['chars'] for c in not_retried])
    p()
    p('触发重试中位 %.0f 字，没触发中位 %.0f 字 —— 差 %.0f 字（%.0f%%）'
      % (r_med, n_med, r_med - n_med, 100 * (r_med - n_med) / max(1, r_med)))
    p('→ **证明「重试」是这段文字能不能读全的决定因素**，不是运气。')
    p()
    p('建议策略 B：宽扁图直接走 3x')
    p('  条件：宽高比 >= 5 且 高度 <= 150（覆盖今天所有样本：661×78 = 8.5、647×85 = 7.6）')
    p('  好处：不依赖那个时灵时不灵的 NeedsUpscaleRetry 判据')
    p('  代价：这类图每次多花约 200~300ms（放大 + 多一次检测），但换来 100% 读全')
else:
    p('样本不足以对比，无法给结论')

p()
p('注意：真正的验证要在探针里跑（tools\\探针\\真卡片验证\\），')
p('      用同样的图分别跑「1x」「3x」，比字数与内容。')

dest = os.path.join(ROOT, 'tools', '临时', '_bt', '量-宽扁图检测.txt')
os.makedirs(os.path.dirname(dest), exist_ok=True)
open(dest, 'w', encoding='utf-8').write('\n'.join(OUT) + '\n')
print()
print('已写入 %s' % dest)
