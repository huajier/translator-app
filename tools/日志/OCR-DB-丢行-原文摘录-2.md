# 英文原句摘录 第2部分 · RapidOCR / ORT / 切片族（2026-11-14）

> 本文件是第 2 部分（RapidOCR / RapidOcrNet / ONNX Runtime / 切片族 / 找不到清单）。
> 第 1 部分（DBNet 论文 / PaddleOCR 官方源码 / PaddleOCR issue）见 `OCR-DB-丢行-原文摘录-1.md`。

---

## 4. RapidOCR / RapidOcrNet

**4.1 rapidocr Python 参考实现默认值（注意 use_dilation=true）**
https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/config.yaml
> ```yaml
> Det:
>     limit_side_len: 736
>     limit_type: min
>     std: [ 0.5, 0.5, 0.5 ]
>     mean: [ 0.5, 0.5, 0.5 ]
>     thresh: 0.3
>     box_thresh: 0.5
>     max_candidates: 1000
>     unclip_ratio: 1.6
>     use_dilation: true
>     score_mode: fast
> ```
> 以及顶层预处理开关：
> ```yaml
>     use_preprocess_img: true
>     min_side_len: 30
>     max_side_len: 2000
>     use_vertical_padding: true
>     min_height: 30
>     width_height_ratio: 8
> ```

**4.2 rapidocr python 的 det 后处理（min_size 同样是 3，另有 filter_det_res）**
https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/ch_ppocr_det/utils.py
> ```python
> self.min_size = 3
> ...
> if sside < self.min_size:
>     continue
> ...
> box, sside = self.get_mini_boxes(box)
> if sside < self.min_size + 2:
>     continue
> ```
> ```python
> def filter_det_res(self, dt_boxes, scores, img_height, img_width):
>     ...
>     rect_width = int(np.linalg.norm(box[0] - box[1]))
>     rect_height = int(np.linalg.norm(box[0] - box[3]))
>     if rect_width <= 3 or rect_height <= 3:
>         continue
> ```

**4.3 RapidOCR 官方参数文档**
https://rapidai.github.io/RapidOCRDocs/main/install_usage/rapidocr/parameters/
> 「thresh (float): 图像中文字部分和背景部分分割阈值。**值越大，文字部分会越小**。取值范围：[0, 1]，默认值为 0.3。」
> 「box_thresh (float): 文本检测所得框是否保留的阈值，**值越大，召回率越低**。取值范围：[0, 1]，默认值为 0.5。」
> 「unclip_ratio (float): 控制文本检测框的大小，值越大，检测框整体越大。取值范围：[1.6, 2.0]，默认值为 1.6。」
> 「use_dilation (bool): 是否使用膨胀。**默认为 true**。该参数用于将检测到的文本区域做形态学的膨胀处理。」
> 「limit_side_len (float): 限制图像边的长度的像素值。默认值为 736。」
> 「limit_type (str): 限制图像的最小边长度还是最大边为 limit_side_len ... 当 limit_type=min 和 limit_side_len=736 时，图像最小边小于 736 时，会将图像最小边拉伸到 736」

**4.4 RapidOCR discussion #648「English Text Detection Issue: Small Font Text Not Recognized」**
https://github.com/RapidAI/RapidOCR/discussions/648
> "I'm using RapidOCR (via Docling) to process English PDF documents, but certain text lines with smaller/lighter fonts are not being detected and recognized. This causes important information to be **completely missing** from the extracted text."
> "Is there a PP-OCRv5 English detection model? ... But no en_PP-OCRv5_det for detection"
> "I tested it using the online demo and confirmed that I had missed this line."
> "I tried using these in my code, replacing only the detection and recognition models with the ones I found above, but **I still got the missing-line issue** described earlier."

**4.5 RapidOcrNet `RapidOcrOptions.Default`（本仓在用）**
https://github.com/BobLd/RapidOcrNet/blob/4.2.0/RapidOcrNet/RapidOcrOptions.cs
> ```csharp
> public static readonly RapidOcrOptions Default = new RapidOcrOptions()
> {
>     Padding = 50,
>     ImgResize = 1024,             // > 0 = legacy max-side cap (best fit for bundled v5 model)
>     LimitSideLen = 736,           // used when ImgResize == 0 (Python-style adaptive)
>     MaxSideLen = 2000,
>     MinSideLen = 30,
>     WidthHeightRatio = -1f,
>     MinHeight = 30,
>     TextScore = 0.5f,
>     ClsThresh = 0.9f,
>     BoxScoreThresh = 0.5f,
>     BoxThresh = 0.3f,
>     UnClipRatio = 1.6f,
>     DoAngle = true,
>     ...
> };
> ```
> ```csharp
> public static readonly RapidOcrOptions PythonCompat = new RapidOcrOptions()
> {
>     Padding = 0,
>     ImgResize = 0,                  // 0 = Python-style adaptive short-side resize
>     LimitSideLen = 736,             // Det.limit_side_len
>     ...
> };
> ```

