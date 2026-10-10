using System;

namespace TranslatorApp.Core.Capture;

/// <summary>取词失败的原因。每一种都要能翻成一句给用户看的大白话。</summary>
internal enum CaptureFailure
{
    None = 0,

    /// <summary>选区太小或太小块，认不出东西。</summary>
    RegionTooSmall,

    /// <summary>截图失败（区域超出屏幕、显示器被拔掉等）。</summary>
    ScreenshotFailed,

    /// <summary>模拟 Ctrl+C 后剪贴板没变化（对方不让复制）。</summary>
    ClipboardNoChange,

    /// <summary>剪贴板被别的程序占用，重试后仍打不开。</summary>
    ClipboardLocked,

    /// <summary>OCR 引擎起不来（语言包缺失等）。</summary>
    OcrUnavailable,

    /// <summary>OCR 跑完了，但一个有效文字都没认出来。</summary>
    NoTextRecognized,

    /// <summary>OCR 取消了（用户中途按 Esc 等）。</summary>
    Canceled,

    /// <summary>没预料到的错误。</summary>
    Unknown,
}

/// <summary>取词结果：无论成功失败都返回它，不抛异常。</summary>
internal sealed class CaptureResult
{
    /// <summary>认出来的文字（已做过清理）。失败时为空串。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>实际用的是哪条通道："Clipboard" / "WindowsOcr"。</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>从开始到出结果花了多久（毫秒）。</summary>
    public long ElapsedMs { get; init; }

    /// <summary>失败原因；成功时是 <see cref="CaptureFailure.None"/>。</summary>
    public CaptureFailure Failure { get; init; } = CaptureFailure.None;

    /// <summary>自动判断出来的语言（"zh" / "en" / "unknown"）。</summary>
    public string DetectedLanguage { get; init; } = "unknown";

    /// <summary>识别出的行数（仅 OCR 通道有意义）。</summary>
    public int LineCount { get; init; }

    /// <summary>失败时的补充说明，直接写进日志便于排查。</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>认出的文字去掉空白和标点后，是否还够长度算「有效」。</summary>
    public bool HasMeaningfulText => TextNormalizer.CountMeaningfulChars(Text) >= 2;

    /// <summary>是否成功。</summary>
    public bool Success => Failure == CaptureFailure.None && HasMeaningfulText;

    /// <summary>把失败原因翻成一句用户能看懂的话。</summary>
    public string ToUserMessage() => Failure switch
    {
        CaptureFailure.None => "取到文字了",
        CaptureFailure.RegionTooSmall => "框选区域太小，换个大一点的地方试试",
        CaptureFailure.ScreenshotFailed => "没能截取这块画面，请重试",
        CaptureFailure.ClipboardNoChange => "这块内容不支持复制",
        CaptureFailure.ClipboardLocked => "剪贴板正被别的程序占用，稍后再试",
        CaptureFailure.OcrUnavailable => "文字识别功能不可用，请检查系统是否安装了「光学字符识别」",
        CaptureFailure.NoTextRecognized => "这块内容里没有识别到文字",
        CaptureFailure.Canceled => "已取消",
        _ => "取词失败，请重试",
    };
}
