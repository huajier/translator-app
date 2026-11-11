using System.Globalization;
using System.IO;
using System.Text;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 取词验收记录：把**每一次取词**的结果单独存一份，供事后逐字核对。
///
/// 【为什么要有这个文件（2026-10-09 用户提的）】
///   用户原话：「你这样自己测太慢了，而且还容易出错……
///   你把需要测的步骤给我列出来，然后我来操作，
///   提取出文字了软件里应该有记录的，你读取记录比对文本，这样就能分析出来了。」
///
///   这条意见是对的，而且指出了一个我一直没意识到的问题：
///   我用脚本模拟鼠标点击，**只能测我「想」让用户做的事**，
///   测不出用户真实操作里的意外情况；而且模拟坐标经常偏，
///   框到的是别的地方，浪费了大量时间在「测的是不是同一块」上。
///   让用户自己框，我这边只负责**读记录**，职责就分清了：
///   用户负责「框到哪」，我负责「结果对不对」。
///
/// 【和普通日志的区别】
///   `tools\日志\app-*.log` 里的「最终原文」是**截断到 300 字**的，
///   而且格式给人看的，不能直接拿来做逐字比对。
///   这个文件是**给机器比对用的**：原文一字不截，字段固定分隔。
///
/// 【文件长什么样】
///   每次取词追加一段，段与段之间用 `##### 记录结束 #####` 隔开。
///   原文里的换行会先换成 `⏎`（一个不会出现在正文里的符号），
///   这样「一条记录」永远占一整行，比对时按行拆就行，不会被换行搞乱。
/// </summary>
internal static class CaptureRecord
{
    /// <summary>记录文件放在 exe 旁边的 logs 目录，跟着产品走，方便用户直接找到。</summary>
    private static readonly object Gate = new();

    /// <summary>换行的替身。挑它是因为正常文字里几乎不会出现，
    /// 就算原文真出现了，替换前也会先被记下来，不至于把记录搞断行。</summary>
    private const string NewlineMark = "⏎";

    /// <summary>
    /// 追加一条取词记录。
    /// </summary>
    /// <param name="text">识别出来的原文（会一字不截地记下来）</param>
    /// <param name="sourceProcess">框选那一刻前台窗口的进程名（如 chrome.exe）；取不到时是「取不到」</param>
    public static void Write(string text, string channel, string language, string detail, int elapsedMs,
                             string sourceProcess)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"取词记录-{DateTime.Now:yyyyMMdd}.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"时间: {DateTime.Now:HH:mm:ss}");
            sb.AppendLine($"窗口: {(string.IsNullOrWhiteSpace(sourceProcess) ? "（未知）" : sourceProcess)}");
            sb.AppendLine($"通道: {channel}");
            sb.AppendLine($"语言: {language}");
            sb.AppendLine($"耗时: {elapsedMs} 毫秒");
            sb.AppendLine($"说明: {detail}");
            sb.AppendLine($"字数: {text.Length}");
            sb.Append("原文: ").AppendLine(Flatten(text));
            sb.AppendLine("##### 记录结束 #####");

