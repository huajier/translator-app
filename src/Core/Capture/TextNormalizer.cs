using System;
using System.Text;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 识别结果清理：把 OCR / 剪贴板吐出来的原始文本收拾干净。
/// </summary>
internal static class TextNormalizer
{
    /// <summary>
    /// 整理识别文本：
    ///   1. 去掉首尾空白；
    ///   2. **去掉汉字之间的多余空格**（见 <see cref="RemoveSpacesBetweenChinese"/>）；
    ///   3. 行尾多余空格；
    ///   4. 合并纯空行（最多保留一个空行当段落分隔）；
    ///   5. **只在明显是断行时才合并两行**（见 <see cref="ShouldJoinLines"/>）。
    ///
    /// 规格第 22 条明确要求：**不许无脑合并所有行**，
    /// 否则中文段落的结构会被压成一坨。
    /// </summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string[] lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        int blankRun = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = TrimLeadingStrayQuotes(FixSpuriousSpaces(lines[i].Trim()));

            if (line.Length == 0)
            {
                blankRun++;
                continue;
            }

            if (sb.Length > 0)
            {
                // 【踩过的坑 · 2026-10-08 实验时才发现】
                //   原来只在「有空行」时才补换行，结果**连续的非空行被直接粘在一起**：
                //   四行文字识别出来变成一整段「…识别质量。English test line: …」。
                //   句子都黏住了，送去翻译肯定翻错。
                //   现在的规矩：空行当段落分隔；否则判断是不是明显断行，
                //   是就接成一句，不是就老老实实换行。
                if (blankRun >= 1) sb.Append('\n');
                else if (ShouldJoinLines(LastLine(sb), line)) sb.Append(' ');
                else sb.Append('\n');
            }

            sb.Append(line);
            blankRun = 0;
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// 去掉行首多出来的引号类符号。
    ///
    /// 【2026-10-09 用户实测发现的】
    ///   橙底绿字那几段，行首都凭空冒出一个引号：
    ///     `‚Съешь же ещё…`   （应该是 `Съешь же ещё…`）
    ///     `‘другой.`         （应该是 `другой.`）
    ///     `‘Small printed…`  （应该是 `Small printed…`）
    ///
    ///   这些符号**不是原文里的**，是框选边缘蹭到的一点像素被认成的。
    ///   它们紧贴着一行文字的开头，看着就像打头一个引号，
    ///   送去翻译会被当成真的引号，句子就莫名其妙断开了。
    ///
    /// 【为什么只动行首，不动行尾和中间】
    ///   行首这个位置**必然紧跟着字母**，一个引号站在字母前面毫无意义，
    ///   删掉它百分之百安全。
    ///   行尾和行中的引号可能是原文自带的（`他说"好"`），删了就是篡改内容。
    /// </summary>
    private static string TrimLeadingStrayQuotes(string line)
    {
        if (line.Length == 0) return line;

        const string quotes = "'\"‘’‚‛“”«»";
        int start = 0;

        while (start < line.Length)
        {
            char c = line[start];
            if (quotes.Contains(c))
            {
                start++;
                continue;
            }
            // 引号后面偶尔还跟着空格，一起去掉
            if (char.IsWhiteSpace(c))
            {
                start++;
                continue;
            }
            break;
        }

        if (start == 0) return line;

        string trimmed = line[start..].TrimStart();

        // 全是引号和空白 → 这行本来就没内容，保持原样
        if (trimmed.Length == 0) return line;

        return trimmed;
    }

