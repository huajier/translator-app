using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Input;

/// <summary>
/// ScreenGeometry —— 屏幕与多显示器的几何计算（阶段 2）
///
/// 这个类干嘛的：
///   1. 算出「整个虚拟屏幕」有多大（多显示器时是一整块大矩形，可能包含负坐标）；
///   2. 算出鼠标当前在哪块显示器上、那块屏的缩放比例是多少；
///   3. 把坐标钳制在屏幕范围内，防止鼠标拖出屏幕后算出负数宽高把界面搞崩。
///
/// 【虚拟屏幕 vs 主显示器】
///   主显示器只是「有任务栏那块」。多显示器时用户可以在第二块屏上框选，
///   遮罩必须盖住**所有**显示器，所以要用 SM_XVIRTUALSCREEN 系列而不是 SM_CXSCREEN。
///
/// 【负坐标是正常的】
///   如果第二块显示器接在主屏左边，它的坐标是负数（x 从 -1920 开始）。
///   所以代码里不许把负数当错误「修正」掉，只允许原样使用。
///
/// 它和谁交互：SelectionMaskWindow（盖多大）、SelectionStateMachine（钳制坐标、算 DPI）。
/// </summary>
internal static class ScreenGeometry
{
    /// <summary>整个虚拟屏幕的范围（物理像素，原点可能为负）。</summary>
    public static NativeMethods.RECT GetVirtualScreen()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        return new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    /// <summary>鼠标这个点落在哪块显示器上，以及那块屏的范围与 DPI。</summary>
    public static (NativeMethods.RECT bounds, uint dpi) GetMonitorAt(int x, int y)
    {
        var pt = new NativeMethods.POINT(x, y);
        IntPtr monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        if (monitor == IntPtr.Zero)
        {
            // 取不到就退回主显示器，并按 100% 处理，绝不因此崩掉
            return (new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = NativeMethods.GetSystemMetrics(0),
                Bottom = NativeMethods.GetSystemMetrics(1)
            }, DpiHelper.FallbackDpi);
        }

        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return (new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = NativeMethods.GetSystemMetrics(0),
                Bottom = NativeMethods.GetSystemMetrics(1)
            }, DpiHelper.FallbackDpi);
        }

        return (info.rcMonitor, NativeMethods.GetDpiForMonitor(monitor));
    }

    /// <summary>点是否还在屏幕范围内（鼠标拖出屏幕边界时用来判断该不该取消）。</summary>
    public static bool IsInsideVirtualScreen(int x, int y)
    {
        var r = GetVirtualScreen();
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    /// <summary>把一个点钳制到屏幕范围内（防止拖出屏幕算出负宽高）。</summary>
    public static (int X, int Y) ClampToVirtualScreen(int x, int y)
    {
        var r = GetVirtualScreen();
        int cx = Math.Clamp(x, r.Left, r.Right - 1);
        int cy = Math.Clamp(y, r.Top, r.Bottom - 1);
        return (cx, cy);
    }

    /// <summary>人类可读的虚拟屏幕描述，写日志用。</summary>
    public static string DescribeVirtualScreen()
    {
        var r = GetVirtualScreen();
        return $"虚拟屏幕=({r.Left},{r.Top})-({r.Right},{r.Bottom})，尺寸={r.Right - r.Left}×{r.Bottom - r.Top}";
    }
}