import re, sys
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

lines = open('tools/日志/app-20261019.log', 'rb').read().decode('utf-8-sig', errors='replace').splitlines()

# 以「最终原文：」为一次取词的结束，向前切段
starts = [i for i, l in enumerate(lines) if '最终原文' in l]
bounds = []
prev = 0
for s in starts:
    bounds.append((prev, s))
    prev = s + 1

def parse(seg):
    planes = []
    final = None
    for i, l in enumerate(seg):
        if '最终原文' in l:
            final = l.split('最终原文：', 1)[1].strip()
        m = re.search(r'四路之一「(.+?)」：置信度 ([\d.]+)，垫底词分 ([\d.]+)（(.+?)），内容【(.*)】', l)
        if not m:
            continue
        outdict = None
        for j in range(i - 1, max(i - 6, -1), -1):
            if '词级自报' in seg[j]:
                outdict = seg[j].count('词典✗')
                break
        planes.append({
            'plane': m.group(1),
            'conf': float(m.group(2)),
            'minw': float(m.group(3)),
            'text': m.group(5).strip(),
            'outdict': outdict,
            'ts': l[1:20],
        })
    return planes, final

shown = 0
for a, b in bounds:
    seg = lines[a:b + 1]
    planes, final = parse(seg)
    valid = [p for p in planes if p['text'] and p['outdict'] is not None]
    if len(valid) < 2:
        continue
    best = min(valid, key=lambda p: (p['outdict'], -p['minw']))
    shown += 1
    print(f"=== #{shown} {valid[0]['ts']} ===")
    for p in valid:
        print(f"  {p['plane']:6s} 分{p['conf']:.2f} 垫底{p['minw']:6.2f} 词典外{p['outdict']}  {p['text'][:50]}")
    print(f"  现状最终：{final}")
    print(f"  新规则选：{best['plane']}  {best['text'][:50]}")
    print()

print('可比较的取词次数:', shown)
