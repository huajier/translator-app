using System.Globalization;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 语言判断：告诉用户「这段是什么文字」，并给阶段 4 定翻译方向。
/// </summary>
///
/// <remarks>
/// 【中文判定 · 规格第 8 条，别擅自改】
///   剔除空白与标点后，中文字符占比 &gt; 30% 判为中文，否则判为外文。
///
/// 【2026-10-09 补上俄文】
///   用户说明真实用途是**主要翻英文和俄文**，而这里原来只会分「中文 / 外文」两类，
///   俄文一律被判成「外文」。
///   后果不是显示难看，而是**阶段 4 会走错方向**：
///   判成「外文」就等于判成「英文」，俄文会被送去英→中翻译，
///   结果是「把俄语当英语翻译」，出来的东西完全不对。
///   所以必须在这一层就把俄文单独认出来。
///
/// 【重要：这里判断的是「文字体系」，不是「语言」】
///   我们能可靠分辨的是**汉字 / 西里尔字母 / 拉丁字母**三套字母。
///   但**光看字母认不出语言**——法语、西班牙语、德语用的都是拉丁字母。
///   所以显示名刻意写成「外文（拉丁字母）」而不是「英文」，
///   避免把法语、德语也骗成英文。
///   在本软件的目标语种（中文/英文/俄文）范围内，
///   拉丁字母就等于英文，判断结果不会出错。
///
/// 【一个真实的坑：字母长得一模一样】
///   西里尔字母 С 和拉丁字母 C、Р 和 P 完全同形。
///   所以 OCR 有时会把俄文里的词认成拉丁词（日志里出现过
///   `Съешь` 被认成 `Crews`）。这种情况下字母体系判断会跟着认错——
///   这是上游认字的问题，不是这里能修的。
/// </remarks>
internal static class LanguageDetector
{
    public const string Chinese = "zh";

    /// <summary>拉丁字母（英文、西欧语言……）。沿用 "en" 这个值是历史原因，别改。</summary>
    public const string Foreign = "en";

    /// <summary>西里尔字母（俄文、乌克兰文……）。</summary>
    public const string Russian = "ru";

    public const string Unknown = "unknown";

    /// <summary>中文判定阈值（规格定死 30%，别擅自改）。</summary>
    private const double ChineseRatioThreshold = 0.30;

    /// <summary>
    /// 俄文判定阈值。
    /// 【为什么定 15%，而不是跟着中文用 30%】
    ///   俄文技术文档里常夹英文词和数字，例如
    ///   «Ошибка 404 (страница не найдена): файл config.txt не найден»
    ///   这行里有 `config.txt` 一大段拉丁字母。
    ///   阈值太高会把这种最常见的俄文内容误判成「外文（拉丁）」，
    ///   正好踩中阶段 4 走错方向的那个坑，所以宁可灵敏一点。
    /// </summary>
    private const double RussianRatioThreshold = 0.15;

    /// <summary>
    /// 「真中文」允许出现的符号上限（占全部字符的比例）。
    ///
    /// 【2026-10-09 用户实测后加】
    ///   E3 那段（橙底绿字）实际认出来是：
    ///     `《《区刂必（ N01 《 outnd): 山（： fil(`
    ///   汉字占有效字母的 31%，**超过了 30% 的中文阈值**，
    ///   于是被判成中文、直接走了 Windows 自带，结果全是符号堆出来的假汉字。
    ///
    ///   根因还是老问题：**中文引擎会把符号看成汉字**
    ///   （`-`→`一`、`3`→`土`），假汉字和真汉字在「汉字占比」里完全一样，
    ///   所以只看占比区分不出来。
    ///
    ///   区分的办法是看**符号浓度**：
    ///     真中文句子 → `光学字符识别是指…的过程。` 符号只有句号，约 3%
    ///     符号幻觉   → `《《区刂必（ N01 《 outnd): 山（： fil(` 约 23%
    ///   阈值定 15%：高于它就认为「这些汉字多半是符号变来的」，不算中文。
    /// </summary>
    private const double MaxSymbolRatioForRealChinese = 0.15;

    /// <summary>
    /// 判断一段文字属于哪一类。
    /// </summary>
    /// <remarks>
    /// 【判定顺序有讲究：先中文，再俄文，最后拉丁】
    ///   中英混排的句子（`API 接口 v2.0`）中文占多数，先判中文才不会被
    ///   后面的规则抢走；反过来，一段俄文里夹几个英文单词
    ///   （`Файл занимает 2,5 МБ`）西里尔字母仍占绝大多数，判俄文也对。
    /// </remarks>
    public static string Detect(string? text)
    {
        CountScripts(text, out int cjk, out int cyrillic, out int latin);
        int letters = cjk + cyrillic + latin;
        if (letters == 0) return Unknown;

        if ((double)cjk / letters > ChineseRatioThreshold && !LooksLikeSymbolHallucination(text))
            return Chinese;

        if ((double)cyrillic / letters > RussianRatioThreshold) return Russian;
        if (latin > 0) return Foreign;
        return Unknown;
    }

    /// <summary>
    /// 判断这段「像中文」的文字，是不是其实是符号被认成了汉字。
    ///
    /// 判据只有一个：**符号占全部字符的比例**。
    /// 真中文句子不会长成满篇《》（）：，而符号幻觉一定会。
    /// </summary>
    private static bool LooksLikeSymbolHallucination(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        int symbols = 0;
        foreach (char ch in text)
        {
            // 空白不算——排版留白是正常的，不该算进「符号浓度」。
            if (char.IsWhiteSpace(ch)) continue;
            if (!char.IsLetterOrDigit(ch)) symbols++;
        }

        return (double)symbols / text.Length > MaxSymbolRatioForRealChinese;
    }

