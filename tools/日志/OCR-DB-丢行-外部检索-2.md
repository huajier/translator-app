# OCR 丢行外部检索（后半）· 缩放算术与落点（2026-11-14）

> 本文件是后半：§3 起的缩放算术与落点表。§0~§2（结论、DB 失效模式、参数语义与默认值）见 `OCR-DB-丢行-外部检索.md`。

---

## 3. 图像缩放：800×300 的截图到底被缩成多大

**本报告最硬的部分，全部可从源码逐行复刻。** 复刻脚本 `tools/临时/det-scale-check.mjs`。

### 3.1 本仓实际走的是哪条路

`src/Core/Capture/RapidOcrEngine.cs:291` → `engine!.Detect(skBitmap, RapidOcrOptions.Default)`。
`RapidOcrOptions.Default`（NuGet 4.2.0，与本仓 `TranslatorApp.csproj:144` 的包版本一致）
https://github.com/BobLd/RapidOcrNet/blob/4.2.0/RapidOcrNet/RapidOcrOptions.cs
```csharp
Padding = 50,
ImgResize = 1024,   // > 0 = legacy max-side cap (best fit for bundled v5 model)
LimitSideLen = 736, // used when ImgResize == 0 (Python-style adaptive)
BoxThresh = 0.3f, BoxScoreThresh = 0.5f, UnClipRatio = 1.6f, TextScore = 0.5f
```
`ImgResize > 0` → 走 **legacy（长边封顶）**，`LimitSideLen = 736` **完全不生效**。
本仓历史日志其实已经踩到过：
`tools/日志/阶段3-S4D-LD类-诊断.md:43`
> 「**不能靠调 `RapidOcrOptions.LimitSideLen` 实现**：实测 `limit1216` 单独 = 85/121 不变，因为它是「封顶」不是「放大」。」

### 3.2 legacy 缩放的算法（含一个多减一格的 bug）

https://github.com/BobLd/RapidOcrNet/blob/master/RapidOcrNet/ScaleParam.cs
```csharp
if (dstWidth % 32 != 0) {
    dstWidth = (dstWidth / 32 - 1) * 32;   // ← 向下取整，还多减一格
    dstWidth = Math.Max(dstWidth, 32);
}
```
`PrepareDetectorInput` 的 dstSize = `min(1024, 原图长边) + 2*Padding`。

### 3.3 算出来的数（实跑输出）

```
=== 1. 800×300 截图在 det 之前被缩放成多大（本仓走 RapidOcrOptions.Default）
  Default     : 补边 900×400 → det 864×352  (x×0.960 y×0.880)
  PythonCompat: 不补边     → det 1952×736  (x×2.440 y×2.453)

=== 2. 关键一步：det 空间的行高，再被 DB 的 shrink 砍掉 84%
  屏幕行高 12px → det 10.6px → 概率图正样本 1.8px 高
  屏幕行高 14px → det 12.3px → 概率图正样本 2.2px 高
  屏幕行高 18px → det 15.8px → 概率图正样本 2.8px 高
  存活门槛：det 空间行高需 ≥ 18.7px（= min_size / r² = 3 / 0.16）
  换算回屏幕：原始行高需 ≥ 21.3px 才稳定留下

=== 3. /32 取整是台阶：选区宽差 1px，det 输入差 32px
  选区宽 826 → det 864×352
  选区宽 828 → det 896×352      ← 1px 让缩放比整体变化

=== 4. 换成 PythonCompat（等于先放大 2.4 倍）之后同一个行高还剩多少
  14px → det 34.3px → 正样本 6.1px 高   ← 过 3 且过 5，稳
```

**逐条读法**：

1. **你问的「缩放后行高只剩几个像素、会不会低于 min_size」——方向对，机制要修正。**
   本仓这条路径**不是放大，是缩小**：800×300 补 50px 白边成 900×400，再缩到 864×352
   （横 ×0.960、纵 ×0.880 —— **横纵不等，字被纵向压扁**）。12~18px 的行 → det 空间 10.6~15.8px。
