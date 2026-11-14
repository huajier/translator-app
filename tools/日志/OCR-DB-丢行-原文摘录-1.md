# 英文原句摘录 · OCR / DB 丢行检索（2026-11-14）

> 配套 `OCR-DB-丢行-外部检索.md`。这里只放**逐字原文 + 出处**，不做解读。
> 抓取方式：Chrome CDP（`grab.mjs` / `gh-fetch.mjs`），页面 innerText 直取。

---
## 1. DBNet 论文 · AAAI 2019

来源：https://ar5iv.labs.arxiv.org/html/1911.08947 （PDF: https://ojs.aaai.org/index.php/AAAI/article/download/6812/6666）

**1.1 唯一承认的 limitation**
> "One limitation of our method is that it can not deal with cases 'text inside text', which means that a text instance is inside another text instance. Although the shrunk text regions are helpful to the cases that the text instance is not in the center region of another text instance, it fails when the text instance exactly locates in the center region of another text instance. This is a common limitation for segmentation-based scene text detectors."

**1.2 分辨率决定小字检出率（Table 5 正文）**
> "Multi-oriented text detection The ICDAR 2015 dataset is a multi-oriented text dataset that contains lots of small and low-resolution text instances. In Tab. 5, we can see that 'DB-ResNet-50 (1152)' achieves the state-of-the-art performance on accuracy."

Table 5 原值（P / R / F / FPS）：

| Method | P | R | F | FPS |
|---|---|---|---|---|
| DB-ResNet-18 (736) | 86.8 | 78.4 | 82.3 | 48 |
| DB-ResNet-50 (736) | 88.2 | 82.7 | 85.4 | 26 |
| DB-ResNet-50 (1152) | 91.8 | 83.2 | 87.3 | 12 |

> "The values in the bracket mean the height of the input images."

**1.3 shrink 公式（正样本为什么薄）**
> "The offset 𝐷 of shrinking is computed from the perimeter 𝐿 and area 𝐴 of the original polygon: 𝐷 = 𝐴(1 − 𝑟²)/𝐿 (6) where 𝑟 is the shrink ratio, **set to 0.4 empirically**."

**1.4 推理期 box 形成三步 + 经验值**
> "The box formation process consists of three steps: (1) the probability map/the approximate binary map is firstly binarized with a constant threshold (0.2), to get the binary map; (2) the connected regions (shrunk text regions) are obtained from the binary map; (3) the shrunk regions are dilated with an offset 𝐷′ the Vatti clipping algorithm. 𝐷′ is calculated as 𝐷′ = 𝐴′ × 𝑟′/𝐿′ (10) where 𝐴′ is the area of the shrunk polygon; 𝐿′ is the perimeter of the shrunk polygon; **𝑟′ is set to 1.5 empirically**."

---

## 2. PaddleOCR 官方源码

**2.1 DBPostProcess 默认值与 min_size（硬编码）**
https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/postprocess/db_postprocess.py
> ```python
> def __init__(self, thresh=0.3, box_thresh=0.7, max_candidates=1000,
>              unclip_ratio=2.0, use_dilation=False, score_mode="fast",
>              box_type="quad", **kwargs):
>     ...
>     self.min_size = 3
>     self.dilation_kernel = None if not use_dilation else np.array([[1, 1], [1, 1]])
> ```

两处裁剪：
> ```python
> points, sside = self.get_mini_boxes(contour)
> if sside < self.min_size:
>     continue
> ...
> if self.box_thresh > score:
>     continue
> box = self.unclip(points, self.unclip_ratio)
> ...
> box, sside = self.get_mini_boxes(box)
> if sside < self.min_size + 2:
>     continue
> ```

`get_mini_boxes` 返回值：`return box, min(bounding_box[1])` → sside = **短边**。

**2.2 CLI 默认值**
https://github.com/PaddlePaddle/PaddleOCR/blob/main/tools/infer/utility.py
> ```python
> parser.add_argument("--det_limit_side_len", type=float, default=960)
> parser.add_argument("--det_limit_type", type=str, default="max")
> # DB params
> parser.add_argument("--det_db_thresh", type=float, default=0.3)
> parser.add_argument("--det_db_box_thresh", type=float, default=0.6)
> parser.add_argument("--det_db_unclip_ratio", type=float, default=1.5)
> parser.add_argument("--max_batch_size", type=int, default=10)
> parser.add_argument("--use_dilation", type=str2bool, default=False)
> parser.add_argument("--det_db_score_mode", type=str, default="fast")
> ```

