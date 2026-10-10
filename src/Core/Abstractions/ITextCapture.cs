using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Capture;

namespace TranslatorApp.Core.Abstractions;

/// <summary>
/// 取词通道统一接口：把「屏幕上的一个矩形区域」变成「一段文字」。
///
/// 阶段 3 自然形成，阶段 10 只做正式化与文档化，不再改结构。
/// 两个实现：<see cref="ClipboardTextCapture"/>（模拟 Ctrl+C）
/// 与 <see cref="OcrTextCapture"/>（看图认字）；
/// 由 <see cref="CaptureDispatcher"/> 按决策 3-1「混合」的策略依次调用。
/// </summary>
internal interface ITextCapture
{
    /// <summary>通道名称（英文标识，日志与统计用）："Clipboard" / "WindowsOcr"。</summary>
    string ChannelName { get; }

    /// <summary>这个通道当前能不能用（例如 OCR 语言包没装就说 false）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 尝试取词。失败不抛异常，一律通过 <see cref="CaptureResult.FailureReason"/> 返回原因，
    /// 由调度器决定要不要换下一条通道。
    /// </summary>
    Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken);
}
