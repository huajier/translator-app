using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TranslatorApp.Core.Abstractions;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 剪贴板通道：模拟按一下 Ctrl+C，拿到程序里的**真文字**。
///
/// 【为什么先试这条】
///   剪贴板拿到的是原始文字——不会认错字、不受图片清晰度影响，
///   而且快得多（通常几十毫秒，而 OCR 要几百毫秒）。
///   决策 3-1 选「混合」，所以先走这条，不行再交给 OCR。
///
/// 【线程】
///   本类**必须在 UI 线程上执行**（剪贴板 API 要求 STA + 消息循环）。
///   因此全程不许有 Thread.Sleep，所有等待都用 await，让界面保持响应。
///
/// 【三个必须做对的细节】
///   1. **Ctrl+Alt 必须先松开**：框选刚结束时修饰键可能还没弹起，
///      这时候发 Ctrl+C，目标程序收到的是 **Ctrl+Alt+C**，复制必然失败。
///      这是原文里最隐蔽的坑。
///   2. **200 毫秒超时**：对方程序也可能弹「是否允许访问剪贴板」之类的东西，
///      不能无限等。
///   3. **用完必须还原**：见 <see cref="ClipboardBackup"/>。
/// </remarks>
internal sealed class ClipboardTextCapture : ITextCapture
{
    public string ChannelName => "Clipboard";

    public bool IsAvailable => true;

    /// <summary>复制后等待剪贴板变化的最长时间（规格：200 毫秒）。</summary>
    private const int ClipboardTimeoutMs = 200;

    /// <summary>等待 Ctrl/Alt 弹起的最长时间。</summary>
    private const int ModifierReleaseTimeoutMs = 300;

    /// <summary>
    /// 一次 Ctrl+C 要注入几条事件：Ctrl↓、C↓、C↑、Ctrl↑。
    /// SendInput 的返回值是「实际塞进去几条」，用来判断有没有被系统拦下来。
    /// </summary>
    private const uint CopyShortcutEventCount = 4;

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        long start = Environment.TickCount64;

        // 1) 等修饰键松开。不等的话发出去的是 Ctrl+Alt+C
        if (!await WaitForModifiersReleasedAsync(cancellationToken).ConfigureAwait(true))
        {
            return Fail(CaptureFailure.ClipboardNoChange, start, "Ctrl/Alt 还没松开，复制会被组合键污染");
        }

        // 2) 先把用户剪贴板整个快照下来
        if (!await ClipboardBackup.TrySnapshotAsync().ConfigureAwait(true))
        {
            return Fail(CaptureFailure.ClipboardLocked, start, "读不到剪贴板（可能被占用）");
        }

        try
        {
            // 3) 发送 Ctrl+C
            uint sent = SendCopyShortcut();
            if (sent != CopyShortcutEventCount)
            {
                // 返回值是「实际注入成功的事件条数」。
                // 一共发 4 条（Ctrl↓、C↓、C↑、Ctrl↑），少于这个数就说明没全发出去，
                // 常见于权限不足（中途被 UAC 提权对话框截断）。
                return Fail(CaptureFailure.ClipboardNoChange, start,
                    $"Ctrl+C 注入失败（成功 {sent}/{CopyShortcutEventCount}，可能权限不足）");
            }

            // 4) 等剪贴板变化（最多 200 毫秒）
            bool changed = await WaitForClipboardChangeAsync(ClipboardTimeoutMs, cancellationToken)
                                                .ConfigureAwait(true);
            if (!changed)
            {
                return Fail(CaptureFailure.ClipboardNoChange, start,
                    $"{ClipboardTimeoutMs} 毫秒内剪贴板没变化（这块内容不支持复制）");
            }

            // 5) 读文本
            string? text = ClipboardBackup.TryReadText();
            if (string.IsNullOrWhiteSpace(text))
            {
                return Fail(CaptureFailure.ClipboardNoChange, start, "剪贴板变了但里面没有文字（可能复制到的是图片）");
            }

            string cleaned = TextNormalizer.Normalize(text);
            if (TextNormalizer.CountMeaningfulChars(cleaned) < 2)
            {
                return Fail(CaptureFailure.NoTextRecognized, start, "复制到的内容里没有有效文字");
            }

            AppLogger.Info($"剪贴板取词成功：{cleaned.Length} 字，耗时 {Environment.TickCount64 - start} 毫秒");
            return new CaptureResult
            {
                Text = cleaned,
                Channel = ChannelName,
                ElapsedMs = Environment.TickCount64 - start,
                Failure = CaptureFailure.None,
                DetectedLanguage = LanguageDetector.Detect(cleaned),
                Detail = "直接取到原始文字，无需识别"
            };
        }
        finally
        {
            // 无论如何都要还原——失败路径尤其不能漏，否则用户剪的东西就没了
            await ClipboardBackup.TryRestoreAsync().ConfigureAwait(true);
            ClipboardBackup.Discard();
        }
    }

    /// <summary>
    /// 等 Ctrl / Alt 真的松开。
    /// 框选刚结束的那一刻，物理按键可能还按着；这时发 Ctrl+C 会被识别成 Ctrl+Alt+C。
    ///
    /// 用 await 而不是 Thread.Sleep —— 这个方法跑在 UI 线程上，睡 300 毫秒界面就卡 300 毫秒。
    /// </summary>
    private static async Task<bool> WaitForModifiersReleasedAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ModifierReleaseTimeoutMs)
        {
            if (!AreModifiersDown()) return true;
            if (cancellationToken.IsCancellationRequested) return false;
            await Task.Delay(10).ConfigureAwait(true);
        }
        return !AreModifiersDown();
    }

    private static bool AreModifiersDown()
    {
        bool ctrlDown = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL)
                     || NativeMethods.IsKeyDown(NativeMethods.VK_LCONTROL)
                     || NativeMethods.IsKeyDown(NativeMethods.VK_RCONTROL);
        bool altDown = NativeMethods.IsKeyDown(NativeMethods.VK_MENU)
                    || NativeMethods.IsKeyDown(NativeMethods.VK_LMENU)
                    || NativeMethods.IsKeyDown(NativeMethods.VK_RMENU);
        return ctrlDown || altDown;
    }

    /// <summary>发一次 Ctrl+C（按下 Ctrl → 按下 C → 松开 C → 松开 Ctrl）。</summary>
    private static uint SendCopyShortcut()
    {
        int size = Marshal.SizeOf<NativeMethods.INPUT>();

        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, false),
            Key(0x43, false),                                  // 'C'
            Key(0x43, true),
            Key(NativeMethods.VK_CONTROL, true),
        };

        return NativeMethods.SendInput((uint)inputs.Length, inputs, size);
    }

    private static NativeMethods.INPUT Key(ushort vk, bool up)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0u,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    /// <summary>轮询等待剪贴板序号变化（每 10 毫秒看一次）。</summary>
    private static async Task<bool> WaitForClipboardChangeAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (ClipboardBackup.HasChangedSinceSnapshot()) return true;
            if (cancellationToken.IsCancellationRequested) return false;
            await Task.Delay(10).ConfigureAwait(true);
        }
        return ClipboardBackup.HasChangedSinceSnapshot();
    }

    private CaptureResult Fail(CaptureFailure reason, long start, string detail)
    {
        AppLogger.Info($"剪贴板取词失败：{detail}");
        return new CaptureResult
        {
            Channel = ChannelName,
            Failure = reason,
            ElapsedMs = Environment.TickCount64 - start,
            Detail = detail
        };
    }
}
