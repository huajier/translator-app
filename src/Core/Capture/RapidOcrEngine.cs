using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// RapidOCR 认字引擎（PP-OCRv5，ONNX Runtime + SkiaSharp，纯离线纯 C#）。
///
/// 【2026-10-27 阶段三 S3 接入：外文主通道】
///   选型过程与对照数据见 tools\日志\阶段3-S3-第三引擎选型.md。
///   一句话：旧引擎（Tesseract 5.5.3）在这套游戏像素字体上到顶了
///   （L_行 50.7%），换这个引擎裸跑是 63.8%，且「整行丢」从 64% 掉到 6%。
///
/// 【它自带预处理，所以吃原图】
///   DBNet 检测 + 角度分类 + CTC 识别都在内部做，**不需要外部放大/二值化**。
///   旧通道（Windows OCR / Tesseract）的预处理一个字没动，那条路照样用它自己的。
///
/// 【模型路径】
///   包会把 models\v5\ 拷到 exe 旁边（build\RapidOcrNet.targets 干的）。
///   这里**用绝对路径**按 exe 所在目录拼，不依赖工作目录 ——
///   双击启动和从别处调用，工作目录不一样，相对路径会找不到模型。
///
/// 【失败即沉默】
///   模型缺失、初始化抛异常、识别抛异常 → 一律返回 null 并写一行日志，
///   由调用方回退旧通道。**绝不因为新引擎挂了就不认字。**
/// </summary>
internal static class RapidOcrEngine
{
    /// <summary>字符分低于这个值算「弱字符」。与 tools/临时/S4 离线模拟的 0.60 同口径。</summary>
    private const float WeakCharScore = 0.60f;

    private static readonly object Gate = new();
    private static RapidOcr? _ocr;

    /// <summary>
    /// 【2026-11-10 阶段三 P1-西里尔】第二套 rec 会话（西里尔模型）。
    ///
    /// 【为什么需要它】en 模型的字典 436 行里西里尔字母 **0 条** ——
    ///   en 识别头结构上就不可能在输出里出现俄文字母，俄文必吐拉丁乱码。
    ///   实测：俄文 11 段，en 单独 0/11。
    /// 【为什么是第二套实例而不是换掉 _ocr】det/cls 复用同一批文件，
    ///   两套 RapidOcr 实例各自带一份 rec；同一张图跑两遍 rec、各自出文本，交给调用方择优。
    /// 【为 null 时】文件缺失 → 只跑 en，主通道不受影响（绝不允许因此整条失效）。
    /// </summary>
    private static RapidOcr? _ocrCyr;

    /// <summary>会话选项。创建好之后必须一直活着 —— 提前 Dispose 会让推理抛异常。</summary>
    private static SessionOptions? _sessionOptions;

    /// <summary>第二套实例的会话选项（和第一套分开持有，同样不能提前 Dispose）。</summary>
    private static SessionOptions? _sessionOptionsCyr;
    private static bool _tried;

    /// <summary>识别要串行：一个引擎实例带三份 ONNX 会话，并发调用没有意义也不保证安全。</summary>
    private static readonly object RunGate = new();

