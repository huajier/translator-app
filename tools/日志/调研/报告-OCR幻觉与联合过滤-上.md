# RapidOCR(PP-OCRv5) 多出一个 "7" —— 检索报告

> 调研日期 2026-11-16。浏览方式：Chrome CDP（curl → localhost:3456）。
> 本机 google.com 被拦，DuckDuckGo/Brave/Startpage/Mojeek 均超时或被反爬拒绝；
> 实际可用的检索入口是 **cn.bing.com（ensearch=1）**、**github.com 搜索/issue/discussion**、
> **arxiv.org 搜索与 HTML 全文**、**raw.githubusercontent.com 源码**。
> WebSearch 工具 401 不可用，以下全部靠上述一手渠道取得。

先说结论：**你问的两条路，方向一（幻觉）有大量一手证据；方向二（孤立框启发式）我找不到任何一手来源说有人这么做**。分开讲。

---

## 方向一：OCR 幻觉

### Q1. CTC 识别模型会在「没有文字的区域」强行输出字符吗？是不是已知现象？

**是。而且不止"识别"这一步，官方 collaborator 承认"检测"这一步就会幻觉。**

一手证据 A —— PaddleOCR 官方仓库 issue #18373（collaborator 亲自复现并确认）
<https://github.com/PaddlePaddle/PaddleOCR/issues/18373>

报告人（PaddleOCR 3.7.0）：
> "我测试图片时发现检测模型容易把手写字的印迹判读为文字，然后再经过识别模型，会识别成不相关的文字。"
> "即使我设置了识别置信度为0.7，还是无法很好解决这个问题。"

PaddleOCR collaborator `Curry-Christopher`（原句，英文）：
> "Hi, I've reproduced this issue. **The v6 detection model has some hallucination on handwritten images like this one, and adjusting the thresholds probably won't fix it for now.** I'd recommend using PaddleOCR-VL-1.6 instead, which does better at both detecting and recognizing handwritten text like this."

这条直接回答了你的问题的两个关键点：
1. 官方用词就是 **hallucination**，用在**检测模型**上，不只是识别模型。
2. **调阈值修不好**（"adjusting the thresholds probably won't fix it"）。这条对着你「关掉按分数过滤整行就多出一个 7」的现场：框已经产生了，分数过滤只是把它的识别分挡掉，不是把框消掉。
3. 同一 issue 里另一位用户 `piaobuliao` 建议「试试把图片高斯模糊下再识别」——这就是社区级 workaround，不是官方机制。

一手证据 B —— 空白/低对比区域被识别成字符，PaddleOCR #12111（collaborator 回答，中文）
<https://github.com/PaddlePaddle/PaddleOCR/issues/12111>

标题：`字符间距大跟空格很像，造成空白区域被误识别成错误字符，如何解决？`
报告人：
> "字符间距大跟空格很像，造成空白区域被误识别成错误字符"
> "识别结果有时会出现多余错误字符"

collaborator `UserWangZz` 回复：
> "这个场景目前没有很好的解决方案，可以尝试压缩图像解决，或者针对该场景训练特定的识别模型"

也就是说：**空白区域 → 多出一个/几个字符，是维护者承认存在的现象，且没有阈值层面的解法。** 这和你的 "7" 高度同型。

一手证据 C —— 机制层面：短框右侧补 0 会把噪声喂给识别模型，PaddleOCR #10534（collaborator 给了机制解释，中文）
<https://github.com/PaddlePaddle/PaddleOCR/issues/10534>

collaborator `ToddBear`：
> "det和rec之间的关联是这样的：det完成后，会将所有检测出的文字区域的图片作为一个batch输入到识别模型中
> 该batch内的所有图片会被resize到同一高度，对于'宽高比'较低的图片，需要在右侧补0，以保证所有图片的宽度是一样的
> 随后该batch的图片就会被送入识别模块中生成识别结果
> 我猜测导致上述现象的原因是**补0操作引入了噪声信息**，使得宽高比较低的文本（即只有几个字的文本）容易被识别错误。"