2. **真正的「行高损失」是 DB 训练的 shrink，不是 resize。**
   12~18px → 正样本 **1.8~2.8px**，正压在 `min_size=3` 上。
   理论上还有两道救援：RapidOcrNet 在二值化后做 radius=1 膨胀（每边 +1px → 厚度 +2px），再加 unclip。
   **但这两道都不可靠：**
   - **膨胀那一步的生效方式可疑。** `TextDetector.cs` 是「把 `thresholdMatBitmap` 用 dilate 滤镜画回它自己」，
     而 `contours` 用的是 `using` 之前就取好的 `Span<byte> thresholdMat = thresholdMatBitmap.GetPixelSpan();`。
     实际有没有原地写回取决于 SKCanvas 实现；作者自己在旁边留了
     `// TODO - Check dilate by rendering thresholdMatBitmap to file` —— **他没验过**。
     → 余量是 **0~2px 不确定**，不是干净的 2px。
   - **unclip 对应的是 `min_size+2` 那道独立门槛**，加大 unclip 只能救「框太小」，
     救不了「概率图里根本没有这块正样本」。
   → `min_size` 在这条路径上是**「0~2px 余量的硬边」**，这就是同一段文字在两次输入之间翻面的原因。
3. **`/32` 取整是台阶函数**：选区宽 826→828 这 1px，det 宽从 864 跳到 896，**整个缩放比换了一套**。
   同一个人框两次，宽度差 1~2px 是常态 → **送进网络的像素尺寸都不一样**。
   这条直接解释「重复识别结果不同」——**不是模型随机，是输入被量化台阶改了**。

---

## 4. `use_dilation` 到底是什么、默认值是什么

**语义**：对二值化后的概率图做 3×3 形态学膨胀（`np.array([[1,1],[1,1]])`）。
**它确实是用来「把断裂的文本区域连起来」的**，也正是**让相邻行粘成一个框的元凶**。

| 实现 | 默认 | 证据 |
|---|---|---|
| PaddleOCR `db_postprocess.py` | `use_dilation=False` | `self.dilation_kernel = None if not use_dilation else np.array([[1,1],[1,1]])` |
| PaddleOCR CLI | `--use_dilation` `default=False` | https://github.com/PaddlePaddle/PaddleOCR/blob/main/tools/infer/utility.py |
| PaddleX | `use_dilation=False` | https://github.com/PaddlePaddle/PaddleX/blob/develop/paddlex/inference/models/text_detection/processors.py |
| **rapidocr（Python 参考）** | **`true`** | https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/config.yaml |
| **RapidOcrNet（本仓）** | **没有开关，永远开着** | `private const float DilateRadius = 1f;` + 无条件 `canvas.DrawBitmap(...)` |

→ **本仓和 PaddleOCR 官方 CLI 的默认行为不一致，差的就是这一层膨胀。**

**官方推荐靠它救漏检（原文）**：
https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/module_usage/text_detection.md
> 「#### Q: 检测框不准确或漏检怎么办？
> **A**: （1）调整检测参数：`thresh=0.3, box_thresh=0.5, unclip_ratio=2.0, limit_side_len=1216`
> （2）使用更精确的后处理模式：设置 `det_db_score_mode="slow"`
> （3）**启用膨胀处理：设置 `use_dilation=True`**」

> 官方三档配置：高精度 `limit_side_len=1216, thresh=0.3, box_thresh=0.5, unclip_ratio=1.5`；
> 平衡 `960 / 0.4 / 0.6 / 1.5`；高速度 `640 / 0.5 / 0.7 / 1.2`。

**有人靠打开 dilation 救回丢行吗？** 没有找到任何「打开 dilation 就救回整行」的案例。
反方向的证据倒是有：#18244 维护者把粘连归因于 unclip 扩张，而 dilation 只会让粘连更严重。
**本仓要做的恰恰相反：把行拆开（缩小 unclip / 关掉膨胀 / 先切行），不是打开它。**

---

## 5. 有没有开源项目专门解决「屏幕截图长文本识别丢行」

**没有专门针对这个场景的项目。** 找到的都是「切片后再检测」这一族，可借用但不是现成答案。

