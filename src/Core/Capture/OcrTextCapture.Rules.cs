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

internal sealed partial class OcrTextCapture : ITextCapture
{

    // ==========================================================
    // 【2026-10-13 X2 专用】劈词证据优先
    //
    //   X2（`版本 2.0 已于 2024 年发布 config.txt` 被读成 `config txt`）
    //   是 V0.3.3.0 唯一判错段。病根：chi_sim 把 `config.txt` 切成了两个词，
    //   词级自报 `【config】71.45 ｜【txt】71.45` —— 两词同分，它自己也不知道
    //   那个点算不算分隔点。Windows 自带 OCR 每一次都读对 → **图够清楚**。
    //
    //   「分数更高才换」救不了：主票 0.94（拆词的错答案）比第三/四票 0.93（对的）还高，
    //   两次实测都被挡住。→ **置信度与对错反相关，不能拿它判对错**。
    //   但「同一个词被劈成两半」是硬证据，可以据此换票。
    //
    //   【四条硬门槛，缺一不换】
    //     1. 主票必须是中文（英文句子里的 `a`/`I` 不算劈词，绝不能让这条规则碰英文）
    //     2. 两份去掉空格和标点后，字母数字完全一样（`API` vs `APIl` 这种真差异直接挡住）
    //     3. 分数差 < 0.02（差得多说明本来就不是同一件事，别插手）
    //     4. 主票有「孤立短词」而候选票没有（`config txt` 的 `txt` 是，`config.txt` 里的不是）
    //
    //   【离线验过】tools\日志\app-20261013.log 里 47 对「主票 vs 第三票」，
    //   23 对不一致：正向触发 13 条（全是 `config txt`→`config.txt`，全对）、
    //   反向触发 0 条、不触发 10 条（全是真差异）。
    //
    //   ponytail: 只在「两个引擎读出来不一样」这个本来就在犹豫的时刻调用；
    //   一致时一秒都不多花，识别流程和投票主干一个字没改。
    // ==========================================================
    private static bool SplitWordEvidenceFavorsCandidate(
        string mainText, string candidateText, double mainConfidence, double candidateConfidence)
    {
        if (string.IsNullOrWhiteSpace(mainText) || string.IsNullOrWhiteSpace(candidateText)) return false;

        // 门槛 1：只管中文，英文段一个都不碰
        if (!LanguageDetector.IsMostlyChinese(mainText)) return false;

        // 门槛 2：字母数字必须完全一样（真差异在这里就被挡住了）
        if (LettersAndDigitsOnly(mainText) != LettersAndDigitsOnly(candidateText)) return false;

        // 门槛 3：分数接近才说明本来就在争同一件事
        if (Math.Abs(mainConfidence - candidateConfidence) >= 0.02) return false;

        // 门槛 4：主票把连着的词劈开了，候选票没有
        return HasIsolatedShortWord(mainText) && !HasIsolatedShortWord(candidateText);
    }

