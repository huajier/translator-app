using System;
using System.Drawing;
using TranslatorApp.Core.Abstractions;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

internal sealed partial class OcrTextCapture : ITextCapture
{
    private readonly record struct ForeignOutcome(
        string Text,
        int LineCount,
        string ChannelUsed,
        string ScoreNote,
        bool RejectedAsGarbage);

    private ForeignOutcome RunForeignPath(
        Bitmap raw, PassResult first, string text, int lineCount, string note,
        string channelUsed, string scoreNote, bool rejectedAsGarbage,
        bool windowsSaysChinese, int windowsCharsForCompare, int windowsScoreForCompare)
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

            // ==========================================================
            // 【2026-10-14 用户拍板「甲方案」：英文加第二票，只治「结尾被咬掉」】
            //
            //   【E6 那个老毛病的病根，是这张图本身】
            //   `foreignImage` = 放大 4 倍 + 变灰，**不二值化**。
            //   它被喂给 6 次识别（eng/rus/rus+eng/四路/红通道/chi_sim），
            //   俄文段和中文兜底全靠它，所以**不能直接换掉这张图**
            //   （V0.3.1 就是为治一个特例去动全局机制，三语全崩）。
            //
            //   【离线复现，不是猜】
            //   拿用户 10-14 08:40:08 存的那张框图（493×29，E6-1 咬成 `editable te`），
            //   tools\探针\E6末尾 跑 12 种预处理：
            //     只放大+变灰（= foreignImage）→ ❌ `editable te`（61 个字母数字，和软件错的 4 次一模一样）
            //     放大+变灰**+再二值化**        → ✅ `editable text.`（63）
            //     其余 10 种（含原图、只灰度、只二值化、红通道…）→ ✅ 全读全
            //   **12 种里只有放大+变灰这一种会咬尾巴**，加一道二值化就好。
            //
            //   【为什么是「加一票」而不是「换图」】
            //   那张坏图只有 1 张，好图一张都没存（存的截图会被重建发布目录冲掉）。
            //   拿 1 张图去换掉 56 段的饭，赔面太大。
            //   加一票则相反：**老的那一票一个字没改**，它认对就还是它说了算。
            //
            //   【规矩：两份「对得上」才换】
            //   四条路：①字母数字一模一样（差别只在标点/空格怎么摆）；
            //   ②第一票的字按原顺序在第二票里都找得着；
            //   ③两份只差零星几个字（不超过 min(4, 第一票长度/4)）；
            //   ④第二票比第一票少一个字，且两份只差这一个。
            //   ②③都要求第二票更长；①不看长短；④要的正是「第二票更短」。
            //   治的是「第一票读坏」——尾巴被吃掉（`editable te`）、
            //   句中打磕巴（`editab e te`）、把词读错几个字（A5 的 `edital le`）、
            //   末尾符号读错（`text.` → `text` / `text |`）、
            //   末尾凭空多吐一个字母（`text.` → `te ext` / `text a`），这五种第二票都读对了。
            //   第二票读糊了/交白卷/少读一截时，四条路都不成立，规矩不动手。
            //   对不上 = 这次当没发生，走老路。
            //
            //   ponytail: 英文取词会多花约 0.1 秒（实测 Tesseract 单次 ~110 毫秒）；
            //   这是买「看不准就不动手」那份保险的钱。
            // ==========================================================
            if (tryForeignModels && !string.IsNullOrWhiteSpace(engResult.Text))
            {
                try
                {
                    using Bitmap sharpImage = ImagePreprocessor.PreprocessAndBinarize(foreignImage);
                    var secondVote = TesseractOcr.TryRecognize(sharpImage, TesseractOcr.Languages.English);

                    // 护栏（2026-10-17）：第一票已经够格，第二票却读糊一大截时，不换。
                    // 判据在 Rules.cs 的 SecondVoteTooWeak 里（含数据来源与天花板）。
                    if (SecondVoteTooWeak(engResult.Confidence, secondVote.Confidence))
                    {
                        AppLogger.Info(
                            $"【英文第二票】第一票读得好好的（{engResult.Confidence:0.00}），" +
                            $"第二票只有 {secondVote.Confidence:0.00}，差得太多——这次不换，仍用第一票");
                    }
                    else if (SecondVoteRecoversTail(engResult.Text, secondVote.Text))
                    {
                        string gain = DescribeTailGain(engResult.Text!, secondVote.Text!);
                        // 文案对三条路都成立：路子③两份一样长，说「读得更全」是假话。
                        AppLogger.Info(
                            $"【英文第二票】第一票读坏了（{engResult.Confidence:0.00}），" +
                            $"第二票（放大+变灰+二值化，{secondVote.Confidence:0.00}）" +
                            $"跟第一票对得上——采用第二票" +
                            (gain.Length > 0 ? $"（第二票比第一票多认了「{gain}」）" : ""));

                        engResult = secondVote;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"英文第二票失败（已忽略，仍用第一票）：{ex.GetType().Name} {ex.Message}");
                }
            }

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

                // ==============================================================
                // 【2026-10-12 X1 实测暴露的漏洞：这道闸门只保护拉丁字母】
                //
                // 现场（X1 = `错误 404 (Not Found)：файл не найден`，中英俄三语混排）：
                //   rus+eng  0.88 分  【ER 404 (Not Found): файл не найден 】 ← 俄文是对的
                //   eng      0.71 分  【Fir 404 (Not Found): dain He Halex  】 ← 俄文全糊
                //   **结果 modelUsed 留在了 eng**，
                //   四路循环也跟着用 eng 跑，**结构上就认不了西里尔字母**。
                //
                // 为什么换不过去——三个条件对三语混排**一个都不占**：
                //   ① rusEngKeepsLatin：比的是**拉丁字母比例**，
                //      两边都是拉丁字母为主，rus+eng 并不占优 → 判 false
                //   ② stillRussian：要求**俄文占比 > 50%**，
                //      而 X1 的俄文只占一半（`файл не найден` 对 `404 (Not Found)`）→ 判 false
                //   ③ notWorse：这一条 rus+eng 其实是过的（0.88 ≥ 0.71-0.08）
                //
                //   **根因：这道闸门是在 2026-09 为「拉丁字母」设计的，
                //   从头到尾就没有保护过西里尔字母。**
                //   R8 修的是 `config.txt`→`сопйддхе`（丢拉丁字母），
                //   方向对、但只做了一半——**另一半（丢西里尔字母）没人管**。
                //
                // 【怎么补：对称地补上西里尔这一侧】
                //   如果当前最好的结果**一个西里尔字母都没有**，
                //   而 rus+eng 认出了**实实在在的西里尔字母**，
                //   那说明 rus+eng 读到了一段当前结果**根本读不到的东西**——
                //   这跟「保住拉丁字母」是同一个性质的理由。
                //
                // 【会不会破坏 R8？不会】
                //   R8 当时的局面是**反过来**的：纯 rus 满是假西里尔（`сопйддхе`），
                //   rus+eng 满是真拉丁（`config.txt`）。
                //   新条件要求「**当前最好的结果没有西里尔**」才触发——
                //   纯 rus 的结果西里尔满满当当，**新条件不成立**，
                //   仍旧由原来的拉丁字母条件接管。**两条各管一侧，不重叠。**
                //
                // 【实测依据，不是拍脑袋】
                //   X1 连框 5 次：**用 rus+eng 的那 1 次全对，
                //   用 eng 的那 4 次俄文全糊。**
                //   换句话说「这次到底走哪条路」和结果对错**完全相关**。
                //
                // ⚠️ **这是一处全局闸门的改动，可能影响别段**——
                //    所以下一轮**必须把材料 1、2、3 全测一遍**，不能只看 X1。
                // ==============================================================
                bool currentHasNoCyrillic = LanguageDetector.CountCyrillicRatio(best.Text) <= 0.02;
                bool rusEngFindsCyrillic = LanguageDetector.CountCyrillicRatio(rusEngResult.Text) >= 0.10;
                double currentCyr = LanguageDetector.CountCyrillicRatio(best.Text);
                double rusEngCyr = LanguageDetector.CountCyrillicRatio(rusEngResult.Text);

