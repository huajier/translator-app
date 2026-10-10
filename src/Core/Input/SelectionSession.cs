using TranslatorApp.Core.Native;

// RECT 是 NativeMethods 内部嵌套的结构体，用 using 命名空间是**引不到**嵌套类型的，
// 必须这样写一条别名，后面才能直接写 RECT。
using RECT = TranslatorApp.Core.Native.NativeMethods.RECT;

namespace TranslatorApp.Core.Input;

/// <summary>
/// SelectionSession —— 一次框选的全部结果（阶段 2）
///
/// 这个类干嘛的：
///   把「用户在屏幕上框了哪一块」这件事完整地记下来，包含两份坐标：
///     - 物理像素坐标：Windows 内部用的单位，将来截图、OCR、翻译都要用这份；
///     - DIP 坐标：WPF 界面用的单位，阶段 5 的悬浮窗定位要用这份。
///   两份都留着，是为了以后换算规则变了不用重新框一次。
///
/// 本阶段这个数据只写进日志、显示在提示框上（给人核对）。
/// 阶段 3 才把它接到 ITextCapture 去取词 —— 本类刻意不依赖任何取词/翻译代码，保持阶段边界。
///
/// 它和谁交互：SelectionStateMachine 生成它；SelectionCoordLog 写日志；App.xaml.cs 弹提示框。
/// </summary>
internal sealed record SelectionSession
{
    /// <summary>按下左键的时刻。</summary>
    public required DateTime StartedAt { get; init; }

    /// <summary>抬起左键的时刻。</summary>
    public required DateTime EndedAt { get; init; }

    /// <summary>框选矩形（物理像素，已规整为宽高为正、已钳制在屏幕内）。</summary>
    public required RECT PhysicalRect { get; init; }

    /// <summary>框选矩形（DIP，已按所在显示器的缩放换算）。</summary>
    public required (double Left, double Top, double Width, double Height) DipRect { get; init; }

    /// <summary>这块框所在的显示器的范围（物理像素）。</summary>
    public required RECT MonitorRect { get; init; }

    /// <summary>所在显示器的 DPI（96 = 缩放 100%）。</summary>
    public required uint MonitorDpi { get; init; }

    /// <summary>触发方式（阶段 2 目前只有「Ctrl+Alt 拖拽」这一种，双击 Ctrl 翻剪贴板属阶段 3）。</summary>
    public required string TriggerMode { get; init; }

    /// <summary>物理像素宽。</summary>
    public int PhysicalWidth => PhysicalRect.Right - PhysicalRect.Left;

    /// <summary>物理像素高。</summary>
    public int PhysicalHeight => PhysicalRect.Bottom - PhysicalRect.Top;

    /// <summary>拖拽用时（毫秒）。</summary>
    public long DurationMs => (long)(EndedAt - StartedAt).TotalMilliseconds;

    /// <summary>给日志和提示框看的一句话摘要。</summary>
    public override string ToString()
        => $"左上=({PhysicalRect.Left},{PhysicalRect.Top}) 尺寸={PhysicalWidth}×{PhysicalHeight}"
         + $" 显示器={MonitorRect.Left},{MonitorRect.Top} {DpiHelper.Describe(MonitorDpi)}"
         + $" 用时={DurationMs}ms";
}