1. **PaddleOCR 官方 slice 算子**（唯一官方一级方案）
   - 代码 https://github.com/PaddlePaddle/PaddleOCR/blob/main/tools/infer/utility.py
     （`slice_generator` + `merge_fragmented`，第 934 行起）
   - 调用 https://github.com/PaddlePaddle/PaddleOCR/blob/main/tools/infer/predict_system.py
     ```python
     for slice_crop, v_start, h_start in slice_gen:
         dt_boxes, elapse = self.text_detector(slice_crop, use_slice=True)
         dt_boxes[:, :, 0] += h_start; dt_boxes[:, :, 1] += v_start
     dt_boxes = merge_fragmented(boxes=dt_boxes, x_threshold=..., y_threshold=...)
     ```
   - 文档 https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version2.x/ppocr/blog/slice.en.md
     > "The way the `slice` operator works is that it runs a **sliding window** across the large input image, creating slices of it and runs the OCR algorithms on it."
     > 推荐值（6616×14886 图）：`{'horizontal_stride': 300, 'vertical_stride': 500, 'merge_x_thres': 50, 'merge_y_thres': 35}`
   - 中文 http://www.paddleocr.ai/v3.0.3/version2.x/ppocr/blog/slice.html

2. **SAHI** —— 非 OCR 专用，但「大图里的小目标靠切片救」是它的主打
   https://github.com/obss/sahi ｜ 论文 https://arxiv.org/html/2202.06934
   > "SAHI helps developers overcome real-world challenges in object detection by enabling **sliced inference** for detecting small objects in large images."
   思路可直接搬：**分块跑 + 重叠 + 再合并**。不需要这个依赖，`slice_generator` 三十行就能复刻。

3. **Umi-OCR** —— 不是算法方案，但官方建议就是「调大限制边长」
   https://github.com/hiroi-sora/Umi-OCR
   > 「如果要识别像素超大的长图或大图，请调整：**页面的设置→文字识别→限制图像边长→【调高数值】**」

4. **PaddleOCR-json #151**（同一图像缺行的实测案例，与你的症状同型）
   https://github.com/hiroi-sora/PaddleOCR-json/issues/151
   > 「下面这张图，总是识别不到这两行文本」
   > 「后续测试我把限制长度提升到1920发现能正常识别到了」
   > 「进一步测试发现是启用的 `use_angle_cls` 参数导致的，如果不开启，960下能识别到，开启之后需要提高边长才能识别」

> **诚实结论**：没有现成项目能「装上就好」。「先切行再识别」在你这个仓里已做过一半
> （`RapidOcrEngine.RecognizeWithRetry` 的 3x 重试），缺的是**切片而不是整体缩放**。

---

## 6. 「同一张图重复识别结果不稳定」是不是已知现象

### 6.1 已知，但**没人专门分析过**

- PaddleOCR #5201「同一张图片首次推理和后续推理结果不一致」
  https://github.com/PaddlePaddle/PaddleOCR/issues/5201
  > 「得到的识别结果B和首次推理的结果A在**每个文本框的坐标上都有像素级的差异**」
  > 「对比过传入模型的输入，确认几次输入的图片A没有任何差异」
- PaddleOCR #13140「GPU和CPU推理结果不一致」 https://github.com/PaddlePaddle/PaddleOCR/issues/13140
- PaddleOCR #10697 / #6367 / #4956：命令行 vs 代码、serving vs predict 一族。

### 6.2 ONNX Runtime 官方：**不保证确定性**

- https://github.com/microsoft/onnxruntime/issues/9704
  > hariharans29 (MEMBER): "**ORT's results are not guaranteed to be deterministic** and what really matters is if the results are significantly off."
  > 用户实测抖动幅度：`0.930632 / 0.930677 / 0.930689 / 0.930678`
  > 一位用户的修法："I fixed my non determinism by replacing `GraphOptimizationLevel::ORT_ENABLE_ALL` with `GraphOptimizationLevel::ORT_DISABLE_ALL`"（精度差 1e-7~1e-4，耗时 +<5%）
