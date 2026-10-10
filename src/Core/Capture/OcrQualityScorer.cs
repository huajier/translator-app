using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 给两版识别结果打「哪个更像人话」的分数。
///
/// 【为什么需要它】
///   现在有两个引擎：Windows 自带（快）和 Tesseract（慢）。
///   两者各有胜负，必须有个**说得出道理**的规则来挑，
///   而不是随便取一个——不然用户会看到「同样的框，有时准有时不准」，根本没法用。
///
/// 【打分规则（每条都能用人话解释）】
///   1. **有效字符多的一版加分**
///      「有效」= 不是空白、不是把汉字拆成偏旁剩下的零碎。
///      这是最基本的：读出 200 个字的总比读出 20 个强。
///   2. **汉字之间不该有空格 —— 出现就扣分**
///      这是阶段 3 实测到的 Windows 自带引擎的老毛病：
///      「取 词 测 试 ： 这 一 行」——汉字被逐个塞空格。
///      那是典型的「读出来了但读得很难看」，该被扣分。
///   3. **偏旁碎片成串出现就扣分**
///      「氵」「扌」「犭」「忄」这类**单独出现且后面不跟一个像样的字**，
///      基本就是「法」被拆成两半认出来的痕迹（用户 2026-10-08 报的）。
///   4. **混排字母数字（像 API、v2.0、2024年）加分**
///      Tesseract 在这类行上明显更好，不能让 Windows 版凭长度把它压下去。
///   5. **一串乱码（如 2 ik ES 28 BS HER）重扣**
///      既没有中文、又没有正常英文词，看着就是噪声。
///
/// 【为什么要写成独立的一个类】
///   规则很容易被改坏。单独放一个文件、每条都写清理由，
///   将来调不动的时候能一眼看出是哪条规则在捣乱。
/// </summary>
internal static class OcrQualityScorer
{
    /// <summary>偏旁/部首类字符——单独出现基本就是「字被拆开了」。</summary>
    private static readonly char[] RadicalLike =
    {
        '氵', '扌', '犭', '忄', '灬', '亻', '彳', '纟', '钅', '饣',
        '艹', '衤', '礻', '辶', '阝', '刂', '勹', '匕', '冫', '冖'
    };

    /// <summary>像「API」「v2.0」「2024」这种中英/数字混排片段。</summary>
    private static readonly Regex MixedAlnum = new(@"[A-Za-z0-9][A-Za-z0-9\.\-_]{1,}", RegexOptions.Compiled);

    /// <summary>
    /// 给一段识别结果打分。分数越高越可信。
    /// </summary>
    public static int Score(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        int score = 0;

        // ---- 规则 1：有效字符数量 ----
        int effective = text.Count(c => !char.IsWhiteSpace(c));
        score += Math.Min(effective, 400);   // 封顶，免得超长文本一家独大

        // ---- 规则 2：汉字之间的空格 ----
        // 「取 词 测 试」这种，每一处都要扣。
        int spacedCjk = Regex.Matches(text, @"(?<=[\u4e00-\u9fff])\s(?=[\u4e00-\u9fff])").Count;
        score -= spacedCjk * 3;

        // ---- 规则 3：偏旁碎片 ----
        foreach (char radical in RadicalLike)
        {
            // 后面既不跟汉字、也不是词尾，就当成「被拆开的碎片」扣分
            score -= Regex.Matches(text, radical + @"(?![一-鿿])").Count * 4;
        }

        // ---- 规则 4：中英数字混排 ----
        score += MixedAlnum.Matches(text).Count() * 6;

        // ---- 规则 5：纯乱码重扣 ----
        bool hasCjk = text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        bool hasLetters = text.Any(char.IsLetter);
        if (!hasCjk && !hasLetters && effective >= 4)
        {
            score -= 30;
        }

        return score;
    }

    /// <summary>
    /// 在两版结果里挑更好的那版，并把「为什么选它」写进日志。
    ///
    /// 【为什么一定要把理由写进日志】
    ///   用户看到结果不对时会问「为什么这次准那次不准」。
    ///   没有理由就只能猜；有了理由才能定位到是哪条规则在捣乱。
    /// </summary>
    public static bool IsBetter(string candidate, string incumbent)
    {
        int a = Score(candidate);
        int b = Score(incumbent);

        // 平手时**保留原来那版**：Windows 自带更快，
        // 分数一样就没理由为了慢的那版多花几百毫秒。
        return a > b;
    }
}