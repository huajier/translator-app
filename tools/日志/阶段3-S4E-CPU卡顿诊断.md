# 阶段三 S4-E：识别时 CPU 过高 / 游戏卡顿 —— 1 页诊断（只读，未改一行 src）

日期：2026-10-30　状态：**只诊断，未实现任何改动**

## 1. 结论

**CPU 过高的根因是 RapidOCR 的 ONNX Runtime 线程池全放开（IntraOp/InterOp = 0 = ORT 自选），
本机实测占满 12 个逻辑核的 83~92%；限到 2 线程后降到 33%，而且更快。**
`OcrWorker` 的线程优先级**不是**卡顿来源（实测降优先级代价 7.8 倍，收益只有 +32%）。

## 2. RapidOcrNet 4.2.0 是否暴露线程数 API —— 暴露，但不在 Options 上

`RapidOcrOptions` **没有** 任何线程字段（反射实测只有 Padding/ImgResize/LimitSideLen/MaxSideLen/
MinSideLen/WidthHeightRatio/MinHeight/TextScore/ClsThresh/ClsPreserveAspectRatio/
RecMaxDegreeOfParallelism/BoxScoreThresh/BoxThresh/UnClipRatio/DoAngle/MostAngle/ReturnWordBox/
ReturnSingleCharBox）。

线程数只在 `InitModels` 上，共 6 个重载，其中 3 个带线程参数、3 个吃 `SessionOptions`：

```
InitModels(int numThread = 0)
InitModels(string det, string cls, string rec, string keys, int numThread = 0)
InitModels(RapidOcrModelSet models, int numThread = 0)
InitModels(SessionOptions op)
InitModels(string det, string cls, string rec, string keys, SessionOptions op)
InitModels(RapidOcrModelSet models, SessionOptions op)
```

`GetDefaultSessionOptions(n)` 实测：`0 → IntraOp=0 InterOp=0`（=ORT 自选）、`1 → 1/1`、
`2 → 2/2`、`4 → 4/4`、`-1 → -1/-1`。`ExecutionMode = ORT_SEQUENTIAL`。

**生产代码当前是 `src/Core/Capture/RapidOcrEngine.cs:104` `ocr.InitModels(det, cls, rec, keys);`
—— 走默认 `numThread = 0`，即不限制。** 这就是 CPU 全开的直接原因。

## 3. 当前实际用掉多少 CPU

本机：`12th Gen Intel(R) Core(TM) i5-12400F`，6 核 12 线程。

**真实 exe 回放**（`release\v0.3.4.3-up3\run`，80 张，`tools\临时\cpu-sample2.ps1` 每 100ms 采 `TotalProcessorTime`）：

| 指标 | 值 |
|---|---|
| 墙钟 | 23,255 ms |
| 采样数 | 170 |
| CPU 平均占满载 | **83.1%** |
| CPU 中位 | 87.8% |
| CPU P95 | **100.2%**（= 12 核全占满） |
| CPU 峰值 | 104.9% |
| >50% 的采样 | 160 / 170 |

进程线程峰值 **34**（含 WPF/钩子/OCR worker；ORT 自开的 intra-op 池就藏在这里面）。

## 4. 线程数对比（探针 `tools\临时\CpuProbe`，同模型同 78 张入参图，预热 3 张后计时）

| 线程设置 | 总墙钟 | CPU 总 | CPU 占满载 | 平均 | 中位 | P95 | 最慢 |
|---|---|---|---|---|---|---|---|
| **auto（现状）** | 7,631 ms | 84,703 ms | **92.5%** | 97.3 | 57 | **380** | 550 |
| **2 intra / 1 inter** | 3,596 ms | 14,359 ms | **33.3%** | **45.6** | **26** | **170** | 258 |
| 3 / 1 | 3,282 ms | 22,797 ms | 57.9% | 41.6 | 23 | 163 | 250 |
| 2 / 2 | 3,751 ms | 14,938 ms | 33.2% | 47.6 | 27 | 194 | 271 |
| 4 / 4 | 3,532 ms | 32,984 ms | 77.8% | 44.8 | 27 | 157 | 251 |
| 1 / 1 | 5,175 ms | 5,188 ms | **8.4%** | 65.8 | 38 | 246 | 375 |

三轮重复（0 / 2-1 / 4-4 / 1-1）稳定：auto 86.6~92.2%、2/1 33.2~33.4%、1/1 8.3~8.4%。

只算 23 张大图（面积 ≥2 万像素，卡顿感受主要来自这些）：