    /// <summary>
    /// 修掉识别引擎凭空塞进去的空格。
    ///
    /// 【为什么必须去 —— 2026-10-08 用户反馈「读不全」的实测原因】
    ///   Windows 自带 OCR 处理中文时，会在**每个汉字之间插一个空格**，
    ///   原样吐出来是「取 词 测 试 ： 这 一 行」，一句话被拆成十几个「词」，
    ///   送去翻译会把整句结构打乱。
    ///
    /// 【规则】
    ///   只有**左右两边都是中文/中文标点**时才删这个空格。
    ///
    /// 【踩过的坑 · 试过又撤回的一条规则】
    ///   放大之后识别引擎还会把英文单词劈开（「b rown」「ove r」），
    ///   一度想用「两边都是小写字母就删空格」来修——
    ///   结果**把正常的单词间隔也吃掉了**，
    ///   变成「Thequickbrownfoxjumpsoverthelazydog」，错得更离谱。
    ///
    ///   教训：小写字母之间**也可能**是正常词边界，靠字符本身分不出来。
    ///   真正的解法是**换输入**而不是硬修输出：
    ///   放大对中文有帮助、对英文有伤害，那就分两次识别、按语种挑结果
    ///   （见 OcrTextCapture 里的「两遍识别」）。
    ///   **不要在文本层硬凑规则。**
    /// </summary>
    public static string FixSpuriousSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!text.Contains(' ')) return text;      // 没有空格就别白跑一趟

        var sb = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == ' ')
            {
                int left = sb.Length - 1;
                int right = i + 1 < text.Length ? i + 1 : -1;

                if (left >= 0 && right >= 0 &&
                    IsCjkOrCjkPunctuation(sb[left]) && IsCjkOrCjkPunctuation(text[right]))
                {
                    continue;   // 吞掉这个空格
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 汉字，或中文里常见的标点（含全角符号）。
    /// </summary>
    private static bool IsCjkOrCjkPunctuation(char c)
    {
        if (c >= 0x4E00 && c <= 0x9FFF) return true;    // 基本汉字
        if (c >= 0x3400 && c <= 0x4DBF) return true;    // 扩展 A
        if (c >= 0x3000 && c <= 0x303F) return true;    // 中文标点：，。！？、；：
        if (c >= 0xFF00 && c <= 0xFFEF) return true;    // 全角：％＃＆ 等
        return false;
    }

    /// <summary>取已拼好内容里的最后一行原文。</summary>
    private static string LastLine(StringBuilder sb)
    {
        int idx = sb.ToString().LastIndexOf('\n');
        return idx < 0 ? sb.ToString() : sb.ToString(idx + 1, sb.Length - idx - 1);
    }

    /// <summary>
    /// 判断两行是不是被 OCR 错误断开的同一句话。
    ///
    /// 只在**同时满足**下面两条时才合并，宁可漏合也不能错合：
    ///   上一行结尾没有句末标点（说明句子还没结束）；
    ///   下一行开头不是大写字母（被硬断的话首字母往往被识别成小写）。
    /// </summary>
    public static bool ShouldJoinLines(string previous, string current)
    {
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current)) return false;

        // 上一行已经以句号/问号/叹号/分号/冒号结尾 → 是完整句子，不合并
        char tail = previous[^1];
        if (tail is '.' or '?' or '!' or ';' or ':' or '。' or '？' or '！' or '；' or '：') return false;

        // 中文本来就逐字换行排版，一律不合并（合了会毁掉段落结构）
        if (IsCjk(tail) || IsCjk(current[0])) return false;

        // 下一行以大写字母开头 → 大概率是新句子
        if (char.IsUpper(current[0])) return false;

        return true;
    }

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;

    /// <summary>
    /// 数一数「有意义的字符」有多少：剔除空白与标点后的长度。
    /// 少于 2 个就当作没认出来（规格第 20 条）。
    /// </summary>
    public static int CountMeaningfulChars(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int n = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            n++;
        }
        return n;
    }

    /// <summary>
    /// 数一数有几行（非空行）。
    ///
    /// 【为什么需要】
    ///   接了第二个引擎（Tesseract）之后，它给的是整段文字、没有「几行」这个概念，
    ///   而界面上的小字会显示「N 行」。所以这里按换行数补一个。
    /// </summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// 判断识别结果是不是一堆乱码 / 纯符号。
    /// 判定标准：符号占比过高（> 60%）就当无效，避免把乱码送去翻译。
    /// </summary>
    /// <summary>
    /// 删掉「整行只有符号、一个字母数字都没有」的行。
    ///
    /// 【2026-10-10 材料 3 的 M2：三轮里两轮都多出一个孤零零的 `[`】
    ///   实际输出长这样：
    ///     `光学字符识别是指计算机识别图像中的文字⏎并转换为可编辑文本的过程。⏎[`
    ///   那个 `[` **自己占一整行**。
    ///
    ///   【为什么专门治它，而不靠别的办法】
    ///     这类「多出来的字符」正好是四十二节说过的那一类**盲区**：
    ///       · 置信度高（0.90，一眼看去很正常）
    ///       · 文本形态检查看不出（符号占比只有 3%）
    ///       · 双引擎一致也救不了
    ///     **所以只能从内容本身找规律。**
    ///
    ///   规律很硬：**任何真实的一行文字里都有字母或者数字。**
    ///   一行只剩 `[` `·` `—` `|` 这种，纯属图像噪声。
    ///
    ///   【为什么这样删是安全的】
    ///     · 真文字被删掉的概率极低（要整行都没有一个字母数字）；
    ///     · 万一真删光了，整个函数原样返回，不会把内容清空。
    /// </summary>
    public static string DropSymbolOnlyLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        foreach (string line in lines)
        {
            bool hasLetterOrDigit = false;
            foreach (char c in line)
            {
                if (char.IsLetterOrDigit(c)) { hasLetterOrDigit = true; break; }
            }
            if (hasLetterOrDigit) kept.Add(line);
        }

        // 全被删掉说明判断有问题，宁可原样返回，也不要把内容清空
        if (kept.Count == 0) return text;

        return string.Join("\n", kept).TrimEnd('\n');
    }

    /// <summary>
    /// 取文本里**第一段连续的汉字**（只取汉字，不含标点、空格、字母）。
    /// 没有汉字时返回空字符串。
    ///
    /// 【2026-10-10 材料 3 的 X1 就是靠它修的】
    ///   原文 `错误 404 (Not Found): файл не найден`（中英俄三语混排）
    ///   Windows 第一遍读成：`错误 404 (Not Found) / 巾 a H e H 烈 H`
    ///   **「错误」认对了**，俄文因为系统没装 ru-RU 语言包而乱码。
    ///
    ///   【为什么不能只看「Windows 有没有汉字」就补】
    ///     因为上面那个 `烈` **也是汉字**——它是俄文乱码的一部分。
    ///     只按「有没有汉字」来判断，会把 `烈` 当成真的中文补进去，更糟。
    ///
    ///   【所以必须两个引擎对得上才认】
    ///     chi_sim 在同一张图上也会读出 `错误`，
    ///     **两边第一段汉字一致**，才说明这段中文是真的。
    /// </summary>
    public static string FirstChineseRun(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        System.Text.StringBuilder run = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            if (c >= '一' && c <= '鿿')
            {
                run.Append(c);
            }
            else if (run.Length > 0)
            {
                break;      // 遇到第一个非汉字就收工，**只取第一段**
            }
        }
        return run.ToString();
    }

    /// <summary>
    /// 把 Tesseract 结果**开头那一段外文**替换成 Windows 认对的那段中文。
    ///
    /// 【2026-10-10 材料 3 的 X1】
    ///   ```
    ///   Windows   ：错误 404 (Not Found) / 巾 a H e H 烈 H
    ///   Tesseract ：Hi 404 (Not Found): файл не найден
    ///                    ↑ Tesseract 自己那份错的「Hi」
    ///   要得到  ：错误 404 (Not Found): файл не найден
    /// ```
    ///
    ///   **注意不是「往前加」**——加上会变成
    ///   `错误 Hi 404 ...`，**错的还在**。**必须把那一段换掉。**
    ///
    ///   【怎么找到该换掉哪一段：锚点对齐】
    ///     取 Windows 里**紧跟在那段中文后面的第一个词**当锚点（这里是 `404`），
    ///     再到 Tesseract 结果里找同一个锚点，**把锚点之前的全部内容换掉**。
    ///
    ///   【为什么这样安全】
    ///     · **锚点找不到就原样返回**，宁可不改也不改错；
    ///     · 锚点太短（少于 2 个字符）也不动——容易撞上；
    ///     · 只用**第一段**中文，所以俄文乱码里的「烈」永远轮不到。
    /// </summary>
    public static string SpliceLeadingChinese(string windowsText, string tessText)
    {
        if (string.IsNullOrEmpty(windowsText) || string.IsNullOrEmpty(tessText)) return tessText;

        // 找第一段汉字的结束位置
        int runEnd = -1;
        for (int i = 0; i < windowsText.Length; i++)
        {
            char c = windowsText[i];
            bool isHan = c >= '一' && c <= '鿿';
            if (!isHan) continue;
            int j = i;
            while (j < windowsText.Length && windowsText[j] >= '一' && windowsText[j] <= '鿿') j++;
            runEnd = j;
            break;
        }
        if (runEnd <= 0) return tessText;

        string chinese = windowsText.Substring(0, runEnd).Trim();
        if (chinese.Length == 0) return tessText;

        // 锚点：这段中文后面紧跟的第一个词
        int k = runEnd;
        while (k < windowsText.Length && char.IsWhiteSpace(windowsText[k])) k++;
        int anchorStart = k;
        while (k < windowsText.Length && !char.IsWhiteSpace(windowsText[k])) k++;
        string anchor = windowsText.Substring(anchorStart, k - anchorStart);

        // 锚点太短容易撞上，不动
        if (anchor.Length < 2) return tessText;

        int at = tessText.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0) return tessText;

        return chinese + " " + tessText.Substring(at);
    }

    /// <summary>
    /// 这段文字里有没有汉字。
    ///
    /// 【为什么需要它】
    ///   `RemoveSpaceBeforeChinesePunctuation`（删中文标点前的空格）曾经**误伤俄文**：
    ///   `Машинный перевод — это` 里的空格是**对的**，却因为破折号 `—` 被写进了
    ///   「中文标点」名单而���删掉——**把对的改成了错的**。
    ///
    ///   规则本身没错，错在**没看语种就动手**。
    ///   「中文标点前面的空格」这条规矩**只对中文成立**，
    ///   西文/俄文里 `—` 两侧本来就该有空格。
    ///
    /// 【为什么不在名单里一个一个排除 `—`】
    ///   因为名单以后还会加标点，**逐个排除迟早又会漏一个**。
    ///   **整段判语种是一道闸**，比在名单里挑拣可靠。
    /// </summary>
    private static bool ContainsChinese(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
        {
            if (c >= '一' && c <= '鿿') return true;
        }
        return false;
    }

    /// <summary>
    /// 删掉「中文标点前面的空格」。
    ///
    /// 【2026-10-10 材料 3 的 P2】
    ///   Windows 读出来是：
    ///     `服务已启动，端口 8080 。 / 数据库连接失败。 / 请检查配置文件。`
    ///                          ↑ 这里凭空多个空格
    ///
    ///   **中文的句号、逗号前面从来不会有空格**——
    ///   `端口 8080 。` 一定是识别过程插进去的，不是原文。
    ///
    ///   【为什么这个修法安全】
    ///     只处理**中文标点**（。，、；：？！（）【】《》「」）。
    ///     · 中文正文里这些标点前有空格，**一定是错的**；
    ///     · 英文正文里根本不会出现这些标点，**不受影响**；
    ///     · 已经是全对的那些段（M1/M2/M3/X2/X3）**本来就没有这种空格**，
    ///       删不掉东西。
    ///
    ///   【为什么不交给用户自己改】
    ///     用户验收标准是「一个词错，整段就错」，
    ///     **软件能确定的错就不该留给用户承担**。
    /// </summary>
    public static string RemoveSpaceBeforeChinesePunctuation(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        // 🔴【2026-10-10 材料 2 的 R2：这条规则误伤了俄文】
        //
        //   **识别本身是对的，是这条规则把它改错的**：
        //     Tesseract 认出来：`Машинный перевод — это раздел…`   ← 两边空格都在，完全正确
        //     本规则处理之后：`Машинный перевод— это раздел…`       ← 空格被删了
        //     日志原话：「删掉了中文标点前面的空格：… → …」
        //
        //   **根因**：`—`（em-dash 破折号）**根本不是中文标点**，
        //   但它被写进了下面的标点名单里。
        //
        //     · 中文里破折号写成 `——` **紧贴**，前面不该有空格 → 删掉是对的；
        //     · 西文/俄文里 `—` **两侧本来就该有空格**
        //       （`перевод — это`），删掉就是**把对的改成错的**。
        //
        //   【修法：整条规则只在「这段确实是中文」时才动手】
        //     这条规则从头到尾就是为中文加的，
        //     纯俄文/纯英文的段落**一条都不用碰**——
        //     **不是靠逐个标点排除，是整段判语种**，这样以后再往名单里加标点也不会再误伤。
        if (!ContainsChinese(text)) return text;

        const string marks = "。，、；：？！）】》」』〉…—～";
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (marks.IndexOf(c) >= 0)
            {
                // 前面有空格就跳过（只删空格，别的都留着）
                while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                {
                    sb.Length -= 1;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool LooksLikeGarbage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        int total = 0, symbol = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            total++;
            if (char.IsPunctuation(c) || char.IsSymbol(c) || !char.IsLetterOrDigit(c)) symbol++;
        }

        return total == 0 || (double)symbol / total > 0.6;
    }
}