这条在 RapidOCR 源码里可以逐行对上（见下面 Q2 的 decode 分析）：`resize_norm_img()` 把宽度算成 `img_height * max_wh_ratio`，而 `max_wh_ratio` 是**整个 batch 里的最大值**，短图右侧全部是 0 padding：

`python/rapidocr/ch_ppocr_rec/main.py`
<https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/ch_ppocr_rec/main.py>
```python
max_wh_ratio = max(max_wh_ratio, wh_ratio)     # 取整个 batch 的最大宽高比
...
img_width = int(img_height * max_wh_ratio)     # 每张图都按 batch 最大宽度算
...
padding_im = np.zeros((img_channel, img_height, img_width), dtype=np.float32)
padding_im[:, :, 0:resized_w] = resized_image  # 右边全是 0
```
**这对你很重要**：一个孤立的小框，如果和一行长文本同 batch，右侧 0 padding 极长 —— 正是"幻觉更容易发生在短/孤立框上"的物理原因（这半句是机制推断，见文末「推测」清单）。

一手证据 D —— PP-OCRv5 自己的论文把 hallucination 当成 VLM 的缺陷卖点
<https://arxiv.org/abs/2603.24373>（HTML 全文 <https://arxiv.org/html/2603.24373v1>）
> "(2) Hallucination: In complex layouts, models can confidently "hallucinate," generating plausible yet incorrect text. This presents a critical vulnerability for data-sensitive applications."
> "PP-OCRv5 ... while simultaneously providing superior localization precision, a **drastic reduction in hallucinations**, and a fraction of the computational cost."

注意口径：论文把 hallucination 作为**大 VLM 的缺点**、把 PP-OCRv5 作为**缓解手段**来讲。**论文没有声称传统两阶段 CTC 流水线完全不会幻觉**，也没有给出"空图必空串"的保证。这一点很关键，不要被"reduced hallucinations"读成"不会幻觉"。

顺带：PP-OCRv5 论文全文中 `hallucin*` 只出现 7 次，**全部在 intro/related work/contribution 里**，正文实验部分没有 hallucination 的量化指标。也就是说：**没有官方的"幻觉率"数字可以引用。**

### Q2. PP-OCRv5 / PaddleOCR 的识别模型在空图上会输出什么？空串还是随机字符？有人测过吗？

**没有人公开测过"纯空图"的固定输出**——这点我明确说：找不到。我搜过 PaddleOCR issues/discussions 的 `空白`、`empty`、`blank`、`纯色`、`乱码`，没有一条是"给识别模型一张纯白图，看它输出什么"的实验。

但**源码可以推断出确定性的行为**，而且是两种相反结果，取决于你喂的是什么：

**(a) 如果整条预测序列全是 blank → 结果是空串 `""`，不是随机字符。**

`ppocr/postprocess/rec_postprocess.py`（release/2.7 与 main 分支一致）
<https://github.com/PaddlePaddle/PaddleOCR/blob/main/ppocr/postprocess/rec_postprocess.py>
```python
def get_ignored_tokens(self):
    return [0]  # for ctc blank
```
```python
for idx in range(len(text_index[batch_idx])):
    if text_index[batch_idx][idx] in ignored_tokens:
        continue
    if int(text_index[batch_idx][idx]) == int(end_idx):
        break
```
`ignored_tokens == [0]`，`end_idx == 0`，所以**遇到第一个 blank 就 break**。全 blank ⇒ `char_list` 为空 ⇒ `text = ""`，`conf_list` 为空 ⇒ `np.mean([])`。