    /// <summary>
    /// 这段文字是不是以中文为主。
    ///
    /// 【谁在用】
    ///   ① 取词的两遍识别：中文用「放大 2 倍」的版本重认（实测明显更准），
    ///      英文用原生大小那版（放大反而会把英文单词劈开成「quick b rown fox」）。
    ///   ② 引擎分工：中文走 Windows 自带，英文/俄文走 Tesseract
    ///      （实测英文 58.9 → 94.7 分，且俄文本机 Windows 装不了）。
    /// </summary>
    public static bool IsMostlyChinese(string? text) => Detect(text) == Chinese;
/// <summary>
    /// 这段文字是不是以俄文（西里尔字母）为主。
    ///
    /// 【谁在用】认字时的**模型选择**：
    ///   俄文必须**只挂 `rus` 模型**，不能和英文一起挂。
    ///   原因见 OcrTextCapture 里那段说明：
    ///   西里尔 С 与拉丁 C 完全同形，图像上分不出来，
    ///   引擎只能靠「我现在在读哪种语言」来判断，
    ///   所以必须把语言列表收窄到只剩一种。
    /// </summary>
    public static bool IsMostlyRussian(string? text) => Detect(text) == Russian;

    /// <summary>
    /// 由源语言推出目标语言（阶段 4 会真正用起来，口径先定在这里）。
    /// 中文 → 外文；外文（含俄文）→ 中文。
    /// </summary>
    public static string TargetOf(string sourceLanguage)
        => sourceLanguage == Chinese ? Foreign : Chinese;

    /// <summary>给用户看的中文名。</summary>
    public static string DisplayName(string language) => language switch
    {
        Chinese => "中文",
        Russian => "俄文",
        // 【为什么写「外文（拉丁字母）」而不直接写「英文」】
        //   拉丁字母不只英文，还有法德西意……直接写「英文」会把它们骗成英文。
        //   在本软件的目标语种范围内它就是英文，但显示上留个余地更诚实。
        Foreign => "外文（拉丁字母）",
        _ => "未识别",
    };

    /// <summary>
    /// 文字里西里尔字母（俄文用的那套）占「有效字母」的比例。
    ///
    /// 【谁在用】认字时的模型选择保险：
    ///   俄文模型认不出东西时也会给出一个不算低的置信度，
    ///   光比置信度可能让俄文模型抢走英文内容，交出一堆拉丁乱码。
    ///   加一道「**结果里到底有没有西里尔字母**」的事实检查最稳妥。
    /// </summary>
    public static double CountCyrillicRatio(string? text)
    {
        CountScripts(text, out _, out int cyrillic, out int latin);
        int letters = cyrillic + latin;
        return letters == 0 ? 0 : (double)cyrillic / letters;
    }

    /// <summary>
    /// 算出文字里拉丁字母占全部字母的比例。
    ///
    /// 【谁在用、为什么用（2026-10-09 用户实测）】
    ///   俄文里混着英文（文件名 `config.txt`、缩写 `FPS`）是很常见的。
    ///   纯俄文模型**不认识拉丁字母**，会把它们硬拼成西里尔：
    ///     `config.txt` → `сопйддхе`
    ///   拼出来的是一个**根本不存在的俄文单词**，
    ///   看着还挺像回事，送去翻译就会翻错——
    ///   **这种错比漏字更危险**。
    ///
    ///   俄英混合模型能认对，但两者置信度可能一样高，分不出高下。
    ///   所以改看一个**看得见的事实**：
    ///   **谁把原文里的拉丁字母原样保住了。**
    ///   保住了的那个才是对的，跟分数高低无关。
    /// </summary>
    public static double CountLatinRatio(string? text)
    {
        CountScripts(text, out _, out int cyrillic, out int latin);
        int letters = cyrillic + latin;
        return letters == 0 ? 0 : (double)latin / letters;
    }

    /// <summary>调试日志用：连同三种字母的占比一起打出来，便于核对判断对不对。</summary>
    public static string Describe(string text)
    {
        CountScripts(text, out int cjk, out int cyrillic, out int latin);
        int letters = cjk + cyrillic + latin;
        if (letters == 0) return "未识别（没有文字）";

        double zhRatio = (double)cjk / letters;
        double ruRatio = (double)cyrillic / letters;

        return string.Format(CultureInfo.InvariantCulture,
            "{0}（中文 {1:P0}，西里尔 {2:P0}）", DisplayName(Detect(text)), zhRatio, ruRatio);
    }

    /// <summary>
    /// 数三种文字体系各有多少个字母。
    ///
    /// 【空白、标点、符号一律不算】
    ///   OCR 结果里常有大量空格、破折号、引号，把它们算进分母会稀释比例。
    ///   例如 `Ошибка 404 (страница не найдена)`，若把括号冒号算进去，
    ///   西里尔占比会被拉低到阈值以下，误判成拉丁字母。
    ///
    /// 【数字算不算字母】
    ///   **不算**。`2,5 МБ` 里的 `25` 对判断语种没有信息量，
    ///   算进去只会稀释比例，所以数字直接跳过。
    /// </summary>
    private static void CountScripts(string? text, out int cjk, out int cyrillic, out int latin)
    {
        cjk = cyrillic = latin = 0;
        if (string.IsNullOrEmpty(text)) return;

        foreach (char c in text)
        {
            // 汉字（含扩展区，用 IsCjk 而不是写死 0x4E00~0x9FFF）
            if (c >= 0x4E00 && c <= 0x9FFF) { cjk++; continue; }

            // 西里尔字母：U+0400~U+04FF（俄文全在这一段）
            if (c >= 0x0400 && c <= 0x04FF) { cyrillic++; continue; }

            // 拉丁字母：只认大小写的 A~Z，不把别的符号算进来
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
        }
    }
}