using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Translate;

/// <summary>
/// 长文本分段翻译（2026-11-06 第22轮）。
///
/// 【为什么要分段】
///   真机日志实测：966 字 4889ms 放弃、1070 字 4894ms 放弃，
///   而同量级的 971 字 4466ms 成功、966 字 4569ms 成功 ——
///   同一量级成败看运气，因为全都贴着 HardTimeoutMs = 5000 那条线。
///   一次请求翻的字越多，越容易撞线。切小一点，每段各走一次硬闸，
///   一次抖动只毁一段，不毁整块。
///
/// 【为什么串行，不并行】
///   必应网页翻译没有公开的速率限制，并发请求会被当成爬虫。
///   用户已拍板：串行。快不是这一轮的目标，稳才是。
///
/// 【为什么不写「已翻 N/M 段」进度】
///   用户原话：「不写进度，只让译文长出来」。中途报数字会一直跳，
///   比没有更烦人。所以 progress 里只带累积译文，卡片照着画字就行。
///
/// 【单段失败继续，连续 2 段失败立刻停手】
///   一次抖动不该让整块作废 —— 已翻好的那几段都还有用。
///   但连续 2 段失败已经能证明网络有问题，再往下发只是让用户白等
///   （8 段 × 5 秒 = 40 秒，那比失败更糟）。
///
/// 【日志口径：段级日志不能含「翻译：成功」「翻译：放弃」】
///   历史验收脚本（验收-T3a-翻译.py 等）靠 grep 这两个精确子串数条数。
///   分段后如果每段都打一条，一条长文本会被数成 N 条，等于把已经验过的
///   历史验收全破坏掉。所以段级日志是「翻译：段 2/3，…」，
///   整块结束时这里再照**原格式**补一条汇总 —— 已有脚本一行都不用改。
/// </summary>
internal static class LongTextTranslator
{
    /// <summary>一段大概多少字。400 是实测里「一次请求能在硬闸内翻完」的量级。</summary>
    private const int SegmentChars = 400;

    /// <summary>
    /// 最多切几段。段数多了总等待就长（最坏 8 段 × 5 秒），
    /// 而且每段一次网络往返，边际收益越来越低。
    /// </summary>
    private const int MaxSegments = 8;

    /// <summary>连续几段失败就认定网络有问题、立刻停手。</summary>
    private const int MaxConsecutiveFailures = 2;

    /// <summary>
    /// 翻译一段可能很长的文字。短文本（切完只有一段）直接走
    /// <see cref="TranslationService"/>，行为和分段前**完全一致**。
    /// </summary>
    /// <param name="progress">
    /// 每翻好一段就回调一次，里面是「已成功各段译文拼起来」的累积结果。
    /// 传 null 就只返回最终结果（回放用 null，让它和真机走同一个函数，
    /// 免得再出现「回放全绿、真机挂」那种第二次）。
    /// </param>
    internal static async Task<TranslationResult> TranslateAsync(
        string text,
        int budgetPerSegmentMs,
        IProgress<TranslationResult>? progress,
        CancellationToken ct)
    {
        List<string> segments = SplitIntoParagraphs(text);

        // 只有一段：原样交给服务层。用原始 text 而不是切出来的那段，
        // 保证短文本这条路上缓存键、日志、耗时和改动前一模一样。
        if (segments.Count <= 1)
            return await TranslationService.Shared
                .TranslateAsync(text, budgetPerSegmentMs, ct)
                .ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var okTexts = new List<string>();
        var failedSegments = new List<int>();
        string provider = string.Empty;
        string detected = string.Empty;
        bool fromCache = false;
        int cacheHits = 0;
        int consecutiveFailures = 0;
        TranslationResult? firstFailure = null;

        for (int i = 0; i < segments.Count; i++)
        {
            // quiet: true —— 段级调用不发「翻译：成功/放弃/命中缓存」，
            // 免得历史验收脚本把一条长文本数成 N 条。汇总在下面统一补。
            TranslationResult r = await TranslationService.Shared
                .TranslateAsync(segments[i], budgetPerSegmentMs, ct, quiet: true)
                .ConfigureAwait(false);

            if (r.Success)
            {
                consecutiveFailures = 0;
                if (okTexts.Count == 0)
                {
                    // 提供方/源语言/缓存标记都取第一段成功的那个：
                    // 同一块的各段本来就走同一个提供方、同一种语言。
                    provider = r.Provider;
                    detected = r.DetectedLanguage;
                    fromCache = r.FromCache;
                }
                if (r.FromCache) cacheHits++;
                okTexts.Add(r.Text);

                // 【2026-11-06 第22轮 S5 踩到的】命中缓存时 r.ElapsedMs 是**当初那次联网**
                //   的耗时（TranslationService 的命中分支就是这么填的，见那边的注释），
                //   不是这次花了多久。直接打出来就是「段 1/3 … 用时 316 ms」——而这次其实
                //   只花了 0 ms，日志在说谎。所以命中时补一句标明这个数是谁的时间。
                AppLogger.Info($"翻译：段 {i + 1}/{segments.Count}，{segments[i].Length} 字 → " +
                               $"译 {r.Text.Length} 字，用时 {r.ElapsedMs} ms" +
                               (r.FromCache ? "（缓存命中，这个数是当初那次联网的耗时）" : string.Empty));

                // 每段翻完立刻把「已翻好的拼起来」推出去，卡片照着画字。
                // 中途不报数字（TransDetail 那边由卡片负责不显示）。
                progress?.Report(BuildAggregate(okTexts, failedSegments, provider, detected,
                                                fromCache, sw.ElapsedMilliseconds));
            }
            else
            {
                failedSegments.Add(i + 1);
                firstFailure ??= r;
                consecutiveFailures++;
                AppLogger.Warn($"翻译：段 {i + 1}/{segments.Count}，{segments[i].Length} 字，没翻出来" +
                               $"（{r.Failure.ToUserMessage()}，用时 {r.ElapsedMs} ms）");

                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    AppLogger.Warn($"翻译：连续 {consecutiveFailures} 段没翻出来，剩下 " +
                                   $"{segments.Count - i - 1} 段不再发请求");
                    break;
                }
            }
        }

