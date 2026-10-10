using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Abstractions;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 通道调度：按决策 3-1「混合」的策略依次尝试，汇总失败原因。
///
/// 【策略】
///   先试剪贴板（快、准），失败了再试 OCR（慢，但什么都能认）。
///   任何一条通道拿到有效文字就立刻返回，不再往下走。
///
/// 【线程分工（重要）】
///   本类跑在 **UI 线程**上：
///     · 剪贴板通道直接在这里跑——剪贴板 API 要求 STA + 消息循环，
///       全程 await 不阻塞，界面不会卡；
///     · OCR 通道甩给 <see cref="OcrWorker"/> 那条专用线程，
///       避免识别过程冻住界面，也保证引擎始终在同一线程上被调用。
///
/// 【OCR 整体不可用时要降级】
///   比如系统没装「光学字符识别」语言包，
///   这时**不能静默失败**——用户会以为框选没反应。
///   要明确告诉用户「现在只能复制能用，识别用不了」。
/// </remarks>
internal sealed class CaptureDispatcher
{
    private readonly ClipboardTextCapture _clipboard = new();
    private readonly OcrTextCapture _ocr = new();

    /// <summary>程序运行期间共用一个调度器，状态（如降级标记）才能连续。</summary>
    public static CaptureDispatcher Shared { get; } = new();

    /// <summary>OCR 引擎初始化失败过（记下来，用于降级提示）。</summary>
    public bool OcrDisabled { get; private set; }

    /// <summary>
    /// 要不要走「剪贴板假复制」这条通道。
    ///
    /// 【为什么默认关掉 —— 决策 3-1 改判，2026-10-08】
    ///   用户原话：「选中文字后再框选就丧失了方便性，我要的就是框完后就直接翻译出来」。
    ///   框选不会在目标程序里选中文字，所以这条路实际只在
    ///   「用户事先已经选中文字」时有效——而那正是用户不要的额外步骤。
    ///
    ///   代码保留着：万一以后要做「自动模拟选中再复制」，接回去就行。
    ///
    ///   【注意】这里用 static readonly 而不是 const：
    ///   写成 const false 的话，编译器会把剪贴板那一整段判成「永远走不到的死代码」
    ///   并报警告 CS0162。留成 static readonly 语义一样，但不会有那条噪音。
    /// </summary>
    private static readonly bool UseClipboardChannel = false;

    /// <summary>
    /// 取词主入口。**任何情况下都不抛异常**。
    /// 必须在 UI 线程上调用。
    /// </summary>
    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        long start = Environment.TickCount64;
        var failures = new List<string>();

        AppLogger.Info($"开始取词：区域 {request.WidthPixels}×{request.HeightPixels}，" +
                       $"触发方式 {request.TriggerMode}");

        // ---- 取词通道 ----
        // 【决策 3-1 改判：只走 OCR】（2026-10-08 用户反馈后）
        //
        //   原来是「先试剪贴板、再 OCR」。用户实测后改判，理由很实在：
        //   **框选动作不会在目标程序里选中任何文字**，
        //   所以剪贴板那条路只有在「用户事先已经选中了文字」时才管用——
        //   而那正是用户嫌麻烦、不想要的步骤。
        //
        //   剪贴板通道的代码**没有删**（ClipboardTextCapture 还在），
        //   只是默认不走。想开回来的话，把下面的开关改成 true 即可。
        if (UseClipboardChannel)
        {
            CaptureResult clipboardResult = await _clipboard.CaptureAsync(request, cancellationToken)
                                                         .ConfigureAwait(true);
            if (clipboardResult.Success)
            {
                LogSuccess(clipboardResult, start);
                return clipboardResult;
            }

            if (clipboardResult.Failure == CaptureFailure.Canceled)
            {
                AppLogger.Info("取词已取消，不再尝试其它通道");
                return clipboardResult;
            }

            failures.Add($"剪贴板：{clipboardResult.Detail}");
        }

        // 用户主动取消就别再往下走了
        if (cancellationToken.IsCancellationRequested)
        {
            return new CaptureResult
            {
                Failure = CaptureFailure.Canceled,
                Detail = "用户取消",
                ElapsedMs = Environment.TickCount64 - start
            };
        }

        // ---- OCR（甩到专用线程，界面不卡）----
        if (!OcrEngineFactory.IsAvailable)
        {
            OcrDisabled = true;
            AppLogger.Warn("OCR 不可用（系统缺识别语言包），无法取词");
            return BuildFinalFailure(failures, start, "系统没装文字识别功能");
        }

        CaptureResult ocrResult = await OcrWorker.RecognizeAsync(_ocr, request, cancellationToken)
                                                 .ConfigureAwait(true);
        if (ocrResult.Success)
        {
            LogSuccess(ocrResult, start);
            return ocrResult;
        }

        if (ocrResult.Failure == CaptureFailure.Canceled)
        {
            return ocrResult;
        }

        failures.Add($"OCR：{ocrResult.Detail}");
        return BuildFinalFailure(failures, start, "两条通道都没取到文字");
    }

    private static void LogSuccess(CaptureResult result, long start)
    {
        AppLogger.Info(
            $"取词成功：通道={result.Channel} 字数={result.Text.Length} " +
            $"语言={LanguageDetector.Describe(result.Text)} " +
            $"总耗时={Environment.TickCount64 - start} 毫秒（{result.Detail}）");
    }

    /// <summary>两条都失败：汇总原因，并给出最能帮用户解决问题的那个原因。</summary>
    private CaptureResult BuildFinalFailure(List<string> failures, long start, string summary)
    {
        AppLogger.Info($"取词失败：{summary}｜{string.Join("｜", failures)}");

        // 引擎本身起不来时要明确告诉用户能力受限，
        // 否则用户只会以为「框选没反应」。
        bool ocrBroke = OcrDisabled || failures.Exists(f => f.Contains("OCR 引擎"));

        return new CaptureResult
        {
            Failure = ocrBroke ? CaptureFailure.OcrUnavailable : CaptureFailure.NoTextRecognized,
            ElapsedMs = Environment.TickCount64 - start,
            Detail = $"{summary}｜{string.Join("｜", failures)}"
        };
    }

    /// <summary>当前能力说明，托盘菜单或提示框显示用。</summary>
    public string CapabilityDescription()
    {
        if (OcrDisabled || !OcrEngineFactory.IsAvailable)
        {
            return "当前能力受限：只能用「复制」取词，识别功能不可用";
        }
        return "取词方式：优先复制，复制不了则用文字识别";
    }
}
