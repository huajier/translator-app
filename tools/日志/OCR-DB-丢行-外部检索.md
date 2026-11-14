# OCR 长段落「整行成片消失」外部检索报告（2026-11-14）

> 只读检索，未改 `src/`。链接一律优先官方源码 / 官方 issue / 论文原文。
> 本机 google 被代理拦截，全程走 Chrome CDP + cn.bing.com + github.com 直连。
> 可达：github.com、api.github.com、raw.githubusercontent.com、huggingface.co、ar5iv、paddleocr.ai、nuget。
> 算术自检：`node tools/临时/det-scale-check.mjs`（assert 全过打印 ALL OK）。
> 英文原句逐条摘录见 **`OCR-DB-丢行-原文摘录-1.md`**（DBNet 论文 / PaddleOCR 源码 / PaddleOCR issue）
> 与 **`OCR-DB-丢行-原文摘录-2.md`**（RapidOCR / RapidOcrNet / ONNX Runtime / 切片族）。
> 本文件只保留最关键的引用；**§3 起的缩放算术与落点表在 `OCR-DB-丢行-外部检索-2.md`**。
---

## 0. 结论

**主因是两件事叠加，`min_size=3` 只是最后补刀的那一刀：**

1. **DB 的 shrink 把行压薄。** 网络被训练成输出「收缩 r=0.4」的正样本区，长条行剩余厚度 = `h·r² = 0.16h`
   （推导：长行 `L≈2w`，`D ≈ h(1−r²)/2`，剩余 = `h − 2D = h·r²`）。
   屏幕正文 12~18px 行 → det 空间 10.6~15.8px → 正样本 **1.8~2.8px**，正好压在 `min_size=3` 上，
   余量只有 0~2px。**这是「小字检出率天然低」的地基。**
2. **粘连与拆框。** `unclip_ratio` 扩张 + 膨胀把行间距小的相邻行粘成一个框，一个框里挤多行；
   反过来同一行也可能被拆成几块。官方 #18244 复现结论原话：
   > "DB post-processing with unclip_ratio expansion can cause adjacent cells to merge or small boxes to be truncated near borders."

**「16 行 → 4 行」假说：粘成 4 个框，不是 16 次独立漏检。**
一条命令可证伪：把 `DetectBoxes` 的框数打出来看是不是掉到 4。
**「0 行」则是粘出来的框被识别端整块丢光**——`TextScore 0.5` 按**整块平均分**丢
（`RapidOcr.cs` 里 `if (avg < textScore) continue;`），一块里挤几行 → 平均分必掉 → 整块消失。

**「同一张图重复识别行数不同」没有专门分析文**（找过，没有）。
但反证链很硬：RapidOcrNet 全流程**没有任何随机源**（16 个 .cs 全 dump grep：`Random`/`DateTime.Now` 零命中，
`Guid.NewGuid` 4 处全在注释掉的 debug 存图里；DB 后处理**没有 NMS**），
ORT 的非确定性只有 1e-7 量级浮点差，**不足以让整行消失**。
→ 两次结果不同，几乎一定是**喂进去的字节不一样**（截图时机 / 选区亚像素 / DPI / 光标），
叠加 `/32` 取整台阶把 1px 的差放大成 32px 的缩放比变化。
**要复现必须先把同一张 PNG 落盘再跑**，否则测的是「截图抖动」不是模型。

---

## 1. DB 在多行密集 / 小字上的已知失效模式

**论文没有写「密集多行」这条。** 官方承认的唯一 limitation 是「text inside text」：

- 论文（AAAI 2019, *Real-time Scene Text Detection with Differentiable Binarization*）
  https://ar5iv.labs.arxiv.org/html/1911.08947 ｜ 原 PDF https://ojs.aaai.org/index.php/AAAI/article/download/6812/6666
  > "One limitation of our method is that it can not deal with cases 'text inside text' ... This is a common limitation for segmentation-based scene text detectors."
- **但论文明确承认分辨率决定小字检出率**（最接近「行高小 → 检出率降」的一手数据）：
  > "The ICDAR 2015 dataset is a multi-oriented text dataset that contains lots of **small and low-resolution text instances**. In Tab. 5, we can see that 'DB-ResNet-50 (1152)' achieves the state-of-the-art performance on accuracy."
  Table 5：`DB-ResNet-50 (736)` P 88.2 / R 82.7 / F 85.4 → `(1152)` P 91.8 / R 83.2 / F 87.3。
  **同模型同测试集，输入高度 736→1152，F +1.9、召回 +0.5。**

**官方 issue 承认「分辨率一变结果就变，甚至全空」**（维护者原话）：

- PaddleOCR #11781「某些分辨率下图片检测结果为空」 https://github.com/PaddlePaddle/PaddleOCR/issues/11781
  报告者：1617×936 原图有**小字检不出**；放大到 1920×1111 后**一条框都没有**；1900×1099、2000×1157 又正常。
  维护者 cuicheng01：
  > 「这块在不同的分辨率下的结果确实是有可能不一样甚至出现空的情况，优化的方式有很多，建议在训练的时候见足够多尺寸的样本」

**最贴近你症状的一条（表格密集行，行高 ~15px）**：PaddleOCR #18244
https://github.com/PaddlePaddle/PaddleOCR/issues/18244
> "a large fraction of the table rows are dropped entirely from the output — **not misread, just missing**"
> "PP-OCRv5 mobile scores ~22-57% (**varies by run**) on the same image/ground truth pair."

