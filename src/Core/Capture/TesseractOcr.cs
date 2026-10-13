using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Tesseract;
using Tesseract.Interop;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// Tesseract 认字引擎（**备用引擎，默认关闭**）。
///
/// 【2026-10-09 定位大改：从「默认关的备用」变成「英俄主力」】
///
///   之前默认关着，理由是「实测比 Windows 自带更差」。
///   **那个结论当时只在中文上成立。** 用户 2026-10-09 说明真实用途是
///   **主要翻英文和俄文**，重跑实验后数据完全相反：
///
///     引擎                      英文平均分   俄文
///     Tess-fast（原样）           94.7       100 分 ✅
///     Windows 英文引擎            63.3        装不上 ❌
///     Windows 中文引擎（曾上线）   58.9        装不上 ❌
///
///   两个决定性事实：
///   ① **英文差 36 分**——这不是「优化空间」，是「能不能用」的差别；
///   ② **俄文本机根本没装识别包**（ru-RU 引擎创建失败，已实测确认），
///      俄文**只有** Tesseract 这一条路。
///
///   所以现在的分工：**中文走 Windows 自带（快、实测够好），
///   英文和俄文走 Tesseract。**
///
/// 【慢多少，划算吗】
///   实测：Windows 自带约 10~30 毫秒，Tesseract 约 200~260 毫秒。
///   用 200 毫秒换 36 分，划算。
///   而且**中文那条路没有变慢**——中文根本不跑 Tesseract。
///
/// 【踩过的坑 —— 单文件打包会让它直接崩】
///   Tesseract 的 .NET 封装靠「程序集所在目录」拼原生 DLL 的路径。
///   单文件打包下 `Assembly.Location` 是空字符串，拼出来是 null，
///   在**拼路径那一步**就抛 `ArgumentNullException: path1`。
///   我试过用绝对路径预加载原生库绕过——**没用**，
///   因为它压根没走到 LoadLibrary 就崩了。
///   所以构建脚本已经改成 PublishSingleFile=false。
///   顺带说明：那两个原生 DLL 本来就得以散文件躺在 exe 旁边，
///   「一个 exe 走天下」在引入 Tesseract 之后已经不成立了。
/// </summary>
internal sealed class TesseractOcr
{
    /// <summary>
    /// 程序全局共用引擎——初始化一次要一百多毫秒，不能每次认字都重来。
    /// 【2026-10-09】从「一个」改成「按语言组合各一个」，
    /// 原因见 <see cref="GetEngine"/> 的说明（同形字必须靠语种来区分）。
    /// </summary>
    private static readonly Dictionary<string, TesseractEngine> _engines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>初始化失败过就别再试了，否则每次框选都要白等一次失败。</summary>
    private static bool _initFailed;

    private static readonly object Gate = new();

    /// <summary>引擎是否真的就绪（供界面显示与调用方判断用）。</summary>
    public static bool IsReady => _engines.Count > 0;

    /// <summary>
    /// 能不能用。
    ///
    /// 【2026-10-09 修一个把自己锁死的严重 bug】
    ///
    ///   这里原来写的是 `_engines.Count &gt; 0`（「引擎已经建好了吗」）。
    ///   但同一次改动里，`EnsureInitialized()` 被改成**不再预先建引擎**，
    ///   只负责预加载原生库和检查语言包文件在不在。
    ///   于是形成了一个**互相等待的死结**：
    ///
    ///     调用方先问 IsAvailable → 要看 _engines.Count &gt; 0
    ///     → 但引擎只有 Recognize() 里才会真正创建
    ///     → 而 Recognize() 开头就 if (!IsAvailable) return null
    ///     → **谁也进不去，引擎永远建不出来**
    ///
    ///   结果是**整整一轮实测，Tesseract 一次都没跑起来**，
    ///   所有英文俄文都掉回中文引擎，识别质量断崖式下跌
    ///   （用户实测：英文乱码、俄文几乎无法识别、S10/S13 完全空白）。
    ///   用户反馈「更差了」——**这是纯粹由这个 bug 造成的，不是真实退步。**
    ///
    ///   【正确的问法】
    ///   `IsAvailable` 要回答的是「**允不允许用**」，
    ///   而不是「引擎建好了没有」。引擎本来就是**按需懒加载**的，
    ///   判断它有没有建好没有意义——第一次用的时候自然会建。
    ///
    /// 【为什么这里不再看 AppSettings.UseTesseract】
    ///   那个开关是 2026-10-08 加的，当时它是「默认关掉的备用引擎」的总闸。
    ///   2026-10-09 定位变了——**英俄文的主力引擎就是它**，
    ///   如果还拿那个开关当总闸，一旦文件里是 false，英文又会掉回 58 分。
    ///   开关语义已改为「强制只用 Windows 自带（用于对照排查）」，
    ///   默认 false，也就是走新的分工。
    /// </summary>
    /// <summary>
    /// tesseract 的切行方式：6 = Assume a single uniform block of text（一整块整齐的文字）。
    /// 为什么不是 7（单独一行）见 <see cref="GetEngine"/> 里那段注释。
    /// </summary>
    private const string PageSegModeUniformBlock = "6";