            lock (Gate)
            {
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            // 记录失败绝不能影响取词本身——用户拿到文字才是正事
            AppLogger.Warn($"写取词记录失败（不影响取词）：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>把换行和回车换成看得见的记号，保证一条记录只占连续几行。</summary>
    private static string Flatten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "（空）";
        return text.Replace("\r\n", NewlineMark)
                    .Replace("\n", NewlineMark)
                    .Replace("\r", NewlineMark)
                    .Trim();
    }

    /// <summary>给用户看的存档路径（启动日志里会打出来，方便他找到）。</summary>
    public static string CurrentFilePath
    {
        get
        {
            try
            {
                return Path.Combine(AppContext.BaseDirectory, "logs",
                                    $"取词记录-{DateTime.Now:yyyyMMdd}.txt");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"取不到取词记录路径：{ex.GetType().Name} {ex.Message}");
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 【2026-11-10 用户需求】一条取词记录的精简版，供主界面「识别记录」窗口显示。
    ///
    /// 只留界面要用的几项；窗口/耗时/说明不进结构体，避免为了显示再解析一遍。
    /// 【2026-11-11 加 Translation】用户要求记录里同时有译文。
    ///   **老记录没有这一行，必须是可空** —— 缺失时为 null，界面按「没有译文」处理。
    /// </summary>
    public readonly record struct CaptureRecordEntry(
        string Time, string Channel, string Language, int CharCount, string Text,
        string? Translation);

    /// <summary>
    /// 【2026-11-10 用户需求】读最近 <paramref name="max"/> 条取词记录，**最新的在前**。
    ///
    /// 【用户原话】「我现在主页面下面加个识别日志，让用户识别出来后不小心点掉了
    ///   还能在识别日志里面找出来。」
    ///
    /// 【只读当前 exe 旁边的 logs】换版本会换发布目录，所以绝不跨目录捞历史 ——
    ///   那样会读到别的版本留下的记录，时间对不上还容易误导。
    ///
    /// 【为什么点开才读】主窗口启动时读文件会拖慢启动，而这个功能是低频的，
    ///   所以只在用户点按钮时调一次（调用方在 MainWindow）。
    ///
    /// 【绝不让界面崩】任何异常都吞掉 → 打一条 Warn → 返回空列表，
    ///   界面上就是「还没有识别记录。」，不影响其它功能。
    /// </summary>
    public static List<CaptureRecordEntry> ReadRecent(int max)
    {
        var result = new List<CaptureRecordEntry>();
        if (max <= 0) return result;

        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            if (!Directory.Exists(dir)) return result;

            // 文件名倒序 = 日期新的先读；够 max 条就停（不在旧文件上白花时间）
            var files = Directory.GetFiles(dir, "取词记录-*.txt");
            Array.Sort(files, (a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));

            foreach (string f in files)
            {
                if (result.Count >= max) break;

                var lines = File.ReadAllLines(f, Encoding.UTF8);
                // 从文件末尾往前扫，后写的记录更新
                var current = new List<string>();
                var chunks = new List<List<string>>();
                foreach (string line in lines)
                {
                    if (line.StartsWith("##### 记录结束", StringComparison.Ordinal))
                    {
                        if (current.Count > 0) chunks.Add(current);
                        current = new List<string>();
                    }
                    else
                    {
                        current.Add(line);
                    }
                }
                if (current.Count > 0) chunks.Add(current);

                for (int i = chunks.Count - 1; i >= 0 && result.Count < max; i--)
                {
                    CaptureRecordEntry? entry = ParseChunk(chunks[i]);
                    if (entry is not null) result.Add(entry.Value);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"读取取词记录失败（不影响取词）：{ex.GetType().Name} {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 把一段记录行解析成 <see cref="CaptureRecordEntry"/>。
    /// 失败记录（「结果: 失败」那种）没有通道/原文，返回 null 直接跳过 ——
    /// 界面上只显示真正的识别结果。
    /// </summary>
    private static CaptureRecordEntry? ParseChunk(List<string> lines)
    {
        string time = string.Empty, channel = string.Empty, language = string.Empty, text = string.Empty;
        string? translation = null;
        int charCount = -1;
        bool isFailure = false;

        foreach (string raw in lines)
        {
            int sep = raw.IndexOf(':');
            if (sep <= 0) continue;
            string key = raw.Substring(0, sep).Trim();
            string val = raw.Substring(sep + 1).Trim();

            switch (key)
            {
                case "时间": time = val; break;
                case "通道": channel = val; break;
                case "语言": language = val; break;
                case "结果": isFailure = true; break;
                case "字数":
                    int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out charCount);
                    break;
                case "原文":
                    text = val.Replace(NewlineMark, "\n");
                    break;
                // 【2026-11-11】译文是新加的，老记录没有这一行 —— 缺失就保持 null
                case "译文":
                    translation = val.Replace(NewlineMark, "\n");
                    break;
            }
        }

        if (isFailure) return null;
        if (string.IsNullOrWhiteSpace(channel) && string.IsNullOrWhiteSpace(text)) return null;
        if (charCount < 0) charCount = text.Length;
        // Flatten 把空原文写成「（空）」，还原成空串更诚实
        if (text == "（空）") text = string.Empty;

        return new CaptureRecordEntry(time, channel, language, charCount, text, translation);
    }

    /// <summary>
    /// 【2026-11-11 用户需求】给**最后一条**取词记录补上译文。
    ///
    /// 【为什么不能在 Write 里写】Write 是在识别完成时调的，那时翻译还没发生，
    ///   根本拿不到译文。所以要等翻译结束再回头补一次。
    ///
    /// 【只追加，不改老格式】
    ///   在目标记录的「##### 记录结束 #####」**之前**插一行「译文: …」。
    ///   老记录本来就没有这一行，解析端（ParseChunk）容忍它缺失（Translation = null）。
    ///
    /// 【怎么认准那一条】从文件末尾往前找，最后一条「原文:」等于 Flatten(capturedText)
    ///   的记录段。同一段文字可能被框多次，取最后一条正好是最新的那次。
    ///   找不到就静默返回 —— 这不是错误（比如旧文件/被清理过）。
    ///
    /// 【绝不影响取词】任何异常全吞 + 一条 Warn。
    /// </summary>
    /// <param name="capturedText">识别出来的原文（和传给 Write 的是同一份）</param>
    /// <param name="translatedText">译文；成功时用</param>
    /// <param name="success">翻译成功与否；失败时写「译文: （翻译失败）」</param>
    public static void AttachTranslation(string capturedText, string translatedText, bool success)
    {
        if (string.IsNullOrEmpty(capturedText)) return;

        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            string path = Path.Combine(dir, $"取词记录-{DateTime.Now:yyyyMMdd}.txt");
            if (!File.Exists(path)) return;

            string wantOriginal = "原文: " + Flatten(capturedText);
            var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();

            // 从后往前找最后一条匹配的原文行
            int originalLine = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (string.Equals(lines[i], wantOriginal, StringComparison.Ordinal))
                {
                    originalLine = i;
                    break;
                }
            }
            if (originalLine < 0) return;   // 找不到就什么都不做

            // 从原文行往后找本段的「##### 记录结束 #####」
            int endLine = -1;
            for (int i = originalLine + 1; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("##### 记录结束", StringComparison.Ordinal))
                {
                    endLine = i;
                    break;
                }
            }
            if (endLine < 0) return;

            // 已经有译文就不重复插（长文本分段翻译会回调多次，这里兜一层）
            for (int i = originalLine + 1; i < endLine; i++)
            {
                if (lines[i].StartsWith("译文:", StringComparison.Ordinal)) return;
            }

            string textToWrite = success
                ? "译文: " + Flatten(translatedText)
                : "译文: （翻译失败）";

            lock (Gate)
            {
                lines.Insert(endLine, textToWrite);
                File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine,
                                  new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"补写译文失败（不影响取词）：{ex.GetType().Name} {ex.Message}");
        }
    }


    /// <summary>记一个「这次没成功」，方便统计失败率。</summary>
    public static void WriteFailure(string reason)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"取词记录-{DateTime.Now:yyyyMMdd}.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"时间: {DateTime.Now:HH:mm:ss}");
            sb.AppendLine("结果: 失败");
            sb.AppendLine($"原因: {reason}");
            sb.AppendLine("##### 记录结束 #####");

            lock (Gate)
            {
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"写取词记录失败（不影响取词）：{ex.GetType().Name} {ex.Message}");
        }
    }
}