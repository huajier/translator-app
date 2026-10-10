using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Abstractions;
using TranslatorApp.Core.Logging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// OCR 通道：截图 → 预处理 → 识别 → 清理结果。
///
/// 【为什么必须这样】
///   1. 引擎**只创建一次**（见 <see cref="OcrEngineFactory"/>），
///      每次现建要慢 0.5~2 秒，这是「按了快捷键半天没反应」的头号原因；
///   2. 识别在**后台线程**跑（WinRT 是异步 API），绝不阻塞界面；
///   3. 每一张 GDI / WinRT 对象都**及时释放**，连续识别几十次内存不许涨。
///
/// 【Windows OCR 只认拉丁文字？】
///   Windows 自带 OCR 支持中文，但**对中文的识别质量依赖清晰度**，
///   所以前面的放大 + 灰度预处理很关键。
/// </summary>
internal sealed class OcrTextCapture : ITextCapture
{
    /// <summary>
    /// 置信度达到多少，就算「这个模型认得很确定」，不必再试另一种语言。
    ///
    /// 【2026-10-09 数值来自用户实测数据，不是拍脑袋】
    ///   同一批内容、同一套引擎，只换模型，置信度差得非常干净：
    ///     真英文配 eng 模型 → **0.9**
    ///     俄文配   eng 模型 → **0.1 ~ 0.4**
    ///
    ///   0.75 卡在中间那片空白里：
    ///   离 0.4 有余量（俄文绝不会被误判成「英文很确定」），
    ///   离 0.9 也有余量（稍差的英文不会白白多跑一次俄文）。
    /// </summary>
    private const double GoodConfidence = 0.75;

    /// <summary>
    /// 「兜底尝试」的门槛，**比择优门槛更保守（更高）**。
    ///
    /// 【为什么不能两个都用 0.75（用户实测踩出来的）】
    ///   Z1 那张橙底绿字的中文，外文模型只认出 `SVMs`（4 个字），
    ///   置信度 **0.76**——**就比 0.75 高一点点**，
    ///   中文兜底因此完全没被触发，结果四个字交到了用户面前。
    ///
    ///   置信度高**不等于内容对**：
    ///   图上只剩几根杂碎笔画时，Tesseract 会「很自信地」把它认成几个像样的字，
    ///   分数自然就高。**越是这种看着「还挺确定」的小结果，越要多问一句。**
    ///
    ///   定 0.90：实测里真正认对了的外文都在 0.81~0.95，
    ///   门槛放在 0.90 意味着**只有接近完美的结果才免去这道兜底**，
    ///   多花的那 30 毫秒换掉「四个字糊弄过去」的惨剧，很划算。
    /// </summary>
    private const double GoodConfidenceForRescue = 0.90;

    /// <summary>
    /// 「这段文字算不算俄文」的门槛：**西里尔字母至少要占 50%**。
    ///
    /// 【为什么不是 0.10（用户实测数据定出来的）】
    ///   放大到 4 倍之后 `rus+eng` 分数普遍变高，
    ///   E3（**英文**）被 rus+eng 以 0.92 顶掉 eng 的 0.91，
    ///   英文邮箱被拼成了西里尔 `те@ехатре.сот`。
    ///
    ///   把那一轮每段的西里尔占比拉出来：
    ///     认对的俄文 → R7 87%、R8 78%、R9 90%、其余全部 100%
    ///     认错的 E3  → **20%**
    ///   **正确的一律 78% 以上，错误的那个只有 20%，中间一大片空白。**
    ///   0.50 卡在空白正中间，不存在「卡在哪都可能出错」的问题。
    ///
    /// 【道理】
    ///   真正的俄文句子，字母里绝大多数是西里尔；
    ///   「把英文硬拼成西里尔」的产物，西里尔只集中在被误认的那几个词里，占比很低。
    /// </summary>
    private const double MinCyrillicRatioForRussian = 0.50;

    /// <summary>
    /// **低于这个分数就当没认出来**，绝不把乱码当正式结果给用户。
    ///
    /// 【这道门槛从 0.60 提到 0.78 —— 材料 3 把空档填清楚了】
    ///
    ///   当初定 0.60 的办法是「找认对和认错之间的空档，卡在中间」。
    ///   那时手上的样本是：
    ///     认对的：0.81 ~ 0.96
    ///     认错的：0.08 ~ 0.46
    ///     中间 0.46 ~ 0.81 什么都没有，所以 0.60 卡在中间。
    ///
    ///   **材料 3 之后，空档里掉进来两个新样本，全是乱码**：
    ///     `Масппе {гап|айоп сопуей$ {ех тот опе |апдцаде © апо{ег.`   0.70
    ///     小灰字那段 `这一切专| J亲六革路刚队急材衬宫人小由有加了息…`    0.57
    ///   **0.70 从 0.60 那道门槛底下钻过去了，正是用户看到乱码的原因。**
    ///
    ///   现在的完整分布：
    ///     认成乱码的：0.13  0.32  0.33  0.57  0.70  0.76
    ///     ────────────────────────────────
    ///                                        ↑ 0.76 ~ 0.81 又是空的
    ///     认对了的：0.81  0.85  0.86  0.87  0.88  0.93  0.94  0.95  0.96
    ///   **0.78 卡在新的空档里，用的还是当初那个办法。**
    ///
    /// 【2026-10-10 用户问「能不能识别出乱码的时候返回未识别到文字」】
    ///   这是用户亲口提的要求：**宁可说没认出来，也不要给一堆乱码。**
    ///   代价是偶尔会拒掉一些其实还能看的结果——**这个代价用户自己认了**。
    ///
    /// 【两条走不通的路，已经用实测数据否掉，别再走】
    ///   ① **靠文本形态判断乱码**——不可靠。
    ///      实测符号占比：正常技术文本 `HTTP/2.0 … 85% CPU` 是 **0.19**，
    ///      乱码 `Масппе {гап|айоп сопуей$` 是 **0.16**，**乱码反而更低，分不开**。
    ///      而且 `{ } | $ ©` 在正常代码和文档里本来就大量出现。
    ///   ② **靠「两个引擎结果一致」判断可信**——也不可靠。
    ///      实测那一段乱码：`rus` 和 `rus+eng` 给出**一模一样的 0.70 / 64 字**，
    ///      一致度 100%，**结果还是错的**。两个引擎会一致地错。
    ///   **目前唯一被数据支持的信号，还是置信度。**
    /// </summary>
    private const double MinimumAcceptableConfidence = 0.78;

    /// <summary>
    /// 四路二值图的顺序说明，只用于把日志写成人能看懂的话。
    /// 顺序必须和 ImagePreprocessor.PrepareAllPlanes 的返回顺序一致。
    /// </summary>
    private static readonly string[] PlaneNames = ["红通道", "绿通道", "蓝通道", "灰度", "局部阈值"];

    public string ChannelName => "WindowsOcr";

    public bool IsAvailable => OcrEngineFactory.IsAvailable;

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        long start = Environment.TickCount64;


        // 区域太小就别白费工夫截图识别了
        if (request.WidthPixels < 8 || request.HeightPixels < 8)
        {
            return Fail(CaptureFailure.RegionTooSmall, start,
                $"选区 {request.WidthPixels}×{request.HeightPixels} 太小");
        }

        // 1) 截图
        using Bitmap? captured = ScreenCaptureService.TryCapture(request.PhysicalRect);
        if (captured is null)
        {
            return Fail(CaptureFailure.ScreenshotFailed, start, "截图失败");
        }

        // ==========================================================
        // 【2026-10-10 材料 3 的 C1：深色模式要在这里就整张翻过来】
        //
        //   C1 是 `background:#1e1e2e; color:#e8e8f0;`——**深底浅字**，
        //   实战里就是**深色模式的界面**，非常常见。
        //
        //   【上一轮修错了地方】
        //     当时是在「四路」里加了第五路「反色」，
        //     结果**两次都返回 0.00 空**——白跑一趟，还多花几十毫秒。
        //     原因是反色发生得太晚：那之前 `PadAround` **已经先补了一圈白边**，
        //     白边翻过来变成**黑边**，Otsu 被这条黑边带偏，直接废掉。
        //
        //   【这一次在哪儿翻】
        //     **一拿到图就翻。** 这样后面每一个环节拿到的都是一张正常的浅底图：
        //       · `PadAround` 补的白边是白边 ✅
        //       · Otsu 二值化正常 ✅
        //       · 红/绿/蓝/灰四路正常 ✅
        //       · **Windows 自带引擎也受益**（C1 那一轮 Windows 认成
        //         `cnwerts`，根子也在深底上）
        //       · 「切到半个字」的检测也正常（它按「暗的才算笔画」判断，
        //         深色背景上会**每一列都判成有笔画**，全是误报）
        //
        //   【判断深底还是浅底，不拍阈值，看平均亮度】
        //     图上文字通常只占一小块，**平均亮度基本就是底色**。
        //     这是数据本身的性质，不是拍脑袋定的数。
        // ==========================================================
        using Bitmap? raw = ImagePreprocessor.MakeLightBackground(captured);

        // ==========================================================
        // 【2026-10-10】把「底色判断」这件事本身的数字打进日志，
        // 并且在判为深底时**自动把原图和反色图都存成 PNG**。
        //
        //   【为什么不再依赖 Ctrl+Alt+D】
        //     那个快捷键连续两轮都没能把图存下来，光靠它已经拖了三轮了。
        //     调试阶段最怕的就是「查不动」——
        //     **直接把现场存下来，比再猜一轮快得多。**
        //
        //   【存哪些图】
        //     只在**判为深底**时存，也就是 C1 这种情况，一轮也就一两张。
        //     图很小（几百×几十），存到程序目录 `debug-screenshots`。
        //     每次重新构建都会清空这个文件夹，不留垃圾。
        // ==========================================================
        //   【2026-10-10 材料 3 满贯后关掉自动存图】
        //     这套自动存图是为了查 C1 的深色模式才加的（那时候连着四轮查不出根因）。
        //     现在 C1 已经修好（3/3、置信度 0.95），
        //     **不该再让正式版往发布目录里扔 PNG**——
        //     那是给开发者看的东西，不该出现在用户拿到的软件里。
        //
        //     **要查问题时的办法没变**：`App.xaml.cs` 里那个 `Ctrl+Alt+D` 仍然可用，
        //     框完选区按一下，那一块区域就会存成 PNG。
        // ======================================================================
        double brightness = ImagePreprocessor.MeasureAverageLuminance(captured);
        if (brightness < 110)
        {
            AppLogger.Info($"这一段是深底浅字（深色模式）：平均亮度 {brightness:F1}（低于 110），" +
                           "已整张反色后再识别");
        }
        else
        {
            AppLogger.Info($"这一段是浅底（平均亮度 {brightness:F1}），不反色");
        }


