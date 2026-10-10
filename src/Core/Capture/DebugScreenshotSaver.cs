using System;
using System.Diagnostics;
using System.IO;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;
using RECT = TranslatorApp.Core.Native.NativeMethods.RECT;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 调试截图：按 `Ctrl+Alt+D` 把刚才框选区域的截图存到磁盘，方便排查「为什么没认出来」。
///
/// 【为什么需要它】
///   「没识别到文字」这类问题光看日志是看不出来的——
///   得看到当时到底截到了什么画面（是不是框歪了、是不是全黑、有没有反光）。
///
/// 【默认是关的】
///   存截图等于把屏幕内容写进磁盘，**没开就不能写**。
///   开启方式见 <see cref="Enabled"/>。
/// </summary>
internal static class DebugScreenshotSaver
{
    /// <summary>是否开启。默认 false——不主动开启就绝不落盘。</summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// 用户有没有**主动按过** `Ctrl+Alt+D`。
    ///
    /// 【为什么单独一个开关】
    ///   开启调试截图 ≠ 允许程序自己往磁盘写图。
    ///   用户没按过快捷键，说明他没要看图，那就一个字都不该落盘。
    ///   按过一次之后才算「他想看」，这时把框选当场生成的中间图也存下来。
    /// </summary>
    public static bool Requested { get; set; }

    private static RECT? _lastRect;

    /// <summary>记住最近一次框选的区域，供 Ctrl+Alt+D 使用。</summary>
    public static void RememberRegion(RECT rect) => _lastRect = rect;

    /// <summary>
    /// 保存最近一次框选区域的截图。
    /// **返回存下来的文件路径**；没开启 / 没框选过 / 截不到时返回 null。
    ///
    /// 【为什么要返回路径：2026-10-10 用户反馈】
    ///   「按了没有任何反馈，也不知道截图成没成功」——
    ///   所以调用方要拿这个路径**在屏幕上告诉用户**，
    ///   写日志是给开发看的，**用户不会去翻日志**。
    /// </summary>
    public static string? SaveLastRegion()
    {
        if (!Enabled)
        {
            AppLogger.Info("调试截图未开启，已跳过（要开启请设置 DebugScreenshotSaver.Enabled = true）");
            return null;
        }

        if (_lastRect is null)
        {
            AppLogger.Warn("调试截图：还没有框选过，没有可保存的区域");
            return null;
        }

        string dir = Path.Combine(AppContext.BaseDirectory, "debug-screenshots");
        Directory.CreateDirectory(dir);

        string file = Path.Combine(dir, $"sel_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        using var bitmap = ScreenCaptureService.TryCapture(_lastRect.Value);
        if (bitmap is null)
        {
            AppLogger.Warn("调试截图失败：截不到那块区域");
            return null;
        }

        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        AppLogger.Info($"调试截图已保存：{file}");
        return file;
    }

    /// <summary>
    /// 存一张「排查用的图」。**只在判为深底时才会被调用**，
    /// 所以平时框选不会产生任何文件。
    ///
    /// 【为什么不用快捷键来触发】
    ///   依赖用户「先按快捷键、再框选」这个顺序太脆了——
    ///   顺序反了就永远存不到。已经因此白查了两轮。
    /// </summary>
    public static void SaveImage(Bitmap bitmap, string suffix)
    {
        if (bitmap is null) return;

        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "debug-screenshots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"sel_{DateTime.Now:yyyyMMdd_HHmmss_fff}{suffix}.png");
            bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            AppLogger.Info($"调试截图已保存（{suffix}）：{file}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"调试截图（{suffix}）失败：{ex.Message}");
        }
    }

    /// <summary>把开关状态写进日志，便于事后追溯「当时到底开没开」。</summary>
    public static void LogState()
        => AppLogger.Info($"调试截图：{(Enabled ? "已开启" : "未开启")}");
}
