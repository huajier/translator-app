using System;
using System.IO;
using System.Text;

namespace TranslatorApp.Core.Logging;

/// <summary>
/// AppLogger.cs —— 全项目唯一的日志出口（阶段 1 版：写文件 + 级别 + 按天分文件）
///
/// 这个文件干嘛的：
///   把「出事了」写进 翻译软件\tools\日志\app-yyyyMMdd.log。
///   阶段 1 只要求三件事：能写、按天分文件、写日志失败绝不允许搞崩主程序。
///
/// 它和谁交互：
///   App.xaml.cs 与 TrayIcon.cs 的所有 catch 分支都调它；不依赖任何第三方库。
///
/// 要改它该动哪里：
///   - 日志目录推断规则 → ResolveLogDir()；
///   - 文件名格式       → ResolveLogFile()；
///   - 以后加级别过滤（阶段 8）→ 在 Write() 里加开关。
/// </summary>
public static class AppLogger
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static string? _logFile;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(Exception ex, string message) => Write("ERROR", $"{message} | {ex}");

    /// <summary>当前日志文件的完整路径（写失败时也能拿到，便于排查）。</summary>
    public static string LogFilePath => ResolveLogFile();

    private static string ResolveLogFile()
    {
        if (_logFile != null) return _logFile;
        _logFile = Path.Combine(ResolveLogDir(), $"app-{DateTime.Now:yyyyMMdd}.log");
        return _logFile;
    }

    /// <summary>
    /// 日志目录 = 从 exe 所在目录往上找「tools」目录，取它下面的「日志」子目录。
    /// 发布后 exe 在 release\v0.1\，往上两级正好是项目根，那里有 tools\日志。
    /// 实在找不到就退到 %TEMP%\TranslatorApp（装在只读目录里也绝不崩）。
    /// </summary>
    private static string ResolveLogDir()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (string.Equals(dir.Name, "tools", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(dir.FullName, "日志");

                var candidate = Path.Combine(dir.FullName, "tools");
                if (Directory.Exists(candidate))
                    return Path.Combine(candidate, "日志");
            }
        }
        catch { }

        return Path.Combine(Path.GetTempPath(), "TranslatorApp");
    }

    private static void Write(string level, string message)
    {
        try
        {
            var file = ResolveLogFile();
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                // 带 BOM 的 UTF-8：记事本、Windows PowerShell 5.1 都能正确显示中文，不会变乱码。
                // BOM 只在文件第一次创建时写入，后续追加不会再插。
                File.AppendAllText(file, line, Utf8WithBom);
            }
        }
        catch
        {
            // 写日志失败绝不能影响主程序（阶段 1 异常清单明确要求）
        }
    }
}