                bool keepLatinAsBefore = stillRussian && rusEngKeepsLatin && notWorse;
                bool keepCyrillicNow = currentHasNoCyrillic && rusEngFindsCyrillic && notWorse;

                if (keepLatinAsBefore || keepCyrillicNow)
                {
                    bool usedCyrillicRule = keepCyrillicNow && !keepLatinAsBefore;
                    best = rusEngResult;
                    modelUsed = TesseractOcr.Languages.RussianPlusEnglish;
                    AppLogger.Info(
                        $"改用俄英混合模型（置信度 {rusEngResult.Confidence:F2}）：" +
                        (usedCyrillicRule
                            ? $"原结果一个西里尔字母都没有（{currentCyr:F2}），而它认出了（{rusEngCyr:F2}）"
                            : "它保住了原文里的拉丁字母"));
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
                        // 【2026-10-19 只加日志、不改行为：把垫底词分一起打出来】
                        //   这张图（PLAY THE DEMO NOW ON，彩色插画背景+白字描边）
                        //   暴露了一个判据问题：红通道 0.82 分、只多一个引号，
                        //   却因为「字数 16 < 20」被否；错的版本字多是因为**黏字**
                        //   （PLAY THE → PUAYATHE）和**多认**（凭空一个 DE）。
                        //   而 TesseractOcr.Result 里本来就有 MinWordConfidence
                        //   （垫底词分，E5 那次十次全错时它出现过 0.00），
                        //   一直「只是拿来看的」——现在四路择优里也没用它。
                        //   先只打出来，等这张图的数据够了再定规则。
                        AppLogger.Info(
                            $"  四路之一「{PlaneNames[i]}」：置信度 {planeResult.Confidence:F2}，" +
                            $"垫底词分 {planeResult.MinWordConfidence:F2}" +
                            $"（{(planeResult.MinWordText ?? "—")}），" +
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

                        // ==========================================================
                        // 【2026-10-12 用户定的「甲」：分数更高、字少得不多，就该认分数】
                        //
                        //   **原来那条规矩的代价，是 B3 上 6 次全错。**
                        //   B3（`… файл "config.txt" не найден — попробуйте /help?id=123`）
                        //   四路里**唯一两份全对的**，被这样否掉：
                        //     「局部阈值」0.88  【… файл “config.txt” … /help?id=123】 ← 两处都对
                        //     「绿通道」  0.86  【… файл "config.txt™ … /helptid=123】   ← 两处都错
                        //     「局部阈值」分数更高（0.88 > 0.86）但有效字符更少（66 < 67），**不采用**
                        //
                        //   **6 次一模一样，连日志文字都不差一个字。**
                        //   → 这一段的错，不是引擎认不出，是**选那一份的时候把它扔了**。
                        //
                        //   【为什么不能简单地「分数高就用分数高」】
                        //     F5 当年就是**只比分数**换出来的整段乱码（19 字 < 55 字也照换）。
                        //     所以「字少就不采用」这条**有道理，得留着**。
                        //     **这次补的是它漏掉的那一小块**：
                        //     字**少得很少**的时候，它否决的是**更好的**结果。
                        //
                        //   【补法：给「字少」定一个容忍范围】
                        //     少的比例 ≤ 一成 → 认分数（字少是正常的，不是内容缺失）
                        //     少的比例 >  一成 → 仍然否决（这才是 F5 那种内容缺失）
                        //
                        //   另加一条**分数要拉开**才作数：
                        //     分数只高 0.001 那是噪声，不能拿来换掉已经对的那份。
                        //
                        //   【拿已知案例验一遍】
                        //     B3  66 / 67 → 少 1.5% ≤ 一成，且 0.88 > 0.86+0.01 → **采用** ✅
                        //     F5  19 / 55 → 少 65% > 一成 → **仍然否决** ✅（保住原来的修复）
                        // ==========================================================
                        const double CharDeficitTolerance = 0.10;
                        const double ConfidenceMargin = 0.01;

                        // ==========================================================
                        // 【2026-10-20 补：垫底词分出口】
                        //
                        //   16:46:28 那次 PLAY THE DEMO NOW ON 的实证：
                        //     红通道 0.81、有效字符 16、垫底词分 33.50  ← 内容全对
                        //     基准   0.40、有效字符 22、垫底词分 3.31   ← `fs rs TR Ws` 碎词
                        //   红通道被「16 < 22×0.9」否掉，最后整段判乱码。
                        //
                        //   乱码的特征不是「字数少」，是「有词分低到离谱」（3.31 / 0.00）。
                        //   所以补一个出口：垫底词分明显更高、且字数没少太多（≥60%）时采用。
                        //   字少太多的仍然否决（F5：19/55 → 19 < 33，照样挡住）。
                        // ==========================================================
                        const double CharDeficitToleranceWithCleanWords = 0.40;
                        const double MinWordConfidenceMargin = 20.0;

                        int allowedChars = bestChars <= 0
                            ? 0
                            : (int)Math.Ceiling(bestChars * (1.0 - CharDeficitTolerance));

                        int allowedCharsWithCleanWords = bestChars <= 0
                            ? 0
                            : (int)Math.Ceiling(bestChars * (1.0 - CharDeficitToleranceWithCleanWords));

                        bool moreChars = planeChars > bestChars;
                        bool sameCharsBetterScore =
                            planeChars >= bestChars && planeResult.Confidence > best.Confidence;
                        bool slightlyFewerButClearlyBetterScore =
                            planeChars >= allowedChars
                            && planeResult.Confidence > best.Confidence + ConfidenceMargin;
                        bool cleanWordsButFewerChars =
                            planeChars >= allowedCharsWithCleanWords
                            && planeResult.MinWordConfidence > best.MinWordConfidence + MinWordConfidenceMargin;

                        // ==========================================================
                        // 【2026-10-20 补：过线的无条件优先】
                        //
                        //   08:11:55 那次 PLAY THE DEMO NOW ON 的实证：
                        //     基准   0.53、17 有效字符  ← 不过线（0.78），本来就该扔
                        //     绿通道 0.80、14 有效字符  ← **过线**，但被「14 < 15.3」否掉
                        //   最后基准带着 0.52 的乱码版一路走到「判乱码」，
                        //   一份已经过线的结果被一份注定要扔的当尺子量掉了。
                        //
                        //   规矩和下面「红通道不二值化」那步的 rescued 是**同一条**：
                        //   **过线的比不过线的强，不管谁字多。**
                        // ==========================================================
                        bool candidatePassesButBestDoesNot =
                            best.Confidence < MinimumAcceptableConfidence
                            && planeResult.Confidence >= MinimumAcceptableConfidence;

                        // ==========================================================
                        // 【2026-10-20 补：词典命中率】
                        //
                        //   08:37:49 那次 PLAY THE DEMO NOW ON 的实证：
                        //     蓝通道  0.75、词典 1/3（PLAYsTHE✗ DEMO.NOW✗ ON✓）
                        //     灰度    0.74、词典 5/5（PLAY✓ THE✓ DEMO✓ NOW✓ ON✓）← 内容全对
                        //   分数只差 0.01，旧规则按分数选了黏字那一版，
                        //   然后 0.75 < 0.78 判乱码——**正确答案就在旁边，被 0.01 分挡掉了**。
                        //
                        //   整句平均分分不出「黏字乱码」和「认得很清楚只是分数低」，
                        //   但「有几个词是词典里的真词」一眼分得出。
                        //   所以补一条：分数几乎打平（差 ≤0.05）时，词典命中率高的胜。
                        //   要求命中率高出 30 个百分点以上，防噪声。
                        // ==========================================================
                        const double DictionaryScoreWindow = 0.05;
                        const double DictionaryHitMargin = 0.30;

                        double planeDictRate = planeResult.TotalWordCount > 0
                            ? (double)planeResult.DictWordCount / planeResult.TotalWordCount
                            : 0.0;
                        double bestDictRate = best.TotalWordCount > 0
                            ? (double)best.DictWordCount / best.TotalWordCount
                            : 0.0;

                        bool dictionaryHitIsBetter =
                            planeResult.TotalWordCount > 0 && best.TotalWordCount > 0
                            && Math.Abs(planeResult.Confidence - best.Confidence) <= DictionaryScoreWindow
                            && planeDictRate > bestDictRate + DictionaryHitMargin;

                        // ==========================================================
                        // 【2026-10-20 再补：词典内词数（绝对个数）】
                        //
                        //   08:53:30 实测，同一张 PLAY THE DEMO NOW ON：
                        //     蓝通道 0.83，3 个词，只有 ON 在词典   ← 黏字版，分数最高
                        //     灰度   0.66，5 个词，**全在词典**     ← 内容全对，分数最低
                        //   分数差了 0.17，光靠「分数打平才比词典」这条救不回来。
                        //
                        //   但看**绝对词数**就一目了然：黏字会把两个词吞成一个
                        //   （PLAY THE → PLAYsTHE），所以**词数少、词典内词更少**。
                        //   这一版有 5 个真词，那一版只有 1 个。
                        //
                        //   所以补一条：候选的词典内词数多出 2 个以上、
                        //   它自己的词典命中率不低于 60%、分数也没低超过 0.20 → 换。
                        //   60% 那道闸是防「碎词一大堆但都不在词典」的垃圾。
                        // ==========================================================
                        const int DictionaryWordAdvantage = 2;
                        const double DictionaryRateFloor = 0.60;
                        const double DictionaryWordScoreFloor = 0.20;

                        bool dictionaryWordsAreMore =
                            planeResult.TotalWordCount > 0 && best.TotalWordCount > 0
                            && planeResult.DictWordCount >= best.DictWordCount + DictionaryWordAdvantage
                            && planeDictRate >= DictionaryRateFloor
                            && planeResult.Confidence >= best.Confidence - DictionaryWordScoreFloor;

                        // ==========================================================
                        // 【2026-10-20 再补：全词典版优先】
                        //
                        //   10:05:46 实测，同一张 PLAY THE DEMO NOW ON：
                        //     灰度   0.72，5 个词，**全在词典**（内容全对）← 被「有效字符 16 < 18」否掉
                        //     蓝通道 0.69，7 个词，5 个在词典（含 PLAYsTHE 黏字 + `-—` `=` 垃圾）
                        //   两条词典规则都没触发：命中率 71% vs 100% 只差 28.6 个百分点（门槛 30），
                        //   词典内词数 5 vs 5 打平。最后选了 0.69 那版，低于 0.78 判成乱码。
                        //
                        //   所以补一条最硬的：**这一版每个词都在词典里（至少 2 词），
                        //   而当前最好的那版还带生词** → 换。
                        //   依据：真乱码很难做到「2 个以上词全在词典」，
                        //   黏字（PLAY THE→PLAYsTHE）和碎词一定会漏出词典外的词。
                        // ==========================================================
                        const int AllInDictionaryMinWords = 2;

                        bool allWordsInDictionary =
                            planeResult.TotalWordCount >= AllInDictionaryMinWords
                            && planeResult.DictWordCount == planeResult.TotalWordCount
                            && (best.TotalWordCount == 0 || best.DictWordCount < best.TotalWordCount);

                        bool planeClearlyBetter =
                            moreChars || sameCharsBetterScore || slightlyFewerButClearlyBetterScore
                            || cleanWordsButFewerChars || candidatePassesButBestDoesNot
                            || dictionaryHitIsBetter || dictionaryWordsAreMore
                            || allWordsInDictionary;

                        if (planeClearlyBetter)
                        {
                            AppLogger.Info(
                                $"「{PlaneNames[i]}」这一路确实更好（分数 {planeResult.Confidence:F2}/{best.Confidence:F2}，" +
                                $"有效字符 {planeChars}/{bestChars} 个），改用它");

                            if (slightlyFewerButClearlyBetterScore && !moreChars && !sameCharsBetterScore)
                            {
                                double lostPct = (1.0 - (double)planeChars / Math.Max(bestChars, 1)) * 100;
                                AppLogger.Info(
                                    $"　　↑ 它字少 {bestChars - planeChars} 个（{lostPct:F0}%，在容忍范围内），" +
                                    $"但分数高 {planeResult.Confidence - best.Confidence:F2}（超过 {ConfidenceMargin:F2} 的门槛）" +
                                    $"——**字少这么点不算内容缺失，认分数**");
                            }

                            if (cleanWordsButFewerChars && !moreChars && !sameCharsBetterScore
                                && !slightlyFewerButClearlyBetterScore)
                            {
                                AppLogger.Info(
                                    $"　　↑ 它字少 {bestChars - planeChars} 个，但**垫底词分 {planeResult.MinWordConfidence:F2} > " +
                                    $"{best.MinWordConfidence:F2}**（最差的那个词都比对面最差的强 " +
                                    $"{planeResult.MinWordConfidence - best.MinWordConfidence:F2} 分）" +
                                    $"——碎词多的一版是乱码，认词认得清的一版");
                            }

                            if (candidatePassesButBestDoesNot && !moreChars && !sameCharsBetterScore
                                && !slightlyFewerButClearlyBetterScore && !cleanWordsButFewerChars)
                            {
                                AppLogger.Info(
                                    $"　　↑ 它置信度 {planeResult.Confidence:F2} **过线**（≥ {MinimumAcceptableConfidence:F2}），" +
                                    $"当前这份 {best.Confidence:F2} 不过线（本来就要当乱码扔掉）" +
                                    $"——**过线的比不过线的强，不管谁字多**");
                            }

                            if (dictionaryHitIsBetter && !moreChars && !sameCharsBetterScore
                                && !slightlyFewerButClearlyBetterScore && !cleanWordsButFewerChars
                                && !candidatePassesButBestDoesNot)
                            {
                                AppLogger.Info(
                                    $"　　↑ 分数几乎打平（差 {Math.Abs(planeResult.Confidence - best.Confidence):F2}），" +
                                    $"但**词典命中率 {planeDictRate * 100:F0}% > {bestDictRate * 100:F0}%**" +
                                    $"（{planeResult.DictWordCount}/{planeResult.TotalWordCount} vs " +
                                    $"{best.DictWordCount}/{best.TotalWordCount}）" +
                                    $"——黏字那一版词都不在词典，认词认得清的这一版全在");
                            }

                            if (dictionaryWordsAreMore && !dictionaryHitIsBetter && !moreChars
                                && !sameCharsBetterScore && !slightlyFewerButClearlyBetterScore
                                && !cleanWordsButFewerChars && !candidatePassesButBestDoesNot)
                            {
                                AppLogger.Info(
                                    $"　　↑ 分数低 {best.Confidence - planeResult.Confidence:F2}，但**真词多 " +
                                    $"{planeResult.DictWordCount - best.DictWordCount} 个**" +
                                    $"（{planeResult.DictWordCount} 个 vs {best.DictWordCount} 个在词典）" +
                                    $"——黏字把两个词吞成一个，词数少的那版分数反而虚高");
                            }

                            if (allWordsInDictionary && !dictionaryHitIsBetter && !dictionaryWordsAreMore
                                && !moreChars && !sameCharsBetterScore && !slightlyFewerButClearlyBetterScore
                                && !cleanWordsButFewerChars && !candidatePassesButBestDoesNot)
                            {
                                AppLogger.Info(
                                    $"　　↑ **这一版每个词都在词典里**（{planeResult.DictWordCount}/{planeResult.TotalWordCount}），" +
                                    $"那版有 {best.TotalWordCount - best.DictWordCount} 个生词" +
                                    $"——全认成真词的比带生词的可靠，不管谁字多");
                            }

                            best = planeResult;
                            usedPrepared = true;
                            bestPlane = planes[i];
                        }
                        else if (planeResult.Confidence > best.Confidence)
                        {
                            // 分数高但**内容明显更少**：以前一律不换，
                            // F5 那次就是「只比分数」换出来的整段乱码。
                            AppLogger.Info(
                                $"「{PlaneNames[i]}」分数更高（{planeResult.Confidence:F2} > {best.Confidence:F2}）" +
                                $"但有效字符少太多（{planeChars} < {allowedChars}，比容忍下限还少 {allowedChars - planeChars} 个），**不采用**");
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

                        // ==========================================================
                        // 【2026-10-12 用户定的「甲」，与四路择优**用同一把尺子**】
                        //
                        //   **B3 上这一处也被扔过。** 6 次里这一版的现场：
                        //     「红通道不二值化」0.91，66 个有效字符，内容**两处都对**
                        //       【… файл “config.txt” … /help?id=123】
                        //     当时手上那份是 0.86、67 字、两处都错
                        //       【… файл "config.txt™ … /helptid=123】
                        //     三条老规矩**一条都不成立**：
                        //       notWorseAtAll  66 ≥ 67  → 不成立
                        //       muchMore       66 ≥ 77  → 不成立
                        //       rescued        0.86 已过 0.78 线 → 不成立
                        //
                        //   跟四路那次是**同一个毛病**：
                        //   字少这么一两个，恰恰是**更干净的结果**，却被当成「内容缺失」。
                        // ==========================================================
                        const double CharDeficitTolerance2 = 0.10;
                        const double ConfidenceMargin2 = 0.01;
                        int allowedChars2 = oldChars <= 0
                            ? 0
                            : (int)Math.Ceiling(oldChars * (1.0 - CharDeficitTolerance2));
                        bool slightlyFewerButClearlyBetter =
                            newChars >= allowedChars2
                            && grayResult.Confidence > oldConfidence + ConfidenceMargin2;

                        // ==========================================================
                        // 【2026-10-10 材料 2 的 R3：第四次栽在「字数多」这个坑上】
                        //
                        //   **现场**（橙底绿字的俄文，两遍都返回「（空）」）：
                        //     base           0.58，39 个有效字符  ← 胡话，凭什么字多
                        //     红通道二值化     0.95，38 个，内容**完全正确** → 不采用
                        //     红通道不二值化   0.96，38 个，内容**完全正确** → 不采用
                        //     最终原文：（空）
                        //
                        //   三条规矩为什么都不成立：
                        //     38 ≥ 39         → 不成立
                        //     38 ≥ 39 × 1.15  → 不成立
                        //   **一份 0.96 的正确答案，就这么被一份 0.58 的胡话挤掉了。**
                        //
                        //   🔴 **这里暴露的是一条规则的���本错误**：
                        //
                        //     那份 0.58 的结果**本来就要被「低于 0.78 = 乱码」扔掉**，
                        //     它**根本没资格当基准**去和别人比字数。
                        //     **拿一个注定要扔的东西当尺子，量出来的结果当然是错的。**
                        //
                        //   【所以新规矩：旧的不达标时，字数根本不参与比较】
                        //     不是「少几个字也换」这种拍脑袋的宽松，
                        //     而是**取消这个比较项**——
                        //     既然旧的会被扔，它多一个字少一个字毫无意义；
                        //     真正的取舍是**过线**还是**不过线**。
                        //
                        //   **同一个坑的第四次**：
                        //     F5（62 个空格）→ 五十八轮（110 个噪点）→ 六十二轮（28 个乱码）→ 这次
                        // ==========================================================
                        bool oldIsDoomed = oldConfidence < MinimumAcceptableConfidence;
                        bool rescued = oldIsDoomed
                                    && grayResult.Confidence >= MinimumAcceptableConfidence;

                        // 【2026-10-20 补：不过线的不许覆盖过线的】
                        // 08:11 那次：绿通道 0.80 已过线，被 0.52 的乱码版按「字更全」覆盖，
                        // 最后整段判乱码。过线是硬门槛，字数只是软比较。
                        bool newVersionFailsButOldPasses =
                            best.Confidence >= MinimumAcceptableConfidence
                            && grayResult.Confidence < MinimumAcceptableConfidence;

                        if (!newVersionFailsButOldPasses
                            && (notWorseAtAll || rescued || slightlyFewerButClearlyBetter))
                        {
                            best = grayResult;
                            usedPrepared = true;
                            if (slightlyFewerButClearlyBetter && !notWorseAtAll && !rescued)
                            {
                                double lostPct2 = (1.0 - (double)newChars / Math.Max(oldChars, 1)) * 100;
                                AppLogger.Info(
                                    $"这一版字少 {oldChars - newChars} 个（{lostPct2:F0}%，在容忍范围内），" +
                                    $"但分数高 {grayResult.Confidence - oldConfidence:F2}（超过 {ConfidenceMargin2:F2} 的门槛）" +
                                    $"——**字少这么点不算内容缺失，认分数**，改用它");
                            }
                            else
                            AppLogger.Info(rescued
                                ? $"原来那份置信度只有 {oldConfidence:F2}（低于 {MinimumAcceptableConfidence:F2}，"
                                  + $"本来就要当乱码扔掉），这一版有 {grayResult.Confidence:F2} 已经过线——"
                                  + $"**过线的比不过线的强，不管谁字多**，改用它"
                                : $"这一版分数没降（{grayResult.Confidence:F2} ≥ {oldConfidence:F2}）"
                                  + $"且字数没少（{newChars} ≥ {oldChars}），两项都不差，改用它");
                        }
                        else if (!newVersionFailsButOldPasses && muchMore && notMuchWorse)
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
                                // ==========================================================
                                // 【2026-10-15 路二第二步（Z1）：垫底的那个字先删掉再用】
                                //
                                //   【现场】Z1（`今天天气不错适合出门`）反复出成
                                //   `今天天和气不错适合出门`，全部归档命中 12 次。
                                //     Windows 第一遍读空 0 字 → 只剩 chi_sim 一张票
                                //     chi_sim 自报【和】63.37，是 9 个词里最低的，
                                //     第二低的【气】89.05 ——**中间隔着 20 多分**。
                                //
                                //   【为什么敢删】离线拿 245 段中文块（2245 个可判定块）逐块对拍：
                                //     限定在「中文兜底」这条路时 **出手 12、救回 12、弄坏 0**；
                                //     一放开到全部块就多出 2 次弄坏（把 Z2 的「图」删了）
                                //     → **所以这个规矩只能长在这一个分支里。**
                                //
                                //   【为什么删字而不是换票】同一次识别里其他 8 个字都是对的，
                                //     整体换回 Windows 那份没有票（Windows 读空）。
                                //
                                //   【天花板，明知故犯】引擎把一个**读对了但没把握**的字
                                //     报成垫底时，这一条会把它删掉。2245 块里 0 次，
                                //     样本就这么大，已用探针断言如实钉住。
                                // ==========================================================
                                //   两个坑都记在这：
                                //     ① 一开始拿 `chiOnPlaneText`（已归一化）当入参、
                                //        又拿没归一化的返回值去比 → 两边不同源；
                                //     ② 改完是拿**原始**文本当入参 —— 原文其实是
                                //        `今天天 和 气不错适合出门`（字两边各有一个空格，
                                //        归一化时才被吞掉），把「和」一删就剩**两个挨着的空格**，
                                //        而 `FixSpuriousSpaces` 只看紧挨着的那一个字符，
                                //        `天␣␣气` 里第二个空格的左边是空格、不是汉字 → **吞不掉**，
                                //        最后用户就看到 `今天天  气不错适合出门`。
                                //   现在的规矩：**改就改归一化后的文本**（下游本来也都会
                                //   再过一次 Normalize，同一件事只做一遍）。
                                // ==========================================================
                                string withoutLowWord = DropLowScoreSingleChar(chiOnPlaneText, chiOnPlane);
                                if (!string.Equals(withoutLowWord, chiOnPlaneText, StringComparison.Ordinal))
                                {
                                    AppLogger.Info(
                                        $"【路二·低分单字】垫底字【{chiOnPlane.MinWordText}】只有 " +
                                        $"{chiOnPlane.MinWordConfidence:F2} 分（第二低 " +
                                        $"{chiOnPlane.SecondMinWordConfidence:F2} 分），先删掉再采用：" +
                                        $"{Preview(chiOnPlaneText)} → " +
                                        $"{Preview(withoutLowWord)}");
                                    chiOnPlane = chiOnPlane with { Text = withoutLowWord };
                                }

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

            // ==========================================================
            // 【2026-10-10 材料 2 的 Z1：中文也要能走「红通道」那一路】
            //
            //   **Z1 的现场**（`今天天气不错适合出门`，橙底绿字）：
            //     Windows 自带 → 0 字
            //     chi_sim 在**灰度图**上 → 0.76，卡在 0.78 门槛下面被拒
            //     → 判为乱码 → **界面什么都不显示**
            //
            //   **这一段当年就写过，但挂错了地方**：
            //   原来的红通道补试加在「中文流程」里，
            //   而 Z1 实际走的是**外文流程里遇到中文的那条支路**——
            //   Windows 先吐出一点中文，这一段就被判成「不是中文」，
            //   于是**压根不会进中文流程**，那段代码**一次都没执行过**。
            //
            //   **代码在、注释在、逻辑对，就是不在会被执行到的地方。**
            //
            //   **为什么红通道能救**（数据本身的性质，不是猜的）：
            //     橙底 RGB(255,165,0)，红分量 255
            //     绿字 RGB(0,128,0)，红分量   0
            //     → 红通道上对比度拉满，颜色天然分得开；
            //       转灰度是三通道加权平均，**绿字那点红被稀释掉了**。
            //
            //   【放在这儿的理由：必须在 `best` 定下来之后、任何分支判断之前】
            //     这样**中文流程和外文流程两边都覆盖到**，不用再赌它走哪条路。
            //     （上一版补在中文流程里，实测三次**一次都没触发**。）
            //
            //   【只在不过线时才多跑一次】
            //     条件是 `置信度 < 0.78`，
            //     平时所有达标段（0.93 上下）**完全不会触发**，
            //     不影响速度，也不影响已验收的那些段。
            // ==========================================================
            // 【2026-10-10 探针查到的真相（这是前面两版都白做的原因）】
            //   外文流程此处 best 置信度=0.76，模型=chi_sim，内容【      KESL       】
            //
            //   **chi_sim 根本没读出中文——它读出的是 `KESL`，纯垃圾。**
            //     Windows 自带：今天天气不台出「 J   ← 8 个字里对了 5 个
            //     chi_sim 灰度图：      KESL         ← 17 个字符，全是空格和乱码
            //
            //   所以那 0.76 是在给 `KESL` 背书。
            //   前面两版我把补试的条件写成「best 认出来像中文」，
            //   `KESL` 当然不像中文——**门是自己把自己关上的。**
            //
            //   【正确的判断依据是 Windows 那边】
            //     Windows 已经明明白白认出这是中文，
            //     **这个信号比信 chi_sim 可靠得多**——
            //     因为 chi_sim 正是在这张橙底图上失灵的那个。
            //
            // ==============================================================
            // 【2026-10-12 Z1 加的诊断日志】
            //
            //   Z1（`今天天气不错适合出门`，橙底绿字）**又失败了**，
            //   但日志里只看到 chi_sim 一次调用（0.77、6 字），
            //   **「中文红通道补试」这一行根本没出现**——
            //   也就是说下面这个门没开。
            //
            //   **可到底是哪个条件没满足，光看代码看不出来**
            //   （静态分析已经猜错两轮，不想再猜第三次）。
            //   所以把两个条件**各自的实际取值**打出来，
            //   下轮一跑就知道该修哪个。
            // ==============================================================
            bool redGateLowConf = best.Confidence < MinimumAcceptableConfidence;
            bool redGateWindowsIsChinese = LanguageDetector.IsMostlyChinese(TextNormalizer.Normalize(text));

            AppLogger.Info(
                $"中文红通道补试的门：best 置信度 {best.Confidence:F2}（要 < {MinimumAcceptableConfidence:F2} → {redGateLowConf}）、" +
                $"Windows 那边是不是中文（{redGateLowConf} ? {redGateWindowsIsChinese}）→ " +
                $"{(redGateLowConf && redGateWindowsIsChinese ? "会跑" : "**不会跑**")}");

            if (best.Confidence < MinimumAcceptableConfidence
                && LanguageDetector.IsMostlyChinese(TextNormalizer.Normalize(text)))
            {
                // 【用 `raw` 而不是 `captured`】
                //   `raw` 是所有其他 Tesseract 调用用的那张底图，
                //   **这样这一遍和别的一遍只差「红通道 + 不二值化」这一个变量**，
                //   别的什么都不动——好比较，也和已验收的那些段保持一致。
                using Bitmap redChineseImage = ImagePreprocessor.PreprocessRedChannel(raw, 2.0);
                var chiRed = TesseractOcr.TryRecognize(redChineseImage, TesseractOcr.Languages.Chinese);
                string chiRedText = TextNormalizer.Normalize(chiRed.Text ?? "");
                int redChars = TextNormalizer.CountMeaningfulChars(chiRedText);

                AppLogger.Info(
                    $"中文红通道补试：置信度 {chiRed.Confidence:F2}（原来 {best.Confidence:F2}），" +
                    $"有效字符 {redChars} 个，内容【{Preview(chiRedText)}】");

                // 🔴【2026-10-10 踩过的坑】`best` 换了，**`tessText` 也必须一起换**
                    //   `string? tessText = best.Text;` 这一行**跑在下面这段补救之前**，
                    //   所以局部变量 `tessText` 里装的**还是旧的 `KESL`**——
                    //   结果 `best` 明明已经换成 0.92 的正确结果了，
                    //   **最后返回给用户的仍然是那句垃圾**。
                    //   探针实测：`红通道这一版更好（0.92…），采用它` → 实际输出 `KESL`。
                    double oldConfidence = best.Confidence;
                    if (LanguageDetector.IsMostlyChinese(chiRedText)
                    && redChars >= 2
                    && chiRed.Confidence > oldConfidence)
                {
                    best = chiRed;
                    tessText = chiRed.Text;      // ← 关键：局部变量要一起换
                    modelUsed = TesseractOcr.Languages.Chinese;
                    usedPrepared = true;
                    AppLogger.Info($"红通道这一版更好（{chiRed.Confidence:F2} > {oldConfidence:F2}），采用它");
                }
            }

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

                    // 【2026-10-13】两个引擎在这一张图上各自的表现。
                    // 只记下来做对比，**不参与选择**（理由见上面 windowsScoreForCompare 的注释）。
                    AppLogger.Info($"两个引擎的比分（仅记录，不参与选择）：" +
                                   $"Windows 用的是{note}：{windowsScoreForCompare} 分（{windowsCharsForCompare} 字）；" +
                                   $"Tesseract：{OcrQualityScorer.Score(cleanedTess)} 分（{tessChars} 字，置信度 {best.Confidence:F2}）" +
                                   $"；Windows 第一遍原始得分 {OcrQualityScorer.Score(first.Text)}（{TextNormalizer.CountMeaningfulChars(first.Text)} 字）");

                    text = cleanedTess;
                    lineCount = TextNormalizer.CountLines(cleanedTess);
                    channelUsed = "Tesseract";
                    scoreNote = $"识别到的不是中文，改用 Tesseract（{modelUsed} 模型，置信度 {best.Confidence:F2}，{tessChars} 个有效字符{(usedPrepared ? "，用的是择优二值化后的图" : "")}）";
                }
                else if (tessChars >= 2)
                {
                    // 以前这里是「保留 Windows 自带」，见 rejectedAsGarbage 的说明：
                    // 实测退回去给的也是乱的，所以改成直接报「未识别到文字」。
                    //
                    // 【2026-10-19 用户批准：深色背景先裁掉四周噪声边再局部阈值】
                    // 用户实测 BATTLEBOARD（深色游戏纹理背景上的按钮字）五次框选只出一次。
                    // 离线实验（tools\探针\深色实验）证明：
                    //   不裁边 + 局部阈值 → 0.000（纹理垃圾 `~-Y` 把整页均分拉死）
                    //   裁掉四周噪声边 + 同一组参数 → 0.87~0.88，文本干干净净
                    // 放大、膨胀、换全局阈值这三条都试过，全更差，别再走。
                    // 只在「Tesseract 各路都不到及格线」时才跑，正常取词不经过这里。
                    (string Text, double Confidence)? rescued = TryCroppedLocalRescue(raw, modelUsed);
                    // 【2026-10-20 补：best 本身够好时，不许兜底覆盖】
                    //   09:13:05 实测：四路已经选出灰度 `'PLAY THE DEMO NOW ON`
                    //   （5 个词全在词典，置信度 0.7799，差 0.0001 没过 0.78 的线）→ 进了兜底分支，
                    //   兜底拿 `HE:DEMO;NOW, ON`（0.84）把它盖掉——**PLAY THE 直接消失**。
                    //   兜底本来是治「深色纹理背景」的，不该拿来换掉一份词全在词典的结果。
                    if (rescued is { } rescue && rescue.Confidence >= MinimumAcceptableConfidence
                        && !HasEnoughDictionaryWords(best, out _))
                    {
                        text = rescue.Text;
                        lineCount = TextNormalizer.CountLines(text);
                        channelUsed = "Tesseract";
                        scoreNote = $"Tesseract 置信度只有 {best.Confidence:F2}" +
                                    $"（低于 {MinimumAcceptableConfidence:F2}），" +
                                    $"但裁掉四周噪声边后重新识别拿到 {rescue.Confidence:F2}，采用它";
                        AppLogger.Info($"【深色裁边兜底】各路都不到及格线，裁掉四周噪声边后重新识别：" +
                                       $"置信度 {rescue.Confidence:F2}，文本「{Preview(text)}」");
                    }
                    // ==========================================================
                    // 【2026-10-20 补：词典命中率豁免】
                    //
                    //   08:37:49 那次的实证：四路择优按新规则选中灰度通道的
                    //   `'PLAY THE DEMO NOW ON`（5 个词全在词典、内容全对），
                    //   但置信度 0.74 < 0.78 → 走到这里仍然要判乱码。
                    //
                    //   0.78 这条线是拿「整句平均分」卡垃圾的，可它分不清
                    //   「黏字乱码」和「认得很清楚、只是字体描边让分数偏低」。
                    //   词典命中率分得出：真乱码的词几乎全不在词典。
                    //   所以补一个出口——分数 ≥0.70、至少 3 个词、8 成以上在词典
                    //   → 不判乱码，采用。
                    //
                    //   【三道闸缺一不可】
                    //     0.70 以下仍判乱码（0.45 那种 `PIAYSTHE:` 挡得住）；
                    //     3 词以下不放（两三个常用词凑不出「一句话」）；
                    //     8 成以下不放（真乱码碰巧含一两个词典词是常事）。
                    // ==========================================================
                    else if (HasEnoughDictionaryWords(best, out string dictNote))
                    {
                        text = best.Text!.Trim();
                        lineCount = TextNormalizer.CountLines(text);
                        channelUsed = "Tesseract";
                        scoreNote = $"Tesseract 置信度只有 {best.Confidence:F2}" +
                                    $"（低于 {MinimumAcceptableConfidence:F2}），" +
                                    $"但 {dictNote}，内容可信，采用它";
                        AppLogger.Info($"【词典命中率豁免】置信度 {best.Confidence:F2} 不到 " +
                                       $"{MinimumAcceptableConfidence:F2}，但 {dictNote}，不判乱码：" +
                                       $"「{Preview(text)}」");
                    }
                    // 【2026-10-17 用户 m01856 批准：只放「一个干净的外文单词」】
                    // BATTLEBOARD 这种按钮上的大写单词，Windows 每次读对、Tesseract 每次读糊，
                    // 以前一律报乱码，用户一个字都看不到。现在多开一个口子：
                    // Windows 读出来是「单行 + 纯字母数字 + 只有一个词」时，采用 Windows 的。
                    // 判据只放单字、不放多词句子 —— 理由和样本见 IsCleanSingleWord 的注释。
                    else if (IsCleanSingleWord(first.Text))
                    {
                        // 【2026-10-19 用户 m04050「修」】再加一道核对：
                        // Windows 读出的词，Tesseract 那边也得有对得上的影子。
                        // 起因：13:40:20 那次 Windows 把末尾 R 读成 O（`BATTLEBOAO`），
                        // 单字退路只查「是不是干净的一个词」，不查「对不对」，把错的放了出去。
                        // 判据见 WindowsWordBackedByTesseract：切词后编辑距离 ≤1 才算对得上。
                        if (!WindowsWordBackedByTesseract(first.Text, best.Text))
                        {
                            rejectedAsGarbage = true;
                            scoreNote = $"Tesseract 置信度只有 {best.Confidence:F2}" +
                                        $"（低于 {MinimumAcceptableConfidence:F2}），" +
                                        $"Windows 读出的「{Preview(first.Text.Trim())}」在 Tesseract 的结果里找不到对得上的词，" +
                                        $"判定为乱码，不返回结果";
                            AppLogger.Info($"【Windows 单字退路】拦下了：Windows 读的是「{Preview(first.Text.Trim())}」，" +
                                           $"但 Tesseract 各路都没有对得上的词（差 2 个字母以上），宁可不给");
                        }
                        else
                        {
                            text = first.Text.Trim();
                            lineCount = TextNormalizer.CountLines(text);
                            channelUsed = "WindowsOcr";
                            scoreNote = $"Tesseract 置信度只有 {best.Confidence:F2}" +
                                        $"（低于 {MinimumAcceptableConfidence:F2}），" +
                                        $"但 Windows 自带读出了干净的一个词「{Preview(text)}」，采用它";
                            AppLogger.Info($"【Windows 单字退路】Tesseract 各路都不到及格线，" +
                                           $"Windows 第一遍读的是「{Preview(text)}」，采用它");
                        }
                    }
                    else
                    {
                        rejectedAsGarbage = true;
                        scoreNote = $"Tesseract 置信度只有 {best.Confidence:F2}" +
                                    $"（低于 {MinimumAcceptableConfidence:F2}，判定为乱码），不返回结果";
                    }
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

        // ==============================================================
        // 【2026-10-20 补：首尾装饰符号清洗】
        //
        //   09:05 实测 10 次：黏字已经绝迹，剩下的是图片背景里的装饰符号
        //   被一起认了进来——
        //     ‘PLAY THE DEMO NOW ON      ← 前面多一个左引号
        //     •'PLAY'THE DEMO NOW ON ：  ← 前面 `•'`、后面全角冒号
        //   这些符号在图上**真实存在**，Tesseract 认出来没错，但对用户是噪声。
        //
        //   只删**开头和结尾**的装饰标点，中间一律不动
        //   （don't / it's 那种真缩写不能碰），
        //   也不动 `.` `,` `!` `?` —— 句尾句号是正常标点。
        // ==============================================================
        if (!string.IsNullOrEmpty(text) && !rejectedAsGarbage)
        {
            string cleaned = TrimEdgeSymbols(text);
            if (cleaned.Length > 0 && cleaned != text)
            {
                text = cleaned;
                lineCount = TextNormalizer.CountLines(text);
            }
        }

        return new ForeignOutcome(text, lineCount, channelUsed, scoreNote, rejectedAsGarbage);
    }

    /// <summary>
    /// **深色背景兜底：把四周的噪声边裁掉，再用局部阈值重新识别。**
    ///
    /// 【它只做一件事】拿到原始截图 → 转成浅底深字 → 找文字的外接框 → 裁掉框外的东西
    ///   → 用三个窗口各做一张局部阈值图 → 各识别一次 → 返回分数最高的那个。
    ///
    /// 【为什么值得单独写一个方法】
    ///   主路径（PrepareAllPlanes）一次做五路，五路都喂给引擎选。
    ///   那五路**都是补过白边的整图**，深色纹理背景上四周的暗角会被当成笔画，
    ///   于是每一路前面都粘着垃圾、整页均分被拉到 0.00。
    ///   这一路反过来：**先找到文字、把文字外面的东西全扔掉**，再二值化。
    ///
    /// 【任何一步出错都返回 null，绝不抛异常影响取词】
    /// </summary>
    private static (string Text, double Confidence)? TryCroppedLocalRescue(Bitmap raw, string language)
    {
        try
        {
            using Bitmap light = ImagePreprocessor.MakeLightBackground(raw);
            Bitmap[] candidates = ImagePreprocessor.PrepareCroppedLocal(light);
            if (candidates.Length == 0) return null;

            string? bestText = null;
            double bestConfidence = 0;
            try
            {
                foreach (Bitmap image in candidates)
                {
                    TesseractOcr.Result result = TesseractOcr.TryRecognize(image, language);
                    if (string.IsNullOrWhiteSpace(result.Text)) continue;
                    if (result.Confidence <= bestConfidence) continue;
                    bestConfidence = result.Confidence;
                    bestText = result.Text;
                }
            }
            finally
            {
                foreach (Bitmap image in candidates) image?.Dispose();
            }

            if (bestText is null) return null;
            string normalized = TextNormalizer.Normalize(bestText);
            if (string.IsNullOrWhiteSpace(normalized)) return null;
            return (normalized, bestConfidence);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"裁边兜底失败（不影响取词）：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// **分数不高但内容可信**——词典命中率豁免的门槛。
    ///
    /// 【为什么是这三个数（2026-10-20 实测数据定）】
    ///   0.70：08:37:49 那次的灰度通道是 0.74（内容全对）；
    ///         而 08:37:49 的 `PIAYSTHE:` 只有 0.45、`PAYATHE:DEMO;NOW` 0.48，真垃圾都在 0.5 以下。
    ///   3 词：两三个词凑不出「一句话」，也挡不住单个乱词碰巧在词典里。
    ///   80%：真乱码里偶尔混进一两个 `ON`/`THE` 是常事，但成不了八成。
    /// </summary>
    private const double DictionaryRescueMinConfidence = 0.70;
    private const double DictionaryRescueMinHitRate = 0.80;
    private const int DictionaryRescueMinWords = 3;

    /// <summary>
    /// 这份结果够不够格走「词典命中率豁免」：分数 ≥0.70、至少 3 个词、8 成以上在词典。
    /// </summary>
    /// <param name="note">够格时给出「5/5 个词在词典里（100%，≥80%）」这种人话说明。</param>
    private static bool HasEnoughDictionaryWords(TesseractOcr.Result result, out string note)
    {
        note = "";
        if (result.TotalWordCount < DictionaryRescueMinWords) return false;
        if (result.Confidence < DictionaryRescueMinConfidence) return false;

        double rate = (double)result.DictWordCount / result.TotalWordCount;
        if (rate < DictionaryRescueMinHitRate) return false;

        note = $"{result.DictWordCount}/{result.TotalWordCount} 个词在词典里" +
               $"（{rate * 100:F0}%，≥{DictionaryRescueMinHitRate * 100:F0}%）";
        return true;
    }

    /// <summary>
    /// **删掉开头和结尾的装饰标点**（图片背景里的 `•`、引号、全角冒号之类）。
    ///
    /// 【为什么只删首尾】
    ///   中间的撇号可能是 `don't` / `it's` 这种真缩写，删了就把词弄坏了。
    ///   句尾的 `.` `,` `!` `?` 是正常标点，**不在这个名单里**，一律保留。
    /// </summary>
    private static string TrimEdgeSymbols(string text)
    {
        int start = 0;
        while (start < text.Length && IsEdgeSymbol(text[start])) start++;

        int end = text.Length;
        while (end > start && (IsEdgeSymbol(text[end - 1]) || IsTrailingEdgeSymbol(text[end - 1]))) end--;

        if (start == 0 && end == text.Length) return text;
        return text.Substring(start, end - start).Trim();
    }

    /// <summary>这些字符出现在首尾时一律当装饰品删掉（`. , ! ?` 不在此列）。</summary>
    private static bool IsEdgeSymbol(char c) =>
        c is '•' or '·' or '‘' or '’' or '“' or '”' or '\'' or '：' or '、' or '|'
        || char.IsWhiteSpace(c);

    /// <summary>
    /// **只在结尾**才当装饰品删的横杠类字符（`-` `–` `—` `_`）。
    ///
    /// 【为什么只删结尾，不并进上面那个名单】
    ///   2026-10-23 扫了三份日志全部 7600 条「最终原文」：
    ///     以横杠**结尾**的 40 条 → 去重后**只有 `BATTLEBOARD-`** 这一个，
    ///       就是那张实战图右侧 14px 空白被引擎多认出一个横杠造成的。
    ///     以横杠**开头**的 18 条 → 是 `- HTTP/2.0 …`、`— =" / The quick brown…`
    ///       这种**正常内容**，削掉就错。
    ///   → 所以横杠**只进结尾名单**，开头一律不动。
    /// </summary>
    private static bool IsTrailingEdgeSymbol(char c) => c is '-' or '–' or '—' or '_';
}