| 线程设置 | 总墙钟 | CPU 占满载 | 平均 | P95 | 最慢 |
|---|---|---|---|---|---|
| auto | 4,274 ms | 91.0% | 185.3 | 410 | 504 |
| 2 / 1 | 2,473 ms | 33.5% | 107.0 | 270 | 277 |
| 3 / 1 | 2,071 ms | 58.9% | 89.5 | 220 | 245 |
| 4 / 4 | 1,997 ms | 81.1% | 86.3 | 207 | 234 |
| 1 / 1 | 3,419 ms | 8.5% | 148.1 | 372 | 378 |

**auto 不但最耗 CPU，还最慢**——12 个线程抢同一块内存带宽，开销大于并行收益。

## 5. `OcrWorker` 优先级是不是卡顿来源 —— 不是（实测否决）

`src/Core/Capture/OcrWorker.cs:35` 现在是 `ThreadPriority.Normal`（`8fd17b9` 改的）。

关键机理：**`ThreadPriority` 只作用于我们自己那条 worker 线程，管不到 ONNX Runtime 内部
创建的 intra-op 线程池**——那些线程由 ORT 建，优先级是 Normal。所以降优先级不会减少 CPU，
只会让 worker 在几次 `Detect` 调用之间多等一会儿。

探针 `tools\临时\PrioProbe`（12 核上放 6 个 Normal 忙线程模拟游戏，OCR 用 auto 线程）：

| OCR 线程优先级 | OCR 跑完 78 张 | 整机占满载 | 「游戏」每线程迭代/秒 |
|---|---|---|---|
| Normal | 14,094 ms | 87.4% | 1.58 ×10⁹ |
| BelowNormal | **109,560 ms（7.8× 慢）** | 91.0% | 2.09 ×10⁹（仅 +32%） |

**代价 7.8 倍、收益 +32% → E2 不可取。**

## 6. 实验方案（**只是方案，一行代码都没改**）

### E1（推荐）推理线程限 2：`IntraOp = 2, InterOp = 1`

改 `src/Core/Capture/RapidOcrEngine.cs:104` 一行：

```csharp
ocr.InitModels(det, cls, rec, keys, new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 });
```

预期（探针实测，非承诺）：CPU 占满载 92.5% → **33%**；OCR 单张平均 97 → **46 ms**、P95 380 → **170 ms**。

- 准确率风险：**理论上为零**（线程数不改模型、不改预处理、不改阈值），但必须按老规矩验：
  关时 80/80 逐字节不变、L_行 51/69、L_整块 36/52、Barony 17/34、Z1/Z2/Z3/R1 不变。
- 端到端 P95 预期：单张 OCR 段省约 50ms，端到端 P95（现状 618ms）应小幅下降，不会上升。
- 风险：大图（>2000px 长边缩回）上并行收益本来就小；若某些图反而变慢，退到 3/1（CPU 58%、更快）。

### E2（不推荐，仅作对照）：优先级回 `BelowNormal`

第 5 节实测代价 7.8 倍，不建议做。若要保留对照，只改 `src/Core/Capture/OcrWorker.cs:35` 一行。

**E1 / E2 不同时改，一次只动一个变量。**

## 7. `rapidOcrSmallBoxUpscale`（本条按用户指示写结论，不再推广）

实机开/关无差别 → **保持默认 false**。开关与其代码留在仓库作记录（`3e36ad4`，
tag `S4-D-小框3倍重试`），不写进默认路径、不再做变体。回放收益（+2 行）只在语料上成立，
实机没体现 → 不推广。

## 8. 复现

```
# 1) 线程 API 与默认值
tools\临时\ReflProbe\bin\Release\net10.0\ReflProbe.exe

# 2) 线程数 vs CPU（78 张 dump 图）
tools\临时\CpuProbe\bin\Release\net10.0\CpuProbe.exe ^
  release\v0.3.4.3-up3\models\v5 tools\临时\dump-a tools\临时\cpu-线程数.txt "0,2/1,3/1,4/4,1/1"

# 3) 真实 exe 回放 CPU 采样
powershell -NoProfile -ExecutionPolicy Bypass -File tools\临时\cpu-sample2.ps1

# 4) 优先级代价
tools\临时\PrioProbe\bin\Release\net10.0\PrioProbe.exe ^
  release\v0.3.4.3-up3\models\v5 tools\临时\dump-a tools\临时\prio-满载.txt 0 6
```

产物：`tools\临时\cpu-线程数.txt`、`cpu-大图.txt`、`cpu-终选.txt`、`cpu-sample.txt`（含
`.timeline.txt`）、`prio-满载.txt`。探针源码在 `tools\临时\{ReflProbe,CpuProbe,PrioProbe}\`（均被 .gitignore 忽略）。