维护者 wq-vibe 复现后（这条最值钱）：
> "**DB post-processing with unclip_ratio expansion can cause adjacent cells to merge or small boxes to be truncated near borders.**"
> "row height is only ~15px, and crops resized to 32px have limited quality"
> 建议："Consider 2x upscaling before OCR. Try using PP-OCRv6 medium det model which is **more robust on small text**."

注：该 issue 维护者复现时 38 行**全都被检出**，但「该行 12~14 个框只出 9~10 个」「相邻列粘成一个框」。
→ **检出 ≠ 拆对**。你看到「行数变少」，很大一部分就是这一层。

**同族证据两条**：
- PaddleOCR discussion #15071「本地v4模型漏检严重」（bot 结论被标为 answer）
  https://github.com/PaddlePaddle/PaddleOCR/discussions/15071
  > 「如果图片较大或文字很小，建议调高到1280或更大，保证输入图不被压得太小导致细节丢失」
- RapidOCR discussion #648「Small Font Text Not Recognized」
  https://github.com/RapidAI/RapidOCR/discussions/648
  > "certain text lines with smaller/lighter fonts are not being detected and recognized. This causes important information to be **completely missing** from the extracted text."

---

## 2. DB 后处理五个参数的语义与默认值

**源码** https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/postprocess/db_postprocess.py

```python
def __init__(self, thresh=0.3, box_thresh=0.7, max_candidates=1000,
             unclip_ratio=2.0, use_dilation=False, score_mode="fast",
             box_type="quad", **kwargs):
    ...
    self.min_size = 3          # ← 硬编码，不是构造参数
```
两处裁剪（`boxes_from_bitmap`）：
```python
points, sside = self.get_mini_boxes(contour)
if sside < self.min_size:            # ← 3px
    continue
...
box = self.unclip(points, self.unclip_ratio)
...
if sside < self.min_size + 2:        # ← unclip 之后再判一次，5px
    continue
```
`sside` = `min(bounding_box[1])`，即**短边**（对横排文字就是行高）。

**注意：类默认值 ≠ 推理默认值**，这是最容易踩的坑：

| 来源 | thresh | box_thresh | unclip | dilation | 备注 |
|---|---|---|---|---|---|
| `db_postprocess.py` 类默认 | 0.3 | **0.7** | **2.0** | False | 几乎没人直接用 |
| `tools/infer/utility.py` CLI | 0.3 | **0.6** | **1.5** | **False** | 2.x 命令行实际值 |
| `PP-OCRv5_mobile_det/inference.yml` | 0.3 | **0.6** | **1.5** | — | 官方导出模型自带 |
| `configs/det/PP-OCRv5/PP-OCRv5_mobile_det.yml` | 0.3 | 0.6 | 1.5 | — | 训练侧 |
| rapidocr（Python 参考）`config.yaml` | 0.3 | **0.5** | **1.6** | **true** | 与上面全部不同 |
| PaddleX `text_detection/processors.py` | 0.3 | **0.7** | **2.0** | False | `min_size` 同为 3 |
| **RapidOcrNet（本仓）** `Default` | 0.3 | **0.5** | **1.6** | **无条件膨胀** | `Padding=50, ImgResize=1024` |

官方对每个参数的定义（中英同源）：
https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/module_usage/text_detection.md
> `thresh`：「像素得分阈值。输出概率图中得分大于该阈值的像素点被认为是文本像素。」
> `box_thresh`：「检测结果边框内，所有像素点的平均得分大于该阈值时，该结果会被认为是文字区域。」
> `unclip_ratio`：「Vatti clipping算法的扩张系数，使用该方法对文字区域进行扩张。」
> `limit_side_len`：「`"min"` 表示保证图像最短边不小于 det_limit_side_len，`"max"` 表示保证图像最长边不大于 limit_side_len」

英文版 https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/module_usage/text_detection.en.md
> "box_thresh: If the **average score of all pixels inside the bounding box** is greater than this threshold, the result is considered a text region."

RapidOCR 参数文档（唯一把 use_dilation 语义写清的地方）
https://rapidai.github.io/RapidOCRDocs/main/install_usage/rapidocr/parameters/
> 「box_thresh (float): 文本检测所得框是否保留的阈值，**值越大，召回率越低**。默认值为 0.5。」
> 「unclip_ratio (float): 控制文本检测框的大小，值越大，检测框整体越大。取值范围：[1.6, 2.0]，默认值为 1.6。」
> 「use_dilation (bool): 是否使用膨胀。**默认为 true**。该参数用于将检测到的文本区域做形态学的膨胀处理。」

**`min_size` 导致小字丢行——有没有人报告过？**
**没有。** 搜 `repo:PaddlePaddle/PaddleOCR "min_size" in:body` 命中仅 2 条，都不是这个事。
唯一相关：维护者在 #18244 把 `min_size = 3` 列进「标准参数核对表」——官方当它是固定值，不是旋钮。
→ **诚实结论：屏幕正文（12~18px 行高）这个尺寸段上，`min_size` 不是首要凶手，但它是最后那道硬边。** 见 §3。

---


---

> 后半（缩放算术 / use_dilation / 开源方案 / 重复识别不稳定 / 找不到清单 / 落点表）见 `OCR-DB-丢行-外部检索-2.md`。
