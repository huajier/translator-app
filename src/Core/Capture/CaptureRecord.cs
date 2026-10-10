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
    public static void Write(string text, string channel, string language, string detail, int elapsedMs)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"取词记录-{DateTime.Now:yyyyMMdd}.txt");

            var sb = new StringBuilder();
            sb.AppendLine($"时间: {DateTime.Now:HH:mm:ss}");
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