        sw.Stop();

        // 一段都没成功：原样返回第一段的失败结果，失败原因（断网/超时/…）
        // 保持原样 —— 「断网时立刻显原文」这条行为一个字都没变。
        if (okTexts.Count == 0)
        {
            TranslationResult fail = firstFailure ?? new TranslationResult
            {
                Failure = TranslationFailure.Unknown,
                Detail = "所有分段都没翻出来",
            };
            AppLogger.Warn($"翻译：放弃，{fail.Failure.ToUserMessage()}（{fail.Detail}，" +
                           $"用时 {sw.ElapsedMilliseconds} ms）");
            return fail;
        }

        TranslationResult aggregate = BuildAggregate(okTexts, failedSegments, provider, detected,
                                                     fromCache, sw.ElapsedMilliseconds);

        // 照**原格式**补一条汇总，字数用整块原文和整块译文总数。
        AppLogger.Info($"翻译：成功，{text.Length} 字 → 译 {aggregate.Text.Length} 字，" +
                       $"用时 {sw.ElapsedMilliseconds} ms（{provider}，检测到源语言「{detected}」；" +
                       $"分 {segments.Count} 段{DetailSuffix(aggregate.Detail)}）");

        // 全部来自缓存时补一条命中日志：S5 要的是「第二次框立刻出全文」，
        // 而段级命中日志被 quiet 压掉了，不给一条就没法从日志看出来。
        if (cacheHits == segments.Count)
            AppLogger.Info($"翻译：命中缓存（整块 {segments.Count} 段都命中，共 {aggregate.Text.Length} 字，" +
                           $"本次 {sw.ElapsedMilliseconds} ms）");

        return aggregate;
    }

    /// <summary>把已成功的各段译文拼成累积结果（不传 progress 的最终值也用它）。</summary>
    private static TranslationResult BuildAggregate(
        List<string> okTexts, List<int> failedSegments, string provider, string detected,
        bool fromCache, long elapsedMs)
    {
        return new TranslationResult
        {
            // 失败段跳过、不占位：拼的是「真拿到的译文」，不掺空行。
            Text = string.Join("\n", okTexts),
            Provider = provider,
            ElapsedMs = elapsedMs,
            Failure = TranslationFailure.None,
            Detail = failedSegments.Count == 0
                ? string.Empty
                : $"第 {string.Join("、", failedSegments)} 段没翻出来",
            DetectedLanguage = detected,
            FromCache = fromCache,
        };
    }

    private static string DetailSuffix(string detail) => detail.Length == 0 ? string.Empty : "，" + detail;

    /// <summary>
    /// 按行切段：从前往后累积，累积到 >= 阈值就成一段。
    ///
    /// 【单行超过阈值不切】
    ///   不切句子中间。一行 800 字就是一段 800 字 —— 翻得慢，但至少是通顺的；
    ///   从句子中间切开的半句话，翻译出来连语序都是错的。
    ///
    /// 【空行丢弃】
    ///   空行不携带内容，留着只是白占字符数。
    ///
    /// 【internal 而 not private】只为了让 src\Tests 能直接验它（本工程测试项目和
    ///   Core 编在同一程序集里）。切段规则是这一轮唯一有分支的纯逻辑，
    ///   没有一条能跑的检查，改个阈值就没人知道它还在不在。
    /// </summary>
    internal static List<string> SplitIntoParagraphs(string text)
    {
        int threshold = SegmentChars;

        // 段数上限：总长超过「默认阈值 × 上限」时，把阈值抬高到「刚好 8 段」，
        // 保证最多 8 次网络往返（否则 5000 字会被切成 13 段，用户等 30 秒）。
        //
        // 【2026-11-06 第22轮审阅：这里原来是整数除法，漏掉了边界】
        //   旧写法 text.Length / SegmentChars > MaxSegments 是整数除法：
        //   3599 / 400 = 8，8 > 8 为 false → 不抬阈值，但 3599 字按 400 一段
        //   实际能切出 9 段。上面注释写着「保证最多 8 次网络往返」，代码没做到。
        //   实测越界区间（每行 100 字）：3130 字 8 段、3332 字 9 段、3433 字 9 段、
        //   3534 字 9 段、3635 字 8 段（3635 恰好被整数除法判出来了）。
        //   改成乘法比较就没有这个缝：> 3200 字才抬阈值，≤ 3200 走 400 一段。
        if (text.Length > SegmentChars * MaxSegments)
            threshold = (int)Math.Ceiling(text.Length / (double)MaxSegments);

        var segments = new List<string>();
        var sb = new StringBuilder(text.Length);

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);

            if (sb.Length >= threshold)
            {
                segments.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0) segments.Add(sb.ToString());

        // 全是空白行这种极端情况：当成一段原样送出去，交给服务层判空。
        if (segments.Count == 0) segments.Add(text);

        return segments;
    }
}
