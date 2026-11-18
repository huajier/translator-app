# RapidOCR "7" 调研报告 · 下半（方向二：联合过滤）

> 上半在 `报告-OCR幻觉与联合过滤-上.md`。2026-11-18 拆分（原文件 30 KB 超钩子）。

## 方向二：检测框与识别结果的联合过滤

### Q4. 业界怎么做「检测+识别」联合置信度过滤？只看识别分够不够？

**只看识别分不够，这一点有直接一手证据。**

`drop_score` / `text_rec_score_thresh` 只作用于识别分数，而 PaddleOCR collaborator 在 #18373 里明说：
> "**adjusting the thresholds probably won't fix it for now**"

为什么不够？因为识别分是**条件概率**：给定"这一块是文字"，模型认为它是什么的置信度。**它不回答"这一块到底是不是文字"**。噪声框里模型可能非常自信地读出某个字形状，分数不低。所以业内标准做法是**两段独立判定 + 后处理**：

**证据 1：PaddleOCR 的检测侧自带几何过滤，但只有两条**
`ppocr/postprocess/db_postprocess.py`（release/2.7，RapidOCR 的 `ch_ppocr_det/utils.py` 逐行同构）
<https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/ppocr/postprocess/db_postprocess.py>
<https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/ch_ppocr_det/utils.py>
```python
num_contours = min(len(contours), self.max_candidates)
for index in range(num_contours):
    points, sside = self.get_mini_boxes(contour)
    if sside < self.min_size:
        continue                                    # ← 几何过滤 1：最小边长
    score = self.box_score_fast(pred, points.reshape(-1, 2))
    if self.box_thresh > score:
        continue                                    # ← 置信度过滤
    box = self.unclip(points, self.unclip_ratio)
    box, sside = self.get_mini_boxes(box)
    if sside < self.min_size + 2:
        continue                                    # ← unclip 之后再查一次
```
`DBPostProcess.__init__` 里 `self.min_size = 3`。

**所以：PaddleOCR / RapidOCR 的检测后处理里，宽高比、面积、与邻居距离，一条都没有。** 我对着两个仓库的源码逐个确认过。你要加，就是自己往这上面加。

**证据 2：Tesseract 用的是"面积/尺寸/密度比"，而不是宽高比**
见 Q3 的 `textord_noise_*` 那一组。注意它的核心量是**相对 x_height 的尺寸**和**dot/norm 计数比**，逻辑是"这一块里碎块太多 ⇒ 它不是字"，跟宽高比无关。

**证据 3：EasyOCR（CRAFT）用的是"连通域面积 + 峰值置信度"，也是几何但其形式是面积阈值**
`easyocr/craft_utils.py`
<https://github.com/JaidedAI/EasyOCR/blob/master/easyocr/craft_utils.py>
```python
for k in range(1, nLabels):
    # size filtering
    size = stats[k, cv2.CC_STAT_AREA]
    if size < 10: continue                       # ← 绝对像素面积
    # thresholding
    if np.max(textmap[labels == k]) < text_threshold: continue
```
且它连"框是不是歪的"都处理了（`box_ratio` 接近 1 时拉直成矩形），但**没有**基于宽高比做拒绝：
```python
box_ratio = max(w, h) / (min(w, h) + 1e-5)
if abs(1 - box_ratio) <= 0.1:
    # 拉成轴对齐矩形（注意：是修正，不是拒绝）
```

**证据 4：EasyOCR 有一个"低置信度二次处理"机制，是联合过滤的另一种思路**
`easyocr/recognition.py`
<https://github.com/JaidedAI/EasyOCR/blob/master/easyocr/recognition.py>
```python
# 第一轮
result1 = recognizer_predict(...)
# 第二轮：只对低置信度的重跑，且用调整对比度的预处理
low_confident_idx = [i for i, item in enumerate(result1) if (item[1] < contrast_ths)]
if len(low_confident_idx) > 0:
    AlignCollate_contrast = AlignCollate(..., adjust_contrast=adjust_contrast)
    result2 = recognizer_predict(...)
# 取两轮里分高的那个
if pred1[1] > pred2[1]:
    result.append((box, pred1[0], pred1[1]))
else:
    result.append((box, pred2[0], pred2[1]))
```
参数：`contrast_ths=0.1, adjust_contrast=0.5, filter_ths=0.003`。
**这是"低分不是直接丢，而是换个预处理再试一次"** —— 对你的场景有参考价值：低分框可能真的没字，也可能是对比度太低，二次判定能区分。

