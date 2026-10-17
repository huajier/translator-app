using System;
using TranslatorApp.Core.Native;

// RECT 是嵌套在 NativeMethods 里的结构体，得用 using 别名单独引进来
using RECT = TranslatorApp.Core.Native.NativeMethods.RECT;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 取词请求：一次框选产生的全部上下文。
/// </summary>
/// <remarks>
/// 坐标约定（全项目统一，改动前先看 <see cref="DpiHelper"/>）：
///   <see cref="PhysicalRect"/> —— 屏幕上的物理像素矩形，直接喂给 CopyFromScreen；
///   <see cref="DipRect"/>     —— 同一块区域的 WPF 设备无关坐标，用于定位提示框。
/// </remarks>
internal sealed class CaptureRequest
{
    /// <summary>物理像素矩形（屏幕坐标系，可含负坐标表示第二块屏在主屏左边）。</summary>
    public RECT PhysicalRect { get; init; }

    /// <summary>同一区域的 DIP 矩形：(左, 上, 宽, 高)。</summary>
    public (double Left, double Top, double Width, double Height) DipRect { get; init; }

    /// <summary>选区所在的显示器矩形（物理像素）。</summary>
    public RECT MonitorRect { get; init; }

    /// <summary>选区所在显示器的 DPI（96 = 100%）。</summary>
    public uint MonitorDpi { get; init; }

    /// <summary>触发方式说明，例如 "Ctrl+Alt 拖拽"。</summary>
    public string TriggerMode { get; init; } = string.Empty;

    /// <summary>
    /// 框选那一刻**前台窗口所属的进程名**（如 `chrome.exe`），给取词记录对号用。
    ///
    /// 【为什么要它（2026-10-17 用户批的）】
    ///   取词记录里原本没有任何「用户当时在看哪个软件」的线索，
    ///   用户截图报错例时只能靠时间戳对号，一天测几十次很容易对错。
    ///   记录时**只记进程名，不记网址、不记窗口标题**。
    ///
    /// 取不到时是「取不到」，不影响取词。
    /// </summary>
    public string SourceProcess { get; init; } = string.Empty;

    /// <summary>选区宽度（物理像素），用于「区域太小」的快速判断。</summary>
    public int WidthPixels => PhysicalRect.Right - PhysicalRect.Left;

    /// <summary>选区高度（物理像素）。</summary>
    public int HeightPixels => PhysicalRect.Bottom - PhysicalRect.Top;
}
