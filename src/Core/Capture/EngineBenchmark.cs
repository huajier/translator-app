using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 「认字引擎到底还有没有提升空间」的实测台（2026-10-08）。
///
/// 【为什么要有这个】
///   用户问「是不是已经到引擎极限了」。这个问题**不能靠看几张图回答**——
///   看图只能看出「好/不好」，看不出「还能提升多少」。
///   所以这里做一件更硬的事：
///
///   1. **先画一张自己知道原文的图**（标准答案 / ground truth）；
///   2. 让不同引擎 + 不同预处理去认它；
///   3. 把认出来的字和标准答案**逐字比对**，算出一个 0~100 的准确率。
///
///   这样「哪个引擎更准」就从感觉变成了数字。
///
/// 【为什么逐字比对要按「字符相似度」算，而不是「完全一样才算对」】
///   OCR 认错一个偏旁是很常见的，直接比全等的话任何引擎都是 0 分，
///   就没有区分度了。所以用编辑距离算相似度：
///   「认对了 9 个字错 1 个」→ 90 分，这样才有意义。
///
/// 【这个文件只做实验，不参与正式取词流程】
///   正式流程还是 OcrTextCapture。这个类只从命令行 --引擎对比 调起来。
/// </summary>
internal sealed class EngineBenchmark
{
    /// <summary>一组测试：一个场景 + 一行**已知原文**。</summary>
    /// <summary>
    /// 一条测试用例。
    /// <paramref name="imagePath"/> 有值就用这张现成的图（真实屏幕截图），
    /// 没有就按 <paramref name="text"/> 自己画一张。
    /// </summary>
    private sealed record Case(string Name, string Text, string? ImagePath = null);

    /// <summary>
    /// 要试的字号（像素）。
    ///
    /// 【这一条是整个实验最要紧的地方 —— 第一版实验栽在这里】
    ///   第一次画测试图时我把字号设成 48 像素，测出来「Windows 自带全面碾压」。
    ///   但 48 像素的字大得离谱，OCR 认起来太轻松了，
    ///   而**用户实际框选的是屏幕上的正文**，普遍只有 12~18 像素。
    ///
    ///   小字号才是 OCR 真正的战场，也是「放大 2 倍」这个功能存在的意义。
    ///   所以这里把字号当成一个变量，让它从小到大都测一遍，
    ///   否则测出来的结论只在我自己画的图上成立，对用户没有意义。
    ///
    ///   14 = 常见的正文小字；18 = 稍大一点的界面字；28 = 大标题（基本白送）。
    /// </summary>
    private static readonly int[] FontSizes = [14, 18, 28];

    /// <summary>
    /// 测试用例。挑的是用户 2026-10-08 亲口报过认不准的那几类：
    /// 橙底绿字、复杂背景、符号、生僻字，另外补上最基础的常规中文做基准线。
    /// </summary>
    private static Case[] ChineseCases =>
    [
        new("常规中文",   "今天天气不错适合出门"),
        new("橙底绿字",   "绿色中文测试识别效果如何"),
        new("深色背景浅字", "深色背景上的浅色中文"),
        new("中英混排",   "API 接口 v2.0 已发布 2024年"),
        new("符号",       "第 § ‡ ※ → ★ ☆ 全部"),
        new("生僻字",     "鑫淼懿燊囍龘麤靐"),
    ];

    /// <summary>
    /// 英文用例（2026-10-09 加）。
    ///
    /// 【为什么这批最重要】
    ///   用户说了真实用途：**主要翻译英文和俄文**，中文认差点可以接受。
    ///   而英文认错**不可接受**，症状有三条：
    ///     ① 长英文识别率极差、经常认成乱码；
    ///     ② 稍带一点符号就乱成一团；
    ///     ③ 乱加空格（词内加空格，读起来根本不像话）。
    ///   所以用例必须**逐条对上这三条**，否则测了也白测。
    ///   （第 ③ 条还有专门的度量方式，见 Similarity 的英文处理。）
    /// </summary>
    private static Case[] EnglishCases =>
    [
        // ① 短句：最基本的底线
        new("英文短句", "The quick brown fox jumps over the lazy dog"),

        // ① 长英文：用户说「长的差得可怜、常被识别成乱码」
        //    特意用一整段连续英文，接近真实网页段落
        new("英文长段",
            "Machine translation is a type of computational linguistics where sentences are translated " +
            "from one language to another by means of a computer program without the need of a human " +
            "translator"),

        // ② 带符号：用户说「稍微带一点符号就乱成一团」
        //    涵盖连字符、括号、引号、百分号、斜杠、@、点、冒号
        new("英文带符号",
            "Error 404 (Not Found): the file \"config.txt\" wasn't found - try /help?id=123 or email me@example.com (50% off)"),

        // ② 带符号 + 大小写混杂 + 缩写 + 数字
        new("英文技术串",
            "HTTP/2.0 2024-05-01 API v3.1 - 85% CPU, RAM 16GB, ID #A17f-9x, e-mail: dev@corp.io; see p.42."),

        // 混合：英文里夹数字和单位，最常见的现实情况
        new("英文数字混排", "The file is 2.5 MB and takes 30 seconds to load on a 1080p monitor at 60 FPS"),
    ];