**2.3 官方导出的 v5 模型自带配置**
https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det/blob/main/inference.yml
> ```yaml
> PostProcess:
>   name: DBPostProcess
>   thresh: 0.3
>   box_thresh: 0.6
>   max_candidates: 1000
>   unclip_ratio: 1.5
> ```

**2.4 训练侧 shrink 参数（PP-OCRv5_mobile_det）**
https://github.com/PaddlePaddle/PaddleOCR/blob/main/configs/det/PP-OCRv5/PP-OCRv5_mobile_det.yml
> ```yaml
>     - MakeShrinkMap:
>         shrink_ratio: 0.4
>         min_text_size: 8
> ```

**2.5 MakeShrinkMap 源码：小于 min_text_size 的标注直接标成 ignore**
https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/data/imaug/make_shrink_map.py
> ```python
> height = max(polygon[:, 1]) - min(polygon[:, 1])
> width = max(polygon[:, 0]) - min(polygon[:, 0])
> if ignore_tags[i] or min(height, width) < self.min_text_size:
>     cv2.fillPoly(mask, polygon.astype(np.int32)[np.newaxis, :, :], 0)
>     ignore_tags[i] = True
> ```
> 以及「缩成多块就加大 ratio 重来」的兜底：
> ```python
> possible_ratios = np.arange(shrink_ratio, 1, shrink_ratio)
> for ratio in possible_ratios:
>     distance = polygon_shape.area * (1 - np.power(ratio, 2)) / polygon_shape.length
>     shrunk = padding.Execute(-distance)
>     if len(shrunk) == 1:
>         break
> ```

**2.6 官方 slice 算子**
https://github.com/PaddlePaddle/PaddleOCR/blob/main/tools/infer/predict_system.py
> ```python
> if slice:
>     slice_gen = slice_generator(img,
>         horizontal_stride=slice["horizontal_stride"],
>         vertical_stride=slice["vertical_stride"])
>     for slice_crop, v_start, h_start in slice_gen:
>         dt_boxes, elapse = self.text_detector(slice_crop, use_slice=True)
>         dt_boxes[:, :, 0] += h_start
>         dt_boxes[:, :, 1] += v_start
>     dt_boxes = merge_fragmented(boxes=dt_boxes,
>         x_threshold=slice["merge_x_thres"], y_threshold=slice["merge_y_thres"])
> ```

文档 https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version2.x/ppocr/blog/slice.en.md
> "If you have a very large image/document that you would like to run PaddleOCR (detection and recognition) on, you can use the slice operation as follows"
> "The way the `slice` operator works is that it runs a sliding window across the large input image, creating slices of it and runs the OCR algorithms on it."
> "All slice-level detections with bounding boxes as close as `merge_x_thres` and `merge_y_thres` will be merged together."
> 推荐（6616×14886）：`slice = {'horizontal_stride': 300, 'vertical_stride':500, 'merge_x_thres':50, 'merge_y_thres': 35}`

**2.7 官方文档：三个参数定义**
https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/module_usage/text_detection.en.md
> "thresh: Pixel score threshold. Pixels in the output probability map with scores greater than this threshold are considered text pixels."
> "box_thresh: If the average score of all pixels inside the bounding box is greater than this threshold, the result is considered a text region."
> "limit_type: `\"min\"` ensures the shortest side of the image is no less than `det_limit_side_len`; `\"max\"` ensures the longest side is no greater than `limit_side_len`."

**2.8 官方 FAQ：漏检怎么调（原文）**
https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/module_usage/text_detection.md
> 「#### Q: 检测框不准确或漏检怎么办？
> **A**: 可以通过以下方式优化：
> （1）调整检测参数：
> ```python
> model = TextDetection(
>     thresh=0.3,  # 降低像素阈值
>     box_thresh=0.5,  # 降低检测框阈值
>     unclip_ratio=2.0,  # 增大扩张系数
>     limit_side_len=1216  # 增大图像尺寸
> )
> ```
> （2）使用更精确的后处理模式：设置 `det_db_score_mode="slow"`
> （3）启用膨胀处理：设置 `use_dilation=True`」

> 「#### Q: 不同场景的参数配置建议？
> - **高精度配置**：`limit_side_len=1216`, `thresh=0.3`, `box_thresh=0.5`, `unclip_ratio=1.5`
> - **高速度配置**：`limit_side_len=640`, `thresh=0.5`, `box_thresh=0.7`, `unclip_ratio=1.2`
> - **平衡配置**：`limit_side_len=960`, `thresh=0.4`, `box_thresh=0.6`, `unclip_ratio=1.5`」

> 「如果输入图形分辨率不大，建议使用 `limit_type=min` 和 `limit_side_len=960` 节省计算资源的同时能获得最佳检测效果。」

---