- https://github.com/microsoft/onnxruntime/issues/4611 「Inference on GPU is not deterministic」
  > "Passing the same input twice to the same model also produces identical results. **Note that you might have to run the test code a few times to reproduce the problem.**"

### 6.3 但这些都解释不了「整行消失」

`RapidOcrNet/*.cs` **16 个文件逐一 dump 后 grep**：
- `Random` / `NextDouble` / `DateTime.Now`：**零命中**
- `Guid.NewGuid` 4 处，**全在注释掉的 debug 存图代码里**，不参与运算
- **DB 后处理没有 NMS**（`FindContours` + 面积/分数过滤），你问的「NMS 随机性」这条**不存在**
- `RecMaxDegreeOfParallelism` 只影响**执行顺序**，不影响每块结果

→ **确定性侧没有随机源，唯一可能就是「输入字节不同」。** 本仓有具体嫌疑点：
1. **选区 1px 之差 → `/32` 台阶 → det 输入差 32px**（§3.3 第 3 条），缩放比整套换掉；
2. **截图本身**：`CopyFromScreen` 抓 DWM 合成后的画面，滚动条亚像素、光标闪烁、子像素抗锯齿都会变；
3. **DPI**：`DpiHelper.DipToPhysical` 用 `Math.Round`，125%/150% 下选区换算差 1px 是常态。

**根因排序：截图 → 选区量化 → 缩放台阶 → 阈值卡边；模型随机排最后，且量级不足。**

---

## 7. 找不到的部分（明说）

1. **没有任何论文 / 官方文档写「DB 对行间距小、行高小的文本检测率下降」。**
   DB 论文只承认「text inside text」。行高这条只有两处间接证据（Table 5 分辨率对比 + #11781 维护者口述）。
2. **没有任何人报告过 `min_size=3` 导致小字丢行。** 全网 + 仓内 issue 搜索 0 命中；
   反向证据是 #18244 维护者把它列为「标准值」。
3. **没有「同一张图重复识别行数不同」的专门分析文。** 只有零散 issue + ORT 的确定性声明。
4. **没有专门解决「屏幕截图长段落丢行」的开源项目。** 只有通用 slice/tiling。
5. **没有找到 `use_dilation` 救回整行的案例。** 官方 FAQ 推荐它，但无实测报告背书。

---

## 8. 给下一步的落点（互相独立可 A/B）

| # | 改什么 | 依据 | 预期 | 风险 |
|---|---|---|---|---|
| A | `RapidOcrOptions.Default` → `PythonCompat`（或自建 `Padding=0, ImgResize=0, LimitSideLen=736`） | §3.3：同 800×300，det 输入 864×352 → **1952×736**；14px 行 12.3px → **34.3px** | 小字/密行检出率大改观 | **耗时翻数倍**（G1 历史教训：长文本 2s→24s）；必须先量 P95 |
| B | 先**切行/切块**再送 det（复刻 `slice_generator`，垂直 stride ≈ 2×行高、带重叠） | §5.1 官方 slice；§3.3 台阶 | 每块吃满 `LimitSideLen`，`/32` 台阶被摊薄 | 块间重复框需 `merge_fragmented` 式合并；块边界会切字 |
| C | 落盘固定 PNG 后跑 N 次，验「同字节是否确定」 | §6.3 | 若同字节结果一致 → **根因在截图/选区**，不在模型 | 一次性实验，不改产品 |
| D | `TextScore` 0.5 与 `BoxScoreThresh` 0.5 各降 0.1 做一次 A/B | §2 表；官方 FAQ 第一条就是降阈值 | 捞回卡边行 | 会多吐碎块（仓内历史：`box_thresh 0.2` 只 +3 图、整块 −2.6pp） |

> 仓内已有可复用实现，别重写：`RapidOcrEngine.cs:405-450` 的 3x 重试 +
> `IsBetterUpscaledResult` 的「行数变多不采用」守卫，以及注释里已经验证过的那句：
> **「放大后 DBNet 把一行拆成几行，不是读得更细，是检测碎掉了」**——
> 这正是 §1 #18244 维护者说的 merge 的镜像：**该靠切片，不是靠整体放大。**