    // ⚠️ 【thresholding_method 不能改成 Sauvola（2）——2026-10-13 实测证伪】
    //   V0.3.2.11 曾经在这里设过 thresholding_method = 2，想治 E6「末尾少读」。
    //   结果：E6 修好了（6/6），但**低对比度底色整片报废**——
    //   灰底灰字（底 185 / 字 154，只差 31）和橙底绿字（灰度上只差 10）
    //   这类「平坦、起伏极小」的图，Sauvola 会把阈值压到局部均值以下，
    //   连字带底一起判成白色 → **字直接消失 → Tesseract 0.00 置信度、0 个字**。
    //   同一张灰底灰字图：V0.3.2.10 Tesseract 置信度 0.96 / 123 字全对，
    //   V0.3.2.11 八条路全空，成绩单从 55/56 掉到 49/56。
    //   → 保持 tesseract 的默认值 0（全局 Otsu），谁再动这里先去看这条。
    //   注：ImagePreprocessor 里第五路「局部阈值」用的是我们自己写的 Sauvola，
    //   那是另一回事（自己切完再喂给引擎），不受这条影响。

    public static bool IsAvailable
    {
        get
        {
            AppSettings.Load();
            // _initFailed 为 true 表示「语言包缺失」等硬伤，
            // 那种情况确实没法用；引擎本身**没建不算失败**，那是懒加载的正常状态。
            return !AppSettings.WindowsOnly && !_initFailed;
        }
    }

    /// <summary>
    /// 认一段图，返回文字；失败返回 null（不抛异常）。
    ///
    /// <param name="bitmap">截图</param>
    /// <param name="language">
    ///   要用哪种语言的模型。
    ///   【2026-10-09 用户实测后加的关键参数】
    ///   以前三种语言一起挂（chi_sim+eng+rus），现在**按语种分开挂**。
    /// </param>
    /// </summary>
    public static string? Recognize(System.Drawing.Bitmap bitmap, string language = Languages.All)
        => TryRecognize(bitmap, language).Text;

    /// <summary>
    /// 一次识别的结果：认出来的文字 + 这一次的置信度。
    /// </summary>
    /// <param name="Text">认出来的文字；失败或没认到是 null。</param>
    /// <param name="Confidence">
    ///   置信度（0~100 里换算成 0~1）。
    ///   实测（用户 2026-10-09 验收材料）：
    ///     认对了 0.81~0.95，在瞎猜 0.09~0.41——中间分得很开，很好用。
    /// </param>
    /// <param name="MinWordConfidence">
    ///   **这一句里分数最低的那个词**是多少（0~100，跟引擎自报的一样，没换算）。
    ///
    ///   【为什么加这个，2026-10-12】
    ///     整句平均分会把错**藏起来**：E5 整句一直是 0.89~0.93 看着很稳，
    ///     可那个 `HTTP/2.0` 一个词的分数十次分别是
    ///     17.31 / 83.02 / 88.66 / 17.31 / 38.00 / **0.00** / 33.89 / 83.02 / 88.66 / 83.02，
    ///     而 E5 **十次全错**。同一个分数，看整句看不出问题，看单词就看得出来。
    ///
    ///     用户实测 20 次的结论：**出现 0.00 的 4 次，全错；没出现的一次也没漏。**
    ///
    ///   【现在还只是拿来看的】
    ///     **还没有任何一个判断用它**——改了它就等于改了行为，
    ///     而「多路投票选哪一份」还是老问题（B3 的教训：多一个候选多一次换错的机会）。
    ///     所以先只记下来，等数据够了再定规矩。
    ///
    ///   没取到词时给 100（意思是「没发现不稳的词」），避免误触发。
    /// </param>
    public readonly record struct Result(string? Text, double Confidence, double MinWordConfidence = 100.0);

