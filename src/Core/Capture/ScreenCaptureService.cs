using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using TranslatorApp.Core.Input;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

// RECT 是嵌套在 NativeMethods 里的结构体，得用 using 别名单独引进来
using RECT = TranslatorApp.Core.Native.NativeMethods.RECT;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 屏幕截图：按物理像素把框选区域那块画面抓下来。
/// </summary>
/// <remarks>
/// 【为什么必须用物理像素】
///   <see cref="Graphics.CopyFromScreen"/> 吃的是**物理像素**，不是 DIP。
///   100% 缩放时两者相同，但在 125% / 150% 下会整体偏掉——
///   这是原文反复出现的坑（「多显示器截图错位」）。
///   所以本类只认 <see cref="RECT"/> 的物理像素，DIP 换算交给 <see cref="DpiHelper"/>。
///
/// 【多显示器 / 负坐标】
///   第二块屏可能在主屏左边（负 X）或上边（负 Y）。
///   这里先把区域**钳制到虚拟屏幕范围内**，再截图——
///   否则会抛异常，或者截出一张全黑图当识别结果。
/// </remarks>
internal static class ScreenCaptureService
{
    /// <summary>小于这个尺寸就不截了（截图再识别也是浪费，直接提示「区域太小」）。</summary>
    private const int MinRegionPixels = 8;

    /// <summary>
    /// 截取指定物理像素矩形。返回 null 表示截不了（原因写进日志）。
    /// 调用方负责 Dispose 返回的位图。
    /// </summary>
    public static Bitmap? TryCapture(RECT rect)
    {
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width < MinRegionPixels || height < MinRegionPixels)
        {
            AppLogger.Warn($"截图跳过：区域太小 {width}×{height}（小于 {MinRegionPixels}×{MinRegionPixels}）");
            return null;
        }

        // 钳制到虚拟屏幕内：区域超出屏幕时（显示器被拔、坐标算错）不至于崩
        RECT safe = ClampToVirtualScreen(rect);
        if (safe.Right - safe.Left < MinRegionPixels || safe.Bottom - safe.Top < MinRegionPixels)
        {
            AppLogger.Warn($"截图跳过：区域完全在屏幕之外（原始 {rect.Left},{rect.Top}）");
            return null;
        }

        if (!SameRect(safe, rect))
        {
            AppLogger.Info($"截图区域已钳制到屏幕内：{rect.Left},{rect.Top} → {safe.Left},{safe.Top}");
        }

        try
        {
            var bitmap = new Bitmap(safe.Right - safe.Left, safe.Bottom - safe.Top, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(safe.Left, safe.Top, 0, 0,
                    new Size(safe.Right - safe.Left, safe.Bottom - safe.Top),
                    CopyPixelOperation.SourceCopy);
            }

            AppLogger.Info($"截图完成：{safe.Right - safe.Left}×{safe.Bottom - safe.Top} 像素");
            return bitmap;
        }
        catch (Exception ex)
        {
            // 典型触发：区域跨到了不存在的显示器上
            AppLogger.Warn($"截图失败：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>两个矩形是否完全相同（RECT 是结构体但没重写 ==，只能逐字段比）。</summary>
    private static bool SameRect(RECT a, RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>把矩形钳制到整个虚拟屏幕（所有显示器拼起来的那块区域）内。</summary>
    private static RECT ClampToVirtualScreen(RECT rect)
    {
        RECT screen = ScreenGeometry.GetVirtualScreen();

        var (left, top) = ScreenGeometry.ClampToVirtualScreen(rect.Left, rect.Top);
        int right = Math.Clamp(rect.Right, left, screen.Right);
        int bottom = Math.Clamp(rect.Bottom, top, screen.Bottom);
        return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
    }
}
