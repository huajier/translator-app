using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Capture;

/// <summary>
/// 剪贴板备份与还原：确保用户剪过的**任何东西**都不会被我们弄丢。
/// </summary>
/// <remarks>
/// 【为什么必须支持多格式】
///   用户剪贴板里可能是一张图、一个文件、一段富文本——
///   只备份纯文本的话，「程序偷偷复制」再「悄悄还原」这个过程中，
///   用户剪的那张图片就没了。这种事用户不会当场发现，几天后才会发现，
///   而且根本不会想到是这个软件干的。**所以必须原样快照整个 IDataObject。**
///
/// 【怎么做到】
///   在 OpenClipboard 期间把 GetDataObject() 整个存下来，
///   还原时 SetDataObject(快照, copy: true) 就能整个还回去。
///   copy: true 很关键——它把延迟渲染的数据立刻实体化，
///   否则用户中途复制别的东西，这个对象就失效了，还原时会是空的。
///
/// 【线程铁律】
///   剪贴板 API 要求**单线程单元（STA）且有消息循环**，
///   所以本类的所有方法只能在 UI 线程上调用。
///   而 UI 线程上一旦 Thread.Sleep，整个界面就冻住——
///   框选工具冻 300 毫秒是能被明显看出来的。
///   所以所有等待一律用 await Task.Delay，让消息循环继续转。
/// </remarks>
internal static class ClipboardBackup
{
    /// <summary>打开剪贴板失败时的重试次数与间隔（规格第 12 条）。</summary>
    private const int OpenRetries = 3;
    private const int OpenRetryDelayMs = 50;

    /// <summary>用户剪贴板内容的快照（null = 本来就没有内容，或没打开成功）。</summary>
    private static IDataObject? _snapshot;

    /// <summary>拍快照时的剪贴板序号，用来判断「复制到底有没有生效」。</summary>
    private static uint _sequenceBeforeCopy;

    /// <summary>
    /// 拍快照。**必须在发送 Ctrl+C 之前调用**（发完就晚了）。
    ///
    /// 【关于 Open/Close】
    ///   .NET 的 <see cref="Clipboard"/> 没有公开的 Open/Close 方法，
    ///   它每次读写都会自己开锁、内部重试。
    ///   所以这里直接用它的静态方法，锁的事交给框架操心——
    ///   真被别的程序占死时它会抛，我们在外层重试即可。
    /// </summary>
    public static async Task<bool> TrySnapshotAsync()
    {
        _snapshot = null;
        _sequenceBeforeCopy = Native.NativeMethods.GetClipboardSequenceNumber();

        for (int i = 0; i < OpenRetries; i++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                _snapshot = data is null ? null : new DataObject(data);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"读取剪贴板失败（第 {i + 1}/{OpenRetries} 次）：{ex.GetType().Name}");
            }

            await Task.Delay(OpenRetryDelayMs).ConfigureAwait(true);
        }

        return false;
    }

    /// <summary>剪贴板序号有没有变（比「内容比对」可靠得多，规格第 11 条）。</summary>
    public static bool HasChangedSinceSnapshot()
        => Native.NativeMethods.GetClipboardSequenceNumber() != _sequenceBeforeCopy;

    /// <summary>读剪贴板里的文本；没有文本返回 null。</summary>
    public static string? TryReadText()
    {
        try
        {
            return Clipboard.GetText();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"读取剪贴板文本失败：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把用户原来的剪贴板还原回去。
    ///
    /// 【铁律】还原失败**只记日志、绝不弹窗、绝不阻塞主流程**——
    /// 为难用户比还原失败更糟（规格异常表明确写了）。
    /// </summary>
    public static async Task TryRestoreAsync()
    {
        if (_snapshot is null) return;

        for (int i = 0; i < OpenRetries; i++)
        {
            try
            {
                // copy: true —— 立刻实体化数据；否则这份快照可能是空的
                Clipboard.SetDataObject(_snapshot, copy: true);
                AppLogger.Info("剪贴板已还原为用户原来的内容");
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"还原剪贴板失败（第 {i + 1}/{OpenRetries} 次）：{ex.GetType().Name}");
            }

            await Task.Delay(OpenRetryDelayMs).ConfigureAwait(true);
        }

        AppLogger.Warn("剪贴板没能还原（已尝试 3 次），不影响继续使用");
    }

    /// <summary>丢掉快照（还原成功或确认不需要时调用）。</summary>
    public static void Discard() => _snapshot = null;
}
