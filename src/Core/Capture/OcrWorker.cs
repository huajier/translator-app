using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// OCR 专用后台线程：截图、预处理、识别都在这条线程上跑。
///
/// 【为什么单独一条线程】
///   1. **界面不能卡**：识别要几百毫秒，放 UI 线程上界面会明显卡住。
///   2. **WinRT 引擎不要跨线程乱用**：OCR 引擎实例是缓存复用的（性能红线），
///      如果一会儿在 UI 线程创建、一会儿在别处调用，
///      轻则慢得离谱，重则抛「对象只能在创建它的线程上访问」。
///      固定在**同一条线程**上创建和调用，这两个问题一起消失。
///
/// 【为什么不把剪贴板也放这条线程】
///   剪贴板 API 要求 **STA + 消息循环**，而这里是一条普通 MTA 线程，
///   在上面调 Clipboard.Open() 会直接抛异常。
///   所以剪贴板通道留在 UI 线程（靠 await 保持响应），只有 OCR 走这里。
/// </remarks>
internal static class OcrWorker
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly Thread Thread;

    static OcrWorker()
    {
        Thread = new Thread(Loop)
        {
            IsBackground = true,          // 不阻止程序退出
            Name = "文字识别线程",
            Priority = ThreadPriority.BelowNormal
        };
        Thread.Start();
        AppLogger.Info("文字识别后台线程已启动（识别时界面不会卡）");
    }

    private static void Loop()
    {
        foreach (var work in Queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // 这条线程绝不能因为一个任务崩掉就整体死掉
                AppLogger.Error(ex, "识别后台线程执行任务时出错");
            }
        }
    }

    /// <summary>在这条线程上跑一次识别，把结果送回调用方。</summary>
    public static Task<CaptureResult> RecognizeAsync(OcrTextCapture ocr, CaptureRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        Queue.Add(() =>
        {
            // 注意：这里**不 await**。如果 await 了，续体会被调度回线程池，
            // 引擎就跑到别的线程上调用了——正是要避免的情况。
            // 改成挂一个延续把结果送回调用方，引擎的创建与调用始终在这条线程。
            Task<CaptureResult> work;
            try
            {
                work = ocr.CaptureAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetResult(new CaptureResult
                {
                    Failure = CaptureFailure.Unknown,
                    Detail = ex.Message
                });
                return;
            }

            work.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var error = t.Exception!.GetBaseException();
                    AppLogger.Error(error, "识别流程异常");
                    completion.TrySetResult(new CaptureResult
                    {
                        Failure = CaptureFailure.Unknown,
                        Detail = error.Message
                    });
                }
                else
                {
                    completion.TrySetResult(t.Result);
                }
            }, TaskScheduler.Default);
        });

        return completion.Task;
    }

    /// <summary>释放缓存的 OCR 引擎（程序退出时调用，避免句柄泄漏）。</summary>
    public static void Shutdown() => OcrEngineFactory.Dispose();
}