## 3. PaddleOCR issue / discussion 原文

**3.1 #11781「某些分辨率下图片检测结果为空」**
https://github.com/PaddlePaddle/PaddleOCR/issues/11781
> 报告者 imoko233：「原图像分辨率是1617 * 936，因为图片里比较小的字会检测不出来，于是尝试在检测前对图片进行放大处理，例如放大到1920 * 1111，随后进行检测就没有任何结果，试了下其他分辨率如1900 * 1099、2000 * 1157都有结果。」
> 维护者 cuicheng01：「这块在不同的分辨率下的结果确实是有可能不一样甚至出现空的情况，优化的方式有很多，建议在训练的时候见足够多尺寸的样本」

**3.2 #18244「PP-OCRv5 mobile det+rec silently drops most text in table rows」**
https://github.com/PaddlePaddle/PaddleOCR/issues/18244

报告者 ns-nawasthi：
> "a large fraction of the table rows are dropped entirely from the output — **not misread, just missing** — while a handful of stray numeric fragments (row numbers) appear in their place."
> "The same image processed by a commercial OCR engine (ABBYY FineReader Engine) extracts the table with ~97% token-sort accuracy ... PP-OCRv5 mobile scores ~22-57% (**varies by run**) on the same image/ground truth pair."
> "That pattern points at the border/grid lines themselves confusing the text detector (DBNet) rather than anything specific to this one image."

维护者 wq-vibe 复现：
> "**Detection stage: all 38 rows plus the header row are detected.** Using standard DB post-processing parameters (thresh=0.3, box_thresh=0.5, unclip_ratio=1.5), we get 449 text boxes covering all rows. The detection probability map shows strong response (>0.6) across every row, and correctly suppresses border lines to 0."
> "However, some of the issues you mentioned do exist: Adjacent cells merged ... Some cell boxes missing or incomplete: some rows only get 9-10 boxes when the full row should have 12-14 ... **row height is only ~15px, and crops resized to 32px have limited quality**"
> "Detection stage: The model shows strong probability response across all rows ... However, **DB post-processing with unclip_ratio expansion can cause adjacent cells to merge or small boxes to be truncated near borders.**"
> "'Entire rows dropped' issue: We could not reproduce full row loss using standard post-processing parameters. Since you're using a custom C++/Go ONNXRuntime pipeline, the discrepancy likely comes from differences in post-processing implementation (**e.g., box_thresh, min_size, unclip_ratio settings, or box merging/filtering logic**)."
> "Verify your post-processing parameters against the standard values: det_db_thresh = 0.3 / det_db_box_thresh = 0.5 / det_db_unclip_ratio = 1.5 / **min_size = 3** / max_side_len = 960"
> "The image resolution is quite low (row height only 15px). **Consider 2x upscaling before OCR.** Try using PP-OCRv6 medium det model which is **more robust on small text**."

**3.3 discussion #15071「本地v4模型漏检严重」**
https://github.com/PaddlePaddle/PaddleOCR/discussions/15071
> 「漏检多数与其设置不合适有关」
> 「det_db_box_thresh（默认0.6）：同理，调低可减少漏检，试试0.4到0.5。」
> 「det_limit_side_len：如果图片较大或文字很小，建议调高到1280或更大，保证输入图不被压得太小导致细节丢失。」
> 「det_db_unclip_ratio：增大（如2.0~2.5）可让检测框更"大方"，对小字、间距小的行检出有帮助。」
> 注：该回答由 bot 生成（页面标注 "Response generated by 🤖 feifei-bot"），但被提问者标为 answer。

**3.4 discussion #13164「Miss detections on Small Size text with same font」**
https://github.com/PaddlePaddle/PaddleOCR/discussions/13164
> "The problem is some text is not been detected, however **same font text in the same page is also detected at a smaller distance**."
> 官方回复："During inference, try to change det_limit_side_len, det_db_box_thresh, det_db_score_mode and other parameters"
> 报告者用 DB++ 检测器。

**3.5 #8117「检测框交叉重叠」（DB 缺陷自认）**
https://github.com/PaddlePaddle/PaddleOCR/issues/8117
> 报告者：「我试了调小 det_db_unclip_ratio，但是调小了就有把7识别成/的概率，并且还是会有交叉」
> 贡献者 thsno02：「根据作者的说法，是 **DB 的缺陷**，只能通过后处理解解决。」
> 维护者 LDOUBLEV：「如果不想重新训练模型，通过后处理解决吧」

---


---

> 后半（RapidOCR / RapidOcrNet / ONNX Runtime 确定性 / 切片族 / 找不到清单）见 `OCR-DB-丢行-原文摘录-2.md`。
