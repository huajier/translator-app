# 阶段三 S4-D：L+D 类 8 条只读诊断（2026-10-30）

> 只读。未改任何 `src/`。全部数字可用 §6 的命令复现。
> 口径：生产管线吃反色图（`RapidOcrUseOriginal=false` → `raw`），基线 85/121=70.2%、L_行 49/69=71.0%、L_整块 36/52=69.2%。
> 探针保真自检：`OptProbe`「反色 1x」= **85/121**，与生产基线**总分相同**，但逐图文本一致只有 **70/80**（生产还叠了规范化/修剪/乱码门）→ 下文所有变体收益都是**上限估计**。

## 1. 8 条逐条拆（漏检 / 塌行 / 字符崩坏）

| id | 游戏 | 尺寸 | 真值 | 现状 | 归类 | 机制 |
|---|---|---|---|---|---|---|
| 行-08 | Barony | 86×26 | `CON 0` | `COM ` | **字符崩坏** | 1 块 chars=3，检出宽度只到 x=79，`N 0` 与数字整体没检到 |
| 行-10 | Barony | 86×26 | `PER -1` | `一 1` | **通道走错** | RapidOCR 根本没跑：`IsMostlyChinese` 判真 → `RunChinesePath`（`OcrTextCapture.cs:322`） |
| 行-13 | Barony | 172×28 | `Ranged ATK Bonus:` | `d 亻 K 孬：` | **通道走错** | 同上，走中文路，chi_sim 三次 0.34/0.19/0.24 全判非中文 → 保留 Windows 垃圾 |
| 行-38 | Galactic | 352×118 | `What are` + `CHALLENGES?` | `CHALLENGES?` | **漏检（整行丢）** | 1 块 h=91px 覆盖两行却只解出一行，首行整个没出框 |
| 行-68 | Big Ambitions | 60×30 | `3d 8h` | `48 pε` | **字符崩坏** | 1 块 chars=4，`3d`→`48`、`8h`→`pε`，字高仅 30px |
| 行-76 | Big Ambitions | 487×19 | 55 字长句 | `oomsnfrany changs cickig RD`（24 字） | **塌行** | 1 块 h=19px/chars=24，矩形把 55 字压成 24 字，weak=8 |
| 行-82 | Big Ambitions | 310×305 | 7 行（含 `0/7`） | 6 块，`0/7` 未检出 + `0/50`/`4.6%` 行序颠倒 | **漏检 + 行序** | 检测框数 6 < 真值 7；行序属 E 类，已记账 |
| 行-83 | Big Ambitions | 491×126 | 4 行 | 4 块，第 2 块 w=415 只出 5 字 `CICDA` | **塌行** | chars=5/真值 55 字，mean=0.683、weak=2 |

**归类合计**：塌行 2（行-76、行-83）｜漏检 2（行-38、行-82）｜通道走错 2（行-10、行-13）｜字符崩坏 2（行-08、行-68）。

## 2. 可行干预（逐条，含现成方案）

### 干预 A：小框先放 3 倍再喂 RapidOCR（覆盖 L+D 里的 行-68、行-76）

判据（运行时可直接算）：**`高 ≤ 40px 且 宽 < 550px`** → 先把反色图放大 3 倍再 `Detect`。

**探针保真自检（必须先看这条）**：`OptProbe` 的「反色 1x」变体与生产管线逐图文本一致只有 **70/80**，不是 80/80——生产还叠了 `TextNormalizer`、`TrimEdgeSymbols`、乱码门、回退路径等。所以下表数字是**上限估计，误差 ±2 图**，落地后必须以真实管线复测为准。

实测（`反色+放大3倍` 变体，只替换命中判据的图，其余保持生产基线的实际输出；**行-10/行-13 生产走中文路、RapidOCR 不跑，故 A 单独时这两张保持原样**）：

| 口径 | 基线 | A 单独 | A + 路由修复(B) |
|---|---|---|---|
| 全量 | 85/121 = 70.2% | **88/121 = 72.7%**（+3 图） | **89/121 = 73.6%**（+4 图） |
| L_行（单行 69） | 49 = 71.0% | **52 = 75.4%**（+3 行 / +4.3pp） | **53 = 76.8%**（+4 行 / +5.8pp） |
| L_整块（整块 52） | 36 = 69.2% | 36 = 69.2% | 36 = 69.2% |

