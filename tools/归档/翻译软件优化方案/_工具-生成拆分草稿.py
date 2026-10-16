# -*- coding: utf-8 -*-
r"""
把项目 progress.md 精确拆成三份草稿（纯搬运，不改字）。
输入：D:\111Desktop\deepseek\翻译软件\progress.md（只读，不改动）
输出：本方案文件夹 04-新文档成品（可直接拷贝）\ 下的三个文件
可重复运行（每次都会用最新读数覆盖生成）。
"""
import os

SRC = r"D:\111Desktop\deepseek\翻译软件\progress.md"
OUT = r"D:\111Desktop\workbuddy工作区\翻译软件优化方案\04-新文档成品（可直接拷贝）"
os.makedirs(OUT, exist_ok=True)

raw = open(SRC, encoding="utf-8").read()
t = raw.replace("\r\n", "\n").replace("\r", "\n")


def at(s, start=0):
    i = t.find(s, start)
    if i < 0:
        raise SystemExit("找不到标记: " + s[:60])
    return i


def clean(s):
    s = s.strip("\n")
    while s.endswith("---"):
        s = s[:-3].rstrip()
    return s


i_status = at("> **当前状态：")
i_stale = at("> **下面是上一轮的详细状态")
i_s1 = at("\n## 一、项目与工作方式")
i_s2 = at("\n## 二、已完成事项")
i_s3 = at("\n## 三、关键决策")
i_s4 = at("\n## 四、当前成绩账本")
i_s5 = at("\n## 五、未完成代办")
i_s6 = at("\n## 六、关键路径索引")
i_s7 = at("\n## 七、新对话开场白模板")

assert i_status < i_stale < i_s1 < i_s2 < i_s3 < i_s4 < i_s5 < i_s6 < i_s7

top_status = clean(t[i_status:i_stale])   # 最新一轮的“当前状态”
stack = clean(t[i_stale:i_s1])            # 被取代的旧状态堆叠
s1 = clean(t[i_s1:i_s2])
s2 = clean(t[i_s2:i_s3])
s3 = clean(t[i_s3:i_s4])
s4 = clean(t[i_s4:i_s5])
s5 = clean(t[i_s5:i_s6])
s6 = clean(t[i_s6:i_s7])
s7 = clean(t[i_s7:])

SEP = "\n\n---\n\n"

header_new = r"""# progress.md — 项目进展交接（AI 每轮必读）

> 🔴🔴 **最重要的一条规矩，先看这个（用户 m00893 原话：「还有没有我的允许不准随便回退！！！需要回退的时候需要询问我！！」）**：
> **没有用户明着点头，任何形式的「回退」都不许自己做**，必须先问。范围包括：撤代码、改 git 回退点、换回旧的引擎/语言包/参数、把版本号退回去、把发布目录换回旧的。**哪怕上一版成绩更好，也只能把「建议回退」摆出来让用户拍板，不许先斩后奏。**

> 📖 **用途**：新开对话时，把这个文件 + `制作流程-AI版.md` 一起喂给 AI，即可接着干。
> 本文件**只放 AI 要读、要记住的东西**（规矩 / 当前状态 / 已完成 / 关键决策 / 待办 / 索引）。
> 详细内容都在别处，要查再翻——**本文件不再承担下面这几种职能**：
>   - 逐轮详细记录（最权威）→ `tools\日志\阶段3-验收.md`
>   - 全部成绩账本（现行 + 历史）→ `tools\日志\成绩账本.md`
>   - 被取代的旧状态块（翻旧账）→ `tools\日志\进度历史-归档.md`
>   - 版本级更新日志 → `docs\更新日志.md`

> 📌 **流程文档（2026-10-13 起）**：现行制作流程是 `制作流程-AI版.md`（AI 执行规矩）+ `制作流程-用户版.md`（用户大白话）；旧 9 份已归档到 `tools\归档\`。**每次开工：先读本文件，再读 `制作流程-AI版.md`。**

> 🧹 **维护规矩（2026-10-16 定，防止再变臃肿）**：
> ① 每轮结束**只更新两处**：「★ 当前状态」块（**整块替换**）+「五、未完成代办」。
> ② 旧的「当前状态」块**不许往下堆**——整块剪进 `tools\日志\进度历史-归档.md`，要翻旧账去那里。
> ③ 完整过程写 `阶段3-验收.md`；成绩数字写 `成绩账本.md`；版本记录写 `docs\更新日志.md`——别往本文件里塞。
> ④ 本文件任何时候都要保持「读一遍（十分钟内）就能接着干」；**超过 500 行就是又出毛病了。**"""

