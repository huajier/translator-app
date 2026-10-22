using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Abstractions;
using TranslatorApp.Core.Logging;
using Windows.Media.Ocr;

namespace TranslatorApp.Core.Capture;

internal sealed partial class OcrTextCapture : ITextCapture
{
    private readonly record struct FirstPassOutcome(
        PassResult First,
        string Text,
        int LineCount,
        long OcrMs,
        string Note,
        Bitmap? Upscaled);

    private async Task<FirstPassOutcome> RunFirstPassAsync(
        Bitmap plain, Bitmap raw, OcrEngine engine, CancellationToken cancellationToken)
    {
        long preprocessStart;
        var first = await RecognizeOnce(engine, plain, cancellationToken).ConfigureAwait(false);
        AppLogger.Info($"第一遍完成：{first.LineCount} 行，" +
                       $"{LanguageDetector.Describe(first.Text)}，" +
                       $"有效字符 {TextNormalizer.CountMeaningfulChars(first.Text)} 个");
        // 把识别到的原文记进日志：不然「读不全」这类问题根本没法定位
        //
        // 【2026-10-13 加了分数】
        //   为什么要单独打第一遍的分数：`shouldUpscale = false`（英文段落）时
        //   根本不会跑第二遍，下面「两遍比分」那行也就不会打，
        //   于是**英文段落的 Windows 第一遍从来没被记过分**。
        //   而 V0.3.2.7 那版比的是 `text`，它有可能已经是第二遍了（见一百二十五节），
        //   分不清是哪一遍，所以这里把**第一遍原样**记一份。
        //   **只记分，不参与任何选择。**
        AppLogger.Info($"第一遍原文：{Preview(first.Text)}" +
                       $"（人话分 {OcrQualityScorer.Score(first.Text)}，仅记录）");

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

                // 【2026-10-13 只加日志，不改逻辑】
                //   为什么加：复测 7 段（灰底灰字/橙底绿字/英俄混排）21 次全错，
                //   日志里却出现矛盾的两行——
                //     「两遍比分：原图 0 分（0 字），二值化版 228 分（100 字）」
                //     「四路之一「灰度」：置信度 0.00，内容【】」
                //   也就是「放大 2 倍 + 二值化」这张图上 **Tesseract 认出过 100 个字**，
                //   但最后挑结果用的是六路通道的 best（全 0 字），
                //   那 100 个字在 OcrTextCapture.cs:1595 被覆盖丢弃了（`text = cleanedTess`）。
                //   **但日志没记第二遍的原文，没法证明那 100 个字是准的还是垃圾**——
                //   两套都瞎和「对的结果被丢掉」，改法完全不同。
                //   所以先把第二遍原文打出来，重测一次就能定。
                AppLogger.Info($"第二遍原文：{Preview(second.Text)}" +
                               $"（人话分 {OcrQualityScorer.Score(second.Text)}，{TextNormalizer.CountMeaningfulChars(second.Text)} 个有效字符，仅记录）");

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

                    // ==========================================================
                    // 【2026-10-22 补末尾句号】
                    //
                    //   现象：`材料3-P2` 第二遍认成
                    //   `服务已启动，端口 8080。⏎数据库连接失败。⏎请检查配置文件`
                    //   **最后那个句号丢了**，差 1 字符判错。
                    //
                    //   做法：第二遍末尾是个「字」、而第一遍末尾是句末标点，
                    //   就把第一遍那个标点补到第二遍末尾。
                    //
                    //   离线对拍（`tools\临时\模拟补句号2.ps1`，14 个历史日志、
                    //   2884 次「采用第二遍」）：只触发 6 次，**6 次全对**。
                    //
                    //   ⚠️ 判据必须带「第二遍末尾是字」这一条。第二遍常把
                    //   「。」认成「．」(U+FF0E) 或「·」(U+00B7)，光看
                    //   「结尾不是 。」会把它们也当成丢了，补成「过程．。」。
                    //   不加这条：19 次触发里 13 次是错的（数据见同一脚本）。
                    // ==========================================================
                    string secondTrimmed = text.TrimEnd();
                    string firstTrimmed = (first.Text ?? string.Empty).TrimEnd();
                    if (secondTrimmed.Length > 0 && firstTrimmed.Length > 0
                        && char.IsLetterOrDigit(secondTrimmed[^1])
                        && firstTrimmed[^1] is '.' or '!' or '?' or '。' or '？' or '！')
                    {
                        char tail = firstTrimmed[^1];
                        text = secondTrimmed + tail;
                        note += "（末尾补回句号）";
                        AppLogger.Info($"补末尾句号：第二遍末尾是「{secondTrimmed[^1]}」，第一遍以「{tail}」结尾");
                    }
                }

                ocrMs += second.OcrMs;
            }

            AppLogger.Info($"OCR 最终采用：{note}，两遍合计识别耗时 {ocrMs} 毫秒");

            return new FirstPassOutcome(first, text, lineCount, ocrMs, note, upscaled);
        }
        catch
        {
            upscaled?.Dispose();
            throw;
        }
    }
}