    /// <summary>
    /// 俄文用例（2026-10-09 加）。
    /// 用户明说也要翻俄文，而**本机 Windows OCR 根本没装俄文识别包**，
    /// 所以俄文只能靠 Tesseract——这一点必须用数据坐实。
    /// </summary>
    private static Case[] RussianCases =>
    [
        new("俄文短句", "Съешь же ещё этих мягких французских булок да выпей чаю"),
        new("俄文长段",
            "Машинный перевод — это раздел компьютерной лингвистики, в котором предложения " +
            "переводятся с одного языка на другой с помощью компьютерной программы"),
        new("俄文带符号",
            "Ошибка 404 (страница не найдена): файл \"config.txt\" не найден — попробуйте /help?id=123"),
        new("俄文数字混排", "Файл занимает 2,5 МБ и загружается 30 секунд на мониторе 1080p при 60 FPS"),
    ];

    /// <summary>
    /// 用户 2026-10-09 亲自框选、且**当场认错**的真实内容。
    ///
    /// 【为什么把这几段单独拎出来】
    ///   实测里 A4「英文技术串」只有 78 分，日志显示走的是 Windows 自带，
    ///   理由是「Tesseract 认得更少（60 &lt; 62），保留 Windows 自带」。
    ///   但那条 Windows 的结果是：
    ///     `HTTP/2.0 2024 一 05 一 01 API v 土 1 - 85 ％ CPU, RAM 16G 巳 ID # A17f 一 9 ...`
    ///   ——`3` 认成 `土`、`B` 认成 `巳`、`-` 认成 `一`、`.` 认成 `·`。
    ///   **中文引擎把符号认成了汉字，而这些「汉字」被当成有效字符计数，
    ///   字数于是虚高，反倒把 Tesseract 挤掉了。**
    ///
    ///   这正是用户说的「稍微带一点符号就乱成一团」。
    ///   下面这几段就是查证「到底该信哪个引擎」的**原始材料**。
    /// </summary>
    private static Case[] RealWorldCases =>
    [
        // A3 · 英文带符号（实测 95 分）
        new("实测A3符号",
            "Error 404 (Not Found): the file \"config.txt\" wasn't found - try /help?id=123 or e-mail me@example.com (50% off)"),
        // A4 · 英文技术串（实测 78 分，最差的一段）
        new("实测A4技术串",
            "HTTP/2.0 2024-05-01 API v3.1 - 85% CPU, RAM 16GB, ID #A17f-9x, e-mail: dev@corp.io; see p.42."),
        // B3 · 俄文带符号（实测 97 分）
        new("实测B3俄文符号",
            "Ошибка 404 (страница не найдена): файл \"config.txt\" не найден — попробуйте /help?id=123"),
        // D1 · 中英混排（实测 93 分）
        new("实测D1中英混排",
            "使用 Windows 10 系统，API 版本 v2.0 已于 2024 年发布"),
        // C2 · 中文长段（实测 97 分，走 Windows 自带）
        new("实测C2中文长段",
            "光学字符识别是指计算机识别图像中的文字并将其转换为可编辑文本的过程，它在扫描文档、电子书、票据录入等场景中有非常广泛的应用。"),
    ];