ptr_s4 = r"""## 四、成绩账本（指针）

> 完整成绩账本（现行 + 全部历史）= `tools\日志\成绩账本.md`（2026-10-16 整体搬的家，一行没删）。
> 「最近一轮得了多少分」看本文件顶部「★ 当前状态」块。"""

note_newfiles = r"""> 📁 **存档位（2026-10-16 起）**：全部成绩账本 → `tools\日志\成绩账本.md`；被取代的旧状态块 → `tools\日志\进度历史-归档.md`。"""

s7_slim = r"""## 七、新对话开场白（给用户，可整段复制）

> 请先读 `progress.md`，再读 `制作流程-AI版.md`，最后读 `tools\日志\阶段3-验收.md` 的最后一节。
> **🔴 头号规矩：没有我明着点头，不许擅自回退**——撤代码、换旧引擎、退版本号都不行，只能提建议等我拍板。
> **测试方式（RULE 7）**：日常只框 `tools\素材\快测材料.html` 的 28 段，框完我说「测完了」，你跑 `tools\脚本\比对成绩.ps1` 出成绩。
> **当前任务和最新状态：见 `progress.md` 顶部「★ 当前状态」块。**
> 动手前先把三样说清：这轮做什么（一句话）、要动哪些文件、做完怎么验证；等我说「开始」再动手。
> 版本号按 RULE 4 放慢；做完先汇报、再停、等我指令。"""

header_arch = r"""# 进度历史归档 — 被取代的旧状态块（翻旧账专用）

> **这份文件是干什么的**：`progress.md` 顶部曾经一层叠一层的「上一轮状态……已被上面取代」块，2026-10-16 整体搬来这里，**一行没删**。
> **什么时候看**：要查「某一轮当时是什么状态、什么 commit、什么 tag、下一步原本要做什么」时。
> ⚠️ 里面的「当前状态 / 下一步」全是**当时**的，早已过期——最新的永远看 `progress.md` 顶部。
> 更权威的逐轮细节仍在 `tools\日志\阶段3-验收.md`；这里只是把「当时写给下一个对话的交接摘要」留档。"""

header_ledger = r"""# 成绩账本 — 全部测试成绩（现行 + 历史）

> **这份文件是干什么的**：本文件 = `progress.md` 原「四、当前成绩账本」「四之二、历史成绩」「四之三、更早的历史成绩」三节，2026-10-16 整体搬家，**一行没删**。
> **什么时候看**：查「某个版本 / 某一轮得了几分、哪几段判错、当时什么结论」时翻这里。日常干活只看 `progress.md` 顶部「★ 当前状态」。
> **维护**：每轮实测完，把新成绩表加在**最上面**（或先写进 `阶段3-验收.md`，再同步一条到这里）。"""

new_progress = (
    header_new
    + SEP
    + "## ★ 当前状态（每轮只更新这一块；旧的整块搬进 `tools\\日志\\进度历史-归档.md`）\n\n"
    + top_status
    + SEP + s1
    + SEP + s2
    + SEP + s3
    + SEP + ptr_s4
    + SEP + s5
    + SEP + s6 + "\n\n" + note_newfiles
    + SEP + s7_slim
    + "\n"
)

new_archive = (
    header_arch
    + SEP + stack
    + SEP + "# 附录：旧版「新对话开场白模板」全文\n\n"
    + "> （2026-10-16 起被 progress.md 第七节的精简版取代；以下为原文留档。）\n\n"
    + s7
    + "\n"
)

new_ledger = header_ledger + SEP + s4 + "\n"

for name, content in [
    ("progress.md", new_progress),
    ("成绩账本.md", new_ledger),
    ("进度历史-归档.md", new_archive),
]:
    p = os.path.join(OUT, name)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        f.write(content)
    print(name, "->", len(content.split("\n")), "行")

print("原 progress.md 总行数:", len(t.split("\n")))
print("完成。输出目录:", OUT)