    /// <summary>认一段图，返回文字和置信度；失败返回空结果（不抛异常）。</summary>
    public static Result TryRecognize(System.Drawing.Bitmap bitmap, string language)
    {
        if (!IsAvailable) return new Result(null, 0);

        TesseractEngine? engine = GetEngine(language);
        if (engine is null) return new Result(null, 0);

        try
        {
            long start = Environment.TickCount64;

            // 【为什么编成 PNG 字节再喂进去】
            //   这个封装在 net47/net48 上有 PixConverter.ToPix(Bitmap)，
            //   但本项目是 .NET 10，走 netstandard2.0 版，**那个类不存在**。
            //   剩下能用的公开入口只有 Pix.LoadFromMemory(byte[])。
            byte[] png;
            using (var buffer = new MemoryStream())
            {
                bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
                png = buffer.ToArray();
            }

            using var pix = Pix.LoadFromMemory(png);
            string text;
            double confidence;
            float? minWordConfidence;

            // 【2026-10-12 修一个自己挖的坑：必须用大括号把 page 的范围关住】
            //   引擎是**一次只能处理一张图**的（Only one image can be processed at once）。
            //   上一版把「补跑几路」写在 `using var page` 还没释放的时候，
            //   结果补跑的六路**全部报这个错、一路都没跑成**，
            //   白白浪费了用户一整轮 20 次实测。
            //   → 所以这里改成显式的大括号：**先算完、先把 page 释放掉，再去补跑。**
            using (var page = engine.Process(pix))
            {
                text = page.GetText() ?? string.Empty;
                confidence = page.GetMeanConfidence();

                AppLogger.Info($"Tesseract 认完（{language}）：用时 {Environment.TickCount64 - start} 毫秒，" +
                               $"置信度 {confidence:F2}，{text.Length} 字");

            // ==========================================================
            // 【2026-10-12 只加日志、不改行为：让引擎逐词自报】
            //
            //   【为什么加这个】
            //     E5 和 X2 这两段一直抛硬币（E5 只有 52%、X2 64% 正确），
            //     而整句的平均分一直很高（E5 有 0.91~0.95）。
            //     **平均分高，不代表每个字都认得准**——
            //     错的只是那几个符号，平均分被整句稀释掉了。
            //
            //     查日志查实了：E5、E6 **整段只跑了一次识别**（五路那一段根本没跑，
            //     因为门是「置信度低于 0.90 才跑」），**只有一张选票，投票无从下手**。
            //     X2 有三票，但 2:1 站在错的那边。
            //     → **「多路投票」这条路对这两段是走不通的**，得先看清引擎自己
            //       觉得哪儿没把握。
            //
            //   【这两个接口是干什么的】
            //     GetConfidence(Word)          → **这个词**自己有多少把握（不是整句平均分）
            //     GetWordIsFromDictionary()    → **这个词在不在词典里**
            //
            //     第二个尤其关键：LV-ROVER 那篇论文的核心就是
            //     「已经在词典里的词，不许被多数票推翻」。
            //     **这两个接口封装里一直有，我们从来没调用过。**
            //
            //   【这一段严格只打日志，一个字都不改】
            //     所以现在还不能拿它做任何判断——
            //     得先看 E5 那个「|」和「p42.」、X2 那个「config txt」
            //     **词级分数到底低不低、词典认不认**，数据说什么再定规则。
            //
            //   【为什么单独套一个 try】
            //     外面那个 catch 会把任何异常变成「识别失败、结果为空」——
            //     诊断代码一旦出错，**会把好好的识别结果弄丢**。
            //     所以这里自己兜住，坏了就当没打日志，**绝不影响识别**。
            // ==========================================================
            minWordConfidence = LogWordSelfReport(page, language);
            }   // ← page 到这里已经释放，下面才可以再喂图给同一个引擎

            // 【2026-10-13 撤掉了原来的「补跑几路」诊断】
            //   那段会在最低词级分低于 50 时**另外多跑 6 次识别**，
            //   实测让一次取词从 31 毫秒涨到 3797 毫秒，而结果**一律不作数**。
            //   理由和教训写在文件末尾那段注释里。

            return string.IsNullOrWhiteSpace(text)
                ? new Result(null, 0)
                : new Result(text, confidence, minWordConfidence ?? 100.0);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Tesseract 认字失败（不影响主通道）：{ex.GetType().Name} {ex.Message}");
            return new Result(null, 0);
        }
    }