**4.6 RapidOcrNet 缩放的两个实现（legacy 与 adaptive）**
https://github.com/BobLd/RapidOcrNet/blob/master/RapidOcrNet/ScaleParam.cs
> ```csharp
> /// Legacy: treats dstSize as a max-side cap, then rounds down to /32.
> /// (Python rounds to nearest /32, but empirically the bundled PP-OCRv5
> /// detector regresses on several inputs when fed nearest-rounded sizes,
> /// sticking with the legacy round-down.)
> if (dstWidth % 32 != 0) {
>     dstWidth = (dstWidth / 32 - 1) * 32;
>     dstWidth = Math.Max(dstWidth, 32);
> }
> ```
> ```csharp
> public static ScaleParam GetAdaptiveScaleParam(SKBitmap src, int limitSideLen = 736)
> {
>     ...
>     int minWh = Math.Min(srcWidth, srcHeight);
>     if (limitSideLen > 0 && minWh < limitSideLen) { ratio = limitSideLen / (float)minWh; }
>     ...
> }
> ```

**4.7 RapidOcrNet 检测后处理的硬门槛与无条件膨胀**
https://github.com/BobLd/RapidOcrNet/blob/master/RapidOcrNet/TextDetector.cs
> ```csharp
> private const float DilateRadius = 1f;
> ...
> const float maxSideThresh = 3.0f; // Long Edge Threshold
> ...
> SKPoint[] minBox = GetMiniBox(contour, out float maxSide);
> if (float.IsNaN(maxSide) || maxSide < maxSideThresh) { continue; }
> ...
> SKPoint[]? clipBox = Unclip(minBox, unClipRatio);
> ...
> ReadOnlySpan<SKPoint> clipMinBox = GetMiniBox(clipBox, out maxSide);
> if (float.IsNaN(maxSide) || maxSide < maxSideThresh + 2) { continue; }
> ```
> ```csharp
> using (var canvas = new SKCanvas(thresholdMatBitmap))
> {
>     canvas.DrawBitmap(thresholdMatBitmap, 0, 0, _dilatePaint);
>     // TODO - Check dilate by rendering thresholdMatBitmap to file
>     contours = FindContours(thresholdMat, rows, cols);
> }
> ```
> 归一化口径（v5 用 ImageNet，不是 0.5/0.5）：
> ```csharp
> // NOTE: the PP-OCRv5 detector ONNX bundled with this repo was empirically trained with
> // ImageNet normalization, NOT the (0.5,0.5,0.5)/(0.5,0.5,0.5) constants the Python
> // rapidocr config ships (its config.yaml targets PP-OCRv4). Reverting to PP-OCR's
> // canonical 0.5/0.5 here causes the detector to miss most boxes.
> ```

**4.8 RapidOcrNet 识别端的整块过滤（TextScore 按平均分丢整块）**
https://github.com/BobLd/RapidOcrNet/blob/master/RapidOcrNet/RapidOcr.cs
> ```csharp
> // PP-OCR-style filtering: drop blocks with empty recognized text or
> // average char score below `textScore`.
> if (textScore > 0 && block.CharScores is { Length: > 0 })
> {
>     float sum = 0;
>     for (int s = 0; s < block.CharScores.Length; s++) { sum += block.CharScores[s]; }
>     float avg = sum / block.CharScores.Length;
>     if (avg < textScore) { continue; }
> }
> ```

**4.9 PaddleOCR-json #151：同一图像缺两行的实测**
https://github.com/hiroi-sora/PaddleOCR-json/issues/151
> 「下面这张图，总是识别不到这两行文本」
> 「但我用Umi-OCR是能正常识别到的 ... 图片清晰度我认为没问题，并且截图再识别又能识别得到那行字」
> 「后续测试我把限制长度提升到1920发现能正常识别到了，但Umi-OCR设置960是可以识别的，我的就不行？」
> 「进一步测试发现是启用的 `use_angle_cls` 参数导致的，如果不开启，960下能识别到，开启之后需要提高边长才能识别」

---

## 5. ONNX Runtime 确定性