    /// <summary>
    /// 【2026-10-15 路二 · 点号回填】把最终结果里被拆成两个词的 `x` `y`，
    /// 按 Windows 那份里存在的 `x.y` 写法合回去。
    /// </summary>
    /// <remarks>
    /// 治的是 X2 那一处：Windows 读对了 `config.txt`，但它把 `2.0` 读成 `2 ℃`，
    /// 整份 Windows 结果被丢掉；Tesseract 三张票（0.94 / 0.92 / 0.92）**全都把点吞了**，
    /// 劈词证据规则只能从候选里挑，挑不出没有的东西。
    /// 结果是两边各错一半 —— 这一条只做「把点补回来」，不重选任何一张票。
    ///
    /// 【三条硬条件，缺一不回填】
    ///   1. 只在「两个引擎读出来不一样」这个分支里调用（本来就是犹豫的时刻）
    ///   2. 合并出来的样子必须**原样存在于 Windows 那份里**，一个字都不许自己造
    ///   3. 这个点两边都必须是字母或数字，且只有这一个点
    ///      （`2 ℃`、`2 · 0`、中文标点一律够不到，只认 `config.txt` 这种）
    ///
    /// 【离线验过】tools\_中文对拍.csv 里 245 段「两个引擎读出来不一样」，
    /// 能对上标准答案的 219 段上出手 24 次、救回 24 次、**弄坏 0 次**；
    /// 「分不清对错」的样本上出手 0 次。24 次全是 X2 那一句
    /// （该句历史上被框 106 次、点丢 26 次，其中 24 次落在这一条上）。
    /// 更保守的「去掉点号后整句与 Windows 一字不差」版出手 0 次，等于白加，未采用。
    ///
    /// 【天花板，明知故犯】Windows 自己凭空多读出一个点时，这一条会跟着错。
    ///   245 段里一次没出现过，样本就这么大，已用探针断言如实钉住。
    ///
    /// ponytail: 纯字符串处理，不碰识别、不碰投票主干；只在中文不一致那一支里多跑一次。
    /// </remarks>
    private static string MergeSplitDottedTokensFrom(string text, string windowsText)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(windowsText)) return text;

        // Windows 那份里所有「字母数字.字母数字」的写法（只收这一种形状）
        var dotted = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in windowsText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsDottedToken(raw)) dotted.Add(raw);
        }
        if (dotted.Count == 0) return text;

        // 把「两个相邻的字母数字词、中间只隔着空白」的地方合起来（空白连着换行也照合）。
        // ⚠️ 必须自己按词走，不能用 Regex.Replace 那种「边扫边替换」——
        //    匹配是不重叠的：`0 config txt` 里 `0 config` 先被吃掉，
        //    剩下的扫描位置已经越过 `config`，轮不到 `config txt`（探针逮到过）。
        var words = new List<(int Start, int End)>();
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsAsciiAlnum(text[i])) continue;
            int start = i;
            while (i < text.Length && IsAsciiAlnum(text[i])) i++;
            words.Add((start, i));
        }

        var sb = new System.Text.StringBuilder(text.Length);
        int copied = 0;
        for (int i = 0; i < words.Count; i++)
        {
            if (i + 1 < words.Count)
            {
                string joined = text[words[i].Start..words[i].End] + "." + text[words[i + 1].Start..words[i + 1].End];
                if (dotted.Contains(joined) && OnlyWhitespace(text, words[i].End, words[i + 1].Start))
                {
                    sb.Append(text, copied, words[i].Start - copied);
                    sb.Append(joined);
                    copied = words[i + 1].End;
                    i++;   // 两个词一起用掉了
                    continue;
                }
            }
        }
        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }

    /// <summary>
    /// 英文第二票能不能救回第一票读坏的结果（2026-10-14，先叫「甲方案」，后放宽为「顺序关系」，
    /// 再放宽为「顺序关系或只差几个字」）。
    ///
    /// <para>规矩按顺序看三句，命中任一句就换：</para>
    /// <para>　③<b>两份的字母数字一模一样</b>（差别只在标点或者空格怎么摆）——采第二票。
    /// 这一句最先看，它<b>不看长短</b>。</para>
    /// <para>　①<b>顺序关系</b>：第二票更长，并且第一票读出来的每一个字，在第二票那份里都能
    /// **按原来的先后顺序**找着（术语叫子序列）。</para>
    /// <para>　②<b>只差零星几个字</b>：第二票更长，并且两份的编辑距离不超过
    /// <see cref="AllowedSecondVoteDistance"/>。</para>
    /// <para>　④<b>第二票短一个字</b>：两份只差一个字母，而且是第二票**少**一个——
    /// 第一票末尾凭空多吐了一个字。这一句同样<b>不看长短</b>（它要的正是「第二票更短」）。</para>
    ///
    /// <para>【路子①的来历】（2026-10-14 上午）老规矩要求第二票**一字不差以第一票开头**，
    /// 能治「尾巴被咬掉」（`editable text.` → `editable te`），但治不了**第一票在句中打磕巴**——
    /// A16 实测那次第一票读成 `…into editab e te`（`editable` 中间凭空多空一格、又漏了 `xt`），
    /// 第二票明明读全了，却因为开头两个字之后就对不上，被老规矩挡在门外。
    /// 换成顺序关系后，第一票那些字在第二票里照样按顺序找得着，就能换。</para>
    ///
    /// <para>【路子②的来历】（2026-10-14 下午，A5 实测）顺序关系要求第一票**一个错字都不能有**，
    /// 于是又漏掉一类：第一票把 `editable` 读成 `edital le`——**少一个 `b`、多一个 `l`**。
    /// 这种改动是「替换 + 插入」，第一票多出来的那个 `l` 在第二票里没有，
    /// 顺序关系当场不成立（A5 那次第二票读得又全又准、置信度还更高，照样被挡在门外）。
    /// 补一条「只差零星几个字」把这类捞回来。</para>
    ///
    /// <para>【路子③的来历】（2026-10-14 下午，用户补测 10 次）前面两句都要求**第二票更长**，
    /// 于是漏掉一整类：第一票「字母全对、只有末尾符号读错」——`text.` 读成 `text`（句号没了）、
    /// `text.` 读成 `text |`（句号变竖线）。这类两份的**字母数字完全一样**，
    /// 长短条件当场不成立，第二票明明是对的也换不了。
    /// 用户按 RULE 6 对同一处补测 10 次，5 次出错（4 次丢句号、1 次 `t ext` 中间掰开），
    /// 远超「错 1 次以内算过」的线，确认不是抖动。
    /// 补上「字母数字一样就采第二票」这一句，那 5 次全部能救。</para>
    ///
    /// <para>【路子④的来历】（2026-10-14 15:44/15:45，A 系列压力材料）上面三句的方向都是
    /// 「第二票不能比第一票短」，于是又漏掉一类：第一票**末尾凭空多吐一个字母**——
    /// `editable text.` 读成 `editable te ext`（多一个 `e`）、`editable text a`（多一个 `a`），
    /// 第二票干干净净读全了，可它比第一票**短一个字母**，三句全不放行。
    /// 补一句「两份只差一个字母、且是第二票少的」把这类捞回来。</para>
    ///
    /// <para>数据（2026-10-14 三套离线体检）：
    /// 真机日志「两票齐全 + 能对上标准答案」的块——路子③额外出手 8 次、带标点人工核对
    /// 换对了 7 次；唯一那次「换错」其实是两份都对，差别只在直引号/弯引号（按 RULE 1 不算错）。
    /// 另有 1 个「判不了对错」的块出手。规矩①②③并集在真机上共出手 29 次、弄坏 0。
    /// 合成 255 张图：路子③出手 0 次（GDI+ 画出来的图没有「低对比度竖带」这个病，压不出来）。
    /// ⚠️ 真实数据探针判对错**只比字母数字**，竖线和引号它看不见，
    /// 所以治「符号类错法」的规矩必须另配一段人工看原文（这条教训叫量具盲区）。</para>
    ///
    /// <para>【这条规矩挡不住的情况，明说】
    /// ①两份**都不全**时救不了：实测 12:39:59 那次第一票 `…editable ex`、第二票 `…editable tex`，
    /// 第二票只多认一个 `t`，还是错的。
    /// ③第二票自己读得更差、但**更长且改几个字就凑得上**时会被换错。
    /// 历史数据里没出现过（那 2 个「第二票更差」的样本是同长度，被「更长」挡在外面），
    /// 但这是这条规矩明摆着的天花板，放宽门槛时要知道。
    /// ④路子③**不看长短**，所以「第二票把对的字母改错了、又刚好同长度」挡不住。
    /// 历史上一次没出现过（那 2 个「第二票更差」的真实样本是 `trips`→`tnps`，
    /// 第二票更**短**，同样过不了路子③的字母数字相等条件，实测出手 0 次）。
    /// ⑤路子④要的正是「第二票更短」，所以「第一票读对了、第二票反而少认一个字母」时挡不住。
    /// 这是它与前面三句方向**相反**的代价：离线 240 个能对上答案的样本里出手 2 次、
    /// 全部救回，反例 0，但样本就这么多，放宽时要知道这一条的天花板在这儿。</para>
    /// </summary>
    private static bool SecondVoteRecoversTail(string? firstText, string? secondText)
    {
        if (string.IsNullOrWhiteSpace(firstText) || string.IsNullOrWhiteSpace(secondText)) return false;

        string first = LettersAndDigitsOnly(firstText);
        string second = LettersAndDigitsOnly(secondText);
        if (first.Length == 0) return false;

        // 路子③：字母数字一模一样，差别只在标点/空格怎么摆（先看，它不看长短）
        if (first == second) return true;

        // 路子④：第二票比第一票短一个字母，而且两份只差这一个（第一票末尾凭空多吐了一个字母）
        if (second.Length < first.Length && EditDistance(first, second) <= 1) return true;

        // 下面两条都要求第二票更长：不比第一票长就换过去只会丢字。
        if (second.Length <= first.Length) return false;

        // 路子①：第一票的字按原顺序在第二票里都找得着
        if (IsSubsequence(first, second)) return true;

        // 路子②：两份只差零星几个字
        return EditDistance(first, second) <= AllowedSecondVoteDistance(first.Length);
    }

    /// <summary>
    /// `small` 的字是不是按原顺序都出现在 `big` 里（子序列）。
    /// </summary>
    private static bool IsSubsequence(string small, string big)
    {
        int i = 0;
        foreach (char c in big)
        {
            if (i < small.Length && small[i] == c) i++;
        }
        return i == small.Length;
    }

    /// <summary>
    /// 「只差零星几个字」这条路允许多少个字符的差别。
    ///
    /// <para>固定 4 个字符，同时不超过第一票长度的四分之一（至少 1 个）。
    /// 为什么按长度收：固定 4 在**短串**上等于没有门槛——第一票只读出 3 个字母时，
    /// 差 4 个字符已经超过它本身的长度，那就是「只要第二票更长就换」，
    /// 这条无脑规矩早就被实测否掉过（会把对的换成错的）。</para>
    /// <para>60 字上下的长句：`min(4, 15)` = 4，与不带比例的版本完全同分。</para>
    /// </summary>
    private static int AllowedSecondVoteDistance(int firstLength)
        => Math.Min(4, Math.Max(1, firstLength / 4));

    /// <summary>
    /// 两份文本要改几个字符才能变成对方（编辑距离，Levenshtein，滚动数组）。
    /// 只给 <see cref="SecondVoteRecoversTail"/> 和日志用。
    /// </summary>
    private static int EditDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// 【2026-10-15 路二第二步（Z1）】引擎自己都嫌没把握的那个「单个汉字」，
    /// 如果整句只出现一次、又跟别的字拉开一大截，就把它删掉。
    ///
    /// <para>四条门槛，缺一不可：</para>
    /// <para>　①垫底分 &lt; <see cref="LowWordScoreCeiling"/>（70）；</para>
    /// <para>　②垫底分 &lt; 第二低分 × <see cref="LowWordScoreRatio"/>（0.85）；</para>
    /// <para>　③那个词正好是**一个汉字**（拉丁字母、数字、标点都不算）；</para>
    /// <para>　④那个字在整句里**只出现一次**（出现两次就不知道该删哪个）。</para>
    ///
    /// <para>【只能长在「中文兜底」那一个分支里】离线对拍实证：限定在那一条路上
    /// 出手 12、救回 12、弄坏 0；放开到全部块就多 2 次弄坏（把 Z2 的「图」删了）。</para>
    ///
    /// <para>【天花板，明知故犯】引擎把一个读对了但没把握的字报成垫底时，会跟着删错。
    /// 2245 块里 0 次，样本有限，已用探针断言如实钉住。</para>
    ///
    /// ponytail: 纯字符串处理 + 复用引擎已经报回来的分数，不碰识别、不碰投票主干。
    /// </summary>
    private static string DropLowScoreSingleChar(string text, TesseractOcr.Result result)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string? word = result.MinWordText;
        if (string.IsNullOrEmpty(word) || word.Length != 1) return text;
        if (!IsCjk(word[0])) return text;

        if (result.MinWordConfidence >= LowWordScoreCeiling) return text;
        if (result.MinWordConfidence >= result.SecondMinWordConfidence * LowWordScoreRatio) return text;

        int at = text.IndexOf(word[0]);
        if (at < 0) return text;
        if (at != text.LastIndexOf(word[0])) return text;   // 出现两次以上，不知道该删哪个

        return text.Remove(at, 1);
    }

    /// <summary>
    /// 第二票比第一票多认出来的部分（去掉空白），只给日志看。
    /// 两份一样长（或多认的都在前半截）时返回空串。
    /// </summary>
    private static string DescribeTailGain(string firstText, string secondText)
    {
        string first = LettersAndDigitsOnly(firstText);
        string second = LettersAndDigitsOnly(secondText);
        if (second.Length <= first.Length) return "";
        return second[first.Length..];
    }
}