**证据 5：三个参数各自能滤掉什么（官方文档原句 + 实现）**

官方英文定义（**逐字抄自 HTML 源码，不是转述**），
<https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/pipeline_usage/OCR.en.md>

| 参数 | 官方原句（verbatim） | 文档写的默认 |
|---|---|---|
| `text_det_thresh` | "Pixel threshold for text detection. In the output probability map, pixels with scores higher than this threshold will be considered text pixels." | `0.3` |
| `text_det_box_thresh` | "Text detection box threshold. **If the average score of all pixels within the detected result boundary is higher than this threshold, the result will be considered a text region.**" | `0.6` |
| `text_det_unclip_ratio` | "Text detection expansion coefficient. This method is used to expand the text region—**the larger the value, the larger the expanded area.**" | `2.0` |
| `text_rec_score_thresh` | "Text recognition threshold. Text results with scores higher than this threshold will be retained. ... **defaults to `0.0`, i.e., no threshold**" | `0.0` |

> ⚠️ **发现一处官方内部不一致，值得你知道**：`text_det_unclip_ratio` 在**这份 pipeline 文档里写默认 2.0**，但在
> `PaddleX/paddlex/configs/pipelines/OCR.yaml` 里配置的是 **1.5**，而 `DBPostProcess.__init__` 的**类默认值又是 2.0**。
> 三处两个值。**实际生效的是 yaml 的 1.5**（pipeline 会显式传参覆盖类默认）。
> 这说明这类参数没有单一权威默认值 —— 你改它的时候务必先打印实际生效值，别信文档。

2.x 的 `drop_score` 就是 3.x 的 `text_rec_score_thresh`，实现见
`tools/infer/predict_system.py`（release/2.7）
<https://github.com/PaddlePaddle/PaddleOCR/blob/release/2.7/tools/infer/predict_system.py>
```python
for box, rec_result in zip(dt_boxes, rec_res):
    text, score = rec_result[0], rec_result[1]
    if score >= self.drop_score:
        filter_boxes.append(box)
        filter_rec_res.append(rec_result)
```

三者能滤掉的假阳性类型（**这一列是我的工程推断，基于上面的实现，不是官方声明**）：

| 参数 | 机制上能滤掉 | 机制上滤不掉 |
|---|---|---|
| `det_thresh` (0.3) | 概率图里大面积低响应的区域根本没进 bitmap（小于阈值的像素直接不算文字像素） | 小块高响应（纹理/笔画/图标边缘） |
| `box_thresh` (0.6) | 框内**平均**响应低的框 —— 官方原句就是 "average score ... is higher than this threshold" | 小框里三五个像素就很亮 → 均值轻松过 0.6 |
| `unclip_ratio` (1.5) | 不直接滤；**调小会让框更紧，从而让 `min_size` 更可能命中**（官方原句："the larger the value, the larger the expanded area"） | 调大反而把邻域噪声包进框 |
| `drop_score` / `rec_score_thresh` | 识别分低的框（**你的 "7" 就死在这一关**） | 识别分不低的噪声框；且 3.x 默认 0.0 = 不过滤 |

**有没有实测对比？** —— **没有。** 我找不到任何一手来源做过 `drop_score` vs `box_thresh` vs `unclip_ratio` 三者的消融/对比实验。PaddleOCR issue 区搜 `drop_score` 是 0 结果，官方文档没有任何对比表，论文里也没有（PP-OCRv5 论文的消融是 data-centric 的，跟这三个阈值无关）。这一点我明确说找不到。

### Q5. 孤立框：周围没有任何其他框 ⇒ 更可能是噪声？有人用这个启发式吗？

**结论：找不到任何一手来源说有人用"孤立度"作为文本检测的过滤启发式。** 这是本次检索里最干净的否定结论，理由如下。

我尝试过的检索：GitHub issue/PR/discussion 全局搜 `"isolated text box"`、`OCR "isolated" text box noise filter`、`"孤立" 文本框 检测 噪声`；arXiv 搜 `text spotter false positive suppression`、`non-text rejection OCR`；以及通读 PaddleOCR / RapidOCR / Tesseract / EasyOCR 四家的后处理源码。**四个引擎的源码里没有任何一处用"与最近的框的距离"或"邻居数量"做判定。**

我能找到的**最接近**的东西，都是"邻居投票"而不是"孤立判定"：