RapidOCR 的同款实现把这个坑显式补掉了：
`python/rapidocr/ch_ppocr_rec/utils.py`
<https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/ch_ppocr_rec/utils.py>
```python
if len(conf_list) == 0:
    conf_list = [0]          # 全 blank ⇒ 文本 ""，分数 0.0
...
text = "".join(char_list)
result_list.append((text, np.mean(conf_list).round(5).tolist()))
```
然后 `main.py` 的 `filter_by_text_score()` 会把它丢掉（`0.0 < text_score=0.5`）：
```python
def filter_by_text_score(self, ocr_res: RapidOCROutput) -> RapidOCROutput:
    for i, (box, txt, score) in enumerate(zip(ocr_res.boxes, ocr_res.txts, ocr_res.scores)):
        if score < self.text_score:
            continue
```
另外 `build_final_output()` 还有一层空串过滤：
```python
# 过滤识别结果为空的值
valid_ids = [i for i, v in enumerate(rec_res.txts) if v.strip()]
```

**(b) 但如果模型在**部分**时间步输出了非 blank 字符 → 就是真字符，而且分数可能不低。**

这是最要命的一点：CTC 的 blank 是"该时间步没有字符"，模型完全可以在一个纯色/无字区域上让某几个时间步的 argmax 落到字符类。这时：
- 文本非空（不是 `""`），第二轮过滤抓不到；
- 分数是那几个字符时间步的**均值**，如果模型对那个字符很"自信"，分数可以过 0.5。

所以"空图必出空串"**不成立**。真正成立的是：**全 blank 必出空串且分数 0**；否则就是幻觉字符。

还有第二种噪声源（对你的 "7" 更相关）：**PaddleOCR 2.x 的 decode 在第一个 blank 处 break**。这意味着正常文本中间夹一个 blank，后面就整段丢失（这是"少字"类 bug）；反过来，一个孤立框里若有大面积 padding/纯色，模型在这些位置输出字符，就会变成"凭空多字"。两条现象都指向同一处代码。

**(c) 3.x 默认根本不按识别分过滤。** 这条直接命中你的现场。

`PaddleX/paddlex/configs/pipelines/OCR.yaml`（release/3.0 与 develop 均如此）
<https://github.com/PaddlePaddle/PaddleX/blob/develop/paddlex/configs/pipelines/OCR.yaml>
```yaml
  TextRecognition:
    module_name: text_recognition
    model_name: PP-OCRv6_medium_rec
    batch_size: 6
    score_thresh: 0.0
```
`PaddleX/paddlex/inference/pipelines/ocr/pipeline.py`
<https://github.com/PaddlePaddle/PaddleX/blob/develop/paddlex/inference/pipelines/ocr/pipeline.py>
```python
self.text_rec_score_thresh = text_rec_config.get("score_thresh", 0)
...
if rec_res["rec_score"] >= text_rec_score_thresh:
```
官方文档也写得很直白（英文原文）：
<https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/pipeline_usage/OCR.en.md>
> "`text_rec_score_thresh` ... **defaults to `0.0`, i.e., no threshold**"

> "Text results with scores higher than this threshold will be retained."

RapidOCR 反而默认有 `text_score: 0.5`：
`python/rapidocr/config.yaml`（并有单测锁死 `assert default_cfg.Global.text_score == 0.5`，见 `python/tests/test_default_config.py`）
<https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/config.yaml>

**对你现场的解读**：你说「关掉按分数过滤整行之后多出一个 7」——这说明那个 "7" 的识别分**低于你原来的阈值**。开阈值时它被丢掉，关掉就露出来。这**不是**"全 blank 空串"那种情况（那种分数是 0.0，关阈值后文本仍是 `""`，也不会显示成 "7"）。所以：**你的 "7" 是模型在无字区域上真实输出了字符类的 argmax，只是置信度偏低。** 这一条是从源码行为推出的结论，属于「有证据的推断」，不是直接观测。

### Q3. 有没有标准的度量或检测方法（「box 内没有真实文字」的判定）？

**分两半回答，因为标准程度完全不同。**

**识别侧：没有统一的"幻觉率"标准指标。** 我查到的最接近的是一份综述：
"A Survey of OCR Evaluation Methods and Metrics and the Invisibility of Historical Documents" <https://arxiv.org/abs/2603.25761>
> "Most evaluations emphasize character accuracy and task success on modern layouts. They rarely capture structural failures common in historical newspapers, including column collapse, typographic errors, and **hallucinated text**."