- A 单独收益 5 图：**行-68** `48 pε`→`3d 8h`、**行-76** 塌行 24 字→55 字全对、**行-75** `Be CLICK … [`→全对（该图正是 P95 最慢段）、**行-43** `2 - Hell Dive`→`12 - Hell Dive`、**行-52** `CZhang`→`C-Zhang`。其中 行-68/76 属 L+D，行-43/52/75 是 L+D 之外的意外收益。
- A 单独回归 2 图：**行-11** `CHR 1`→`FCHR 1`、**行-69** `0/7`→`017`。两图都是 3~5 字的极短文本，放大后 DB 检测器多吐一个字块碎片。
- 净：+5 图 / -2 图 = **+3 图**。
- 回归无几何/字数单调门可排除：行-10 与 行-11 **同尺寸 86×26**；行-69 现状仅 3 字、行-10 仅 2 字，任何「字数 ≥ N」的门都会先砍掉 行-10（实测 mc8/mc10 掉到 88/121）。
- 阈值敏感性（A+B 口径）：`高≤38/40/42/45/50` 全部 **89/121**，即 40 不是尖峰、附近平坦，取 40 安全。
- 现成方案：**无新增依赖**。放大用 `System.Drawing.Graphics`（项目已用 `UpscaleForForeign` 同款）。**不能靠调 `RapidOcrOptions.LimitSideLen` 实现**：实测 `limit1216` 单独 = 85/121 不变，因为它是「封顶」不是「放大」。等价官方做法见 PaddleOCR 文档 `det_limit_side_len` + `limit_type="min"`（保证短边不小于该值），PaddleOCR 官方把「小字/漏检」的推荐解法写的就是提高检测输入边长。

### 干预 B：RapidOCR 先跑，中文路降为兜底（覆盖 行-10/13）

`src/Core/Capture/OcrTextCapture.cs:322` 现在是 `if (!windowsSaysChinese || colorful) RunForeignPath(); else RunChinesePath();`。改成先跑 RapidOCR，只有 RapidOCR 也失败才落 `RunChinesePath`。

- 增量（A+B 减 A 单独）：**+1 图**，行-10 `一 1`→`PER -1`。
- 行-13 仍错（3x 后是 `Ronged ATK Bonus:`，Ranged→Ronged 形近字），属已停的字级校验线，B 救不了。
- 改动点在通道仲裁，风险高于 A；**建议 B 与 A 同批但独立开关**，A 落地验证后再决定 B 是否默认开。
- 现成方案：无（仓内路由）。PaddleOCR 文档无对应项。

### 干预 C：`box_thresh` 0.5 → 0.2（覆盖漏检类 行-82 的部分）

单独跑（全量）：**88/121 = 72.7%，L_行 78.3%、L_整块 65.4%**。整块反而降 2.6pp（多吐碎块）。

- 现成方案：**PaddleOCR 官方文档明写**「检测准确但有漏检」的调参建议就是 `box_thresh=0.5`（默认）、必要时下调 `thresh`、提高 `limit_side_len`；`RapidOcrOptions.BoxThresh` 默认 0.3、`BoxScoreThresh` 默认 0.5。属官方支持的正规调参，不是自造。
- **结论：C 单独不划算（整块掉），但可作为 A 的叠加项在申请单里做一次 A/B，不许默认开。**

### 干预 D：整块也放大（覆盖 行-38/82/83）—— 不推荐

`宽<550 全放大 3x` → 75.2%，L_行 84.1% 但 **L_整块 63.5%（-5.7pp）**。行-82/83 的 3x 结果确实变好（行-82 出 7 块含 `0/7`；行-83 第 2 块 5 字→55 字全对），但同批整块被放大改坏的更多。

- 结论：**不在本轮做**。整块要么按块单独判据（尚无可靠信号），要么等 S4 的提示路径已引导逐行。

### 干预 E：行-08 的 `CON 0`

3x 后仍是 `CON`（缺 ` 0`），2x 也缺。参数扫描（unclip/limit/box_thresh）全部不出 `0`。**这是 86×26 里的真实像素不足以分辨尾数字**，属超分模型范畴。**在现有约束下无可行干预**——依据见 §5 检索记录（唯一可候选的 NuGet 超分包 `Intel.AI.Skills.SuperResolution v1.0.0.4` 需要 Windows ML/Intel 硬件，体积与 D5-R7 的 ≤300MB 冲突，且不承诺文本保真）。

## 3. 建议的申请单范围（2 条，等批准）

1. **干预 A**：`RapidOcrEngine.TryRecognize` 调用点，判据 `高≤40 && 宽<550` → 3x 放大后送 `Detect`。开关 `rapidOcrSmallBoxUpscale`，**默认 false**，只 A/B。
2. **干预 B**：`OcrTextCapture.cs:322` 让 RapidOCR 先跑，`RunChinesePath` 降为兜底。开关 `rapidOcrBeforeChinese`，**默认 false**，与 A 分开 A/B。

**不做**：C（`box_thresh 0.2`，整块 -2.6pp）、D（整块也放大，整块 -5.7pp）、E（行-08 无可行干预）。

验收口径（照旧）：开关全关 → 80/80 逐字节相同；A 开 → 全量目标 88/121、L_行 52/69，L_整块 36/52 不得降；A+B 开 → 89/121、53/69；Z1/Z2/Z3/R1 逐字节不变；P95 不得升。