**Tesseract 的行级/词级多数投票**（Q3 已引）
```cpp
return super_norm_count < textord_noise_sncount && rejected;
// 以及
if (word_dud[word_index] == 2 || (word_dud[word_index] == 1 && dud_words > ok_words)) {
```
注意它的方向：**"这一行里大多数词都像噪声"才拒绝**。一个孤立但正常的框，按这个规则恰恰**更容易被保留**（因为它没有"一群可疑邻居"）。所以 Tesseract 的逻辑跟你设想的启发式**方向相反**。

**DB 论文本身**（<https://arxiv.org/abs/1911.08947>、<https://arxiv.org/abs/2202.10304>）讲的是可微二值化的阈值学习：
> "a segmentation network can adaptively set the thresholds for binarization, which not only simplifies the post-processing but also enhances the performance of text detection."
没有任何与框间距离/孤立度相关的内容。

**为什么这个启发式在你这个场景可能是危险的（我的推论，不是证据）：**
- 游戏促销横幅里 "WISHLIST NOW" / "ON STEAM" 两行本身就是**两个孤立框**（间距大、没有第三个框）。用孤立度过滤，很可能把真目标一起干掉。
- 反过来，你要杀的 "7" 如果是**紧贴**某个真框产生的那种碎片，孤立度完全测不出来。
- 结论：**孤立度不是这个 bug 的对症特征**。

**那什么是对症特征？** 用你自己的数据就能验，不必找论文：那个 "7" 的框，其**识别分**低于你的阈值（这是你关掉过滤才看到它的直接原因）、其**框面积/短边**大概率显著小于两个真行框。这两条都是可从现场直接量出来的，且 PaddleOCR 实现里已经有现成的挂点（`min_size`）。

### Q6. `drop_score` vs `box_thresh` vs `unclip_ratio` 三者实测对比

**没有。** 见 Q4 末尾。能找到的最接近的东西：

1. **官方文档只给语义，不给对比**（Q4 表格已引全）。
2. **collaborator 的一句话定性结论**（#18373）："adjusting the thresholds probably won't fix it for now" —— 即对**检测阶段幻觉**，调阈值无效。这是唯一一句来自官方、关于"调阈值有没有用"的判断。
3. **PaddleOCR 的默认值本身在不同地方不一致**，这本身是个有用的信号：
   - `tools/infer/utility.py`（2.7）：`det_db_box_thresh=0.6`, `det_db_unclip_ratio=1.5`
   - `PaddleX OCR.yaml`（3.x）：`box_thresh: 0.6`, `unclip_ratio: 1.5`
   - **RapidOCR 默认更激进**：`box_thresh=0.5`, `unclip_ratio=1.6`, `max_candidates=1000`, `use_dilation=True`（单测锁定：<https://github.com/RapidAI/RapidOCR/blob/main/python/tests/test_default_config.py>）
   - `DBPostProcess.__init__` 的**类默认值**又是 `box_thresh=0.7, unclip_ratio=2.0`（只有不传参时生效）
   
   同一族代码三套默认值，说明**没人给出过这组参数的普适最优**，都是拍出来的。

---

## 明确区分：证据 vs 推测

### 有证据的（一手，可点开核对）