        // 2) 第一遍：只转灰度，不放大
        //    【为什么先认一遍小的】对照实验实测：放大对中文明显变准，
        //    但会把英文单词劈开（「quick b rown fox」）。
        //    先用原生大小认一遍看看是什么语种，再决定要不要放大重认。
        long preprocessStart = Environment.TickCount64;
        Bitmap plain;
        try
        {
            plain = ImagePreprocessor.ToGrayscaleOnly(raw);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"预处理失败：{ex.GetType().Name} {ex.Message}");
            return Fail(CaptureFailure.ScreenshotFailed, start, "图像处理失败");
        }

        AppLogger.Info($"预处理耗时 {Environment.TickCount64 - preprocessStart} 毫秒（第一遍只转灰度）");

        if (cancellationToken.IsCancellationRequested)
        {
            plain.Dispose();
            return Fail(CaptureFailure.Canceled, start, "用户取消");
        }

        // 3) 拿引擎（带缓存）
        //    引擎固定「有中文就用中文」——中文引擎也认英文，
        //    见 OcrEngineFactory 里那次实测的教训。
        if (!OcrEngineFactory.TryGetEngine(preferChinese: true, out var engine) || engine is null)
        {
            plain.Dispose();
            return Fail(CaptureFailure.OcrUnavailable, start, "OCR 引擎不可用");
        }

        try
        {
            // 4) 第一遍识别（原生大小）
            var first = await RecognizeOnce(engine, plain, cancellationToken).ConfigureAwait(false);
            AppLogger.Info($"第一遍完成：{first.LineCount} 行，" +
                           $"{LanguageDetector.Describe(first.Text)}，" +
                           $"有效字符 {TextNormalizer.CountMeaningfulChars(first.Text)} 个");
            // 把识别到的原文记进日志：不然「读不全」这类问题根本没法定位
            AppLogger.Info($"第一遍原文：{Preview(first.Text)}");

            // 5) 要不要再放大重认一遍？两种情况：
            //
            //    ① **第一遍什么都没认出来** → 必须重认。
                //       实测（深色背景的小字）：不放大时返回 0 行，放大后能认出 29 个字。
                //       这是「认不全」最常见的一种，必须兜住。
                //    ② **判定是中文** → 重认。实测放大后中文错字明显减少。
                //
                //    英文且认出了东西 → 就用第一遍。
                //       因为放大反而会把英文单词劈开（"quick b rown fox"）。
            //
            //    【实测依据】tools\日志\阶段3-验收.md 的对照实验：
            //      · 深色背景小字：不放大 0 字，放大后 29 字；
            //      · 浅色背景中文：不放大错字多，放大后明显变准；
            //      · 英文：不放大干净，放大后单词被劈开。
            //    所以「放大」是**找回漏字**的手段，代价是可能切坏英文单词，
            //    因此只在上面两种情况下才用。
            Bitmap? upscaled = null;
            try
            {
                string text = first.Text;
                int lineCount = first.LineCount;
                long ocrMs = first.OcrMs;
                string note = "第一遍结果（未放大）";

                int firstChars = TextNormalizer.CountMeaningfulChars(first.Text);
                bool shouldUpscale = firstChars < 2 || LanguageDetector.IsMostlyChinese(first.Text);

                if (shouldUpscale)
                {
                    preprocessStart = Environment.TickCount64;

                    // 【2026-10-08 改动：第二遍改成「放大 2 倍 + 二值化」】
                    //   原来只做了「放大 2 倍 + 灰度」。加了 Otsu 二值化之后，
                    //   实测准确率整体从 73.6 提到 77.2 分，而且更快
                    //   （阈值划分是纯整数运算，比再放大更划算）。
                    //   数据见 ImagePreprocessor.PreprocessAndBinarize 的注释。
                    upscaled = ImagePreprocessor.PreprocessAndBinarize(raw);
                    AppLogger.Info($"重认一遍（放大 2 倍并二值化，耗时 {Environment.TickCount64 - preprocessStart} 毫秒）：" +
                                   $"第一遍有效字符 {firstChars} 个，判定中文={LanguageDetector.IsMostlyChinese(first.Text)}");

                    var second = await RecognizeOnce(engine, upscaled, cancellationToken).ConfigureAwait(false);
                    int secondChars = TextNormalizer.CountMeaningfulChars(second.Text);

                    // 【2026-10-08 改动：择优判据改了两次，最终结论记在这里】
                    //
                    //   ① 原来判据是「第二遍字多就用第二遍」。
                    //      硬伤：**认错得离谱的结果往往字更多**。
                    //      「2 ik ES 28 BS HER」有 13 个有效字符，比正确的
                    //      「鑫淼懿囍」还「多」，于是错的赢了对的。
                    //
                    //   ② 改成「谁分高谁上」（OcrQualityScorer）。
                    //      实测发现**还是不够**：小字时两遍字数一样多、
                    //      空格分布也一样，分数打平，
                    //      而「打平就保留第一遍」恰好保留了更差的那版
                    //      （14px：原样 54.0 分，二值化版 64.7 分）。
                    //      根子在于：**没有标准答案时，长度和排版根本反映不出
                    //      「不错」被认成「错」这种替换错误。**
                    //
                    //   ③ 最终判据：**默认信第二遍，只有第二遍明显坏了才退回第一遍。**
                    //      这条是有数据撑的，不是拍脑袋——
                    //      对照实验里「放大2倍+二值化」在三个字号上**全部**赢「原样」：
                    //          14px：65.0 vs 54.0
                    //          18px：83.3 vs 57.3
                    //          28px：83.3 vs 79.2
                    //      既然它在所有情况下都更好，就不该再去猜哪遍更好，
                    //      猜错反而更糟（第 ② 条就是活生生的例子）。
                    //
                    //   「明显坏了」的判断有三道闸，任一触发就退回第一遍：
                    //      · 第二遍读不出有效文字（空、纯符号、乱码）；
                    //      · 第二遍的字数明显少于第一遍（多半是图处理过头了）；
                    //      · 第二遍的「像不像人话」分数被第一遍甩开一大截。
                    int firstScore = OcrQualityScorer.Score(first.Text);
                    int secondScore = OcrQualityScorer.Score(second.Text);
                    AppLogger.Info($"两遍比分：原图 {firstScore} 分（{firstChars} 字），" +
                                   $"二值化版 {secondScore} 分（{secondChars} 字）");

                    bool secondIsGarbage = TextNormalizer.LooksLikeGarbage(second.Text)
                                           || secondChars < 2;
                    bool secondLostTooMuch = secondChars * 2 < firstChars;   // 字少一半以上
                    bool secondScoredMuchWorse = secondScore + 30 < firstScore;

                    if (secondIsGarbage || secondLostTooMuch || secondScoredMuchWorse)
                    {
                        string why = secondIsGarbage ? "第二遍读不出有效文字"
                                  : secondLostTooMuch ? $"第二遍字太少（{secondChars} < 一半的 {firstChars}）"
                                  : $"第二遍分数差太多（{secondScore} vs {firstScore}）";
                        note = $"{why}，保留第一遍";
                        AppLogger.Info($"退回第一遍：{why}");
                    }
                    else
                    {
                        text = second.Text;
                        lineCount = second.LineCount;
                        note = "第二遍结果（放大 2 倍 + 二值化）";
                        AppLogger.Info("采用第二遍（对照实验显示二值化版在所有字号上都优于原图）");
                    }

                    ocrMs += second.OcrMs;
                }

                AppLogger.Info($"OCR 最终采用：{note}，两遍合计识别耗时 {ocrMs} 毫秒");

                // 【下面两个变量必须在跑备用引擎之前就声明好】
                //   channelUsed：最后到底是哪个引擎认的（界面上要如实显示，不能张冠李戴）；
                //   scoreNote  ：两个引擎的比分（没开备用引擎就一直是空字符串，不显示）。
                string channelUsed = ChannelName;
                string scoreNote = "";

                                // ==========================================================
                // 【2026-10-09 引擎分工 —— 整个阶段最重要的一次改动】
                //
                //   用户说明真实用途是**主要翻英文和俄文**，中文差点可以接受。
                //   重跑对照实验（英文/俄文用已知原文逐字打分）结果完全反过来：
                //
                //     引擎                      英文平均分   俄文        耗时
                //     Tess-fast（原样）           94.7       100 分 ✅   ~210ms
                //     Windows 英文引擎            63.3        装不上 ❌   ~20ms
                //     Windows 中文引擎（曾上线）   58.9        装不上 ❌   ~17ms
                //
                //   两件事：
                //   ① 英文差 36 分——不是「还能优化」，是「不能用」的区别；
                //   ② **俄文本机根本没装 Windows 识别包**（ru-RU 引擎创建失败），
                //      俄文只有 Tesseract 这一条路。
                //
                //   所以分工是：**中文走 Windows 自带（快、中文实测够好），
                //   英文和俄文走 Tesseract。**
                //
                //   【为什么靠「先认一遍再看是什么语种」来决定】
                //     截图之前我们根本不知道框到的是什么语言，
                //     而语种只有认完之后才判断得出来。
                //     所以顺序是：Windows 快速认一遍（10~30ms）→ 判断语种
                //     → 不是中文才补跑 Tesseract（多花 ~210ms）。
                //     **中文那一路完全不受影响，不会变慢。**
                //
                //   【为什么不是「两版比分高谁上」】
                //     之前是这么做的，但数据表明两者差距太大（58.9 vs 94.7），
                //     评分器完全没有必要参与——直接按语种定就行，
                //     规则更简单、行为更可预期、日志也更好读。
                //
                // ==========================================================
                // 【2026-10-09 用户实测后加的一条：彩色底上的中文也不信 Windows】
                //
                //   Z1（橙底绿字的 `今天天气不错适合出门`）在两轮之间来回跳：
                //     有一轮走 chi_sim，认得**逐字全对**；
                //     有一轮走 Windows，认出 `今天天气个台」`——错了三个字还多个引号。
                //   同一张图、同一段文字，**Windows 自带引擎时灵时不灵**。
                //
                //   规律：**只有彩色底上的中文会这样**。
                //     白底中文（Z2、Z3）每一轮都稳稳的；
                //     外文（E3、R3、R9）本来就是彩色底，
                //       但它们走的是外文那条路，本来就不用 Windows 认中文，也不受影响。
                //
                //   所以**彩色底上的中文也一起交给下面那条路**，
                //   那里已经有 chi_sim 在「最能分开底色」的那张图上等着了。
                //
                //   【但要注意：中文进去之后不要再去试外文模型】
                //   「底色是彩色的」只说明**不能全信 Windows**，
                //   **并不说明这是外文**。Z1、Z2 都还是中文。
                //   实测 Z2（斜条纹底）走了完整条外文流程：
                //     eng 109ms + rus 110ms + rus+eng 187ms + 四路 190ms + 红通道 62ms
                //     = 白花 560 毫秒，全是对中文毫无用处的模型，
                //       最后还是靠 chi_sim 认对的，**总耗时 1093 毫秒**。
                //   所以：**Windows 已经断定是中文时，直接跳到中文那一支。**
                //
                //   影响范围：只影响「彩色底上的中文」，其它一律不动。
                // ==========================================================
                bool windowsSaysChinese = LanguageDetector.IsMostlyChinese(text);
                bool colorful = ImagePreprocessor.IsColorfulBackground(raw);

                // ==========================================================
                // 【2026-10-10】这次是不是「认出来了但太像乱码」？
                //   是的话，**返回「未识别到文字」，不要退回 Windows 的结果**。
                //
                //   以前这里是「保留 Windows 自带」。改掉的依据是实测：
                //   凡是 Tesseract 掉到门槛以下的那几次，Windows 给的**同样是乱的**——
                //     Tesseract `Масппе {гап|айоп сопуей$ {ех…`  0.70
                //     Windows   `@aIiJI config.txt He HaIIÅeH`    全乱
                //   **退回去等于没挡**，用户照样看到一堆乱码。
                //
                //   用户的要求原话：「就不能识别出乱码的时候返回未识别到文字吗」——
                //   宁可说没认出来，也不要给错的。
                // ==========================================================
                bool rejectedAsGarbage = false;

                if (!windowsSaysChinese || colorful)
                {
                    TesseractOcr.EnsureInitialized();

                    if (TesseractOcr.IsAvailable)
                    {
                        // ==========================================================
                        // 【2026-10-09 用户实测后改：两种模型都试，让置信度决定】
                        //
                        //   上一轮改成「按语种只挂一种模型」之后，
                        //   实测日志显示 **`rus` 一次都没被调用过**，全部走的 `eng`。
                        //
                        //   原因：选模型要看语种，而语种判断读的是 **Windows 自带的结果**。
                        //   Windows **没装俄文语言包**，读俄文只会吐一串拉丁乱码
                        //   （`CbeLUb 3T × M`），判断器一看「拉丁字母多」就判成英文，
                        //   于是永远选 `eng` ——**那个修复对俄文等于没做**。
                        //
                        //   【现在的做法：不猜，让引擎自己说】
                        //   先用 `eng` 认一遍，看它自己的置信度：
                        //     够高 → 就是英文，直接用，不再多花时间；
                        //     偏低 → 那多半不是英文，再拿 `rus` 认一遍，谁高用谁。
                        //
                        //   为什么置信度可信（同一批实测数据）：
                        //     真英文配 eng → **0.9**
                        //     俄文配 eng   → **0.1 ~ 0.4**
                        //   差得非常干净，而且是引擎自评，不靠任何猜测。
                        //
                        //   西里尔 С 与拉丁 C 完全同形，图像上分不出来，
                        //   引擎唯一的依据就是「我现在在读哪种语言」——
                        //   所以**必须真的把俄文图交给 rus 模型**，它才认得出。
                        // ==========================================================
                        // ==========================================================
                        // 【2026-10-09 用户定方向一：外文统一放大 4 倍】
                        //
                        //   Tesseract 要求 x 高度至少 30 像素才认得准，
                        //   而取词框出来的小字（验收材料正文 15px）只有 7~8 像素，
                        //   放大 2 倍也才 15px，**不够**。
                        //
                        //   字太小直接导致两个问题：
                        //     E5  `see p.42.` → `see p42.`（句点被当噪点滤掉）
                        //     R8  `config.txt` → `сопйддхе`（细笔画挤在一起认错）
                        //
                        //   **放在所有外文识别之前放大一次**，
                        //   下面 eng / rus / rus+eng / 四路 / 红通道 全都共用这一张——
                        //   E5 当初就是从原图那一路认出来的（置信度够高，
                        //   根本走不到四路那一步），所以**光改四路没用**。
                        //
                        //   中文不受影响：中文走 Windows 自带引擎，仍然是 2 倍。
                        // ==========================================================
                        using Bitmap foreignImage = ImagePreprocessor.UpscaleForForeign(raw);

                        // Windows 已经断定「这是中文」时（彩色底上的中文），
                        // **外文模型一个都不用试**：
                        //   对中文内容它们只会给出低分乱码，
                        //   实测白花 500 多毫秒还把 chi_sim 的机会挤掉了。
                        // 直接给一个空的初始结果，后面自然一路走到中文兜底。
                        bool tryForeignModels = !windowsSaysChinese;

                        var engResult = tryForeignModels
                            ? TesseractOcr.TryRecognize(foreignImage, TesseractOcr.Languages.English)
                            : new TesseractOcr.Result(null, 0);
                        var best = engResult;
                        string modelUsed = TesseractOcr.Languages.English;

                        // 英文模型没把握时才启用俄文模型，
                        // 这样**纯英文场景不会多付那 60~150 毫秒**。
                        if (tryForeignModels && engResult.Confidence < GoodConfidence)
                        {
                            var rusResult = TesseractOcr.TryRecognize(foreignImage, TesseractOcr.Languages.Russian);

                            // ==========================================================
                            // 【2026-10-09 加这道保险】
                            //   只看置信度是不够的：俄文模型在**认不出东西**的时候，
                            //   同样会给出一个不算低的置信度。实测里就出现过
                            //   俄文模型 0.50 压过英文模型 0.37 的情况——
                            //   而那次的实际内容是英文，
                            //   真让俄文模型赢，出来的是一串乱码。
                            //
                            //   **「俄文模型认出来的字里，得真有俄文字母」**
                            //   才允许它上位。这道判断不依赖置信度，
                            //   是最直接的事实：认不出俄文，却交出一堆拉丁字母，
                            //   说明这个模型根本没用。
                            // ==========================================================
                            // 【2026-10-09 放大到 4 倍之后，把门槛从 0.10 提到 0.50】
                            //
                            //   放大之后 `rus+eng` 的分数普遍涨了 0.03~0.07，
                            //   于是又冒出「错但分高」的赢家：
                            //     E3（**英文**）被 rus+eng 以 0.92 顶掉 eng 的 0.91，
                            //   结果邮箱被拼成了西里尔：
                            //     `e-mail me@example.com` → `e-mail те@ехатре.сот`
                            //
                            //   【这次不是猜，是有数据】
                            //   把那一轮每段的西里尔占比拉出来看：
                            //     认对的俄文：R7 87%、R8 78%、R9 90%、其余 100%
                            //     认错的 E3：**20%**
                            //   → **正确的一律在 78% 以上，错误的那个只有 20%，中间一片空白。**
                            //   0.50 这条线卡在空白正中间，怎么选都不会碰到边。
                            //
                            //   道理也说得通：真正的俄文句子字母绝大多数是西里尔；
                            //   而「把英文拼成西里尔」的产物，
                            //   西里尔只集中在被误认的那几个词里，占比自然很低。
                            //   **0.10 这种松门槛根本拦不住它。**
                            // ==========================================================
                            bool rusReallyHasCyrillic =
                                LanguageDetector.CountCyrillicRatio(rusResult.Text) > MinCyrillicRatioForRussian;

                            if (rusReallyHasCyrillic && rusResult.Confidence > best.Confidence)
                            {
                                best = rusResult;
                                modelUsed = TesseractOcr.Languages.Russian;
                            }
                            else if (!rusReallyHasCyrillic)
                            {
                                AppLogger.Info(
                                    $"俄文模型置信度 {rusResult.Confidence:F2} 虽高，" +
                                    "但结果里几乎没有西里尔字母，判定为用错模型，不采用");
                            }
                        }

                        // ==========================================================
                        // 【2026-10-09 用户点名要修的：俄文里夹数字/英文】
                        //
                        //   用户反馈：「俄文混数字可能也会有问题，但这是避不开的问题，得修」。
                        //
                        //   实际案例（R3）：
                        //     原文 `Ошибка 404 (страница не найдена): файл не найден`
                        //     认成 `Ошибка И (орамицьне найдены) Файл цемайлеи`
                        //   —— `404` 整个没了，只剩一个像「И」的字符。
                        //
                        //   【为什么纯 `rus` 模型不够】
                        //   `rus` 是**只按俄文字母表训练**的，
                        //   数字和拉丁字母不在它的强项里，
                        //   碰上 `404`、`config.txt`、`2.0` 这类片段就容易糊。
                        //
                        //   【为什么不干脆一直用 rus+eng】
                        //   上一轮吃过亏：三种语言一起挂时，引擎在几套字表之间摇摆，
                        //   西里尔 С 被当成拉丁 C，`Съешь` 变成 `Cbeuub`。
                        //   **但 `rus+eng` 的顺序不一样——俄文排在前面**，
                        //   理论上还是以俄文字表为主。
                        //
                        //   【为什么不干脆一直用 rus+eng】
                        //   上一轮吃过亏：三种语言一起挂时，引擎在几套字表之间摇摆，
                        //   西里尔 С 被当成拉丁 C，`Съешь` 变成 `Cbeuub`。
                        //   **但 `rus+eng` 的顺序不一样——俄文排在前面**，
                        //   理论上还是以俄文字表为主。
                        //
                        // ==========================================================
                        // 【2026-10-09 用户反馈后改的判断依据：不再比分，看「有没有保住英文」】
                        //
                        //   用户指出的问题：R8 的 `config.txt` 被认成 `сопйддхе`——
                        //   纯俄文表不认识拉丁字母，就硬把它们拼成了西里尔，
                        //   结果是**一个根本不存在的俄文单词**，比漏字更糟。
                        //
                        //   【原来按什么判断？错的】
                        //   原来靠「谁置信度高用谁」。但实测两次都是 **0.81 对 0.81**，
                        //   **根本分不出高下**，判据等于没有。
                        //   而且上一轮把门槛从 0.75 放宽到 0.90 想让它多跑几次，
                        //   结果 E5 英文被换成了 `ВАМ`、`p42` 变成 `р.42`——
                        //   **放宽门槛让更差的结果赢了，把已经对的地方弄坏了。**
                        //
                        //   【现在按什么判断？看得见的证据】
                        //   看**谁把原文里的拉丁字母保住了**：
                        //     纯 `rus` 的结果里一个拉丁字母都没有，
                        //       说明它根本没认这些字母，是乱拼的（`config.txt`→`сопйддхе`）；
                        //     `rus+eng` 的结果里 `config.txt` 原样保住了，那才是对的。
                        //
                        //   这么判的好处：
                        //     认得好的纯俄文（实测 0.81~0.93），两个模型都保住 0 个拉丁字母，
                        //       **结果一样，谁也不换，已经对的地方一根汗毛都不会动**；
                        //     不依赖分数，不用调门槛，**不会再出现「差 0.02 就换了」这种事**。
                        // ==========================================================
                        if (tryForeignModels && best.Confidence < GoodConfidenceForRescue)
                        {
                            var rusEngResult = TesseractOcr.TryRecognize(foreignImage, TesseractOcr.Languages.RussianPlusEnglish);

                            bool rusEngKeepsLatin = LanguageDetector.CountLatinRatio(rusEngResult.Text) >
                                                   LanguageDetector.CountLatinRatio(best.Text);
                            bool stillRussian = LanguageDetector.CountCyrillicRatio(rusEngResult.Text) > MinCyrillicRatioForRussian;
                            bool notWorse = rusEngResult.Confidence >= best.Confidence - 0.08;

                            if (stillRussian && rusEngKeepsLatin && notWorse)
                            {
                                best = rusEngResult;
                                modelUsed = TesseractOcr.Languages.RussianPlusEnglish;
                                AppLogger.Info(
                                    $"俄英混合模型保住了原文里的拉丁字母（置信度 {rusEngResult.Confidence:F2}），改用它");
                            }
                        }

                        // ==========================================================
                        // 【2026-10-09 用户实测后加：彩色底再认一遍，让置信度决定】
                        //
                        //   上一轮英文俄文在浅色底上已经很好了（置信度 0.82~0.95），
                        //   但**橙底绿字**那一类彻底崩了：
                        //     E3 → `irae WA Touroune atietiionssainot round thy...`
                        //     R3 → `Ошибка И (орамицьне найдены) Файл цемайлеи`
                        //
                        //   根因不在引擎，在**灰度化**这一步：
                        //     橙底 #ff8c00 灰度后约 158，绿字 #00e676 灰度后约 148，
                        //     **只差 10**——字和底在灰度图里几乎一样亮。
                        //     而红通道上它们一个 255、一个 0，差得干干净净。
                        //     详见 ImagePreprocessor.PrepareForForeignOcr 的说明。
                        //
                        //   【怎么用，才不至于把已经认对的也弄坏】
                        //   **不直接换成处理过的图**，而是让原图和处理图各认一遍，
                        //   谁置信度高用谁——和上面选语言是同一个套路。
                        //   实测置信度能把「认对了」和「在瞎猜」分得很开
                        //   （0.82~0.95 对 0.17~0.40），这个判断已经过用户实测验证。
                        // ==========================================================
                        bool usedPrepared = false;

                        // 【只在没把握时才花这个时间】
                        //   置信度已经够高的（英文、浅色底的俄文都在 0.8 以上）
                        //   直接跳过五路，不额外花那几百毫秒。
                        //   五路全交给引擎认，用它自报的置信度选——
                        //   和选语言、选模型是同一个套路，那个已经被实测验证过了。
                        //
                        // 【2026-10-10 C4：门槛从 0.75 提到 0.90】
                        //   原来这行是 `best.Confidence < GoodConfidence`（0.75）。
                        //   **C4 是 0.82 —— 比 0.75 高，所以四路根本没跑**，C5 那一路
                        //   自然也就没机会上场。
                        //
                        //   改成 0.90 之后，**实际会新增五路识别的只有这些**：
                        //     C4  0.82   ← 目标
                        //     X1  0.86~0.88
                        //   剩下的全在 0.93 以上，仍然跳过：
                        //     F1~F8 / S1~S4 / M1 / M2 / X2 / X3 / P1 / P3 / C1 / C2 / C3  ≥ 0.90
                        //   **所以这次改动的影响面是可控的**：多花几十毫秒的只有
                        //   「认得不太利落」的那两类，而这正是需要多试几路的。
                        if (tryForeignModels && best.Confidence < 0.90)
                        {
                            Bitmap[] planes = ImagePreprocessor.PrepareAllPlanes(foreignImage);
                            try
                            {
                                // 记住四路里谁最能认字：
                                // **下面中文兜底还要在这张图上再试一次**，所以不能随手就释放。
                                Bitmap? bestPlane = null;

                                for (int i = 0; i < planes.Length; i++)
                                {
                                    var planeResult = TesseractOcr.TryRecognize(planes[i], modelUsed);

                                    // ==========================================================
                                    // 【只加日志，不改行为 · 2026-10-09】
                                    //   R9 的 `позже` 为什么丢，猜了三次全错
                                    //   （换找行方式、放宽门槛、补白边，三次都把别处弄坏）。
                                    //   **猜不出来就别猜，把四路各自认出了什么打出来看。**
                                    //   下轮拿到日志就能直接回答：
                                    //     是不是别的路认对了、就因为分数低被扔掉了？
                                    //     还是四路都少了那两个字（那就得从图像本身找原因）？
                                    // ==========================================================
                                    AppLogger.Info(
                                        $"  四路之一「{PlaneNames[i]}」：置信度 {planeResult.Confidence:F2}，" +
                                        $"内容【{(planeResult.Text ?? "").Replace("\n", " ")}】");

                                    // ==========================================================
                                    // 【2026-10-09 F5 整段认错之后改：四路择优也用帕累托规则】
                                    //
                                    //   F5（Calibri）那一轮本来是**逐字全对**的，
                                    //   这一轮却整段变成了 `Le os . quick row fox Jump`。
                                    //   日志把现场完整记下来了：
                                    //
                                    //     eng（原始放大图）  0.60 / 55 字
                                    //     rus+eng            0.64 / 46 字
                                    //     四路「蓝通道」      0.78 / **62 字**
                                    //     → 「蓝通道这一路置信度更高，改用它」
                                    //     结果：Le os .⏎quick row fox Jump
                                    //
                                    //   **两个错叠在一起**：
                                    //     ① 只比分数（0.78 > 0.60 就换），完全没看字数；
                                    //     ② **那 62 个「字」里有一大半是空格**——
                                    //        Tesseract 二值化失败时会吐一堆空格撑场面，
                                    //        而这里比的是**没清洗过的原始输出长度**。
                                    //   两件事合起来 = 「空格多」被当成了「内容多」。
                                    //
                                    //   【现在用的规矩：帕累托，两项都不能更差】
                                    //     只有「**有效字符更多**，或者**有效字符不更少、
                                    //     而且分数确实更高**」才换。
                                    //   跟三十四轮给「红通道不二值化」定的、以及
                                    //   三十七轮给「eng 在四路胜出的图上」定的，
                                    //   **是同一条规矩**。
                                    //
                                    //   F5 套这条规矩立刻不换了（19 字 < 55 字）：
                                    //   **别的一分没改，就把 Calibri 整段认错的问题挡掉了。**
                                    // ==========================================================
                                    int planeChars = TextNormalizer.CountMeaningfulChars(
                                        TextNormalizer.Normalize(planeResult.Text ?? ""));
                                    int bestChars = TextNormalizer.CountMeaningfulChars(
                                        TextNormalizer.Normalize(best.Text ?? ""));

                                    bool planeClearlyBetter =
                                        planeChars > bestChars
                                        || (planeChars >= bestChars
                                            && planeResult.Confidence > best.Confidence);

                                    if (planeClearlyBetter)
                                    {
                                        AppLogger.Info(
                                            $"「{PlaneNames[i]}」这一路确实更好（分数 {planeResult.Confidence:F2}/{best.Confidence:F2}，" +
                                            $"有效字符 {planeChars}/{bestChars} 个），改用它");

                                        best = planeResult;
                                        usedPrepared = true;
                                        bestPlane = planes[i];
                                    }
                                    else if (planeResult.Confidence > best.Confidence)
                                    {
                                        // 分数高但**内容更少**：这种情况以前是直接换，
                                        // 换出来就是 F5 那种整段乱码。
                                        AppLogger.Info(
                                            $"「{PlaneNames[i]}」分数更高（{planeResult.Confidence:F2} > {best.Confidence:F2}）" +
                                            $"但有效字符更少（{planeChars} < {bestChars}），**不采用**");
                                    }
                                }

                                // ==========================================================
                                // 【2026-10-09 补：再试一次「红通道不二值化」的版本】
                                //
                                //   要解决的是 R9 末尾 `позже` 被吞：
                                //     四路**二值化**的结果全都少了它，
                                //     但历史上**没二值化的原图认得出来**。
                                //     → 问题是阈值切割切掉了那半边笔画。
                                //
                                //   这一版：取红通道转灰度、**不二值化**。
                                //     橙底 255、绿字 0，不切也已经分得很开；
                                //     不切就保住了原图的过渡像素。
                                //
                                //   【怎么决定用哪一版：不比分，看谁的内容更全】
                                //   前面几轮吃过太多亏了：
                                //     `0.89 打败 0.87` → 把已经认对的换错了；
                                //     加的候选越多，越容易混进「错但分高」的赢家。
                                //
                                //   所以这条规矩**不看置信度**，
                                //   只看一个能直接回答「有没有漏字」的事实：
                                //     **有效字符多出 15% 以上，才认为这一版更全，才换。**
                                //   字数一样 → 不换，认得准的那版留着（E3、R3 安全）；
                                //   字数明显更多 → 换（正是 R9 缺 `позже` 的情况）。
                                // ==========================================================
                                {
                                    using Bitmap redGray = ImagePreprocessor.PrepareRedChannelGray(foreignImage);
                                    var grayResult = TesseractOcr.TryRecognize(redGray, modelUsed);

                                    int newChars = TextNormalizer.CountMeaningfulChars(grayResult.Text);
                                    int oldChars = TextNormalizer.CountMeaningfulChars(best.Text);
                                    double oldConfidence = best.Confidence;

                                    AppLogger.Info(
                                        $"「红通道不二值化」这一版：置信度 {grayResult.Confidence:F2}，" +
                                        $"有效字符 {newChars} 个（上一版 {oldChars} 个），" +
                                        $"内容【{(grayResult.Text ?? "").Replace("\n", " ")}】");

                                    // ==========================================================
                                    // 【2026-10-09 用户实测后补：原来只有一条 15% 的规矩，把更好的结果丢了】
                                    //
                                    //   原来只有「有效字符多出 15% 以上才换」，
                                    //   是当初为 R9 末尾少一个词 `позже` 设的（约差 15%）。
                                    //
                                    //   **E3 实测直接把它打穿了**：
                                    //     红通道二值化  ：0.92，59 个有效字符，`me@example.com` 变西里尔
                                    //     红通道不二值化：0.94，60 个有效字符，内容完全正确
                                    //     → `60 >= (int)(59 * 1.15)` = `60 >= 67` → 不换 ❌
                                    //     **分数更高、字数也更全的候选，就这么被扔了。**
                                    //
                                    //   补一条**排在最前面**的规矩：
                                    //     **两项都不比原来差（分数没降、字数没少）→ 直接换。**
                                    //     这不是赌一把，因为**根本没有取舍**：
                                    //     分数是权衡项，字数是事实项，两个维度上全面更好，
                                    //     没有理由不用。只有**有得有失**时才需要权衡。
                                    //
                                    //   之前吃过的亏是另一种情况：
                                    //     `0.89 打败 0.87` —— **分数涨了、字数掉了**，那才叫取舍。
                                    //     新规矩管不到那种情况，不会把它带回来。
                                    //
                                    //   【字数多就一定更好吗？不一定】
                                    //   多出来的可能是胡话。所以两个条件必须**同时成立**，
                                    //   而且分数要求**不低于**原来那一版。
                                    // ==========================================================
                                    bool notWorseAtAll = newChars >= oldChars
                                                       && grayResult.Confidence >= oldConfidence;
                                    bool muchMore = oldChars > 0 && newChars >= (int)(oldChars * 1.15);
                                    bool notMuchWorse = grayResult.Confidence >= oldConfidence - 0.10;

                                    if (notWorseAtAll)
                                    {
                                        best = grayResult;
                                        usedPrepared = true;
                                        AppLogger.Info(
                                            $"这一版分数没降（{grayResult.Confidence:F2} ≥ {oldConfidence:F2}）" +
                                            $"且字数没少（{newChars} ≥ {oldChars}），两项都不差，改用它");
                                    }
                                    else if (muchMore && notMuchWorse)
                                    {
                                        best = grayResult;
                                        usedPrepared = true;
                                        AppLogger.Info(
                                            $"这一版内容明显更全（{newChars} > {oldChars} 个有效字符）且置信度相仿，改用它");
                                    }
                                }

                                // ==========================================================
                                // 【2026-10-09 补：中文模型也要在「能分开底色」的那张图上试】
                                //
                                //   Z1（`今天天气不错适合出门`，橙底绿字）被认成了 `| SVMs`：
                                //     底色橙、字绿，**只有红通道能把两者分开**，
                                //     而中文模型当时只试了原图（灰度），等于拿一张糊的图去认。
                                //
                                //   为什么必须放在这里（而不是更早或更晚）：
                                //     上面四路用的是**外文模型**，
                                //     对中文内容会「很自信地认错」——
                                //     `| SVMs` 只有几个字符，置信度却有 0.71，
                                //     轻轻松松压过中文模型在原图上的 0.16。
                                //
                                //   **认对的那一路和语种无关**：
                                //     哪一路能把底色和字分开，对中英文是一样的，
                                //     所以中文直接复用上面选出来的那一张就行。
                                //
                                //   【为什么这一道不能用 0.75 那个门槛（踩过的坑）】
                                //   Z1 只认出 `SVMs`（4 个字），置信度却有 **0.76**——
                                //   **比择优门槛 0.75 高了那么一点点**，
                                //   于是这道兜底根本没触发，四个模型全部错过。
                                //
                                //   **教训：置信度高，不代表内容对。**
                                //   图上只剩几根杂碎笔画时，Tesseract 会「很自信地」
                                //   把它认成几个像模像样的字，置信度自然就高。
                                //   所以**兜底的门槛要比择优的更高（更保守）**，
                                //   否则它永远在最该来的时候不来。
                                // ==========================================================
                                if (bestPlane is not null)
                                {
                                    // 中文兜底：在最能分开底色的那张图上试。
                                    // 这一道**保留 0.90 的门槛**——
                                    // 认对了的外文都在 0.81~0.95，门槛太高就不触发；
                                    // 而 Z1 那次恰恰是被原来的 0.75 挡掉的，所以不能更松。
                                    if (best.Confidence < GoodConfidenceForRescue)
                                    {
                                        var chiOnPlane = TesseractOcr.TryRecognize(bestPlane, TesseractOcr.Languages.Chinese);

                                        // ==========================================================
                                        // 【2026-10-09 C3 被认错之后补：中文模型的结果必须真的是中文】
                                        //
                                        //   C3 = `Error 404 (Not Found): the file was not found`
                                        //   **整段是纯英文**，橙底绿字。
                                        //
                                        //   这一轮日志把现场记得很清楚：
                                        //     chi_sim  0.86 → 「中文模型在这张图上更认得出来，改用它」
                                        //     eng      0.93 →【Error 404 (Not Found): the file was not found】
                                        //     最终却用了 chi_sim 的，把 `Found)` 认成了 `Foundj:`
                                        //
                                        //   **正确答案就在日志里，引擎已经算出来了，最后一步没选它。**
                                        //   而它之所以被挤掉，是因为这一道兜底**只比分数、不看内容是什么**。
                                        //
                                        //   【为什么要加这一条检查】
                                        //     chi_sim 是个**中文模型**，可是它照样会在英文图上吐东西
                                        //     （`(Not Found)` → `(Not Foundj)` 就是这么来的），
                                        //     而且它**还挺自信，0.86**。
                                        //     所以「分数高」根本不能证明「它是中文」。
                                        //
                                        //   【规矩】
                                        //     chi_sim 的结果**必须确实是中文**才有资格被采用。
                                        //     不是中文 → 老实说自己不适用，让后面的 eng 那一路去处理。
                                        //   （Z1 那次 chi_sim 给的确实是中文，这条不会挡它。）
                                        // ==========================================================
                                        string chiOnPlaneText = TextNormalizer.Normalize(chiOnPlane.Text ?? "");
                                        bool chiResultIsChinese = LanguageDetector.IsMostlyChinese(chiOnPlaneText);

                                        AppLogger.Info(
                                            $"中文兜底：chi_sim 置信度 {chiOnPlane.Confidence:F2}，" +
                                            $"是不是中文={chiResultIsChinese}，" +
                                            $"内容【{Preview(chiOnPlaneText)}】");

                                        if (chiOnPlane.Confidence > best.Confidence && chiResultIsChinese)
                                        {
                                            best = chiOnPlane;
                                            modelUsed = TesseractOcr.Languages.Chinese;
                                            AppLogger.Info(
                                                $"中文模型在这张图上更认得出来（{chiOnPlane.Confidence:F2}），改用它");
                                        }
                                        else if (!chiResultIsChinese)
                                        {
                                            AppLogger.Info(
                                                "中文模型给出的结果根本不是中文，这一路不采用" +
                                                "（下面的英文模型那一路会接着处理）");
                                        }
                                    }

                                    // ==========================================================
                                    // 【2026-10-09 用户追问「E3 明明是纯英文，为什么老被认成英文加俄文」后补】
                                    //
                                    //   【用户的原话】
                                    //     E3 = `Error 404 (Not Found): the file was not found - try /help or e-mail me@example.com`
                                    //     **纯英文 + 符号**，结果反复出现 `e-mail те@ехатре.сот`。
                                    //
                                    //   【日志查出来的原因，一步一步】
                                    //     ① 最先用 `eng` 跑**原图**，分数偏低（没到 0.75）
                                    //     ② 于是按规矩去试 `rus` → 再试 `rus+eng`
                                    //     ③ `rus+eng` 在 0.36 的低分上「保住了拉丁字母」，
                                    //        于是 **modelUsed 被锁定成 rus+eng**
                                    //        —— 这条规则只在 rus 和 rus+eng 之间比，
                                    //           **从头到尾没跟 eng 单独比过**
                                    //     ④ 四路彩色搜索**只用 modelUsed 这一个模型**，
                                    //        于是四路全在用 rus+eng 跑
                                    //     ⑤ 结果邮箱被拼成西里尔
                                    //
                                    //     **而同一个红通道，eng 跑出来是 0.94，`me@example.com` 完全正确。**
                                    //     证据在日志里（另一轮 eng 没有被锁掉时）：
                                    //       四路之一「红通道」：置信度 0.91，内容【… me@example.com】
                                    //
                                    //   【一句话：真正的坑是「模型一旦被选定就锁死」】
                                    //     原图上分数低，不代表那张图不行——
                                    //     **真正好用的是「红通道那张图」，而不是「原图」。**
                                    //     模型是在**原图**上比的，图却是**换了四路之后**才对的，
                                    //     两件事被混成一件了。
                                    //
                                    //   【怎么补】
                                    //     中文兜底已经有同款（见上），这里补英文的：
                                    //     四路里胜出的那张图上，**再让 eng 单独跑一次**。
                                    //     判据沿用中文兜底那一套：**分数更高就换**，
                                    //     门槛同样是 GoodConfidenceForRescue（0.90），
                                    //     保证「本来就没问题」的结果**一根汗毛都不会动**。
                                    //
                                    //   【代价】
                                    //     只在 modelUsed 不是 eng、且当前结果不到 0.90 时才多跑一次，
                                    //     实测多花 15~140 毫秒。认得对的段一律不触发。
                                    // ==========================================================
                                    if (bestPlane is not null
                                        && modelUsed != TesseractOcr.Languages.English
                                        && best.Confidence < GoodConfidenceForRescue)
                                    {
                                        var engOnPlane = TesseractOcr.TryRecognize(bestPlane, TesseractOcr.Languages.English);
                                        AppLogger.Info(
                                            $"英文模型在四路胜出的那张图上：置信度 {engOnPlane.Confidence:F2}，" +
                                            $"内容【{(engOnPlane.Text ?? "").Replace("\n", " ")}】");

                                        // ==========================================================
                                        // 【2026-10-09 用户反复实测 E3 十三次后收紧】
                                        //
                                        //   这条分支是上一轮新加的，目的是救 E3
                                        //   （原来 `me@example.com` 被拼成 `те@ехатре.сот`）。
                                        //   **救是救回来了**——13 次里 11 次逐字全对。
                                        //
                                        //   但实测也暴露了它自己的毛病：
                                        //     rus+eng 跑 10 次 → **10 次全对**，一个字母不差
                                        //     eng     跑  3 次 → 1 次全对、**2 次少最后一个字母**
                                        //                        `me@example.co`（59 个有效字符 vs 60）
                                        //   **这一分支是「本来 0 分 → 现在接近满分」的改善，
                                        //   但它又抢走了本来 rus+eng 能拿满分的机会。**
                                        //
                                        //   原来这条只要「分数更高」就换。E3 那两次是
                                        //     rus+eng  0.94 / 60 字  ← 正确
                                        //     eng      0.94 / 59 字  ← 分数**一样**，却因为
                                        //                                              `>` 的严格比较被换掉了 ❌
                                        //
                                        //   **分数一样还去换，就是拿噪声当信号。**
                                        //
                                        //   改成：**分数或字数必须真的更好才换**
                                        //     · 单词数更多 → 换
                                        //     · 单词数不短、且分数确实更高 → 换
                                        //     · 其余情况（尤其是**打平**）→ 保持不动
                                        //
                                        //   这跟前面「红通道不二值化」那条是同一个道理：
                                        //   **有取舍才需要权衡，打平就别折腾。**
                                        // ==========================================================
                                        int engChars = TextNormalizer.CountMeaningfulChars(engOnPlane.Text);
                                        int curChars = TextNormalizer.CountMeaningfulChars(best.Text);
                                        double curConfidence = best.Confidence;

                                        // ==========================================================
                                        // 【2026-10-10 X1 退步的根因：帕累托规则第三次被「字数多」骗了】
                                        //
                                        //   材料 3 完整验收那轮的现场，一行一行都对：
                                        //     五路跑到「局部阈值」→ 0.89
                                        //       内容【tik 404 (Not Found): файл не найден】  ← 俄文全对
                                        //     然后这一步拿 eng 在同一张图上又跑一遍 → **0.63**，
                                        //       只是有效字符 28 > 26，规则就把它换掉了
                                        //     → 0.63 低于 0.78 乱码门槛 → **退回 Windows 的乱码结果**
                                        //
                                        //   **0.89 的正确结果，被 0.63 的垃圾顶掉了。**
                                        //
                                        //   【为什么这一步不能用帕累托规则】
                                        //     这一步存在的意义是**保住俄文里夹着的英文单词**
                                        //     （`404`、`config.txt` 这种），
                                        //     **它不是来比赛的**——
                                        //     用「谁字数多谁赢」来衡量它，等于拿错了尺子。
                                        //
                                        //   【为什么不能直接删掉这个规则】
                                        //     它当初是为了救 Z1（中文压彩色底）加的，
                                        //     删了 Z1 会退步。**不能为了修一个段弄坏另一个段。**
                                        //
                                        //   【所以只挡一种情况：分数大幅退步】
                                        //     差 0.10 以上就不换。
                                        //     · X1：0.63 vs 0.89，差 0.26 → **不换** ✅
                                        //     · Z1 当初那种小幅领先 → 照样换，不会退步
                                        //     · 打平 → 不换（和 F5 那次的结论一致）
                                        // ==========================================================
                                        bool confidenceNotWorse = engOnPlane.Confidence + 0.10 > curConfidence;
                                        bool engClearlyBetter = confidenceNotWorse
                                                             && (engChars > curChars
                                                                 || engOnPlane.Confidence > curConfidence);

                                        if (engClearlyBetter)
                                        {
                                            best = engOnPlane;
                                            modelUsed = TesseractOcr.Languages.English;
                                            usedPrepared = true;
                                            AppLogger.Info(
                                                $"英文模型在这张图上确实更好（分数 {engOnPlane.Confidence:F2} / {curConfidence:F2}，" +
                                                $"有效字符 {engChars} / {curChars} 个），改用它");
                                        }
                                        else if (engChars > curChars)
                                        {
                                            AppLogger.Info(
                                                $"英文模型虽然字数多（{engChars} / {curChars}），" +
                                                $"但分数明显更低（{engOnPlane.Confidence:F2} / {curConfidence:F2}），" +
                                                $"保持原来那份——这一趟只是来保住英文单词的，不是来比赛的");
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                foreach (Bitmap b in planes) b?.Dispose();
                            }
                        }

                        // ==========================================================
                        // 【2026-10-09 补的一条兜底：中文压彩色底】
                        //
                        //   Z1（`今天天气不错适合出门`，橙底绿字）这次退步了：
                        //     上轮 `今天天气不台出门`（虽然错字，认出来了）
                        //     这轮 `IIA Ae tH I)`（置信度 0.09，纯乱码）
                        //
                        //   原因是：Windows 自带引擎把彩色中文认成了乱七八糟的符号，
                        //   被「符号幻觉」判断踢出中文通道，
                        //   于是掉进英文模型里，英文当然认不出中文。
                        //
                        //   这里补最后一道：**实在没把握就再拿中文模型试一次。**
                        //   Windows 自带的中文引擎在正常底色上又快又准（15~30 毫秒），
                        //   所以只在「前面全都没把握」时才轮到 Tesseract 的中文模型，
                        //   不影响平时的速度。
                        // ==========================================================
                        // 【2026-10-10 X1】chi_sim 在外文图上的结果，留作「中文仲裁」。
                        //   **声明必须在 if 外面**——只在低置信度时才认 chi_sim，
                        //   声明在里面的话，高置信度那次就拿不到仲裁文本了。
                        string chiForeignText = "";

                        if (best.Confidence < GoodConfidence)
                        {
                            var chiResult = TesseractOcr.TryRecognize(foreignImage, TesseractOcr.Languages.Chinese);
                            chiForeignText = chiResult.Text ?? "";

                            if (chiResult.Confidence > best.Confidence)
                            {
                                best = chiResult;
                                modelUsed = TesseractOcr.Languages.Chinese;
                            }
                        }

                        string? tessText = best.Text;

                        if (!string.IsNullOrWhiteSpace(tessText))
                        {
                            string cleanedTess = TextNormalizer.Normalize(tessText);
                            int tessChars = TextNormalizer.CountMeaningfulChars(cleanedTess);

                            // ==========================================================
                            // 【2026-10-09 实测后改：非中文时一律用 Tesseract】
                            //
                            //   原来这里有个保护：「Tesseract 认得更少就保留 Windows 自带」。
                            //   **这个判断是错的**，而且错得很贵——用户在实测里踩中了：
                            //
                            //     原文：HTTP/2.0 2024-05-01 API v3.1 - 85% CPU, RAM 16GB, ...
                            //     实际：HTTP/2.0 2024 一 05 一 01 API v 土 1 - 85 ％ CPU,
                            //           RAM 16G 巳 ID # A17f 一 9 ...        ← 只有 78 分
                            //     日志：「不是中文，但 Tesseract 认得更少（60 < 62），保留 Windows 自带」
                            //
                            //   把同一段文字拿去做对照实验：
                            //     Tess-fast/放大2倍  = **100 分**（逐字正确）
                            //     Windows 自带       = 78 分
                            //
                            //   **根因：中文引擎把符号认成了汉字**
                            //   （`-`→`一`、`3`→`土`、`B`→`巳`、`.`→`·`），
                            //   而这些**假汉字**在「有效字数」里和真汉字一样被计数，
                            //   于是字数虚高（62 > 60），把满分的结果挤掉了。
                            //
                            //   **「谁字多谁上」这个前提对外文根本不成立。**
                            //   整体数据也早就说明了：英文平均 94.7 vs 58.9，差 36 分。
                            //
                            // ==========================================================
                            // 【2026-10-09 v0.3.1 翻车后补的最后一道质量门槛】
                            //
                            //   v0.3.1 的全-Tesseract 版本**完全没有这道门槛**，
                            //   只要「有 2 个有效字符」就把结果给用户，
                            //   于是界面上出现过：
                            //     置信度 0.13 → `天沿轩这 :全人由国人 : 生 > 二河天 7`
                            //     置信度 0.32 → `ETT REIN Ee WE RY от = о) feel 11] о) 0)...`
                            //   用户评价是「一坨屎」。
                            //
                            //   【0.60 这个数来自实测数据的空档】
                            //     认对的：0.81 ~ 0.96
                            //     认错的：0.08 ~ 0.46
                            //     **中间 0.46~0.81 没有任何样本**，0.60 卡在正中间。
                            //
                            //   【为什么不用现成的 GoodConfidence(0.75)】
                            //   0.75 是「够不够自信、要不要再换个模型试试」的线；
                            //   这里问的是另一个问题——「**这东西要不要给用户看**」。
                            //   两件事该用不同的线。
                            //
                            //   【低于门槛怎么办】
                            //   老实说没认出来，走失败提示。
                            //   **乱码会被当原文送去翻译，没认出来用户一眼就知道该重选。**
                            // ==========================================================
                            if (tessChars >= 2 && best.Confidence >= MinimumAcceptableConfidence)
                            {
                                // ==========================================================
                                // 【2026-10-10 材料 3 的 X1：中英俄三语混排】
                                //
                                //   原文：`错误 404 (Not Found): файл не найден`
                                //   实测两个引擎是**互补**的：
                                //
                                //     Windows 第一遍：错误 404 (Not Found) / 巾 a H e H 烈 H
                                //       →「错误」和英文**认对了**
                                //       → 俄文因为系统没装 ru-RU 语言包，乱码成 `巾 a H e H 烈 H`
                                //
                                //     Tesseract rus+eng 0.86 → `#1 404 (Not Found): файл не найден`
                                //       → 俄文和英文**认对了**
                                //       → 中文「错误」被认成 `#1`
                                //
                                //   **原来的规则是「不是中文 → 整段丢掉 Windows」，
                                //   等于把认对了的「错误」一起扔了。**
                                //
                                //   【修法：把 Windows 认对的中文补回外文结果前面】
                                //     但**不能只看「Windows 有没有汉字」**——
                                //     上面那个 `烈` 也是汉字，它是俄文乱码的一部分，
                                //     照单全收会把 `烈` 塞进结果里，更糟。
                                //
                                //   【所以要两个引擎对得上才认】
                                //     chi_sim 在同一张图上也会读出「错误」。
                                //     **两边第一段汉字完全一致**，才说明这段中文是真的。
                                //     这是「双向验证」，比单看一边可靠得多。
                                //
                                //   【条件很严，误伤面很小】
                                //     要同时满足四条才补：
                                //       1. 外文结果里**一个汉字都没有**
                                //       2. Windows 的第一段汉字非空
                                //       3. chi_sim 的第一段汉字和 Windows **一模一样**
                                //       4. 两边这段汉字**都不是刚认出来的那个外文结果里已有的**
                                // ==========================================================
                                string windowsChineseRun = TextNormalizer.FirstChineseRun(text);
                                bool foreignHasNoChinese = !LanguageDetector.IsMostlyChinese(cleanedTess);

                                if (windowsChineseRun.Length > 0 && foreignHasNoChinese)
                                {
                                    // ==========================================================
                                    // 【这里现跑一次 chi_sim 当裁判】
                                    //
                                    //   **踩过的坑：不能复用上面那个 chi_sim。**
                                    //   上面那次挂在 `if (best.Confidence < GoodConfidence)` 里面，
                                    //   **只有置信度低于 0.75 才跑**。
                                    //   而 X1 的 rus+eng 是 **0.86** —— 条件不成立，
                                    //   **chi_sim 压根没跑**，没有裁判，合并一次都没触发。
                                    //
                                    //   **正确的触发条件是「有没有中文需要仲裁」，
                                    //   不是「置信度高不高」。**
                                    //   置信度高的外文结果里混着中文，恰恰是 X1 这种情况。
                                    //
                                    //   【代价】只在「Windows 有中文、外文结果一个汉字都没有」
                                    //   时才跑一次，约 15 毫秒。平时完全不触发。
                                    // ==========================================================
                                    if (chiForeignText.Length == 0)
                                    {
                                        // ==========================================================
                                        // 【2026-10-10 换图的关键：仲裁的 chi_sim 要用**中文路那张图**】
                                        //
                                        //   上一版这里用的是 `foreignImage`——**外文路那张放大 4 倍的图**。
                                        //   实测仲裁结果：
                                        //     Windows 的第一段汉字：错误   ← 对
                                        //     chi_sim 的第一段汉字：上误   ← 错，第一个字认成了「上」
                                        //     → 仲裁正确地拒绝合并，但这条路走不通
                                        //
                                        //   **为什么 chi_sim 会认错？**
                                        //     `foreignImage` 是**放大 4 倍**的（UpscaleForForeign）。
                                        //     而**中文路一直都是用 2 倍**的
                                        //     （`Preprocess` = 放大 2 倍 + 转灰度，见 1245 行）。
                                        //     **4 倍对中文模型是过犹不及**——
                        //     笔画被放得太粗、笔画之间的粘连没变，字间空隙被压掉，
                                        //     `错` 的金字旁和 `上` 就分不开了。
                                        //
                                        //   **换成中文路那张 2 倍图再仲裁一次。**
                                        //   这不是拍脑袋：中文路 2 倍的版本
                                        //   在 M1/M2/M3/X2/X3 上一直是全对的。
                                        // ==========================================================
                                        using Bitmap chineseArbImage = ImagePreprocessor.Preprocess(raw);
                                        var chiArb = TesseractOcr.TryRecognize(chineseArbImage, TesseractOcr.Languages.Chinese);
                                        chiForeignText = chiArb.Text ?? "";
                                        AppLogger.Info($"为仲裁中文补跑了一次 chi_sim（中文路 2 倍图，" +
                                                       $"置信度 {chiArb.Confidence:F2}）：{Preview(chiForeignText)}");
                                    }

                                    string chiChineseRun = TextNormalizer.FirstChineseRun(chiForeignText);
                                    AppLogger.Info($"仲裁：Windows 的第一段汉字「{windowsChineseRun}」，" +
                                                   $"chi_sim 的第一段汉字「{chiChineseRun}」" +
                                                   (windowsChineseRun == chiChineseRun ? "——一致" : "——不一致，不补"));

                                    if (windowsChineseRun == chiChineseRun
                                        && !LanguageDetector.IsMostlyChinese(text))
                                    {
                                        // ==========================================================
                                        // 【2026-10-10 关键：这里用「替换」，不是「往前加」】
                                        //
                                        //   上一版是 `windowsChineseRun + " " + cleanedTess`，
                                        //   实测出来是：
                                        //     错误 Hi 404 (Not Found): файл не найден
                                        //            ↑↑ **Tesseract 自己那份错的还在**
                                        //
                                        //   正确做法是**锚点对齐后把它换掉**：
                                        //     Windows 里中文段后面紧跟的词（`404`）当锚点，
                                        //     到 Tesseract 结果里找同一个锚点，
                                        //     **把锚点之前的全部内容换掉**。
                                        //
                                        //   锚点找不到、或者锚点太短，**一律不动**
                                        //   ——宁可不改，也不改错。
                                        // ==========================================================
                                        string spliced = TextNormalizer.SpliceLeadingChinese(text, cleanedTess);
                                        if (spliced != cleanedTess)
                                        {
                                            cleanedTess = spliced;
                                            AppLogger.Info($"两个引擎都认出了开头这段中文「{windowsChineseRun}」，" +
                                                           $"外文结果里一个汉字都没有，用它**替换**掉外文结果开头认错的那一段：" +
                                                           $"{Preview(cleanedTess)}");
                                        }
                                        else
                                        {
                                            AppLogger.Warn("两个引擎的中文一致，但找不到能对齐的锚点，这一轮不替换");
                                        }
                                    }
                                }

                                text = cleanedTess;
                                lineCount = TextNormalizer.CountLines(cleanedTess);
                                channelUsed = "Tesseract";
                                scoreNote = $"识别到的不是中文，改用 Tesseract（{modelUsed} 模型，置信度 {best.Confidence:F2}，{tessChars} 个有效字符{(usedPrepared ? "，用的是择优二值化后的图" : "")}）";
                            }
                            else if (tessChars >= 2)
                            {
                                // 以前这里是「保留 Windows 自带」，见 rejectedAsGarbage 的说明：
                                // 实测退回去给的也是乱的，所以改成直接报「未识别到文字」。
                                rejectedAsGarbage = true;
                                scoreNote = $"Tesseract 置信度只有 {best.Confidence:F2}" +
                                            $"（低于 {MinimumAcceptableConfidence:F2}，判定为乱码），不返回结果";
                            }
                            else
                            {
                                scoreNote = $"不是中文，但 Tesseract 几乎没认出内容（{tessChars} 个有效字符），保留 Windows 自带";
                            }
                        }
                        else
                        {
                            scoreNote = "不是中文，但 Tesseract 没认出内容，保留 Windows 自带";
                        }
                    }
                    else
                    {
                        scoreNote = "不是中文，但 Tesseract 不可用（俄文可能无法识别），只能保留 Windows 自带";
                    }
                }
                else
                {
                    // ==========================================================
                    // 【2026-10-09 材料 3 实测后加：中文也要跑一遍 chi_sim 交叉验证】
                    //
                    //   **背景：中文这一路以前从不调用 Tesseract。**
                    //   白底中文一律直接用 Windows 自带的结果（又快又省，历史上也够好）。
                    //
                    //   **材料 3（实战版）把这个假设打穿了**，25 段里 Windows 中文全错：
                    //     M2  光学字符识别是指汁算机…        （计 → 汁）
                    //     X2  版本 2 ℃ 已于 2024 年发布…     （2.0 → 2 ℃，Windows 经典错法）
                    //     P2  服务已启动，端口 8080 。        （多个空格）
                    //     Z3  使用 Windows 10 系统， API…     （上两轮的老问题）
                    //     Z2  光学…的过程。。，⏎起           （上一轮也多出乱码）
                    //
                    //   **而 chi_sim 在中文上从没失手**：
                    //     Z1  0.87 逐字全对
                    //     Z2  0.94 逐字全对
                    //
                    //   所以现在的做法：**两个引擎都跑，比对结果。**
                    //
                    //   【三条分支，逐条都能讲清楚道理】
                    //     ① chi_sim 根本认不出中文 → 用 Windows。
                    //        （Tesseract 不可用、或它吐的全是乱码时也走这里）
                    //     ② 两个结果**一字不差** → 用它，并如实告诉用户「两个引擎读出来一致」。
                    //        **两个互相独立的引擎认出一模一样的字，可信度极高。**
                    //     ③ 两个结果**不一样** → 这正是「我不会出错」要管的情况：
                    //        **不能猜**。以 chi_sim 为准（实测它中文从没错过），
                    //        同时**把另一个结果也摆出来**，明确告诉用户两个引擎读得不一样。
                    //
                    //   【为什么不干脆不用 Windows 了】
                    //     分两步走：**先并排跑，把数据攒够**。
                    //     一次就把整条中文链路换掉，风险太大——
                    //     前面已经因为「一次改太多、出事无法归因」栽过三次。
                    //
                    //   【代价】
                    //     中文多花 30~50 毫秒（chi_sim 很快）。实测中文那一路总共才 47 毫秒，
                    //     加完大约 100 毫秒以内，仍然远快于外文那条路。
                    // ==========================================================
                    TesseractOcr.EnsureInitialized();

                    if (TesseractOcr.IsAvailable && !string.IsNullOrWhiteSpace(text))
                    {
                        // 中文这边按 2 倍放大就行，不需要外文那套 4 倍（chi_sim 认汉字够用）
                        using Bitmap chineseImage = ImagePreprocessor.Preprocess(raw);
                        var chiCheck = TesseractOcr.TryRecognize(chineseImage, TesseractOcr.Languages.Chinese);

                        string chiText = TextNormalizer.Normalize(chiCheck.Text ?? "");
                        int chiChars = TextNormalizer.CountMeaningfulChars(chiText);
                        bool chiLooksChinese = LanguageDetector.IsMostlyChinese(chiText) && chiChars >= 2;

                        AppLogger.Info(
                            $"中文交叉验证：chi_sim 置信度 {chiCheck.Confidence:F2}，有效字符 {chiChars} 个，" +
                            $"判定为中文={chiLooksChinese}，内容【{Preview(chiText)}】");

                        if (!chiLooksChinese)
                        {
                            // ==========================================================
                            // 【2026-10-10 材料 3 的 P2：行距贴太紧，chi_sim 分不开行】
                            //
                            //   P2 用的是 `line-height:1.0`，行与行几乎贴在一起。
                            //   实测：chi_sim **根本认不出中文**，
                            //   而 Windows 认出来了但错字多（`端囗`、`8C8`）。
                            //
                            //   **chi_sim 认不出，不是「不认识这些字」，是「看不见行边界」。**
                            //
                            //   【这里为什么不能靠整体放大解决】
                            //     整体放大时字和空隙**等比例**变大，
                            //     **「行距相对字高」根本没变**——所以 `Preprocess` 的 2 倍救不了。
                            //
                            //   【办法：只把竖向拉长】
                            //     横向不动（字不会被拉粗、笔画不会粘连），
                            //     竖向多拉 60% → **行间距同比变大，行边界就分开了**。
                            //
                            //   【采纳条件，和正常那一遍完全一样】
                            //     必须是中文、有效字符 ≥2、置信度 ≥ MinimumAcceptableConfidence。
                            //     **门槛不因为「是补救」就放松**——
                            //     否则又把一段乱码塞回给用户。
                            // ==========================================================
                            using Bitmap stretchedImage = ImagePreprocessor.PreprocessStretchY(raw, 1.6);
                            var chiStretch = TesseractOcr.TryRecognize(stretchedImage, TesseractOcr.Languages.Chinese);
                            string chiStretchText = TextNormalizer.Normalize(chiStretch.Text ?? "");
                            int stretchChars = TextNormalizer.CountMeaningfulChars(chiStretchText);
                            bool stretchLooksChinese = LanguageDetector.IsMostlyChinese(chiStretchText) && stretchChars >= 2;

                            AppLogger.Info(
                                $"紧行距补救：竖向拉长 1.6 倍后再认，置信度 {chiStretch.Confidence:F2}，" +
                                $"有效字符 {stretchChars} 个，判定为中文={stretchLooksChinese}，" +
                                $"内容【{Preview(chiStretchText)}】");

                            if (stretchLooksChinese && chiStretch.Confidence >= MinimumAcceptableConfidence)
                            {
                                // 认出来了，**当成正常那一遍的结果往下走**，
                                // 让后面那套「一致就说明没问题 / 不一致就采信 chi_sim」的逻辑照常处理
                                chiCheck = chiStretch;
                                chiText = chiStretchText;
                                chiChars = stretchChars;
                                chiLooksChinese = true;
                                AppLogger.Info($"竖向拉长后 chi_sim 认出来了，采用这一遍的结果（置信度 {chiCheck.Confidence:F2}）");
                            }

                            if (!chiLooksChinese)
                            {
                                scoreNote = "中文：Windows 自带认出来了，Tesseract 中文模型没认出来，保留 Windows 自带";
                            }
                        }
                        else if (chiCheck.Confidence < MinimumAcceptableConfidence)
                        {
                            // ==========================================================
                            // 【2026-10-10 用户实测小灰字时暴露：两个引擎都是乱的时候，我们硬选了一个】
                            //
                            //   当时的现场：
                            //     chi_sim  0.57 →【这一切专| J亲六革路刚队急材衬宫人小由有加了息…】全乱
                            //     Windows        →【、 25 段，每段框一次，按 F S M X P C 的顺序 ·】← 这份反而对
                            //     两个引擎读出来不一样 → 按老规矩采信 chi_sim → **选错了**
                            //
                            //   **这不是「chi_sim 比 Windows 准」的问题，是「两个都是乱的时候没依据」的问题。**
                            //   而现在的规矩恰好在这种最该留手的时候最武断。
                            //
                            //   【怎么补】
                            //     chi_sim 置信度**低于 0.60 就失去候选资格**。
                            //     0.60 这个数不是现拍的，是材料 3 实测出来的空档：
                            //       chi_sim 认对的：0.85、0.88、0.93
                            //       chi_sim 认成乱码的：0.57
                            //     而且 0.60 本来就是「这东西要不要给用户看」那道门槛
                            //     （MinimumAcceptableConfidence），**不是新数字，是同一条线**。
                            //
                            //   低于这条线 → 老老实实保留 Windows 的结果，
                            //   并如实告诉用户「Tesseract 置信度太低，不采信」。
                            // ==========================================================
                            scoreNote = $"中文：两个引擎读出来不一样，但 Tesseract 置信度只有 " +
                                        $"{chiCheck.Confidence:F2}（低于 {MinimumAcceptableConfidence:F2}，判定为乱码），保留 Windows 自带";
                            AppLogger.Warn(
                                $"⚠️ 两个引擎读出来不一样，但 chi_sim 置信度只有 {chiCheck.Confidence:F2}，" +
                                "判为乱码不采信，保留 Windows 自带");
                        }
                        else
                        {
                            bool identical = string.Equals(
                                TextNormalizer.Normalize(text),
                                TextNormalizer.Normalize(chiText),
                                StringComparison.Ordinal);

                            if (identical)
                            {
                                scoreNote = "中文：Windows 自带和 Tesseract 读出来完全一致" +
                                            $"（置信度 {chiCheck.Confidence:F2}）";
                            }
                            else
                            {
                                // 两个引擎读出来不一样 —— 这是最需要如实告诉用户的情况。
                                // ⚠️ 先把 Windows 那份存下来：下面马上就把 text 换掉了，
                                //    不存的话日志里两边会显示成同一个内容，自己骗自己。
                                string windowsText = text;

                                // 采信 chi_sim：实测它认中文从没错过，
                                // 而 Windows 在材料 3 的五段中文上全错。
                                text = chiText;
                                lineCount = TextNormalizer.CountLines(chiText);
                                channelUsed = "Tesseract";

                                AppLogger.Warn(
                                    "⚠️ 两个引擎对中文读出来不一样：" +
                                    $"Windows【{Preview(windowsText)}】 vs chi_sim【{Preview(chiText)}】，" +
                                    $"置信度 {chiCheck.Confidence:F2}，已采用 chi_sim");

                                scoreNote = "⚠️ 两个引擎读出来不一样，已采用 Tesseract" +
                                            $"（置信度 {chiCheck.Confidence:F2}，{chiChars} 个有效字符）；" +
                                            $"Windows 读出来的是【{Preview(windowsText)}】，请核对";
                            }
                        }
                    }
                    else if (!TesseractOcr.IsAvailable)
                    {
                        scoreNote = "中文：走 Windows 自带（Tesseract 不可用）";
                    }
                }

                // ==========================================================
                // 【2026-10-10】删掉「整行只有符号」的行。
                //
                //   M2（宋体两行中文）实测三轮里两轮都多出一个孤零零的 `[`，
                //   形如：
                //     光学字符识别是指计算机识别图像中的文字⏎并转换为可编辑文本的过程。⏎[
                //
                //   这类「多出来的字符」是四十二节说过的**盲区**：
                //   置信度 0.90、符号占比只有 3%、双引擎一致也救不了。
                //   只能从内容本身找规律：**真文字的一行里一定有字母或数字。**
                //
                //   在这里做，而不是在识别之前——因为要删的是**结果里的噪声**，
                //   识别之前那张图里的笔画本身是分不清的。
                // ==========================================================
                string cleanedLines = TextNormalizer.DropSymbolOnlyLines(text);
                if (!string.Equals(cleanedLines, text, StringComparison.Ordinal))
                {
                    AppLogger.Info($"删掉了只有符号的空行：{Preview(text)} → {Preview(cleanedLines)}");
                    text = cleanedLines;
                    lineCount = TextNormalizer.CountLines(text);
                }

                // ==========================================================
                // 【2026-10-10 P2：删掉中文标点前面多出来的空格】
                //
                //   Windows 读出来是：`服务已启动，端口 8080 。 / ...`
                //                             ↑ 凭空多个空格
                //
                //   **中文的句号逗号前面从来不会有空格**，
                //   一定是识别过程插进去的，不是原文。
                //
                //   放在这里而不是「只给中文用」，是因为
                //   **哪个引擎认出来的都可能带这个毛病**（P2 是 Windows 认的），
                //   而且判断依据是内容本身（空格 + 中文标点），
                //   **不需要知道这段是谁认的**。
                // ==========================================================
                string noSpaceBeforeMark = TextNormalizer.RemoveSpaceBeforeChinesePunctuation(text);
                if (!string.Equals(noSpaceBeforeMark, text, StringComparison.Ordinal))
                {
                    AppLogger.Info($"删掉了中文标点前面的空格：{Preview(text)} → {Preview(noSpaceBeforeMark)}");
                    text = noSpaceBeforeMark;
                    lineCount = TextNormalizer.CountLines(text);
                }

                AppLogger.Info($"最终原文：{Preview(text)}");

                // ==========================================================
                // 【2026-10-10 用户提的缺口：框到半个字，要主动告诉他】
                //
                //   用户原话：「而且框选多行可能会框选到多余的字（框了半个字啥的）」
                //
                //   **这一项置信度帮不上忙**，实测过的例子：
                //     `me@example.com` 的 `m` 被切掉一半 → 识别成 `me@example.co`，
                //     **置信度 0.94，比全对的那些还高。**
                //     因为「少一个字母」看起来仍然像一句正常英文，
                //     引擎不会觉得自己错，**我们也就没有任何办法发现。**
                //
                //   所以这里只做一件事：**选区边界顶到笔画时，如实提醒。**
                //   不猜那个字是什么（猜不出来，也不该猜），
                //   只告诉用户「多半切到了，把框放宽一点」。
                // ==========================================================
                string cutEdge = ImagePreprocessor.FindCutEdge(raw);
                if (!string.IsNullOrEmpty(cutEdge))
                {
                    string cutNote = $"⚠️ 选区的{cutEdge}正好顶在文字上，很可能切到了半个字，" +
                                     "结果可能少一两个字，建议把框放宽一点再试";
                    scoreNote = string.IsNullOrEmpty(scoreNote)
                        ? cutNote
                        : scoreNote + "；" + cutNote;
                    AppLogger.Warn($"选区{cutEdge}切到了文字：{cutNote}");
                }

                // ==========================================================
                // 【2026-10-10 用户要求】识别出乱码时，直接返回「未识别到文字」。
                //
                //   用户原话：「就不能识别出乱码的时候返回未识别到文字吗」
                //
                //   乱码当原文送去翻译，比「没认出来」糟糕得多：
                //   用户会拿到一句莫名其妙的话，**根本发现不了自己被坑了**。
                //   而「未识别到文字」用户一眼就知道要重新框。
                // ==========================================================
                if (rejectedAsGarbage)
                {
                    CaptureRecord.Write(
                        text, channelUsed, "（判定为乱码）", scoreNote,
                        (int)Math.Min(ocrMs, int.MaxValue));

                    return new CaptureResult
                    {
                        Channel = ChannelName,
                        Failure = CaptureFailure.NoTextRecognized,
                        ElapsedMs = Environment.TickCount64 - start,
                        LineCount = 0,
                        Detail = "这一段没能可靠地识别出来（判断是乱码），请重新框选一次"
                    };
                }

                // ==========================================================
                // 【2026-10-09】把这次结果另存一份完整记录，供逐字比对。
                //
                //   用户要求：「提取出文字了软件里应该有记录的，
                //   你读取记录比对文本，这样就能分析出来了。」
                //
                //   必须另存而不能只靠普通日志：普通日志的原文**截断到 300 字**，
                //   格式又是给人看的，没法拿来做逐字比对。
                //   详见 CaptureRecord 的说明。
                // ==========================================================
                CaptureRecord.Write(
                    text,
                    channelUsed,
                    $"{LanguageDetector.DisplayName(LanguageDetector.Detect(text))}"
                        + $"（{LanguageDetector.Describe(text).Split('（')[^1].TrimEnd('）')}）",
                    string.IsNullOrEmpty(scoreNote) ? "走的是 Windows 自带两遍流程" : scoreNote,
                    (int)Math.Min(ocrMs, int.MaxValue));

                // 6) 有效性判断（乱码 / 纯符号一律判无效）
                if (TextNormalizer.LooksLikeGarbage(text) || TextNormalizer.CountMeaningfulChars(text) < 2)
                {
                    return new CaptureResult
                    {
                        Channel = ChannelName,
                        Failure = CaptureFailure.NoTextRecognized,
                        ElapsedMs = Environment.TickCount64 - start,
                        LineCount = lineCount,
                        Detail = $"识别到 {lineCount} 行但没有有效文字（可能是纯图标、乱码或背景太杂）"
                    };
                }

                return new CaptureResult
                {
                    Text = text,
                    Channel = channelUsed,
                    ElapsedMs = Environment.TickCount64 - start,
                    Failure = CaptureFailure.None,
                    LineCount = lineCount,
                    DetectedLanguage = LanguageDetector.Detect(text),
                    Detail = $"识别耗时 {ocrMs} 毫秒" +
                            (scoreNote.Length > 0 ? $"    {scoreNote}" : string.Empty)
                };
            }
            finally
            {
                upscaled?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            return Fail(CaptureFailure.Canceled, start, "用户取消");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"OCR 识别失败：{ex.GetType().Name} {ex.Message}");
            return Fail(CaptureFailure.NoTextRecognized, start, ex.Message);
        }
        finally
        {
            // 每一张 GDI 位图都必须释放：连续识别几十次内存不许涨（T3-8 的判定标准）
            plain.Dispose();
        }
    }

    /// <summary>单遍识别的结果。</summary>
    private readonly record struct PassResult(string Text, int LineCount, long OcrMs);

    /// <summary>
    /// 认一遍：处理引擎尺寸上限 → 转 WinRT → 识别 → 清理文本。
    ///
    /// 【引擎有尺寸上限这件事（规格第 17 条）】
    ///   超限不会自动缩，直接抛异常。所以这里必须先自己缩好再送进去。
    ///   注意不能直接给 using 变量重新赋值（编译器会警告 CS0728，
    ///   而且真正被 Dispose 的还是原来那张，等于泄漏），所以另开一个变量收尾。
    /// </summary>
    private async Task<PassResult> RecognizeOnce(
        OcrEngine engine, Bitmap image, CancellationToken cancellationToken)
    {
        Bitmap? shrunk = null;
        Bitmap working = image;
        try
        {
            int maxSide = OcrEngineFactory.MaxImageDimension;
            if (working.Width > maxSide || working.Height > maxSide)
            {
                AppLogger.Warn($"图片 {working.Width}×{working.Height} 超过引擎上限 {maxSide}，缩小后再识别");
                shrunk = Resize(working, maxSide);
                working = shrunk;
            }

            var softwareBitmap = ToSoftwareBitmap(working);
            using (softwareBitmap)
            {
                long ocrStart = Environment.TickCount64;
                var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken)
                                             .ConfigureAwait(false);
                long ocrMs = Environment.TickCount64 - ocrStart;

                string[] lines = result.Lines
                    .Select(l => l.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToArray();

                return new PassResult(
                    TextNormalizer.Normalize(string.Join("\n", lines)),
                    lines.Length,
                    (int)Math.Min(ocrMs, int.MaxValue));
            }
        }
        finally
        {
            if (shrunk is not null && !ReferenceEquals(shrunk, image)) shrunk.Dispose();
        }
    }

    /// <summary>
    /// 把 GDI 位图转成 WinRT 的 SoftwareBitmap（识别完必须 Dispose）。
    ///
    /// 【为什么绕一圈走内存流】
    ///   识别引擎要的是 SoftwareBitmap，不是 GDI 位图。
    ///   两者之间要么用 BitmapDecoder（WinRT 专用，不认 System.IO.MemoryStream），
    ///   要么用 SoftwareBitmap.CreateCopyFromBuffer（要先自己拼 BGRA 像素缓冲）。
    ///   这里选前者：先把图存成 PNG 字节，再塞进 WinRT 的内存流，
    ///   顺手还压缩了体积，大图也不会撑爆内存。
    /// 【为什么是 internal 而不是 private】
    ///   引擎对比实验（EngineBenchmark）也要调 Windows 自带 OCR，
    ///   那边的 WinRT 转图流程必须和这里**一模一样**才公平。
    ///   如果实验台自己另写一份，测出来的差异可能只是「两边转图方式不同」造成的假象。
    ///   所以直接复用这一个方法，保证测的是「引擎的差别」，不是「代码的差别」。
    /// </summary>
    internal static SoftwareBitmap ToSoftwareBitmap(Bitmap bitmap)
    {
        byte[] bytes;
        using (var buffer = new System.IO.MemoryStream())
        {
            bitmap.Save(buffer, ImageFormat.Png);
            bytes = buffer.ToArray();
        }

        using var randomAccess = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(randomAccess.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();

            // DetachStream：不让 DataWriter 在 Dispose 时把底下的流一起关掉，
            // 后面 decoder 还要用它。漏了这一句就是「用完就报流已关闭」。
            writer.DetachStream();
        }

        randomAccess.Seek(0);
        var decoder = BitmapDecoder.CreateAsync(randomAccess).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 日志用的原文预览：换行压成「/」，超长截断。
    /// 排查「读不全」必须能看到实际识别结果，否则无从下手。
    /// </summary>
    private static string Preview(string text)
    {
        if (string.IsNullOrEmpty(text)) return "（空）";
        string flat = text.Replace("\n", " / ").Replace("\r", "");
        return flat.Length <= 300 ? flat : flat[..300] + $"…（共 {flat.Length} 字）";
    }

    /// <summary>
    /// 按比例缩到不超过上限。
    /// </summary>
    private static Bitmap Resize(Bitmap source, int maxSide)
    {
        double scale = (double)maxSide / Math.Max(source.Width, source.Height);
        var result = new Bitmap(
            Math.Max(1, (int)(source.Width * scale)),
            Math.Max(1, (int)(source.Height * scale)),
            PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(result))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, result.Width, result.Height);
        }
        return result;
    }

    private CaptureResult Fail(CaptureFailure reason, long start, string detail)
        => new()
        {
            Channel = ChannelName,
            Failure = reason,
            ElapsedMs = Environment.TickCount64 - start,
            Detail = detail
        };
}
