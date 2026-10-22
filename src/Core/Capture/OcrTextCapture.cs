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
///
/// 【文件地图（2026-10-16 拆分，只是把文件切开，行为一个字没改）】
///   这个类是 `partial` 的，一共 7 个文件。找东西按下表去对应的文件：
///
///   `OcrTextCapture.cs`          主调度：截图 → 深底判断和反色 → 转灰度 → 第一遍
///                                → 分岔（外文 / 中文）→ 收尾（清理、切边、判乱码、写记录、返回）
///   `OcrTextCapture.FirstPass.cs` 第一遍识别，以及「要不要放大 2 倍再认一遍」的决策
///   `OcrTextCapture.Foreign.cs`   外文方向：Tesseract 的 eng / rus 多路、英文第二票、俄文判断
///   `OcrTextCapture.Chinese.cs`   中文方向：chi_sim 交叉验证、红通道补试、紧行距补救、
///                                 第三 / 第四证人、点号回填
///   `OcrTextCapture.Rules.cs`     裁决规矩（纯函数，最好单独测）：劈词证据、点号回填、
///                                 第二票是否救回尾巴、编辑距离
///   `OcrTextCapture.Utils.cs`     基础件：一次识别（RecognizeOnce）、位图转换、日志预览、
///                                 失败结果、字符判断小工具
///   `OcrTextCapture.Tuning.cs`    所有门槛常量（要调数值先来这里）
///
///   【为什么是 partial，不是拆成独立类】
///     三套探针（`tools\探针\` 下 E6末尾 / 折行 / 真实数据）是按「类名 + 方法名」反射
///     调真程序集的。拆成独立类，探针全得改，风险翻倍。
///     partial 是「整理遗产代码」的过渡手法，不是终点站；将来若发现某两个文件
///     「几乎从来不一起改」，那时再考虑升级成独立类 —— 那是以后单独一轮的事。
///
///   【以后加一门新语言（比如日语），3 个动作】
///     1. 新建 `OcrTextCapture.Japanese.cs`，照 `OcrTextCapture.Foreign.cs` 的形状写
///        （入口方法固定签名 + 自己的助手 + 自己的常量）；
///     2. 在 `CaptureAsync` 的分岔处（现在分「外文 / 中文」两块）注册一行调用；
///     3. 在 `OcrTextCapture.Tuning.cs` 里加日语的门槛值。
///     → 除了分岔处那一行，老语言的文件一个字不用改。
///
///   【以后加一个新引擎】
///     引擎封装（怎么调、怎么转图）放在 `TesseractOcr.cs` 的同类位置（或新文件）；
///     「哪个引擎认哪一段」的调度在 `OcrTextCapture.cs` 分岔处 + 对应语言文件里接线；
///     全部门槛值进 `Tuning`。这也是为什么 `RecognizeOnce` / `ToSoftwareBitmap`
///     在第 1 步就搬进 `Utils` —— 引擎相关的基础件以后只有一处要动。
/// </summary>
internal sealed partial class OcrTextCapture : ITextCapture
{
    public string ChannelName => "WindowsOcr";

    public bool IsAvailable => OcrEngineFactory.IsAvailable;

    /// <summary>
    /// 图库回放用：设了这张图，<see cref="CaptureAsync"/> 就不再截屏，直接拿它当「刚截到的画面」。
    ///
    /// 【为什么要这个口子】
    ///   要判断「这次改完有没有退步」，得把图库里那几十张图**同一张图前后各跑一遍**。
    ///   人手框选做不到——每次框的松紧、位置、像素都不一样，比出来的差异分不清是代码变了还是框变了。
    ///
    /// 【为什么放在这里而不是另写一套识别代码】
    ///   从这里塞进去的图，走的是**和真机完全同一条流水线**：
    ///   判深底 → 反色 → 放大 → 补边 → 五路二值化 → 四路择优 → 判乱码 → 清符号。
    ///   另写一套就只测了「另写的那套」，代表不了真机。
    ///
    /// 平时没人设它，恒为 null，对真机行为零影响。
    /// </summary>
    internal static Bitmap? ReplaySource { get; set; }

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
        //    【图库回放】设了 ReplaySource 就直接拿它当截图结果，不碰屏幕；下面一个字都不用改。
        using Bitmap? captured = ReplaySource is { } replay
            ? new Bitmap(replay)
            : ScreenCaptureService.TryCapture(request.PhysicalRect);
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
                    ChineseOutcome chinese = RunChinesePath(raw, text, lineCount, channelUsed, scoreNote);
                    text = chinese.Text;
                    lineCount = chinese.LineCount;
                    channelUsed = chinese.ChannelUsed;
                    scoreNote = chinese.ScoreNote;
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

                // ==========================================================
                // 【2026-10-17 实测期第 2 批：列表圆点被认成 o / e】
                //
                //   用户原话：「识别会把前面的符号点给识别成文字导致多个a、e、o什么的」
                //   现场（MediaWiki 的列表）：
                //     `Adding content o If you are unsure of what to do…`
                //     `e An edit doesn't have to be massive…`
                //
                //   **这不是「认不出来」，是图上那个圆点确实长得像 o/e。**
                //   所以放在结果清理这一层删，不碰识别本身。
                //   判据和全历史验证写在 TextNormalizer.DropMisreadBulletLetters 的注释里。
                // ==========================================================
                string noBullet = TextNormalizer.DropMisreadBulletLetters(text);
                if (!string.Equals(noBullet, text, StringComparison.Ordinal))
                {
                    AppLogger.Info($"删掉了被认成字母的项目符号：{Preview(text)} → {Preview(noBullet)}");
                    text = noBullet;
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
                        (int)Math.Min(ocrMs, int.MaxValue),
                        request.SourceProcess);

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
                    (int)Math.Min(ocrMs, int.MaxValue),
                    request.SourceProcess);

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
