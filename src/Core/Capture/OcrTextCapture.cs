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
internal sealed partial class OcrTextCapture : ITextCapture
{
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
        //   【判断深底还是浅底，2026-10-12 已改判据】
        //     原来比的是「平均亮度 < 110」，**只看平均亮度会把「浅字暗底」漏掉**
        //     （白字会把平均值往上拉，E5 平均亮度 154.4 就这样被判成了浅底）。
        //     现在改成看**四边最外一圈像素落在 Otsu 阈值的哪一侧**，理由和四张图的
        //     对照数据写在 `ImagePreprocessor.NeededInversion` 的注释里。
        // ==========================================================
        bool deepBackground = ImagePreprocessor.AnalyzeBackground(captured, out int bgThreshold, out int ringDark, out int ringTotal);
        using Bitmap? raw = deepBackground
            ? ImagePreprocessor.Invert(captured)
            : new Bitmap(captured);

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
        if (deepBackground)
        {
            AppLogger.Info($"这一段是深底浅字（深色模式）：Otsu 阈值 {bgThreshold}，四边最外一圈 {ringDark}/{ringTotal} 个像素落在暗侧，" +
                           $"已整张反色后再识别（平均亮度 {brightness:F1}，仅供参考，不参与判断）");
        }
        else
        {
            AppLogger.Info($"这一段是浅底，不反色：Otsu 阈值 {bgThreshold}，四边最外一圈 {ringDark}/{ringTotal} 个像素落在暗侧" +
                           $"（平均亮度 {brightness:F1}，仅供参考，不参与判断）");
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
            FirstPassOutcome firstPass = await RunFirstPassAsync(plain, raw, engine, cancellationToken).ConfigureAwait(false);
            PassResult first = firstPass.First;
            string text = firstPass.Text;
            int lineCount = firstPass.LineCount;
            long ocrMs = firstPass.OcrMs;
            string note = firstPass.Note;
            Bitmap? upscaled = firstPass.Upscaled;
            try
            {

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
                // 【2026-10-13 只记不改 —— 攒数据用】
                //
                //   背景（tools\日志\阶段3-验收.md 一百二十四节）：
                //   规则是「英文一律信 Tesseract」，依据是对照实验
                //   （Tess 94.7 分 vs Windows 63.3 分），所以下面 :506
                //   `var best = engResult;` 无条件丢掉了 Windows 那份。
                //   但 E6 实测里出现过反过来的情况：同一张图上
                //   Windows 读全了 `editable text.`，Tesseract 读成
                //   `editak le`，最后交付了更差的那份。
                //
                //   问题是「像不像人话」这个评分器过去栽过好几次
                //   （乱码的分数反而更高），**不能拿它当判据**。
                //   所以这一轮**只把两边的分数都记进日志，不改任何选择**，
                //   先攒够数据，看清楚「Windows 是不是经常更好」再决定。
                // ==========================================================
                int windowsCharsForCompare = TextNormalizer.CountMeaningfulChars(text);
                int windowsScoreForCompare = OcrQualityScorer.Score(text);

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
                    ForeignOutcome foreign = RunForeignPath(
                        raw, first, text, lineCount, note,
                        channelUsed, scoreNote, rejectedAsGarbage,
                        windowsSaysChinese, windowsCharsForCompare, windowsScoreForCompare);
                    text = foreign.Text;
                    lineCount = foreign.LineCount;
                    channelUsed = foreign.ChannelUsed;
                    scoreNote = foreign.ScoreNote;
                    rejectedAsGarbage = foreign.RejectedAsGarbage;
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

                        // ==========================================================
                        // 【2026-10-10 材料 2 的 Z1：中文流程也补上「红通道」那一路】
                        //
                        //   **Z1 的现场**（`今天天气不错适合出门`，橙底绿字）：
                        //     Windows 自带 → 0 字 / 错字
                        //     chi_sim 在**灰度图**上 → 0.76，卡在 0.78 门槛下面被拒
                        //     → 判为乱码 → **界面什么都不显示**
                        //
                        //   **为什么红通道能救**（数据本身的性质，不是猜的）：
                        //     橙底 RGB(255,165,0)，红分量 255
                        //     绿字 RGB(0,128,0)，红分量   0
                        //     → 红通道上对比度拉满，颜色天然分得开；
                        //       转灰度是三通道加权平均，**绿字那点红被稀释掉了**。
                        //
                        //   【这一段先后补过两个地方，两个都留着】
                        //     外文流程（约 1190 行）和中文流程（这里）。
                        //     **只因为「Z1 到底走哪条流程」这件事本身反复测不准**，
                        //     来回搬了两次都没生效。
                        //     两处都只在 `置信度 < 0.78` 时才跑一次，
                        //     **平时一秒都不多花**，那就都放着，一劳永逸。
                        // ==========================================================
                        if (chiCheck.Confidence < MinimumAcceptableConfidence)
                        {
                            using Bitmap redChineseImage = ImagePreprocessor.PreprocessRedChannel(raw, 2.0);
                            var chiRed = TesseractOcr.TryRecognize(redChineseImage, TesseractOcr.Languages.Chinese);
                            string chiRedText = TextNormalizer.Normalize(chiRed.Text ?? "");
                            int redChars = TextNormalizer.CountMeaningfulChars(chiRedText);

                            AppLogger.Info(
                                $"中文红通道补试：置信度 {chiRed.Confidence:F2}（灰度版是 {chiCheck.Confidence:F2}），" +
                                $"有效字符 {redChars} 个，内容【{Preview(chiRedText)}】");

                            bool redLooksChinese = LanguageDetector.IsMostlyChinese(chiRedText) && redChars >= 2;
                            if (redLooksChinese && chiRed.Confidence > chiCheck.Confidence)
                            {
                                chiCheck = chiRed;
                                chiText = chiRedText;
                                chiChars = redChars;
                                chiLooksChinese = true;
                                AppLogger.Info($"红通道这一版更好（{chiRed.Confidence:F2}），采用它");
                            }
                        }

                        

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

                                // ==========================================================
                                // 【2026-10-10 材料 1 的 D1：第三证人——分数更高的那个说了算】
                                //
                                //   **先看探针测出来的数据（3 次 D1 + 3 次 X2）**：
                                //     D1（`API 版本 v2.0`）
                                //       Windows【…系统， API  版本 v2 ℃ 已于…】  ← API 对，v2.0 错
                                //       chi_sim【…系统，ApPl 版本 v2.0 已于…】    ← API 错，v2.0 对
                                //       第三票　【…系统，API  版本 v2.0 已于…】 0.90 ← **两个都对**
                                //       → 第三票 0.90 > chi_sim 0.89 → **换，API 认对了**
                                //
                                //     X2（`版本 2.0`，材料 3 里已通过的那段）
                                //       chi_sim 0.93，第三票 0.93 —— **平手**
                                //       → 不换 → **这一段一个字都不会变**
                                //
                                //   【为什么规矩是「分数更高」，不是「不一样就换」】
                                //     「三票里有两票一样就少数服从多数」听起来更美，
                                //     但 D1 里**第三票和两个都不一样**——
                                //     **不一样不代表更好**。
                                //     而「第三票分数比两个都高」是**两个引擎自己给的数字**，
                                //     不靠我们猜。
                                //
                                //   【平手为什么不换】
                                //     X2 实测就是平手，**平手就保持原来的做法**——
                                //     **宁可什么也不动，也不要把已经对的弄坏**。
                                //
                                //   【什么时候才会跑这条路】
                                //     只在「两个引擎不一致」这个**本来就在犹豫**的时刻，
                                //     两个引擎一致时**一秒都不多花**。
                                // ==========================================================
                                try
                                {
                                    using Bitmap thirdImage = ImagePreprocessor.PreprocessRedChannel(raw, 2.0);
                                    var third = TesseractOcr.TryRecognize(thirdImage, TesseractOcr.Languages.Chinese);
                                    string thirdText = TextNormalizer.Normalize(third.Text ?? "");
                                    int thirdChars = TextNormalizer.CountMeaningfulChars(thirdText);
                                    bool thirdLooksChinese =
                                        LanguageDetector.IsMostlyChinese(thirdText) && thirdChars >= 2;

                                    bool sameAsWindows = string.Equals(
                                        thirdText, TextNormalizer.Normalize(windowsText), StringComparison.Ordinal);
                                    bool sameAsChi = string.Equals(
                                        thirdText, TextNormalizer.Normalize(chiText), StringComparison.Ordinal);

                                    AppLogger.Info(
                                        $"【第三证人】红通道 chi_sim：置信度 {third.Confidence:F2}，" +
                                        $"内容【{Preview(thirdText)}】；" +
                                        $"与 Windows 一致={sameAsWindows}，与 chi_sim 一致={sameAsChi}");

                                    if (thirdLooksChinese && third.Confidence > chiCheck.Confidence)
                                    {
                                        AppLogger.Info(
                                            $"第三票的分数更高（{third.Confidence:F2} > {chiCheck.Confidence:F2}）——" +
                                            "**多一个证人，高的那个说了算**，采用第三票");
                                        chiText = thirdText;
                                        chiChars = thirdChars;
                                        chiCheck = third;
                                        text = thirdText;
                                        lineCount = TextNormalizer.CountLines(thirdText);

                                        scoreNote = $"中文：两个引擎不一致，又多问了一个证人，" +
                                                    $"分数更高的那份（{third.Confidence:F2}）被采用；" +
                                                    $"Windows 读出来的是【{Preview(windowsText)}】，请核对";
                                    }
                                    else
                                    {
                                        AppLogger.Info(
                                            $"第三票没有更高（{third.Confidence:F2} ≤ {chiCheck.Confidence:F2}）——" +
                                            "**平手或更差就保持原样**，不动已经对的那些段");

                                        // ==========================================================
                                        // 【2026-10-13 X2：劈词证据优先】
                                        //   主票和第三票分数打平时本来是「不动」，
                                        //   但 X2 就死在这：主票 0.94（拆词的错答案）
                                        //   比第三票 0.93（对的）还高，两次实测都被这条规矩挡住。
                                        //   → 分数不可信，但**劈词这个现象是硬的**。
                                        // ==========================================================
                                        if (SplitWordEvidenceFavorsCandidate(
                                                chiText, thirdText, chiCheck.Confidence, third.Confidence))
                                        {
                                            AppLogger.Info(
                                                $"【劈词证据】主票被判定劈错（主票 {chiCheck.Confidence:F2} vs 第三票 " +
                                                $"{third.Confidence:F2}，字母数字完全一样、只差空格标点，且主票把连着的词拆开了）——" +
                                                "采用第三票");
                                            chiText = thirdText;
                                            chiChars = thirdChars;
                                            chiCheck = third;
                                            text = thirdText;
                                            lineCount = TextNormalizer.CountLines(thirdText);
                                        }
                                    }

                                    // ==========================================================
                                    // 【2026-10-13 材料 3 的 X2：第四证人——chi_sim+eng】
                                    //
                                    //   X2（`版本 2.0 已于 2024 年发布 config.txt`）撤回 Sauvola 后
                                    //   实测 8 次错 6 次，错法一模一样：chi_sim 把 `config.txt`
                                    //   拆成 `config` + `txt` 两个词，**两个词分数一模一样（71.45）**，
                                    //   等于它自己也不知道那个点算不算分隔点。
                                    //   红通道第三票救回过 4 次，剩下那几次红通道也拆开了、
                                    //   两票平手，救不回来——**候选里根本没有全对的那一份**。
                                    //
                                    //   【为什么该挂英文表，而不是改别的】
                                    //     Windows 自带 OCR **每一次都把 `config.txt` 读对**，
                                    //     它只是把中文的 `2.0` 读成 `2 ℃` 才被整份丢掉。
                                    //     → 图本身够清楚，任何「以拉丁为主」的引擎都读得对，
                                    //       只有 chi_sim 这一个在那一点上骑墙。
                                    //     rus+eng 早就是干这个的（俄文夹 `config.txt`），
                                    //     这里只是把同一招用到 chi_sim 上，不新造机制。
                                    //
                                    //   【规矩不变】同一张图（红通道），只换模型，**分数更高才换**。
                                    //   没把握就不换——宁可不动，也不要把已经对的弄坏。
                                    //
                                    //   ponytail: 只在「两个引擎读出来不一样」这个本来就在犹豫的
                                    //   时刻跑这一票，一致时一秒都不多花；引擎没起来就跳过，
                                    //   不影响用户取词。
                                    // ==========================================================
                                    try
                                    {
                                        var fourth = TesseractOcr.TryRecognize(thirdImage, TesseractOcr.Languages.ChinesePlusEnglish);
                                        string fourthText = TextNormalizer.Normalize(fourth.Text ?? "");
                                        int fourthChars = TextNormalizer.CountMeaningfulChars(fourthText);
                                        bool fourthLooksChinese =
                                            LanguageDetector.IsMostlyChinese(fourthText) && fourthChars >= 2;

                                        AppLogger.Info(
                                            $"【第四证人】红通道 chi_sim+eng：置信度 {fourth.Confidence:F2}，" +
                                            $"内容【{Preview(fourthText)}】");

                                        if (fourthLooksChinese && fourth.Confidence > chiCheck.Confidence)
                                        {
                                            AppLogger.Info(
                                                $"第四票的分数更高（{fourth.Confidence:F2} > {chiCheck.Confidence:F2}）——" +
                                                "**多一个证人，高的那个说了算**，采用第四票");
                                            chiText = fourthText;
                                            chiChars = fourthChars;
                                            chiCheck = fourth;
                                            text = fourthText;
                                            lineCount = TextNormalizer.CountLines(fourthText);
                                        }
                                        else
                                        {
                                            AppLogger.Info(
                                                $"第四票没有更高（{fourth.Confidence:F2} ≤ {chiCheck.Confidence:F2}）——" +
                                                "**平手或更差就保持原样**，不动已经对的那些段");

                                            // 同上：X2 的劈词证据，第四票也适用
                                            if (SplitWordEvidenceFavorsCandidate(
                                                    chiText, fourthText, chiCheck.Confidence, fourth.Confidence))
                                            {
                                                AppLogger.Info(
                                                    $"【劈词证据】主票被判定劈错（主票 {chiCheck.Confidence:F2} vs 第四票 " +
                                                    $"{fourth.Confidence:F2}，字母数字完全一样、只差空格标点，且主票把连着的词拆开了）——" +
                                                    "采用第四票");
                                                chiText = fourthText;
                                                chiChars = fourthChars;
                                                chiCheck = fourth;
                                                text = fourthText;
                                                lineCount = TextNormalizer.CountLines(fourthText);
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // 第四证人失败绝不能影响用户取词
                                        AppLogger.Warn($"第四证人失败（已忽略）：{ex.Message}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // 第三证人失败绝不能影响用户取词
                                    AppLogger.Warn($"第三证人失败（已忽略）：{ex.Message}");
                                }

                                // 【2026-10-15 路二 · 点号回填】
                                // 第三/第四证人都试过之后才跑：它们是「换票」，这一步只是「回填」。
                                string restoredDots = MergeSplitDottedTokensFrom(text, windowsText);
                                if (!string.Equals(restoredDots, text, StringComparison.Ordinal))
                                {
                                    AppLogger.Info(
                                        $"【点号回填】最终结果把 Windows 读出来的词拆开了：" +
                                        $"{Preview(text)} → {Preview(restoredDots)}，按 Windows 的写法合并");
                                    text = restoredDots;
                                    lineCount = TextNormalizer.CountLines(text);
                                }

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
}