    /// <summary>常用的模型组合。命名保持和 tessdata 文件名一致，避免以后看不懂。</summary>
    public static class Languages
    {
        public const string Russian = "rus";

        /// <summary>
        /// 俄文里夹数字、拉丁字母时用（用户 2026-10-09 点名要修的场景）。
        /// **顺序有意义：俄文排在前面**，引擎仍以西里尔字表为主，
        /// 但遇到 `404`、`config.txt` 这类片段时有英文表可以借力。
        /// </summary>
        public const string RussianPlusEnglish = "rus+eng";

        public const string English = "eng";
        public const string Chinese = "chi_sim";

        /// <summary>三种一起挂。只在「不知道是什么语种」时才用。</summary>
        public const string All = "chi_sim+eng+rus";
    }

    /// <summary>
    /// 按语言取引擎，每个语言组合**只初始化一次**并一直复用。
    ///
    /// 【为什么必须按语种分开（2026-10-09 用户实测）】
    ///   用户反馈「俄文识别出来纯俄文字变成英文俄文混用了」，
    ///   实例是 `Съешь же ещё этих мягких французских булок да выпей чаю`
    ///   被认成 `Cbeuub же ещё этих мягких французских булок да выпей чаю`
    ///   ——只有第一个词坏了，**西里尔字母 `С` 被当成了拉丁字母 `C`**。
    ///
    ///   【根因分析】
    ///   西里尔字母表里有 С С Р Р О О М М Н Н Т Т Х Х У К В А Е 和拉丁字母
    ///   **长得完全一样**，没有任何图像特征能把它们区分开——
    ///   引擎唯一能依据的就是「我正在读哪种语言」。
    ///   三种语言一起挂时，模型要在三套字表之间反复权衡，
    ///   遇到同形字就容易滑向字母数量更多、体量更大的那套（拉丁）。
    ///
    ///   **只挂 `rus` 之后，模型没有别的选择，只能按西里尔字表走。**
    ///
    /// 【代价】
    ///   多初始化一个引擎约 100~150 毫秒，但**只付一次**，之后复用。
    /// </summary>
    private static TesseractEngine? GetEngine(string language)
    {
        if (!IsAvailable) return null;

        lock (Gate)
        {
            if (_engines.TryGetValue(language, out var cached)) return cached;

            try
            {
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
                string tessdata = Path.Combine(exeDir, "tessdata");
                if (!Directory.Exists(tessdata))
                {
                    AppLogger.Warn($"没找到语言包目录，英文/俄文将无法识别：{tessdata}");
                    return null;
                }

                long start = Environment.TickCount64;
                var engine = new TesseractEngine(tessdata, language);

                // ==========================================================
                // 【v0.3.1 起唯一保留的配置项，其余全部回退到默认值】
                //
                //   官方 FAQ 原文大意：
                //     「同一个 API 对象用来解码多个图像时，Tesseract 结果会不一致。
                //       把 `classify_enable_learning` 设为 0，
                //       或者用 `ClearAdaptiveClassifier()` 清除自适应数据。」
                //
                //   Tesseract 默认带着一个**自适应学习器**，
                //   它会拿**前面识别过的图**调整自己，越用越飘。
                //   我们把引擎缓存复用、连续识别几十次，
                //   于是同一段文字这轮全对、下轮就变形：
                //     R8  `config.txt` ⇄ `сопйддхе` 来回跳
                //     R7  `МБ` ⇄ `MB` 来回跳
                //
                //   关掉之后**同一张图重复识别，结果必须一模一样**。
                //   这一条对我们是刚性要求：不然用户复测时结果随机变化，
                //   永远说不清到底是修好了还是没修好。
                //
                //   【为什么只留这一条】
                //   v0.3.1 一次改了五样（PSM、放大倍数、模型、白边、DPI），
                //   结果英文模型从 0.95 掉到过不了门槛，**谁也说不清是哪样害的**。
                //   这一轮的原则是：**退回被实测验证过的配置，只加最有把握的一条。**
                // ==========================================================
                try
                {
                    engine.SetVariable("classify_enable_learning", "0");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"配置项 classify_enable_learning 设置失败（模型 {language}）：" +
                                   $"{ex.GetType().Name} {ex.Message}");
                }

                // ==========================================================
                // 【2026-10-13 第一次单独试这个开关：tessedit_pageseg_mode = 6】
                //
                //   【它是什么】
                //     官方参数 `tessedit_pageseg_mode`（简称 psm），决定引擎**怎么切行**。
                //     我们没设过，走的是默认的 3（PSM_AUTO_FULLPAGE）——
                //     引擎会把图当成「一整页」，自己去猜版面有几个块。
                //
                //   【为什么改成 6】
                //     我们喂进去的**永远是用户框出来的一块**，不是整页：
                //       3 = 当成一整页，自己猜版面
                //       6 = 当成**一整块整齐的文字**（官方对「裁剪出来的一块」的推荐）
                //       7 = 当成**单独一行**
                //     选 6 而不是 7：用户经常框两三行（材料1 的长段落就是两行），
                //     psm 7 遇到多行会**只取一行**，把别的行丢掉。
                //
                //   【要治的病：E6 的「editable 被劈成 edi able」】
                //     劈词是**空格插在哪**的问题，而空格位置正是由切行/分段决定的。
                //     官方 ImproveQuality 页建议截图类小区域改用 psm 7/13。
                //     **改 psm 属于「从源头改」**，比事后投票便宜得多。
                //
                //   【为什么这次敢单独试】
                //     v0.3.1 那次一次改了五样（PSM、放大倍数、模型、白边、DPI），
                //     结果英文模型从 0.95 掉到过不了门槛，**说不清是哪样害的**——
                //     所以那一版把 PSM 撤了（见上面那段注释）。
                //     这一轮**只动 psm 这一条**，而且现在日志能逐次看清每次识别的结果，
                //     真出问题一眼就能定位。
                //
                //   【官方文档里 LSTM 引擎是支持的】
                //     man page 给每个参数标了适用引擎，`tessedit_pageseg_mode` 标的是 **Both**。
                // ==========================================================
                try
                {
                    engine.SetVariable("tessedit_pageseg_mode", PageSegModeUniformBlock);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"配置项 tessedit_pageseg_mode 设置失败（模型 {language}）：" +
                                   $"{ex.GetType().Name} {ex.Message}");
                }

                _engines[language] = engine;
                AppLogger.Info($"Tesseract 引擎已就绪：模型 {language}，耗时 {Environment.TickCount64 - start} 毫秒");
                return engine;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Tesseract 引擎（{language}）初始化失败：{DescribeException(ex)}");
                return null;
            }
        }
    }

    /// <summary>
    /// 初始化引擎。整个程序最多成功一次。
    /// 失败就记下来，之后不再重试（免得每次框选都白等）。
    ///
    /// 【为什么挂三种语言 chi_sim+eng+rus】
    ///   用户要翻英文和俄文，而**俄文本机 Windows OCR 装不了**。
    ///   一次挂三种，让 Tesseract 自己按字形判断，
    ///   这样不管框到中文、英文还是俄文都能认，不必按语种分三次跑。
    /// </summary>
    public static void EnsureInitialized()
    {
        // 【2026-10-09】这一步现在只负责「把原生库预加载好 + 检查语言包在不在」，
        // **不再预先建引擎**。
        // 原因：每种语言各建一个引擎（rus / eng），而多数使用场景只会用到一种，
        // 预先全建等于每次启动都白付 200~300 毫秒。
        // 真正的引擎在第一次用到该语种时由 GetEngine 按需创建。
        if (_initFailed) return;

        lock (Gate)
        {
            if (_initFailed) return;

            AppSettings.Load();

            // 用户主动要求「只用 Windows 自带」时才不启用（那是对照排查用的）
            if (AppSettings.WindowsOnly)
            {
                AppLogger.Info("已设置「只用 Windows 自带」，不启用 Tesseract（英文/俄文会明显变差）");
                return;
            }

            try
            {
                string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;

                // 预加载原生库（非单文件发布时其实用不上，但留着无害，
                // 万一将来换打包方式也能多一层保险）
                NativeLibraryPreloader.PreloadTesseractChain(exeDir);

                string tessdata = Path.Combine(exeDir, "tessdata");
                if (!Directory.Exists(tessdata))
                {
                    _initFailed = true;
                    AppLogger.Warn($"没找到语言包目录，英文/俄文将无法识别：{tessdata}");
                    return;
                }

                // 只检查**语言包文件在不在**，不真的建引擎——
                // 缺哪个语言就在真正用到它时才失败，错误信息更准确
                // （现在是「等框到俄文才发现没装俄文模型」，太晚）。
                var missing = new List<string>();
                foreach (string code in new[] { Languages.Chinese, Languages.English, Languages.Russian })
                {
                    string file = Path.Combine(tessdata, code + ".traineddata");
                    if (!File.Exists(file)) missing.Add($"{code}.traineddata");
                }

                if (missing.Count > 0)
                {
                    _initFailed = true;
                    AppLogger.Warn($"语言包不完整，缺：{string.Join("、", missing)}（目录 {tessdata}）");
                    return;
                }

                AppLogger.Info($"Tesseract 可用：语言包 {tessdata}（中/英/俄齐备，" +
                               "按语种分开加载，英文与俄文由它负责，中文仍走 Windows 自带）");
            }
            catch (Exception ex)
            {
                _initFailed = true;
                AppLogger.Warn($"Tesseract 引擎初始化失败，英文/俄文将退回到 Windows 自带（质量会明显变差）：" +
                            $"{DescribeException(ex)}");
            }
        }
    }

    /// <summary>
    /// 把异常一层一层拆开写出来。
    ///
    /// 【2026-10-10 换引擎 5.5.3 时踩到的】
    ///   原来只写 `ex.GetType().Name + ex.Message`，于是日志里全是同一句
    ///     `TargetInvocationException Exception has been thrown by the target of an invocation.`
    ///   **这句话等于没说**——
    ///   真正的原因（到底是哪个函数找不到）藏在 InnerException 里，
    ///   而 .NET 封装是反射调用，**一层套一层，最深的有四五层**。
    ///   结果就是：**知道失败了，却不知道为什么失败，只能靠猜。**
    ///
    ///   这次换引擎最大的教训：
    ///   我拿「35 个库全装上了」当成「引擎能用了」——
    ///   **库能装进内存，和封装能驱动它，是两回事。**
    /// </summary>
    private static string DescribeException(Exception ex)
    {
        var parts = new System.Text.StringBuilder();
        int depth = 0;
        for (Exception? cur = ex; cur != null && depth < 8; cur = cur.InnerException, depth++)
        {
            if (depth > 0) parts.Append(" → 原因：");
            parts.Append(cur.GetType().Name).Append(' ').Append(cur.Message);
        }
        return parts.ToString();
    }

    /// <summary>
    /// 【2026-10-12 新加 · 只打日志，不改任何识别行为】
    /// 让引擎**逐词自报**：这个词多少分、在不在词典里。
    ///
    /// 【要解决什么】
    ///   E5、E6、X2 这三段的整句平均分都在 0.90~0.95 看着很稳，
    ///   可 E5 只有 52%、X2 只有 64% 正确。
    ///   **错的是那几个符号，平均分被整句的字稀释掉了，看不出来。**
    ///
    ///   而现在也没别的办法：E5、E6 整段只跑了一次识别，
    ///   **只有一张选票，投票无从下手**；X2 有三票但 2:1 站在错的那边。
    ///   所以得先让引擎自己说：**哪个词它没把握。**
    ///
    /// 【为什么单独 try】
    ///   外面那个 catch 会把任何异常变成「识别失败、结果为空」。
    ///   诊断代码一旦出错，**会把好好的识别结果弄丢**——
    ///   所以这里自己兜住，坏了就当没打这条日志，**绝不影响识别**。
    ///
    /// 【为什么加在类的最后面】
    ///   这个类中间有大量大括号，插在中间改过一次就把括号平衡弄坏过。
    ///   **以后也一律加在最后。**
    /// </summary>
    /// <returns>
    ///   这一句里**分数最低的那个词**的分数（0~100）；
    ///   一个词都没取到、或者中途出错，返回 null（调用方按「没发现问题」处理）。
    /// </returns>
    private static float? LogWordSelfReport(Page page, string language)
    {
        try
        {
            var report = new System.Text.StringBuilder();
            int count = 0;
            float lowest = 100f;

            using var it = page.GetIterator();
            it.Begin();
            do
            {
                string word = it.GetText(PageIteratorLevel.Word) ?? "";
                if (string.IsNullOrWhiteSpace(word)) continue;

                float wordConf = it.GetConfidence(PageIteratorLevel.Word);
                bool inDict = it.GetWordIsFromDictionary();
                if (wordConf < lowest) lowest = wordConf;

                if (count > 0) report.Append(" ｜ ");
                report.Append('【').Append(word.Trim())
                      .Append('】').Append(' ').Append(wordConf.ToString("F2"))
                      .Append(inDict ? " 词典✓" : " 词典✗");
                count++;
            }
            while (it.Next(PageIteratorLevel.Word));

            if (count > 0)
            {
                AppLogger.Info($"  词级自报（{language}）共 {count} 词，最低 {lowest:F2}：" + report);
            }
            else
            {
                AppLogger.Info($"  词级自报（{language}）：**一个词都没取到**（这条诊断本身可能失效了）");
            }

            return count > 0 ? lowest : (float?)null;
        }
        catch (Exception ex)
        {
            // 诊断代码失败**绝不能影响识别**，所以只记一行，不往上抛。
            AppLogger.Warn($"词级自报没跑成（不影响识别）：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    // ==============================================================
    // 【2026-10-13 撤掉「补跑几路」诊断】
    //
    //   这段是我为了查「几路里到底有没有一份是对的」临时加的：
    //   一旦最低词级分低于 50，就**另外多跑 6 次识别**（红/绿/蓝/灰度/局部阈值/红通道不二值化），
    //   每路一次 200~450 毫秒，**结果只打印、一律不作数**。
    //
    //   实测代价（用户 2026-10-13 测三份材料时发现「识别变得好长」）：
    //     `总耗时=3797 毫秒（识别耗时 31 毫秒）`
    //   ——真正的识别只用 31 毫秒，**3700 毫秒全花在这次诊断上**。
    //
    //   结论已经拿到了：三份材料整体过完（材料1 11/11、材料2 18/20、材料3 25/25），
    //   而「正确答案一直在候选里、被择优规则丢掉」这件事，
    //   在 B3、E5 上早就各有一次实例佐证，不需要再靠补跑来发现。
    //   → **整段删除**。识别速度回到真实耗时。
    //
    //   保留的还有 `LogWordSelfReport`（词级自报）——
    //   它是在原来那一趟遍历里顺手取最低分，**不多跑一次识别**，没有速度代价，
    //   而且「某个词分数 0.00 → 那次一定错」这条是用户实测出来的硬事实。
    // ==============================================================
}