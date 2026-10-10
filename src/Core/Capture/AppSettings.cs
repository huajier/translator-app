using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 程序设置（目前只有认字引擎相关的一项）。
///
/// 【为什么不写死在代码里、也不做成设置窗口】
///   用户 2026-10-08 选了「Tesseract 留着但默认关掉」。
///   默认关掉 = 代码里的默认值必须是 false；
///   「想开的时候能开」 = 需要一个能改的地方。
///   现在只有一项，做设置窗口太重，用一个配置文件刚刚好。
///   阶段 5 做正式设置界面时，本类可以直接被那个界面复用。
///
/// 【为什么不放程序目录】
///   放程序目录的话：换台电脑复制过去，配置也跟着走，容易误开；
///   而且程序目录可能被设成只读。放用户目录最稳。
/// </summary>
internal static class AppSettings
{
    private static readonly object Gate = new();

    /// <summary>
    /// 「只用 Windows 自带引擎」——**排查/对照用的开关，默认 false**。
    ///
    /// 【为什么不再是 useTesseract（2026-10-09 改）】
    ///   原来这里叫 useTesseract、默认 false，语义是
    ///   「Tesseract 是默认关掉的备用引擎」。
    ///   2026-10-09 用户说明真实用途是翻**英文和俄文**，
    ///   重跑实验后数据完全反过来：
    ///     引擎                      英文平均分   俄文
    ///     Tess-fast（原样）           94.7       100 分 ✅
    ///     Windows 英文引擎            63.3        装不上 ❌
    ///     Windows 中文引擎（曾上线）   58.9        装不上 ❌
    ///   Tesseract 从「备用」变成了**英俄文的主力**。
    ///   如果开关还叫 useTesseract 且默认 false，英文就会一直掉回 58 分。
    ///   所以语义翻转成它的反面：**默认走新分工，只有显式打开才退回老办法。**
    ///
    ///   只有需要「跟旧版本对比」或者「Tesseract 出问题想临时躲开」时才用得上。
    /// </summary>
    public static bool WindowsOnly { get; private set; } = false;

    private static bool _loaded;

    /// <summary>配置文件位置（放用户目录，不放程序目录）。</summary>
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TranslatorApp",
        "settings.json");

    /// <summary>读一次就够，读坏了也只影响这一个开关。</summary>
    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(ConfigPath))
                {
                    AppLogger.Info($"还没设置文件，用默认分工（中文走 Windows 自带，英文/俄文走 Tesseract）。{ConfigPath}");
                    return;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ConfigPath));

                if (doc.RootElement.TryGetProperty("windowsOnly", out var v)
                    && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    WindowsOnly = v.GetBoolean();
                    AppLogger.Info($"已读取设置：只用 Windows 自带 = {(WindowsOnly ? "是" : "否")}（来自 {ConfigPath}）");
                    return;
                }

                AppLogger.Info($"设置文件里没有 windowsOnly 这一项，按默认（新分工）处理：{ConfigPath}");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"读设置失败（只影响 Tesseract 开关，认字照常）：{ex.GetType().Name} {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 写设置。用户想开/关时调它。
    /// </summary>
    public static void Save(bool windowsOnly)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new { windowsOnly },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            WindowsOnly = windowsOnly;
            AppLogger.Info($"设置已保存：只用 Windows 自带 = {(windowsOnly ? "是" : "否")} → {ConfigPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"保存设置失败：{ex.GetType().Name} {ex.Message}");
        }
    }
}