    /// <summary>
    /// 从**真实屏幕截图**读用例，而不是自己画。
    ///
    /// 【为什么必须有这个模式（2026-10-09 用户实测打脸）】
    ///   同一句俄文 `Съешь же ещё этих мягких французских булок да выпей чаю`：
    ///     本程序自己画的测试图  → **100 分**，认得完全正确；
    ///     用户真实屏幕上的内容  → 89 分，`Съешь` 变成 `Cbeuub`。
    ///   **同一个引擎、同一个模型，差了 11 分。**
    ///
    ///   原因：GDI+ 画的字和浏览器 ClearType 渲染的字，
    ///   抗锯齿、灰度过渡、字距全都不同——
    ///   自己画的图**比真实屏幕干净太多**，所以分数虚高。
    ///
    ///   **教训：测量仪不准的时候，先怀疑测量仪。**
    ///   从此「自己画图」的分数只作参考，真实结论以用户实测 / 真实截图为准。
    ///
    /// 【素材从哪来】
    ///   `tools\素材\截图取材.html` 每块内容占满整个窗口，
    ///   用脚本逐块截图，存成 PNG；
    ///   同目录的 `manifest.txt` 每行是 `编号|语种|标准答案`。
    /// </summary>
    private static Case[] LoadCasesFromDisk(string imageDir)
    {
        var list = new List<Case>();
        string manifest = Path.Combine(imageDir, "manifest.txt");
        if (!File.Exists(manifest))
            throw new FileNotFoundException($"缺少清单文件：{manifest}");

        foreach (string line in File.ReadAllLines(manifest))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            string[] parts = line.Split('|', 3);
            if (parts.Length < 3) continue;

            string id = parts[0].Trim();
            string expected = parts[2].Trim();
            string png = Path.Combine(imageDir, id + ".png");
            if (!File.Exists(png))
            {
                AppLogger.Warn($"清单里有 {id}，但找不到对应的图：{png}");
                continue;
            }

            // 把图路径塞进 Expected 的方式不可行（要靠图才能画），
            // 所以改用「延迟画图」：Case 允许带一个已存在的图。
            list.Add(new Case(id, expected, png));
        }
        return list.ToArray();
    }

