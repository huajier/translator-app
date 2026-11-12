using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TranslatorApp.Core.Translate;

/// <summary>
/// 【2026-11-12 第22轮】句子级切分 + Gale-Church 长度对齐，供卡片做「悬停某句、两侧同亮」。
///
/// 【为什么放弃「段落级 + [S1] 标记」】
///   标记法真机表现不行：
///     · 粒度太粗 —— 用户截图那段只剩 2 段，307 字一大块，高亮亮一整段而不是一句
///     · 长文本原文 4 段 / 译文 6 段，全降级
///     · [S1] 标记漏进译文（Bing 会把标记翻一遍再原样带回来，重复出现）
///   新方案不发标记、不加请求、速度不变：两边各自切句，再用 Gale-Church
///   按**句长比例**做 DP 对齐 —— 长句被拆成两句也能配上（1:2 / 2:1）。
///
/// 【算法来源】照抄 tools\脚本\量-GaleChurch对齐2.py 的 split_foreign / split_chinese / align。
/// </summary>
internal static class SentenceAligner
{
    /// <summary>不能当句末的缩写（小写比较）。与验证脚本 ABBREV 一致。</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "dr", "mr", "mrs", "ms", "st", "vs", "etc", "e.g", "i.e", "cf", "al",
        "т.е", "т.д", "др", "проф", "г", "гг", "рис", "см", "напр",
    };

    private const string ForeignEnds = ".!?…";
    private const string ChineseEnds = "。！？；…";

    /// <summary>
    /// 外文切句：句末标点(.!?…) **或** 换行（下一行不是小写字母开头）。
    ///
    /// 【为什么要用换行切】用户截图那种「标题行 + 正文行」的结构里，
    ///   标题没有句号，只靠标点切会和正文粘成一句。
    ///   而英文正文折行时下一行通常小写开头 —— 所以「下一行非小写开头」就是新句/新块的信号。
    /// </summary>
    internal static List<string> SplitForeign(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        string norm = text.Replace("\r\n", "\n").Trim();

        // ① 先按「换行 + 下一行非小写开头」切块
        var blocks = new List<string>();
        var cur = new StringBuilder();
        foreach (string raw in norm.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                if (cur.Length > 0) { blocks.Add(cur.ToString().Trim()); cur.Clear(); }
                continue;
            }

            string stripped = line.TrimStart();
            bool startsLower = stripped.Length > 0 && char.IsLower(stripped[0]);

            if (cur.Length > 0 && !startsLower)
            {
                blocks.Add(cur.ToString().Trim());
                cur.Clear();
            }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(line);
        }
        if (cur.Length > 0) blocks.Add(cur.ToString().Trim());

        // ② 每块内部再按标点切句
        foreach (string block in blocks) SplitByPunctuation(block, result);
        return result;
    }

    /// <summary>按句末标点切一块文本，跳过缩写/小数点/文件名里的点。</summary>
    private static void SplitByPunctuation(string text, List<string> outList)
    {
        var buf = new StringBuilder();
        int i = 0;
        int n = text.Length;

        while (i < n)
        {
            char ch = text[i];
            buf.Append(ch);

            if (ForeignEnds.IndexOf(ch) >= 0)
            {
                int j = i + 1;
                while (j < n && ForeignEnds.IndexOf(text[j]) >= 0)
                {
                    buf.Append(text[j]);
                    j++;
                }

                bool isEnd = true;

                if (ch == '.')
                {
                    // 小数点：点前是数字、点后也是数字
                    int k = i - 1;
                    while (k >= 0 && char.IsDigit(text[k])) k--;
                    if (k < i - 1 && j < n && char.IsDigit(text[j])) isEnd = false;

                    // 缩写
                    if (isEnd)
                    {
                        string before = buf.ToString(0, buf.Length - 1);
                        Match m = Regex.Match(before, @"([A-Za-zА-Яа-яЁё.]+)$");
                        if (m.Success)
                        {
                            string word = m.Groups[1].Value.TrimEnd('.');
                            if (Abbreviations.Contains(word)) isEnd = false;
                        }
                    }

                    // 文件名 / URL：点后紧跟字母数字，且前面那段词里已有一个点
                    if (isEnd && j < n && char.IsLetterOrDigit(text[j]))
                    {
                        string before = buf.ToString(0, buf.Length - 1);
                        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]))
                        {
                            string[] words = before.Split(new[] { ' ', '\t' },
                                                          StringSplitOptions.RemoveEmptyEntries);
                            if (words.Length > 0 && words[^1].Contains('.')) isEnd = false;
                        }
                    }
                }

                // 句末标点后必须是空白/结束（中文标点除外）
                if (isEnd && j < n && !char.IsWhiteSpace(text[j])
                    && ChineseEnds.IndexOf(text[j]) < 0) isEnd = false;

                if (isEnd)
                {
                    string s = buf.ToString().Trim();
                    if (s.Length > 0) outList.Add(s);
                    buf.Clear();
                    while (j < n && (text[j] == ' ' || text[j] == '\t')) j++;
                    i = j;
                    continue;
                }

                i = j;
                continue;
            }

            i++;
        }

        string tail = buf.ToString().Trim();
        if (tail.Length > 0) outList.Add(tail);
    }

    /// <summary>
    /// 中文切句：句末标点(。！？；…) + 换行。
    /// 中文没有大小写，所以换行一律当分隔（不像外文要看下一行是不是小写开头）。
    /// </summary>
    internal static List<string> SplitChinese(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        string norm = text.Replace("\r\n", "\n").Trim();

        // 先在句末标点之后断开
        var parts = Regex.Split(norm, @"(?<=[。！？；…])");
        foreach (string part in parts)
        {
            foreach (string raw in part.Split('\n'))
            {
                string s = raw.Trim();
                if (s.Length > 0) result.Add(s);
            }
        }
        return result;
    }

    /// <summary>
    /// Gale-Church 句长比例对齐（DP）。
    ///
    /// 【原理】同一个意思的原文句和译文句，**长度比例**应该接近整篇的平均比例。
    ///   按这个假设给每种配对方式算代价，DP 求最小总代价路径。
    ///   允许 1:1 / 2:1 / 1:2 —— 一句长英文被中文拆成两句、或两句英文并成一句中文，都能配上。
    ///
    /// 【保证全覆盖】DP 从 (0,0) 走到 (ns,nd)，每个源句和目标句都**恰好用一次**，
    ///   所以不会丢句也不会重复 —— 这是本方案比「句数相等才配对」强的关键。
    ///
    /// 【返回】每组是（源句下标列表, 目标句下标列表）。空输入返回空列表。
    /// </summary>
    internal static List<(List<int> Source, List<int> Target)> Align(
        IReadOnlyList<string> source, IReadOnlyList<string> target, int maxGroup = 2)
    {
        var pairs = new List<(List<int> Source, List<int> Target)>();
        if (source is null || target is null) return pairs;

        int ns = source.Count;
        int nd = target.Count;
        if (ns == 0 || nd == 0) return pairs;

        // 句长（空串也按 1 算，避免除零）
        int[] srcLen = new int[ns];
        int[] dstLen = new int[nd];
        long sumSrc = 0, sumDst = 0;
        for (int i = 0; i < ns; i++) { srcLen[i] = Math.Max(1, source[i].Length); sumSrc += srcLen[i]; }
        for (int j = 0; j < nd; j++) { dstLen[j] = Math.Max(1, target[j].Length); sumDst += dstLen[j]; }

        // 整篇的长度比：译文平均句长 / 原文平均句长，钳在 [0.15, 8.0]
        double ratio = ((double)sumDst / nd) / ((double)sumSrc / ns);
        ratio = Math.Max(0.15, Math.Min(ratio, 8.0));

        // 允许的配对形状
        var shapes = new List<(int A, int B)> { (1, 1), (2, 1), (1, 2) };
        if (maxGroup >= 3)
        {
            shapes.Add((3, 1));
            shapes.Add((1, 3));
        }

        const double Inf = double.PositiveInfinity;
        var cost = new double[ns + 1, nd + 1];
        var backI = new int[ns + 1, nd + 1];
        var backJ = new int[ns + 1, nd + 1];
        var backA = new int[ns + 1, nd + 1];
        var backB = new int[ns + 1, nd + 1];

        for (int i = 0; i <= ns; i++)
            for (int j = 0; j <= nd; j++)
            {
                cost[i, j] = Inf;
                backI[i, j] = -1;
            }
        cost[0, 0] = 0.0;

        int SegSum(int[] arr, int start, int count)
        {
            int s = 0;
            for (int k = 0; k < count; k++) s += arr[start + k];
            return s;
        }

        for (int i = 0; i <= ns; i++)
        {
            for (int j = 0; j <= nd; j++)
            {
                if (double.IsPositiveInfinity(cost[i, j])) continue;

                foreach ((int a, int b) in shapes)
                {
                    int ni = i + a;
                    int nj = j + b;
                    if (ni > ns || nj > nd) continue;

                    int ls = SegSum(srcLen, i, a);
                    int ld = SegSum(dstLen, j, b);

                    double c;
                    if (ls > 0 && ld > 0)
                    {
                        double x = Math.Log(ld / (ls * ratio));
                        c = x * x;
                    }
                    else
                    {
                        c = 100.0;
                    }
                    if (a != b) c += 0.4;   // 非 1:1 惩罚

                    double nc = cost[i, j] + c;
                    if (nc < cost[ni, nj])
                    {
                        cost[ni, nj] = nc;
                        backI[ni, nj] = i;
                        backJ[ni, nj] = j;
                        backA[ni, nj] = a;
                        backB[ni, nj] = b;
                    }
                }
            }
        }

        // 回溯
        int ci = ns, cj = nd;
        var rev = new List<(List<int> Source, List<int> Target)>();
        while (ci > 0 || cj > 0)
        {
            int pi = backI[ci, cj];
            if (pi < 0)
            {
                // 理论上不该发生（形状里有 1:1，路径一定存在）；
                // 真发生就兜底把剩下的全配一组，保证不丢句。
                var fallSrc = new List<int>();
                var fallDst = new List<int>();
                for (int x = 0; x < ci; x++) fallSrc.Add(x);
                for (int y = 0; y < cj; y++) fallDst.Add(y);
                rev.Add((fallSrc, fallDst));
                break;
            }

            int pj = backJ[ci, cj];
            int a = backA[ci, cj];
            int b = backB[ci, cj];

            var sList = new List<int>();
            var tList = new List<int>();
            for (int x = pi; x < ci; x++) sList.Add(x);
            for (int y = pj; y < cj; y++) tList.Add(y);
            rev.Add((sList, tList));

            ci = pi;
            cj = pj;
        }

        rev.Reverse();
        return rev;
    }
}