    /// <summary>模型目录（跟着 exe 走）。</summary>
    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "models", "v5");

    /// <summary>引擎是否可用（首次访问会初始化，约 0.3 秒，之后复用）。</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _ocr is not null;
        }
    }

    private static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_tried) return;
            _tried = true;

            string dir = ModelDir;
            string det = Path.Combine(dir, "ch_PP-OCRv5_mobile_det.onnx");
            string cls = Path.Combine(dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

            // 识别模型按设置挑：en（英文专用，默认）/ latin（包自带）。
            // 只换 rec + dict，det/cls 不变；缺文件就退回 latin，不让主通道整个失效。
            string rec, keys, recName;
            if (AppSettings.RapidOcrRec == "latin")
            {
                rec = Path.Combine(dir, "latin_PP-OCRv5_rec_mobile_infer.onnx");
                keys = Path.Combine(dir, "ppocrv5_latin_dict.txt");
                recName = "latin";
            }
            else
            {
                rec = Path.Combine(dir, "en_PP-OCRv5_rec_mobile.onnx");
                keys = Path.Combine(dir, "ppocrv5_en_dict.txt");
                recName = "en";
                if (!File.Exists(rec) || !File.Exists(keys))
                {
                    AppLogger.Warn($"英文识别模型缺失（{dir}），改用包自带 latin 模型");
                    rec = Path.Combine(dir, "latin_PP-OCRv5_rec_mobile_infer.onnx");
                    keys = Path.Combine(dir, "ppocrv5_latin_dict.txt");
                    recName = "latin（en 缺文件回退）";
                }
            }

            if (!File.Exists(det) || !File.Exists(rec) || !File.Exists(keys))
            {
                AppLogger.Warn($"RapidOCR 模型文件缺失（{dir}），外文继续走旧通道");
                return;
            }

            try
            {
                var start = Stopwatch.StartNew();
                var ocr = new RapidOcr();
                var options = new SessionOptions
                {
                    IntraOpNumThreads = 2,
                    InterOpNumThreads = 1
                };
                ocr.InitModels(det, cls, rec, keys, options);
                _ocr = ocr;
                _sessionOptions = options;
                AppLogger.Info($"RapidOCR 引擎已就绪：PP-OCRv5 {recName}，耗时 {start.ElapsedMilliseconds} 毫秒（之后复用，不再重建）");

                // ==========================================================
                // 【2026-11-10 阶段三 P1-西里尔】第二套 rec：西里尔模型
                //
                //   det/cls 共用同一批文件，只是再挂一份 rec + dict。
                //   文件缺失 → Warn 一句、只跑 en（主通道绝不因此失效）。
                //   初始化失败也降级到「只有 en」，不影响第一套。
                // ==========================================================
                string cyrRec = Path.Combine(dir, "cyrillic_PP-OCRv5_rec_mobile.onnx");
                string cyrKeys = Path.Combine(dir, "ppocrv5_cyrillic_dict.txt");
                if (!File.Exists(cyrRec) || !File.Exists(cyrKeys))
                {
                    AppLogger.Warn(
                        $"西里尔识别模型缺失（{dir}），本次只跑 en 模型 —— 俄文会读成拉丁乱码，但不影响英文");
                }
                else
                {
                    try
                    {
                        var cyrStart = Stopwatch.StartNew();
                        var ocrCyr = new RapidOcr();
                        var optionsCyr = new SessionOptions
                        {
                            IntraOpNumThreads = 2,
                            InterOpNumThreads = 1
                        };
                        ocrCyr.InitModels(det, cls, cyrRec, cyrKeys, optionsCyr);
                        _ocrCyr = ocrCyr;
                        _sessionOptionsCyr = optionsCyr;
                        AppLogger.Info(
                            $"RapidOCR 第二模型已就绪：PP-OCRv5 cyrillic，耗时 {cyrStart.ElapsedMilliseconds} 毫秒（与 en 双跑择优）");
                    }
                    catch (Exception exCyr)
                    {
                        _ocrCyr = null;
                        AppLogger.Warn(
                            $"西里尔模型初始化失败（只跑 en，俄文会读成拉丁乱码）：{exCyr.GetType().Name} {exCyr.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _ocr = null;
                AppLogger.Warn($"RapidOCR 初始化失败（外文继续走旧通道）：{ex.GetType().Name} {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 认一张图。成功返回按行拼好的文本（行间用真正的 <c>\n</c>，和旧通道一致），
    /// 失败返回 null，原因写进 <paramref name="note"/>。
    /// </summary>
    /// <param name="averageScore">各检测行的平均置信度（0~1），调用方拿它决定信不信。</param>
    /// <param name="weakChars">字符分低于 0.60 的字符数（跨块累加，纯统计）。</param>
    /// <param name="singleCharBoxes">有效字符（字母/数字）不超过 1 个的框数（纯统计）。</param>
    /// <param name="boxCount">检测框总数（纯统计）。</param>
    public static string? TryRecognize(
        Bitmap bitmap, out double averageScore, out string note,
        out int weakChars, out int singleCharBoxes, out int boxCount)
    {
        averageScore = 0;
        note = string.Empty;
        weakChars = 0;
        singleCharBoxes = 0;
        boxCount = 0;

        if (!IsAvailable)
        {
            note = "引擎不可用";
            return null;
        }

        DumpIfAsked(bitmap);

        try
        {
            Pass first = RecognizeWithRetry(bitmap);

            note = first.Note;
            averageScore = first.AverageScore;
            weakChars = first.WeakChars;
            singleCharBoxes = first.SingleCharBoxes;
            boxCount = first.BoxCount;
            return first.Text;
        }
        catch (Exception ex)
        {
            note = $"{ex.GetType().Name} {ex.Message}";
            AppLogger.Warn($"RapidOCR 识别异常（回退旧通道）：{note}");
            return null;
        }
    }

    /// <summary>
    /// 【2026-11-10 阶段三 P1-西里尔】「一次识别 + 小框 3 倍重试」的完整流程。
    ///
    /// 【为什么要抽出来】TryRecognize 和 TryRecognizeBoth 必须走**同一份**重试逻辑。
    ///   上一轮的 bug 就是把 TryRecognizeBoth 的 en 侧写成裸调 RecognizeOnePass，
    ///   漏掉了这段重试 —— 行-68 从 3d 8h 退成 48 pε、行-76/83 整段崩掉，
    ///   而那三张正是下面注释里「80 张里只触发 2 张」救回来的。
    ///   **绝不许把这段重试代码抄第二份。**
    /// </summary>
    private static Pass RecognizeWithRetry(Bitmap bitmap)
    {
        Pass first = RecognizeOnePass(bitmap);

        // 【2026-10-30 阶段三 S4-D：小框 3 倍重试（默认关）】
        //   申请单与全量模拟见 tools 日志 阶段3-S4D-申请单-小框3倍重试.md。
        //   1x 结果「空」或「某块里有非 ASCII 字符」时，把同一张输入图放大 3 倍再认一次。
        //   80 张里只触发 2 张（行-68 的 ε、行-75 整块空），L_行 49→51、回归 0。
        //   3x 结果要「非空 且 非 ASCII 字符数不增加」才采用，否则保留 1x。
        //   开关关着时这段一次都不进，行为与没有这个功能完全一致。
        if (AppSettings.RapidOcrSmallBoxUpscale && NeedsUpscaleRetry(bitmap, first))
        {
            using Bitmap upscaled = UpscaleForRetry(bitmap);
            Pass second = RecognizeOnePass(upscaled);
            if (IsBetterUpscaledResult(first, second))
            {
                AppLogger.Info(
                    $"小框 3 倍重试：{bitmap.Width}×{bitmap.Height} → {upscaled.Width}×{upscaled.Height}，" +
                    $"1x「{Preview(first.Text)}」→ 3x「{Preview(second.Text)}」");
                first = second;
            }
        }

        return first;
    }

    /// <summary>一次 Detect 的全部产出（1x 与 3x 各产生一个，用来比较与择优）。</summary>
    private readonly record struct Pass(
        string? Text, string Note, double AverageScore,
        int WeakChars, int SingleCharBoxes, int BoxCount, bool WeakBlock);

    /// <summary>对一张位图跑一次完整识别，不涉及任何重试决策。原 TryRecognize 的实现原样搬到这里。</summary>
    private static Pass RecognizeOnePass(Bitmap bitmap) => RecognizeOnePass(bitmap, _ocr);

    /// <summary>
    /// 【2026-11-10 阶段三 P1-西里尔】按指定引擎实例跑一次识别。
    ///
    ///   与上面那个重载唯一的区别就是「用哪个 RapidOcr」——
    ///   这样 en / cyrillic 两套 rec 可以吃同一张图、走完全相同的检测与拼装逻辑，
    ///   两边的输出才有可比性（择优才站得住）。
    /// </summary>
    private static Pass RecognizeOnePass(Bitmap bitmap, RapidOcr? engine)
    {
        // GDI 位图 → Skia 位图：走一次内存 PNG 编码（裁样都只有几十 KB，1 毫秒量级），
        // 比手写 LockBits 逐像素搬运短得多，也不用碰指针。
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var skBitmap = SKBitmap.Decode(ms);
        if (skBitmap is null) return new Pass(null, "图片解码失败", 0, 0, 0, 0, false);

        OcrResult? result;
        lock (RunGate)
        {
            result = engine!.Detect(skBitmap, RapidOcrOptions.Default);
        }

        var blocks = result?.TextBlocks;
        if (blocks is null || blocks.Length == 0)
        {
            return new Pass(null, "没检测到文字行", 0, 0, 0, 0, false);
        }

        var sb = new StringBuilder();
        var scores = new System.Collections.Generic.List<double>(blocks.Length);
        int weakChars = 0, singleCharBoxes = 0, boxCount = 0;
        bool weakBlock = false;
        foreach (var block in blocks)
        {
            // 统计口径与 tools/临时/S4 离线模拟脚本一致：先统计，再跳过空行。
            boxCount++;
            int blockWeak = 0;
            if (block.CharScores is { Length: > 0 } everyScore)
            {
                foreach (float one in everyScore)
                {
                    if (one >= WeakCharScore) continue;
                    weakChars++;
                    blockWeak++;
                }
                // 【2026-10-31 阶段三 S4-D-1b】塌行信号：同一块里低分字符占比过高。
                //   0.60 分以下的字占该块四分之一以上，且至少 2 个。
                //   实测：行-76 一块 27 字里 8 个（30%）、行-83 一块 6 字里 2 个（33%）；
                //   而全部判对的行里最高只到 1 个 / 0.25，余量够。中文块跳过——中文分本来就低。
                //   还要求这块至少有 4 个字母/数字：R1 俄文图的 1x 是「    6」（只有一个数字、
                //   其余全是空格，空格分也低），不加这条会被当成塌行拉去 3x，凭空造出 23 字乱码。
                if (blockWeak >= 2 && blockWeak * 4 >= everyScore.Length
                    && CountLettersDigits(block.Text) >= 4
                    && !LanguageDetector.IsMostlyChinese(block.Text))
                {
                    weakBlock = true;
                }
            }
            if (CountLettersDigits(block.Text) <= 1) singleCharBoxes++;

            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            // 【2026-10-29 用户实机反馈「换行显示：无」】
            //   原来这里拼的是 U+23CE（⏎）——那是**取词记录/回放结果文件**里
            //   为了「一段一行」用的换行替身，不是给人看的文本。
            //   UI 的 MainText 是个 TextBox，它只认真正的 \n，
            //   于是用户看到的是 7 行认出来挤成一整行、中间还夹着 ⏎。
            //   旧通道 RecognizeOnce 拼的是 string.Join("\n", lines)，
            //   这里改成一样，两条通道才算一致。
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(block.Text);
            if (block.CharScores is { Length: > 0 } chars)
            {
                scores.Add(chars.Average(c => (double)c));
            }
        }

        if (sb.Length == 0)
        {
            return new Pass(null, "检测到行但没有文字", 0, weakChars, singleCharBoxes, boxCount, weakBlock);
        }

        return new Pass(sb.ToString(), string.Empty, scores.Count > 0 ? scores.Average() : 0,
            weakChars, singleCharBoxes, boxCount, weakBlock);
    }

    /// <summary>
    /// 【2026-11-10 用户实测补】图高超过这个值就不做 3 倍重试。
    ///
    /// 病根：3 倍重试是为「一行字太小读不清」设计的（60×30 的 3d 8h、487×19 的整行英文），
    ///   但用户框整段长文本（704×474）时它也会触发：放大到 2000×1347 花 2578ms，
    ///   1x/3x 读出来一样烂，最后两个都没被采用 —— 那 2.5 秒是纯浪费。
    ///
    /// 阈值 126 的依据（tools\脚本\量-重试尺寸阈值.py，80 张行级图逐张 1x vs 3x）：
    ///   3x 真正救回来的 2 张：行-81(100×32)、行-07(86×26)；
    ///   3x 反而读坏的 3 张：行-17(910×40)、行-20(910×42)、行-01(862×52)；
    ///   从「高&lt;=40」扫到「宽&lt;=1500」共 19 个候选阈值，判对数全部是 67/80，
    ///   和现在一模一样 —— 砍大图一分不损失。取 126（≈全量图的 P90）留足余量，
    ///   让 92% 的图仍然保留重试能力。
    ///
    /// ponytail: 阈值 126 是按「一行字的高度」定的。整段多行图（高几百像素）
    ///   一律不重试；将来若真出现「很高的单行图」，再按宽高比细分。
    ///
    /// 【2026-11-13 126 → 160】126 是「一行字」的上限，但真机丢行的图是 126~160 高的
    ///   多行小图（661×93、666×87、656×103）。126 会让它们在下面那道尺寸闸就被挡掉，
    ///   连危险区判断都走不到。80 张行级图上扫过 H=160 的组合，成绩仍 71/80 不掉。
    /// </summary>
    private const int UpscaleRetryMaxHeight = 160;

    /// <summary>
    /// 【2026-11-13 用户实测】宽度和高度同时不超过这两个值时，强制走 3 倍重试。
    ///   依据：真机存图 659×80 的 1x 只认到 90 字（词粘连），3x 是 177 字全对。
    ///   阈值取 800/160：覆盖用户实测失败的所有尺寸（659×80、661×93、666×87、656×103），
    ///   不覆盖已经够大的图（671×322、668×247 那些读得不错）。
    ///   80 张行级图扫描：从 400×60 到 9999×9999，40 种组合成绩全部 71/80 不掉。
    /// </summary>
    private const int ForceUpscaleMaxWidth = 800;
    private const int ForceUpscaleMaxHeight = 160;

    /// <summary>
    /// 1x 结果要不要 3x 重试（S1 / S2 / S3 三个信号）。
    ///   S1 空：没有文本。
    ///   S2 非 ASCII：逐行判，任一行里有 <c>c &gt; 127</c> 的字符（典型是 行-68 的 ε）。
    ///   S3 塌行：某一块里 0.60 分以下的字占该块四分之一以上且至少 2 个（行-76 / 行-83）。
    /// S2 是**逐行**判：首版按整图串判，把含 <c>'</c> <c>-</c> <c>?</c> 的图误触 12 张，
    /// 整块 -5.7pp。中文行/块也跳过——中文本来就该是汉字，这两条信号对它没有意义。
    ///
    /// 【2026-11-10 补：前面多一道尺寸闸】大图（整段长文本）一律不重试，
    /// 三条老信号原样保留，只是先被尺寸挡掉。
    ///
    /// 【2026-11-13 补：尺寸危险区强制重试】宽高都在小图区间时无条件重试，
    /// 不看三条老信号 —— 真机 659×80 那张 1x 只认到 90 字（词粘连、缺词），
    /// 同图 3x 是 177 字全对，而三条老信号一条都没命中（文本非空、无塌行、纯 ASCII）。
    /// </summary>
    private static bool NeedsUpscaleRetry(Bitmap bitmap, Pass pass)
    {
        if (bitmap.Height > UpscaleRetryMaxHeight) return false;

        // 【2026-11-13 用户实测】小尺寸图强制重试。
        //   真机存图实测：659×80 这张 1x 只认到 90 字（词粘连），
        //   同图 3x 是 177 字全对、三次结果一致。
        //   而三条老信号（空/非ASCII/塌行）在这张图上一条都没命中，所以它没重试。
        //   80 张行级图上扫过 40 种阈值组合，成绩全部是 71/80（不掉），
        //   平均耗时从 120ms 涨到 145ms（+25ms），可以接受。
        if (bitmap.Width <= ForceUpscaleMaxWidth && bitmap.Height <= ForceUpscaleMaxHeight)
            return true;

        if (pass.Text is null) return true;
        if (pass.WeakBlock) return true;

        foreach (string line in pass.Text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (LanguageDetector.IsMostlyChinese(line)) continue;
            foreach (char c in line)
            {
                if (c > 127) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 3x 结果比 1x 好吗：非空 且 非 ASCII 字符数不增加（非 ASCII 变多说明认得更糟）。
    /// 注意 S3 触发的两块（行-76 / 行-83）1x 与 3x 都是纯 ASCII，这条守卫不会把它们挡回去。
    ///
    /// 【2026-11-13 用户实测补：行数变多一律不采用】
    ///   强制 3x（宽&lt;=800 且 高&lt;=160）上线后，80 张行级图里 12 张结果变了：
    ///   变坏的 5 张**全部**是「行数变多」——行-03 1→3、行-04 1→3、行-23 1→2、
    ///   行-29 3→7、行-38 1→3；而变好的几张（行-09/43/52）行数全是 1→1。
    ///   本质：放大后 DBNet 把一行拆成几行，**不是读得更细，是检测碎掉了**。
    ///   行数用 TextNormalizer.CountLines（与界面「N 行」同一个尺子）。
    /// </summary>
    private static bool IsBetterUpscaledResult(Pass before, Pass after)
    {
        if (after.Text is null) return false;
        if (CountNonAscii(after.Text) > CountNonAscii(before.Text)) return false;
        if (TextNormalizer.CountLines(after.Text) > TextNormalizer.CountLines(before.Text)) return false;
        return true;
    }

    /// <summary>数非 ASCII 字符（&gt; 127），用来判断一次重试有没有把图认得更糟。</summary>
    private static int CountNonAscii(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (char c in text)
        {
            if (c > 127) n++;
        }
        return n;
    }

    /// <summary>
    /// 放大 3 倍给重试用。放大方式与 ImagePreprocessor.UpscaleForForeign 同款
    /// （HighQualityBicubic + PixelOffsetMode.HighQuality），不新增依赖。
    /// 边太长时按 2000 像素上限把系数降下来——超过 MaxSideLen，检测会直接失败。
    /// </summary>
    private static Bitmap UpscaleForRetry(Bitmap source)
    {
        const int retryScale = 3;
        const int maxSide = 2000;

        double scale = retryScale;
        int longest = Math.Max(source.Width, source.Height);
        if (longest * scale > maxSide) scale = (double)maxSide / longest;
        if (scale < 1) scale = 1;

        int nw = Math.Max(1, (int)Math.Round(source.Width * scale));
        int nh = Math.Max(1, (int)Math.Round(source.Height * scale));

        var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, nw, nh));
        return dst;
    }

    /// <summary>日志里截断一长串文本，只留前 40 个字符。</summary>
    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        string oneLine = text.Replace('\n', ' ');
        return oneLine.Length <= 40 ? oneLine : oneLine[..40] + "…";
    }

    /// <summary>数有效字符（字母/数字），和离线模拟里 <c>t.Count(char.IsLetterOrDigit)</c> 同口径。</summary>
    private static int CountLettersDigits(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) n++;
        }
        return n;
    }

    /// <summary>诊断用序号（每进程从 1 开始，按回放顺序与 id 一一对应）。</summary>
    private static int _dumpSeq;

    /// <summary>
    /// 诊断用：`rapidOcrDumpDir` 非空时，把这张**实际入参**存成 `NNNN.png`，
    /// 并把「尺寸 / 平均亮度」追加到 `dump.txt`。平均亮度用来一眼看出是不是反色图
    /// （反色后浅字深底会变成暗字亮底，亮度从 &lt;100 跳到 &gt;150）。
    /// 任何异常都只记一条警告，**绝不影响识别**。
    /// </summary>
    private static void DumpIfAsked(Bitmap bitmap)
    {
        string dir = AppSettings.RapidOcrDumpDir;
        if (string.IsNullOrWhiteSpace(dir)) return;

        try
        {
            Directory.CreateDirectory(dir);
            int seq = System.Threading.Interlocked.Increment(ref _dumpSeq);
            string name = $"{seq:D4}.png";
            bitmap.Save(Path.Combine(dir, name), ImageFormat.Png);

            // 平均亮度：每 4 个像素取一个，几十 KB 的小图足够准，也不慢
            double sum = 0;
            int n = 0;
            for (int y = 0; y < bitmap.Height; y += 2)
            {
                for (int x = 0; x < bitmap.Width; x += 2)
                {
                    Color c = bitmap.GetPixel(x, y);
                    sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    n++;
                }
            }
            double lum = n > 0 ? sum / n : 0;

            File.AppendAllText(
                Path.Combine(dir, "dump.txt"),
                $"{seq:D4}\t{bitmap.Width}x{bitmap.Height}\t亮度 {lum:F1}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"入参存图失败（不影响识别）：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>程序退出时释放 ONNX 会话。</summary>
    public static void Dispose()
    {
        lock (Gate)
        {
            try { _ocr?.Dispose(); } catch { /* 退出路径，忽略 */ }
            try { _sessionOptions?.Dispose(); } catch { /* 退出路径，忽略 */ }
            try { _ocrCyr?.Dispose(); } catch { /* 退出路径，忽略 */ }
            try { _sessionOptionsCyr?.Dispose(); } catch { /* 退出路径，忽略 */ }
            _ocr = null;
            _sessionOptions = null;
            _ocrCyr = null;
            _sessionOptionsCyr = null;
            _tried = false;
        }
    }

    /// <summary>
    /// 【2026-11-10 阶段三 P1-西里尔】同一张图跑两套 rec（en + cyrillic），各自出结果。
    ///
    /// 【为什么要这个】en 字典里西里尔 0 条，俄文必吐拉丁乱码；
    ///   cyrillic 模型能读俄文（离线 11 段 10/11），但它的英文字典只有 52 个拉丁字母，
    ///   单独用会把英文读差。两个都跑、交给调用方择优，才能同时保住英文和俄文。
    ///
    /// 【返回】两套都成功才返回 true，任一失败返回 false（调用方保持原有单模型行为）。
    /// 【不在这里做择优】判据属于识别策略，放在 OcrTextCapture.Foreign.cs 里，
    ///   和「什么时候采纳 RapidOCR」的其他判据待在一起，便于一处读懂。
    /// </summary>
    /// <param name="enText">en 模型结果（可为 null 表示没认到内容）。</param>
    /// <param name="enScore">en 模型各检测行平均分。</param>
    /// <param name="enNote">en 侧失败原因（与 TryRecognize 的 note 同口径）。</param>
    /// <param name="enWeakChars">en 侧弱字符数（字符分 &lt; 0.60，跨块累加）。</param>
    /// <param name="enSingleBoxes">en 侧单字框数（有效字母/数字 ≤1 的框）。</param>
    /// <param name="enBoxCount">en 侧检测框总数。</param>
    /// <param name="cyrText">cyrillic 模型结果（可为 null 表示没认到内容）。</param>
    /// <param name="cyrScore">cyrillic 模型各检测行平均分。</param>
    public static bool TryRecognizeBoth(
        Bitmap bitmap,
        out string? enText, out double enScore, out string enNote,
        out int enWeakChars, out int enSingleBoxes, out int enBoxCount,
        out string? cyrText, out double cyrScore)
    {
        enText = null;
        cyrText = null;
        enScore = 0;
        cyrScore = 0;
        enNote = string.Empty;
        enWeakChars = 0;
        enSingleBoxes = 0;
        enBoxCount = 0;

        if (!IsAvailable) return false;
        // 第二套没起来（文件缺失/初始化失败）：调用方按原有单模型流程走
        if (_ocrCyr is null) return false;

        DumpIfAsked(bitmap);

        try
        {
            // en 侧：走 RecognizeWithRetry（含小框 3 倍重试），和 TryRecognize 完全同一条路。
            // 【2026-11-10 修】上一轮这里是裸调 RecognizeOnePass，漏掉重试，
            //   行-68 从 3d 8h 退成 48 pε、行-76/83 整段崩 —— 就是这段的代价。
            Pass en = RecognizeWithRetry(bitmap);

            // cyr 侧：仍然裸调，不加重试。
            //   它只服务俄文长文本，小框 3x 对那种场景没有意义，加了只会更慢。
            Pass cyr = RecognizeOnePass(bitmap, _ocrCyr);

            // 【只加日志，不改检测参数】cyr 返回空时记清楚现场，
            //   便于判断是不是 DBNet 在窄图上没出框（架构师离线切行后直接喂 rec 是全对的）。
            if (string.IsNullOrWhiteSpace(cyr.Text))
            {
                AppLogger.Info(
                    $"【西里尔模型】返回空（图 {bitmap.Width}×{bitmap.Height}，" +
                    $"检测框 {cyr.BoxCount} 个，note={cyr.Note}）");
            }

            enText = en.Text;
            enScore = en.AverageScore;
            // 【2026-11-10 去重】这四个值和 TryRecognize 的出参一一对齐，
            //   调用方靠它们跑字数量闸门 / blockLooksBad / 「整块疑似没认好」日志。
            //   直接取自上面那次 Pass en，**不许多跑一次识别**。
            enNote = en.Note;
            enWeakChars = en.WeakChars;
            enSingleBoxes = en.SingleCharBoxes;
            enBoxCount = en.BoxCount;
            cyrText = cyr.Text;
            cyrScore = cyr.AverageScore;
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"RapidOCR 双模型识别失败（本次按无结果处理）：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }
}