| # | 结论 | 来源 |
|---|---|---|
| E1 | PaddleOCR 官方 collaborator 用 "hallucination" 描述**检测模型**输出，并称调阈值修不好 | [#18373](https://github.com/PaddlePaddle/PaddleOCR/issues/18373) |
| E2 | 空白区域被识别成多余字符，是维护者承认且无阈值解法的现象 | [#12111](https://github.com/PaddlePaddle/PaddleOCR/issues/12111) |
| E3 | det→rec 之间，短框右侧补 0，补 0 引入噪声，短文本更易错 | [#10534](https://github.com/PaddlePaddle/PaddleOCR/issues/10534) + RapidOCR `ch_ppocr_rec/main.py` |
| E4 | 全 blank 预测 ⇒ 文本 `""`，分数 0.0（RapidOCR 显式 `conf_list=[0]`） | `ch_ppocr_rec/utils.py` |
| E5 | PaddleOCR 2.x decode 遇**第一个** blank 即 break（可致多字/缺字） | `ppocr/postprocess/rec_postprocess.py` |
| E6 | PaddleOCR 3.x 默认 `score_thresh: 0.0` = **不做识别分过滤** | `PaddleX OCR.yaml` + 官方文档原句 |
| E7 | 检测后处理里**只有** `min_size`（=3）和 `max_candidates` 两条几何约束，无宽高比/面积/孤立度 | `db_postprocess.py`、RapidOCR `ch_ppocr_det/utils.py` |
| E8 | Tesseract 有成熟的 `textord_noise_*` 几何噪声拒绝，核心是相对 x_height 的尺寸比 + dot/norm 密度比 + 整行多数投票 | `src/textord/textord.cpp`、`src/textord/tordmain.cpp` |
| E9 | EasyOCR 用连通域面积 (<10 丢弃) + 峰值置信度，且低分框走"二次预处理重试"而非直接丢弃 | `easyocr/craft_utils.py`、`easyocr/recognition.py` |
| E10 | **找不到**任何引擎用"孤立度"过滤；Tesseract 的多数投票方向甚至相反 | 四家源码通读 + issue/arXiv 检索 |
| E11 | **找不到** `drop_score`/`box_thresh`/`unclip_ratio` 的实测对比 | issue 检索 0 结果、文档无对比表、论文无相关消融 |
| E12 | 学界公认现有 OCR 评测**覆盖不到** hallucinated text | [arXiv:2603.25761](https://arxiv.org/abs/2603.25761) |
| E13 | PP-OCRv5 论文全文 `hallucin*` 仅 7 次，全在 intro/related work，无量化指标 | [arXiv:2603.24373 HTML](https://arxiv.org/html/2603.24373v1) |

### 我的推测（**没有一手来源**，请当成假设去验）

- **P1**：你那个 "7" 是模型在无字区域的若干时间步上 argmax 落到字符类，而不是"全 blank 解码成乱码"。依据：全 blank 得 `""`+0.0，关掉分数过滤也不会显示成 "7"；而它关掉过滤就出现，说明只是分数低。（强推断，但未实测）
- **P2**：短/孤立框因 batch 内 0 padding 而更容易幻觉。依据 E3 的机制 + `max_wh_ratio` 取 batch 最大值的源码。**未实测**你的具体 case。
- **P3**：宽高比对这个 "7" 不敏感（数字 "7" 的框宽高比可能很正常），面积/短边更可能敏感。**完全没验证。**
- **P4**：孤立度启发式在你这个两行横幅场景会有**假阴性风险**（两行真文本本身就是孤立的两个框）。**没验证**，但逻辑上成立。
- **P5**：`box_thresh` 调高对"小面积高亮碎片"基本无效，因为框内平均响应会被少数亮点拉过阈值 —— 这是对 `box_score_fast`（框内 mask 均值）的推理。**未实测。**

---

## 没找到的（明确说明，不编）

1. **"给 PP-OCRv5/PaddleOCR 识别模型喂纯空图，输出是什么"的实验** —— 没有。只有源码能推断出两种分支行为（E4/E5）。
2. **`drop_score` vs `box_thresh` vs `unclip_ratio` 的实测对比/消融** —— 没有。
3. **用"孤立框/邻居距离"做 OCR 假阳性过滤的一手来源** —— 没有。四家引擎都没这么做。
4. **PP-OCRv5 的量化幻觉率** —— 论文只有定性表述，没有数字。
5. **"box 内没有真实文字"的标准度量** —— 没有标准；只有 Tesseract 式的工程启发式参数。

---

## 如果要把这些落到你的代码上（最小建议，非要求）

按证据强度排序，最省事且证据最硬的顺序：

1. **先把"分数过滤"当成坐标轴而不是开关。** 你的现场已经证明它在起作用（挡住 "7"）。E6 说明 PaddleOCR 3.x 默认根本不过滤，你现在关掉它等于回到 3.x 默认行为。**先量一下那个 "7" 的实际识别分是多少**——这个数字决定了后面所有方案。
2. **加一条 `min_size` 的"相对"版本**，这是 E7/E8 都支持的：PaddleOCR 的 `min_size=3` 是**绝对像素**，对高分辨率截图几乎不设防。Tesseract 的做法是相对 `x_height`（`0.5 × x_height`）。用"框高相对本图其他框中位数的比例"比绝对像素稳。
3. **不要用孤立度。** E10 明确没有先例，且 P4 说明在你这个场景可能是反向的。
4. **E9 的二次判定值得抄**：低分框不直接丢，换一次预处理（对比度）重跑，两轮取高分。这能区分"真没字"和"对比度低"。

> 关于 `ponytail`：本次是纯调研任务，没有写产品代码。产出只有本报告 + `.tmp-research/notes/` 下的取证笔记。仓库 `src/`、`release/` 未改动（符合「架构师不写代码」的边界）。