## 4. 风险

- **回归 2 图无法用几何/字数门排除**（行-11、行-69 都是极短文本，与目标图行-10 同尺寸）。申请单里必须写明「接受 -2 图回归换 +5 图收益」，或对极短文本另加规则——但该规则会同时砍掉 行-10，需你裁决。
- **探针保真度只有 70/80**（「反色 1x」与生产逐图一致数）。上表全部数字是上限估计；真实管线里 `TextNormalizer`/`TrimEdgeSymbols`/乱码门会再改一部分文本，必须以落地后实测为准。
- 3x 后可能撞 `MaxSideLen=2000` 封顶：最宽命中图 491×126 → 1473×378 未越界，但若出现 `高≤40 且 宽>660` 的图会二次缩放，实现时须加 `min(3, 2000/w)` 保护。
- 耗时未测：52/80 张图要走放大路径（GDI 位图运算），需在真实管线量 P95。

## 5. D8 检索记录（本机联网受限，逐条标注可达性）

**可达性**：`web_search` 工具返回 `HTTP 401 Authentication Fails`（插件 key 无效，只有你能改）；`web_fetch github.com` 被拒（非公网 IP）；CDP 浏览器路线连不上。可直连：`api.nuget.org`、`pypi.org`、`arxiv.org`、`gitee.com`、`www.paddleocr.ai`。不可达：`github.com` / `api.github.com` / `raw.githubusercontent.com` / `huggingface.co` / `jsdelivr`。

| 检索项 | 链接 | 别人怎么解 | 适不适用 | 门槛 |
|---|---|---|---|---|
| PaddleOCR 文本检测参数与漏检 FAQ | https://www.paddleocr.ai/main/version3.x/module_usage/text_detection.html | 官方把漏检归因于 `thresh`/`box_thresh`/`unclip_ratio`/`limit_side_len`；建议下调 box_thresh、提高检测输入边长 | **适用**（`RapidOcrNet` 直接暴露 `BoxThresh`/`UnClipRatio`/`LimitSideLen`） | 无新增体积；调参不增依赖 |
| NuGet `superresolution` / `upscale` 搜索 | https://azuresearch-usnc.nuget.org/query?q=superresolution | 仅 `Intel.AI.Skills.SuperResolution v1.0.0.4`、`Intel.AI.Skills.SuperResolutionWinML`、`AiImageUpscaler v0.1.0`、`tryAGI.Topaz`、`JRK.JevRunner.Image` | **不适用**：Intel 系需 Windows ML + Intel 硬件；Topaz/JRK 为用户级算法封装（体积/许可不明，离线环境无法核） | 违反 D5-R7 ≤300MB 与离线要求 |
| NuGet `rapidocr` | https://azuresearch-usnc.nuget.org/query?q=rapidocr | `RapidOcrNet v4.2.0` 下载 98998（**本项已在用**）；`BrycensRanch.RapidOcrNet v1.0.1`、`RailReader.Core.Ocr.RapidOcr v0.76.0` | 已在用主版本 | — |
| arXiv 文本超分 | https://export.arxiv.org/api/query?search_query=ti:OCR+AND+abs:resolution | 4 篇：TextSR（内容感知文本超分引导识别）、Scene Text SR via Content Perceptual Loss & Criss-Cross Transformer、Self-supervised Character-to-Character Distillation for Text Recognition、Scene Text Image SR in the Wild | **不适用**：都是训练式 SR 模型（需 GPU 训练 + 权重），无现成 .NET 离线权重，体积远超门槛 | 违反 D5-R7 与离线 |
| GitHub issues / 仓库 | — | **不可达**（`github.com` / `api.github.com` 被拒） | 未取得 | — |
| Bing 技术长尾关键词 | https://www.bing.com | 会被中文语境误判（`PP-OCRv5`→聚丙烯、`pixel font OCR`→Google Pixel） | **不可用** | — |

**结论**：干预 A 的「提高检测输入边长」与干预 C 的「下调 box_thresh」**都有官方文档背书**，不是自造；行-08 属像素不足，超分路线在现有体积/离线约束下无可行现成方案（**这一条才写「在现有约束下无解」，并附上表**）。

## 6. 复现

```powershell
# 1) 探针（六变体，反色为基线）
dotnet run --project tools\临时\OptProbe -c Release -- `
  "release\v0.3.4.3-gest\models\v5" "tools\临时\optprobe-全量.txt" "tools\临时\全量-清单.txt"

# 2) 判分（任一结果文件）
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\脚本\比对回放.ps1" -按行 `
  -Result "tools\临时\变体\规则D_H40_w550_inv3x.txt" | iconv -f GBK -t UTF-8

# 3) 逐图改善/回归
#    比对 tools\临时\回放-gest-1.txt.行明细.tsv 与 <结果>.行明细.tsv 的「对」列
```