**5.1 #9704「onnxruntime infer, the same input occasionally has different output」**
https://github.com/microsoft/onnxruntime/issues/9704
> hariharans29 (MEMBER): "ORT's results are not guaranteed to be deterministic and what really matters is if the results are significantly off. Looking at the results you had shown they don't look significantly off"
> 用户档位：`364.738 370.296 452.347 452.397 0.930632` / `364.732 ... 0.930677` / `364.737 ... 0.930689` / `364.74 ... 0.930678`
> jcdatin: "on my faster-rcnnn network inference, I fixed my non determinism by replacing `GraphOptimizationLevel::ORT_ENABLE_ALL` with `GraphOptimizationLevel::ORT_DISABLE_ALL` (variability was very small though with `ORT_ENABLE_ALL` : 10e-7 for confidence score and 10e-4 for box coordinates. Got a inference exeution time regression of <5%"

**5.2 #4611「Inference on GPU is not deterministic」**
https://github.com/microsoft/onnxruntime/issues/4611
> "Passing the same input twice to the same model also produces identical results. Note that you might have to run the test code a few times to reproduce the problem. For me the two results are different roughly every second time I run it."

**5.3 #8970（随机来自 `RandomUniformLike`，与 OCR 无关，作为「随机源可归因」的例子）**
https://github.com/microsoft/onnxruntime/issues/8970
> "RandomUniformLike picks a different a for every Run. As you can imagine, the presence of a random generation function RandomUniformLike is not going to make results deterministic."

**5.4 ORT 官方 troubleshooting（延迟抖动，不是精度）**
https://onnxruntime.ai/docs/performance/tune-performance/troubleshooting.html
> "On some platforms, onnxruntime may exhibit high latency variance during inferencing. This is caused by the constant cost model that onnxruntime uses to parallelize tasks in the thread pool."

---

## 6. PaddleOCR issue：#5201 同图两次结果不一致
https://github.com/PaddlePaddle/PaddleOCR/issues/5201
> 「移动端部署轻量化模型，首次推理使用图片A得到识别结果A，换另一张图片B推理得到结果之后，再换回图片A再次推理，得到的识别结果B和首次推理的结果A在**每个文本框的坐标上都有像素级的差异**，对比过传入模型的输入，确认几次输入的图片A没有任何差异，并且此后每一次使用图片A做输入推理始终得到结果B，通过实际比对，结果B的准确率比结果A略低。」
> ljdang：「实际上应该是只有第一张图识别正确，后面选择的图大概率会不准确，很容易复现。」（指向同族 #4420）
> 维护者 tink2123：「可以debug看下是否是B图的shape大于A，导致再次预测A时对图片进行了resize。」

---

## 7. 切片族 / 其他

**7.1 SAHI**
https://github.com/obss/sahi ｜ 论文 https://arxiv.org/html/2202.06934
> "SAHI helps developers overcome real-world challenges in object detection by enabling **sliced inference** for detecting small objects in large images."

**7.2 Umi-OCR**
https://github.com/hiroi-sora/Umi-OCR
> 「如果要识别像素超大的长图或大图，请调整：**页面的设置→文字识别→限制图像边长→【调高数值】**。」

**7.3 dev.to 实测（放大对小字是否有效，因引擎而异）**
https://dev.to/shu_jing_915fa287b22539ad/upscaling-small-text-before-ocr-fixed-tesseract-and-hurt-another-engine-26ho
> "If your tool accepts user screenshots, sooner or later a tiny one arrives: a thumbnail cropped out of a chat app, text maybe 8 px tall, and the OCR output is mush. The reflex fix is one line before recognition, resize 4x and move on."
> "So the answer depends on the engine and on the height you actually have."
> "With a height in hand, a reasonable rule is to leave 12 px and above alone, and below that upscale toward about 30 px with bilinear, only for engines you've confirmed benefit."
> 反例（与「放大总是更好」相反）："pasting the 8 px Hei paragraph unscaled into a blank 1200×1200 canvas took macOS recognition from 88.0% to 0%, possibly because it **shrinks the whole image internally**"

---

## 8. 明确找不到（不在上面任何一条里）

- 「DB 对行间距小 / 行高小的文本检测率下降」的论文或官方文档段落 —— **不存在**。
- 「`min_size=3` 导致小字丢行」的任何报告 —— **不存在**（`repo:PaddlePaddle/PaddleOCR "min_size" in:body` 仅 2 条命中，均无关）。
- 「同一张图重复识别行数不同」的专门分析文章 —— **不存在**，只有 §5/§6 的零散 issue。
- 「`use_dilation=True` 救回整行」的实测案例 —— **不存在**。
