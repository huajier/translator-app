using System.IO;
using System.Text;
using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Logging;

/// <summary>
/// SelectionCoordLog —— 把每次框选的坐标写进 tools\日志\框选坐标.log（阶段 2）
///
/// 这个类干嘛的：
///   阶段 2 拿到的只是一个「框在哪」的数字，肉眼没法直接验证对不对。
///   所以每框一次就往日志里写一行，方便人拿眼睛核对：
///   「我在屏幕上框的那一块，日志里的坐标应该就是那个位置」。
///
/// 写文件的方式（很重要）：
///   - 只在**主线程**、**框选结束之后**写，绝不在鼠标钩子回调里写
///     （钩子回调里写文件会严重卡顿，规格明令禁止）；
///   - 追加写、UTF-8 带 BOM（和 AppLogger 一致，记事本直接能打开）。
///
/// 它和谁交互：SelectionStateMachine 在框选成功时调 Record。
/// </summary>
internal static class SelectionCoordLog
{
    /// <summary>日志文件名（固定，按规格要求）。</summary>
    public const string FileName = "框选坐标.log";

    private static readonly object WriteLock = new();

    /// <summary>写一次框选记录。坐标相关的字段全部写全，方便逐字段核对。</summary>
    public static void Record(SelectionCoordInfo info)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(info.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
        sb.Append($"触发={info.TriggerMode} ");
        sb.Append($"物理像素=({info.PhysicalLeft},{info.PhysicalTop})-({info.PhysicalRight},{info.PhysicalBottom}) ");
        sb.Append($"尺寸={info.PhysicalWidth}×{info.PhysicalHeight} ");
        sb.Append($"DIP=({info.DipLeft:0.##},{info.DipTop:0.##}) {info.DipWidth:0.##}×{info.DipHeight:0.##} ");
        sb.Append($"显示器=({info.MonitorLeft},{info.MonitorTop})-({info.MonitorRight},{info.MonitorBottom}) ");
        sb.Append($"DPI={info.Dpi}(缩放 {info.Dpi / 96.0 * 100:0}%) ");
        sb.Append($"用时={info.DurationMs}ms");
        sb.AppendLine();

        AppendLine(sb.ToString());
    }

    /// <summary>写一行纯文本（状态机取消框选时也会记一笔，方便排查「为什么没框到」）。</summary>
    public static void RecordNote(string note)
        => AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {note}");

    private static void AppendLine(string line)
    {
        try
        {
            string? dir = ResolveLogDir();
            if (dir == null)
            {
                AppLogger.Warn("找不到 tools\\日志 目录，框选坐标没能写入（不影响框选功能本身）");
                return;
            }

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, FileName);

            lock (WriteLock)
            {
                File.AppendAllText(path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
        }
        catch (Exception ex)
        {
            // 写日志失败绝不能让框选功能挂掉，只记一条警告
            AppLogger.Warn($"写框选坐标日志失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 定位 tools\日志 目录：从 exe 往上最多找 8 层，看到 tools 目录就取它下面的「日志」子目录。
    /// 【踩过的坑】之前这里返回的是 tools 本身，结果文件写到了 tools\框选坐标.log，
    /// 而规格要求是 tools\日志\框选坐标.log —— 和 AppLogger 的日志分了两个地方，很难找。
    /// </summary>
    private static string? ResolveLogDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string logDir = Path.Combine(dir.FullName, "tools", "日志");
            if (Directory.Exists(logDir)) return logDir;

            // tools 在、但「日志」子目录还没有（首次运行必然如此）：建一个再用
            string toolsDir = Path.Combine(dir.FullName, "tools");
            if (Directory.Exists(toolsDir))
            {
                Directory.CreateDirectory(logDir);
                return logDir;
            }

            dir = dir.Parent;
        }

        // 实在找不到就退到临时目录，宁可位置不理想也不能让框选功能因为写不了日志而失败
        string fallback = Path.Combine(Path.GetTempPath(), "TranslatorApp");
        return Directory.Exists(fallback) ? fallback : null;
    }
}

/// <summary>写一行框选坐标日志要用的字段集合（避免到处写一堆参数）。</summary>
internal readonly record struct SelectionCoordInfo
{
    public required DateTime Timestamp { get; init; }
    public required string TriggerMode { get; init; }
    public required int PhysicalLeft { get; init; }
    public required int PhysicalTop { get; init; }
    public required int PhysicalRight { get; init; }
    public required int PhysicalBottom { get; init; }
    public required int PhysicalWidth { get; init; }
    public required int PhysicalHeight { get; init; }
    public required double DipLeft { get; init; }
    public required double DipTop { get; init; }
    public required double DipWidth { get; init; }
    public required double DipHeight { get; init; }
    public required int MonitorLeft { get; init; }
    public required int MonitorTop { get; init; }
    public required int MonitorRight { get; init; }
    public required int MonitorBottom { get; init; }
    public required uint Dpi { get; init; }
    public required long DurationMs { get; init; }
}