也就是说，学界的共识恰恰是：**现有 OCR 评测覆盖不到幻觉**。想用现成指标来判"这个框里其实没字"，没有现成标准。

**检测侧：有工程化的、被大型 OCR 引擎实际采用的"无字区域"判定规则，但它不是指标，是启发式参数。** 最完整的一手实现是 Tesseract 5 的 textord 噪声拒绝。

`src/textord/textord.cpp`（参数默认值）
<https://github.com/tesseract-ocr/tesseract/blob/main/src/textord/textord.cpp>
```cpp
, double_MEMBER(textord_noise_area_ratio, 0.7, "Fraction of bounding box for noise", ...)
, INT_MEMBER(textord_noise_sizefraction, 10, "Fraction of size for maxima", ...)
, double_MEMBER(textord_noise_sizelimit, 0.5, "Fraction of x for big t count", ...)
, INT_MEMBER(textord_noise_translimit, 16, "Transitions for normal blob", ...)
, double_MEMBER(textord_noise_normratio, 2.0, "Dot to norm ratio for deletion", ...)
, BOOL_MEMBER(textord_noise_rejwords, true, "Reject noise-like words", ...)
, BOOL_MEMBER(textord_noise_rejrows, true, "Reject noise-like rows", ...)
, double_MEMBER(textord_noise_syfract, 0.2, "xh fract height error for norm blobs", ...)
, double_MEMBER(textord_noise_sxfract, 0.4, "xh fract width error for norm blobs", ...)
, double_MEMBER(textord_noise_hfract, 1.0 / 64, "Height fraction to discard outlines as speckle noise", ...)
, INT_MEMBER(textord_noise_sncount, 1, "super norm blobs to save row", ...)
, double_MEMBER(textord_noise_rowratio, 6.0, "Dot to norm ratio for deletion", ...)
```

`src/textord/tordmain.cpp` 里的判定（这是**可直接抄的规则**）
<https://github.com/tesseract-ocr/tesseract/blob/main/src/textord/tordmain.cpp>
```cpp
// 行级：这一行里"小碎块"是否压倒性地多于"正常字符块"
bool rejected = dot_count > norm_count * textord_noise_normratio &&
                dot_count > 2;
...
return super_norm_count < textord_noise_sncount && rejected;
```
```cpp
// 词级：dud_words > ok_words 时，把该词的碎块全扔进 reject 列表
if (dot_count > 2 && !word->flag(W_REP_CHAR)) {
  if (dot_count > norm_count * textord_noise_normratio * 2) {
    word_dud[word_index] = 2;
  } else if (dot_count > norm_count * textord_noise_normratio) {
    word_dud[word_index] = 1;
  } else {
    word_dud[word_index] = 0;
  }
}
...
if (word_dud[word_index] == 2 || (word_dud[word_index] == 1 && dud_words > ok_words)) {
  word->CleanNoise(textord_noise_sizelimit * row->x_height());
}
```
可提取出的三条规则：
1. **尺寸比例**：blob 尺寸 < `0.5 × x_height` 记为 dot（噪声候选），≥ `0.5×xh` 且 `< 2×xh` 记为 normal。
2. **密度比**：`dot_count > 2 × norm_count`（且 dot > 2）才考虑拒绝。
3. **上下文投票**：单个词可疑还不够，要**整行里大多数词都可疑**才动刀（`dud_words > ok_words`）。

第 3 条正是"孤立框更可疑"的**近亲**：Tesseract 用的是「同类邻居的多数」，不是「完全没有邻居」。差别很重要，见方向二 Q5。

**度量侧能用的**：因为没有标准指标，实用做法只能是自建。最省事的定义是
`precision = 被判定有字的框里真的有的比例`，靠人工标注一小批（哪怕 50 张）得到基准，而不是找论文。

---