/// <summary>
    /// 本次要跑哪一批用例。
    /// 命令行 `--引擎对比 实测` 跑用户实测内容；`真实` 跑真实屏幕截图；
    /// `中文` 只跑中文；默认跑「英俄」。
    /// </summary>
    private static Case[] SelectCases(string which) => which switch
    {
        "中文" => ChineseCases,
        "实测" => RealWorldCases,
        "真实" => LoadCasesFromDisk(ScreenshotDir),
        "全部" => [.. ChineseCases, .. EnglishCases, .. RussianCases, .. RealWorldCases],
        _ => [.. EnglishCases, .. RussianCases],
    };

    /// <summary>
    /// 真实截图素材目录。命令行没给就用环境变量
    /// <c>OCR_SHOT_DIR</c>，再没有就退回临时目录下的 <c>真实截图</c>。
    /// </summary>
    private static string ScreenshotDir =>
        Environment.GetEnvironmentVariable("OCR_SHOT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "真实截图");

    /// <summary>要比的引擎。</summary>
    private sealed record EngineSpec(string Name, Func<Bitmap, string> Recognize);

    /// <summary>
    /// 「引擎」里有一项其实不是引擎，是**整条正式流水线**。
    /// 把它混在引擎列表里一起排名，是为了让人一眼看出：
    /// 用户实际感受到的水平，跟「最优的单次配置」差多少。
    /// 它是**独立跑一遍**的（每个用例都从原图重新开始），不参与其他组合。
    /// </summary>
    private static readonly HashSet<string> PipelineOnly = ["正式流水线"];

    /// <summary>要比的预处理。Windows 自带和 Tesseract 都能用同一套，所以放一起比。</summary>
    private sealed record PreprocessSpec(string Name, Func<Bitmap, Bitmap> Apply);

    public static void Run(string outputDir, string tessdataRoot, string whichCases)
    {
        Directory.CreateDirectory(outputDir);
        Case[] cases = SelectCases(whichCases);
        AppLogger.Info($"========== 引擎对比实验开始（{whichCases}）==========");

        // ---------- 1) 造出带标准答案的测试图 ----------
        // 每个场景 × 每个字号 = 一张图。字号小的图高度要够，
        // 否则字被裁掉了，测出来的是「图不对」不是「引擎不行」。
        var images = new List<(Case Case, int FontSize, Bitmap Image)>();
        foreach (Case c in cases)
        {
            // 【真实截图】这类用例图是现成的，直接用，不按字号重画。
            // 真实内容的字号、抗锯齿、背景**必须原样保留**——
            // 重新画一遍就又回到「自己画的图比真实屏幕干净」的老问题上去了。
            if (c.ImagePath is not null)
            {
                var loaded = new Bitmap(c.ImagePath);
                images.Add((c, 0, loaded));
                continue;
            }

            foreach (int size in FontSizes)
            {
                Bitmap bmp = RenderSample(c, size);
                string path = Path.Combine(outputDir, $"{Safe(c.Name)}-{size}px.png");
                bmp.Save(path, ImageFormat.Png);
                images.Add((c, size, bmp));
            }
        }
        AppLogger.Info($"已准备测试图：{cases.Length} 个场景，共 {images.Count} 张");

        // ---------- 2) 待比的引擎与预处理 ----------
        var engines = new List<EngineSpec>
        {
            // 整条正式流水线：单独跑，不配任何预处理（它自己会处理）
            new("正式流水线", ProductionPipelineRunner),
            new("Win-中文引擎", WindowsOcrRunner),
            new("Win-英文引擎", WindowsEnRunner),
            new("Tess-fast",   TessRunner("fast", "chi_sim+eng+rus", tessdataRoot)),
            new("Tess-best",   TessRunner("best", "chi_sim+eng+rus", tessdataRoot)),
            new("Tess-标准",   TessRunner("standard", "chi_sim+eng+rus", tessdataRoot)),
            // 【2026-10-09 用户实测后加】
            //   用户反馈「俄文识别出来纯俄文字变成英文俄文混用了」。
            //   怀疑是 `chi_sim+eng+rus` **一次挂三种语言**导致的：
            //   Tesseract 面对西里尔 С 和拉丁 C 这种完全同形的字母时，
            //   挂的语言越多越容易在两者之间摇摆。
            //   这几项就是用来验证「**按语种只挂一种**能不能解决」的。
            new("Tess-仅俄文", TessRunner("fast", "rus", tessdataRoot)),
            new("Tess-俄英",   TessRunner("fast", "rus+eng", tessdataRoot)),
            new("Tess-仅英文", TessRunner("fast", "eng", tessdataRoot)),
        };

        var preprocesses = new List<PreprocessSpec>
        {
            new("原样",       b => (Bitmap)b.Clone()),
            new("放大2倍",   b => ImagePreprocessor.Preprocess(b)),
            new("放大3倍",   b => Scale(b, 3)),
            new("二值化",     b => Otsu(b)),
            new("2倍+二值",  b => Otsu(ImagePreprocessor.Preprocess(b))),
        };

        // ---------- 3) 逐个跑 ----------
        // 结果表：[引擎][预处理][场景+字号] = 准确率
        var table = new Dictionary<string, Dictionary<string, Dictionary<string, (int Score, long Ms, string Text)>>>();

        foreach (EngineSpec engine in engines)
        {
            table[engine.Name] = new();
            foreach (PreprocessSpec pre in preprocesses) table[engine.Name][pre.Name] = new();
        }
        foreach (EngineSpec engine in engines)
        foreach (PreprocessSpec pre in preprocesses)
        foreach ((Case c, int size, Bitmap src) in images)
        {
            // 「正式流水线」是自己会做预处理的，不能再被外面套一层
            if (PipelineOnly.Contains(engine.Name) && pre.Name != preprocesses[0].Name) continue;

            Bitmap? work = null;
            string key = $"{c.Name}@{size}px";
            try
            {
                work = PipelineOnly.Contains(engine.Name)
                    ? (Bitmap)src.Clone()
                    : pre.Apply(src);
                long t0 = Environment.TickCount64;
                string got = engine.Recognize(work) ?? "";
                long ms = Environment.TickCount64 - t0;

                table[engine.Name][pre.Name][key] = (Similarity(c.Text, got), ms, got);
            }
            catch (Exception ex)
            {
                AppLogger.Info($"  组合失败：{engine.Name} / {pre.Name} / {key} —— {ex.GetType().Name} {ex.Message}");
                table[engine.Name][pre.Name][key] = (-1, 0, $"【失败】{ex.GetType().Name}");
            }
            finally
            {
                work?.Dispose();
            }
        }

        // ---------- 4) 出报告 ----------
        PrintReport(table, preprocesses, engines, cases);

        foreach ((_, _, Bitmap bmp) in images) bmp.Dispose();
        AppLogger.Info("========== 引擎对比实验结束 ==========");
    }

    /// <summary>
    /// 按**正式取词流程的步骤**跑一遍：原图认一次 → 放大2倍+二值化认一次 → 按评分择优。
    ///
    /// 【为什么单独列这一项，而不是只看单次预处理】
    ///   上面的实验测的都是「单个引擎 + 单种预处理」的孤立结果，
    ///   但用户实际用的是**整条流水线**，最终输出的是**其中一次的结果**。
    ///   只有把整条流水线跑一遍，才知道改完之后用户到底感觉到多少提升。
    ///
    /// 【这一项和线上代码的关系 —— 必须说清楚，别过度解读】
    ///   这里用的是**和正式流程一模一样的生产方法**
    ///   （ImagePreprocessor.PreprocessAndBinarize、OcrQualityScorer.Score、
    ///     TextNormalizer.Normalize），但**流程本身是在实验台里复刻的**，
    ///   不是直接调 OcrTextCapture.CaptureAsync。
    ///   原因：那条方法内部会去截屏，要测它就得往正式代码里插测试钩子，
    ///   那样改动风险太大（阶段 3 已验收功能不能冒风险）。
    ///   所以：方法同源、流程复刻。真上线前还要再用真实框选复测一遍。
    /// </summary>
    private static string ProductionPipelineRunner(Bitmap bmp)
    {
        try
        {
            // ---- 第一遍：原图（和正式流程一致，走灰度化）----
            using (Bitmap pass1Image = ImagePreprocessor.ToGrayscaleOnly(bmp))
            {
                string pass1 = Clean(WindowsOcrRunner(pass1Image));

                // 和正式流程一样：字太少、或者判定是中文，才值得跑第二遍
                int firstChars = TextNormalizer.CountMeaningfulChars(pass1);
                if (!(firstChars < 2 || LanguageDetector.IsMostlyChinese(pass1)))
                {
                    return pass1;   // 非中文且字够多，就用第一遍（和线上一致）
                }

                // ---- 第二遍：放大 2 倍 + 二值化（2026-10-08 新增）----
                string pass2;
                using (Bitmap pass2Image = ImagePreprocessor.PreprocessAndBinarize(bmp))
                {
                    pass2 = Clean(WindowsOcrRunner(pass2Image));
                }

                // ---- 择优：默认信第二遍，只有它明显坏了才退回（理由见 OcrTextCapture 注释）----
                int secondChars = TextNormalizer.CountMeaningfulChars(pass2);
                bool bad = TextNormalizer.LooksLikeGarbage(pass2)
                           || secondChars < 2
                           || secondChars * 2 < firstChars
                           || OcrQualityScorer.Score(pass2) + 30 < OcrQualityScorer.Score(pass1);

                return bad ? pass1 : pass2;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info($"    正式流程模拟出错：{ex.GetType().Name} {ex.Message}");
            return "";
        }
    }


    private static void PrintReport(
        Dictionary<string, Dictionary<string, Dictionary<string, (int Score, long Ms, string Text)>>> table,
        List<PreprocessSpec> preprocesses,
        List<EngineSpec> engines,
        Case[] cases)
    {
        AppLogger.Info("");
        AppLogger.Info("【准确率总表】0=完全认错，100=一字不差");

        // ---- 先按字号分开看 ----
        // 【为什么必须分字号看】
        //   大字的分数普遍很高（好几个组合都是 100 分），
        //   全堆在一起平均会把差距抹平，看起来「怎么配都差不多」，
        //   那正是第一版实验的毛病。
        //   真实场景以小字为主，所以小字号那一栏才是决定性的。
        foreach (int size in FontSizes)
        {
            AppLogger.Info("");
            AppLogger.Info($"  ===== 字号 {size} 像素 =====");

            var ranked = new List<(string Label, double Avg, long Ms)>();
            foreach (EngineSpec e in engines)
            foreach (PreprocessSpec p in preprocesses)
            {
                if (PipelineOnly.Contains(e.Name) && p.Name != preprocesses[0].Name) continue;

                var row = table[e.Name][p.Name];
                var cells = row
                    .Where(kv => kv.Key.EndsWith($"@{size}px"))
                    .Select(kv => kv.Value)
                    .Where(v => v.Score >= 0)
                    .ToList();
                if (cells.Count == 0) continue;
                double avg = cells.Average(v => v.Score);
                double ms = cells.Average(v => (double)v.Ms);
                ranked.Add(($"{e.Name}/{p.Name}", avg, (long)ms));
            }

            foreach (var (label, avg, ms) in ranked.OrderByDescending(x => x.Avg))
            {
                AppLogger.Info($"    {label,-24} {avg,5:F1} 分   约 {ms,4} 毫秒");
            }
        }

        // ---- 全部字号的整体平均 ----
        AppLogger.Info("");
        AppLogger.Info("【全字号总平均】");
        var best = new List<(string Label, double Avg, long Ms)>();
        foreach (EngineSpec e in engines)
        foreach (PreprocessSpec p in preprocesses)
        {
            if (PipelineOnly.Contains(e.Name) && p.Name != preprocesses[0].Name) continue;

            var row = table[e.Name][p.Name];
            var cells = row.Values.Where(v => v.Score >= 0).ToList();
            if (cells.Count == 0) continue;
            double avg = cells.Average(v => v.Score);
            double ms = cells.Average(v => (double)v.Ms);
            best.Add(($"{e.Name}/{p.Name}", avg, (long)ms));
        }
        foreach (var (label, avg, ms) in best.OrderByDescending(x => x.Avg).Take(8))
        {
            AppLogger.Info($"  {label,-24} {avg,5:F1} 分   约 {ms,4} 毫秒");
        }

        AppLogger.Info("");
        AppLogger.Info("【正式流程说明】");
        AppLogger.Info("  改动前：第一遍原图 → 第二遍放大2倍+灰度 → 「谁字多谁上」");
        AppLogger.Info("  改动后：第一遍原图 → 第二遍放大2倍+**二值化** → 「谁更像人话谁上」");
        AppLogger.Info("  表里「正式流水线/原样」那一行就是改动后的整条流水线的实际表现");
        // ---- 逐场景排名 ----
        // 【真实截图那一批也要能看】
        //   真实截图没有「字号」概念（字号是 0 的占位），
        //   所以这里按用例实际用的那个键去查，而不是写死 @14px，
        //   否则真实截图这一批会一条排名都打不出来。
        bool hasRealShots = cases.Any(c => c.ImagePath is not null);
        AppLogger.Info("");
        AppLogger.Info(hasRealShots
            ? "【逐场景排名 · 真实屏幕截图】"
            : "【小字号（14px，贴近真实屏幕正文）逐场景排名】");
        foreach (Case c in cases)
        {
            string key = hasRealShots && c.ImagePath is not null
                ? $"{c.Name}@0px"
                : $"{c.Name}@14px";
            AppLogger.Info($"  --- {c.Name}：{c.Text}");
            var lines = new List<(string Label, int Score, string Text)>();
            foreach (EngineSpec e in engines)
            foreach (PreprocessSpec p in preprocesses)
            {
                if (!table[e.Name][p.Name].TryGetValue(key, out var v)) continue;
                if (v.Score >= 0) lines.Add(($"{e.Name}/{p.Name}", v.Score, v.Text));
            }
            foreach (var (label, score, _) in lines.OrderByDescending(x => x.Score).Take(4))
            {
                AppLogger.Info($"      {label,-24} {score,3} 分");
            }
            // 打印最高分那一版的实际输出。
            // 【别写成 lines.First() —— 那样打出来的是列表里第一个，
            //   也就是「正式流水线」的结果，和上面列的分数对不上，
            //   看报告的人会以为分数算错了。这是个真踩过的坑。】
            var top = lines.OrderByDescending(x => x.Score).FirstOrDefault();
            if (top.Text is not null && top.Text.Length > 0)
            {
                AppLogger.Info($"      最高分 {top.Label} 认出：{Clean(top.Text)}");
            }
        }
    }

    /// <summary>日志里把换行压掉，免得报告被撑烂。</summary>
    private static string Clean(string s)
        => s.Replace("\r", " ").Replace("\n", " ").Trim();

    // =====================================================================
    //  准确率算法
    // =====================================================================
    /// <summary>
    /// 逐字相似度：认对了多少（0~100）。
    /// 用编辑距离算——认错一个字只扣一点点，比「全等才算对」有区分度得多。
    ///
    /// 【针对英文的三处特别处理 —— 用户 2026-10-09 点名的三个症状】
    ///
    ///   ① **词内多余空格要扣分**。
    ///      用户抱怨「乱加空格」。但打比方说
    ///      "quick brown fox" 被认成 "quick b rown fox"，
    ///      如果先把所有空格删掉再比，这两个就变成一样的了，
    ///      分数会显示 100 分——**那就把用户最烦的问题掩盖掉了**。
    ///      所以这里**不删空格**，让词内多出来的空格如实扣分。
    ///      只把「连续多个空格」压成一个（那个纯粹是排版噪音，不该扣分）。
    ///
    ///   ② **大小写不敏感**：OCR 常把 I/l、O/0 认错，
    ///      这属于识别错误，不该因为大小写再罚一次，所以统一转小写。
    ///
    ///   ③ **连字符/撇号保留**：这些是有意义的符号，不能当噪音删掉，
    ///      删了就等于放过「符号认错」这类错误。
    /// </summary>
    public static int Similarity(string expected, string actual)
    {
        string a = NormalizeForCompare(expected);
        string b = NormalizeForCompare(actual);
        if (a.Length == 0) return 0;
        if (b.Length == 0) return 0;

        int dist = Levenshtein(a, b);
        return (int)Math.Round(100.0 * (1.0 - (double)dist / Math.Max(a.Length, b.Length)));
    }

    /// <summary>
    /// 比对前的归一化。
    /// 【为什么不再无条件删掉所有空白】——见 Similarity 的说明 ①。
    /// 保留单空格（词与词之间、词内多出来的都如实保留），
    /// 只把「连续空格」压成一个，把换行/制表符换成空格。
    /// </summary>
    private static string NormalizeForCompare(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool lastWasSpace = false;
        foreach (char c in s)
        {
            char ch = char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c);
            if (ch == ' ')
            {
                if (lastWasSpace) continue;   // 连续空格压成一个
                lastWasSpace = true;
            }
            else lastWasSpace = false;

            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static int Levenshtein(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
        return d[a.Length, b.Length];
    }

    // =====================================================================
    //  画测试图（标准答案的来源）
    // =====================================================================
    /// <summary>
    /// 按场景画一张测试图：白底黑字，或者按需要画成橙底绿字 / 深色背景。
    ///
    /// 【关键：字号必须真的按像素画，不能缩放一张大字图】
    ///   如果先画大字再整体缩小，笔画会糊成一团，
    ///   测出来的是「抗锯齿糊掉的假小字」，不是屏幕上真正的清晰小字，
    ///   那就又变成一个不能信的测量仪了。
    /// </summary>
    private static Bitmap RenderSample(Case c, int fontSize)
    {
        // 画布高度跟着字号走，并留出上下各 1.5 倍行距的空间，保证字不被裁掉
        int h = fontSize * 4;
        int w = 1400;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            // 注意：不用 StringFormat 居中，就从左上角开始，
            // 因为 OCR 对「文字贴边」更敏感，那才是屏幕上的常态
            float textX = 20f;
            float textY = (h - fontSize * 1.4f) / 2f;

            switch (c.Name)
            {
                case "橙底绿字":
                    using (var bg = new SolidBrush(Color.FromArgb(255, 200, 90))) g.FillRectangle(bg, 0, 0, w, h);
                    DrawText(g, c.Text, Color.FromArgb(255, 30, 160, 60), textX, textY);
                    break;

                case "深色背景浅字":
                    using (var bg = new SolidBrush(Color.FromArgb(255, 32, 32, 40))) g.FillRectangle(bg, 0, 0, w, h);
                    DrawText(g, c.Text, Color.FromArgb(255, 235, 235, 235), textX, textY);
                    break;

                default:
                    using (var bg = new SolidBrush(Color.White)) g.FillRectangle(bg, 0, 0, w, h);
                    // 「符号」这一项故意加一点浅灰竖线当干扰，贴近带下划线/分隔线的界面
                    if (c.Name == "符号")
                    {
                        using var line = new Pen(Color.FromArgb(255, 220, 220, 220), 2);
                        for (int lx = fontSize * 3; lx < w; lx += fontSize * 3) g.DrawLine(line, lx, 10, lx, h - 10);
                    }
                    DrawText(g, c.Text, Color.FromArgb(255, 15, 15, 15), textX, textY);
                    break;
            }
        }

        void DrawText(Graphics gg, string text, Color color, float px, float py)
        {
            using var font = new Font("Microsoft YaHei", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            gg.DrawString(text, font, brush, px, py);
        }

        return bmp;
    }

    // =====================================================================
    //  预处理变体
    // =====================================================================
    private static Bitmap Scale(Bitmap src, double factor)
    {
        int w = (int)(src.Width * factor), h = (int)(src.Height * factor);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, w, h));
        return bmp;
    }

    /// <summary>
    /// Otsu 自动阈值二值化。
    /// 【为什么值得试】OCR 引擎内部都假设「黑字白底」，
    /// 把图压成纯黑白之后，橙底绿字、深色背景这类反色/低对比的图会好转很多。
    /// </summary>
    private static Bitmap Otsu(Bitmap src)
    {
        var gray = ImagePreprocessor.ToGrayscaleOnly(src);
        try
        {
            int w = gray.Width, h = gray.Height;
            var data = gray.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int[] hist = new int[256];
            try
            {
                unsafe
                {
                    byte* p = (byte*)data.Scan0;
                    for (int i = 0; i < w * h; i++)
                    {
                        int b = p[i * 4];       // 32bppArgb 已是灰度，B/G/R 相等，取 B 即可
                        hist[b]++;
                    }
                }

                // Otsu：找让「前景/背景两类内部方差之和最大」的那个阈值
                int total = w * h;
                long sumAll = 0;
                for (int t = 0; t < 256; t++) sumAll += (long)t * hist[t];

                long sumB = 0; int wB = 0; double maxVar = -1; int threshold = 128;
                for (int t = 0; t < 256; t++)
                {
                    wB += hist[t];
                    if (wB == 0) continue;
                    int wF = total - wB;
                    if (wF == 0) break;

                    sumB += (long)t * hist[t];
                    double mB = (double)sumB / wB;
                    double mF = (double)(sumAll - sumB) / wF;
                    double between = (double)wB * wF * (mB - mF) * (mB - mF);
                    if (between > maxVar) { maxVar = between; threshold = t; }
                }

                var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var outData = outBmp.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                unsafe
                {
                    byte* src2 = (byte*)data.Scan0;
                    byte* dst = (byte*)outData.Scan0;
                    for (int i = 0; i < w * h; i++)
                    {
                        byte v = src2[i * 4] > (byte)threshold ? (byte)255 : (byte)0;
                        dst[i * 4] = v; dst[i * 4 + 1] = v; dst[i * 4 + 2] = v; dst[i * 4 + 3] = 255;
                    }
                }
                outBmp.UnlockBits(outData);
                return outBmp;
            }
            finally { gray.UnlockBits(data); }        }
        finally { gray.Dispose(); }
    }

    // =====================================================================
    //  引擎适配
    // =====================================================================
    /// <summary>
    /// 调 Windows 自带 OCR，**指定语言引擎**。
    ///
    /// 【2026-10-09 为什么必须把语言做成变量】
    ///   正式流程现在是**一律用中文引擎**认所有文字
    ///   （见 OcrEngineFactory.TryGetEngine 里那句
    ///     「中文引擎对英文照样能认，反之不成立，所以中文永远排前面」）。
    ///   这个说法在本机是**未经证实的**，而用户抱怨英文认不准。
    ///   Windows 的识别引擎本来就是**按语言分别训练**的，
    ///   用中文引擎去认英文，理论上要吃亏。
    ///
    ///   所以这里让「中文引擎」和「英文引擎」分别去认同一张英文图，
    ///   用数据决定那句话到底成不成立。**不能靠推理代替实测**——
    ///   这正是阶段 3 反复强调的：结论必须有证据。
    /// </summary>
    private static string WindowsOcrRunner(Bitmap bmp) => WindowsOcrWithLanguage(bmp, "zh-Hans-CN");

    /// <summary>用英文引擎认（如果本机没装英文识别包，返回空并记日志）。</summary>
    private static string WindowsEnRunner(Bitmap bmp) => WindowsOcrWithLanguage(bmp, "en-US");

    /// <summary>
    /// <summary>
    /// 用俄文引擎认。本机**没有装俄文识别包**（2026-10-09 已确认），
    /// 所以这一项会返回空——这本身就是一条重要结论：
    /// 俄文在 Windows 自带引擎上**根本走不通**，只能靠 Tesseract。
    /// </summary>
    private static string WindowsOcrWithLanguage(Bitmap bmp, string languageTag)
    {
        string result = "";

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var engine = Windows.Media.Ocr.OcrEngine
                    .TryCreateFromLanguage(new Windows.Globalization.Language(languageTag));
                if (engine is null)
                {
                    AppLogger.Info($"    Windows 引擎 {languageTag}：本机没装这个语言的识别包，无法使用");
                    return;
                }

                using var software = OcrTextCapture.ToSoftwareBitmap(bmp);
                var r = engine.RecognizeAsync(software)
                              .AsTask().GetAwaiter().GetResult();
                result = string.Join("\n", r.Lines.Select(l => l.Text));
            }
            catch (Exception ex)
            {
                AppLogger.Info($"    Windows 引擎 {languageTag} 出错：{ex.GetType().Name} {ex.Message}");
                result = "";
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        return result;
    }

    /// <summary>
    /// 调 Tesseract，指定用哪一档模型、认哪几种语言。
    /// 【关键】上一轮只用 tessdata_fast 就下结论说「Tesseract 更差」是不公平的——
    ///   fast 是 Tesseract 最快也最不准的一档，专门用来跑批量的。
    ///   这次把 fast / best / 标准 三档都摆出来比。
    ///
    /// 【为什么要同时挂三种语言 chi_sim+eng+rus】
    ///   2026-10-09 用户说明真实用途是翻**英文和俄文**。
    ///   而本机 Windows OCR **没装俄文识别包**（实测 ru-RU 引擎创建失败），
    ///   俄文只能靠 Tesseract，所以必须把 rus 挂上。
    ///   三种语言一次挂上，让 Tesseract 自己按字形判断，
    ///   同一张图不论中英俄都能试，不必按语种拆成三趟跑。
    ///
    /// 【为什么模型目录要从外面传进来】
    ///   best 和 标配加起来 92 MB，是给这次实验用的，
    ///   没必要塞进产品里跟着 exe 走。所以让它从命令行指定。
    /// </summary>
    private static Func<Bitmap, string> TessRunner(string variant, string languages, string root)
    {
        return bmp =>
        {
            // fast 直接放根目录；best / standard 放各自的子目录
            string dataDir = variant == "fast"
                ? root
                : Path.Combine(root, variant);

            if (!Directory.Exists(dataDir))
                throw new DirectoryNotFoundException($"语言包目录不存在：{dataDir}");

            using var engine = new Tesseract.TesseractEngine(dataDir, languages);
            byte[] png;
            using (var ms = new System.IO.MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                png = ms.ToArray();
            }
            using var pix = Tesseract.Pix.LoadFromMemory(png);
            using var page = engine.Process(pix);
            return page.GetText() ?? "";
        };
    }

    private static string Safe(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
