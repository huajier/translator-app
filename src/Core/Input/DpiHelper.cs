using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Input;

/// <summary>
/// DpiHelper —— 物理像素 ↔ WPF 的 DIP 换算（阶段 2）
///
/// 【为什么要换算：两种坐标不一样】
///   - 钩子给我们的鼠标坐标是「物理像素」（Windows 内部用的单位）；
///   - WPF 的界面（XAML 里写的数字）用的是「DIP」（逻辑单位）。
///   - 关系：1 DIP = 96/DPI 物理像素。
///     缩放 100% 时 DPI=96 → 1 DIP = 1 像素，两者相同（所以本机看不出问题）；
///     缩放 125% 时 DPI=120 → 1 DIP = 1.25 像素；150% 时 144 → 1 DIP = 1.5 像素。
///   如果不换算，在 125% 缩放的屏幕上，选框会整体偏移 —— 这就是规格里反复强调的坑。
///
/// 【硬规矩】业务代码里不许自己手算比例，必须走这里，换算口径只此一处。
///
/// 它和谁交互：SelectionStateMachine（算矩形）、SelectionMaskWindow（定位窗口）、SelectionSession。
/// </summary>
internal static class DpiHelper
{
    /// <summary>标准 DPI：96 = 缩放 100%（WPF 的 1 DIP 就是 1 个 96 DPI 像素）。</summary>
    public const double DefaultDpi = 96.0;

    /// <summary>取不到 DPI 时按 100% 处理：宁可坐标差一点也不崩。</summary>
    public const uint FallbackDpi = 96;

    /// <summary>物理像素 → DIP。</summary>
    public static double PhysicalToDip(double physical, uint dpi)
        => physical * DefaultDpi / (dpi == 0 ? FallbackDpi : dpi);

    /// <summary>DIP → 物理像素（四舍五入到整像素）。</summary>
    public static int DipToPhysical(double dip, uint dpi)
        => (int)Math.Round(dip * (dpi == 0 ? FallbackDpi : dpi) / DefaultDpi);

    /// <summary>给日志看的可读描述，例如「192 DPI（缩放 200%）」。</summary>
    public static string Describe(uint dpi)
    {
        if (dpi == 0) dpi = FallbackDpi;
        double scale = dpi / DefaultDpi;
        return $"{dpi} DPI（缩放 {scale * 100:0}%）";
    }

    /// <summary>
    /// 屏幕原点（可能是负数，多显示器时副屏在左边/上边）整体换算成 DIP —— 给全屏遮罩窗口定位用。
    /// </summary>
    public static (double left, double top, double width, double height) PhysicalRectToDip(
        int left, int top, int width, int height, uint dpi)
        => (PhysicalToDip(left, dpi), PhysicalToDip(top, dpi),
            PhysicalToDip(width, dpi), PhysicalToDip(height, dpi));

    /// <summary>把一个物理像素矩形规整成「宽高为正」的矩形（用户往左/往上拖时会变成负宽高）。</summary>
    public static NativeMethods.RECT Normalize(NativeMethods.RECT rect)
        => new NativeMethods.RECT
        {
            Left = Math.Min(rect.Left, rect.Right),
            Top = Math.Min(rect.Top, rect.Bottom),
            Right = Math.Max(rect.Left, rect.Right),
            Bottom = Math.Max(rect.Top, rect.Bottom)
        };
}