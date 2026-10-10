using System.Collections.Concurrent;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Input;

/// <summary>一次按键事件（已经由主线程从队列里取出来了，可以放心慢慢处理）。</summary>
internal readonly record struct KeyHookEvent(int VirtualKey, bool IsDown);

/// <summary>
/// LowLevelKeyboardHook —— 全局键盘监听（阶段 2）
///
/// 这个类干嘛的：
///   用 Windows 的低级键盘钩子（WH_KEYBOARD_LL）监听**整个系统**的按键，
///   只关心三件事：Ctrl 按下/松开、Alt 按下/松开、Esc 按下（用来取消框选）。
///
/// 【为什么回调里只排队、不处理 —— 规格硬指标 P99 &lt; 1 毫秒】
///   系统把每一次按键都先交给这个回调，回调慢了整个系统都会卡（鼠标会「顿」一下）。
///   所以回调里只做三件极快的事：
///     1. 把事件塞进队列（ConcurrentQueue，线程安全，纳秒级）；
///     2. 给主窗口发一条 HookEventMessage；
///     3. 立刻 CallNextHookEx 把事件原样放行。
///   真正处理（更新状态、取消框选）由主线程收到消息后做。
///
/// 【绝不能吃掉事件】
///   本回调返回的永远是 CallNextHookEx 的结果，也就是「放行」。
///   如果这里返回非 0 就等于告诉系统「这个键我处理了，你别往下传」，
///   用户会发现打字打不进去 —— 这是最严重的坑，所以本类根本不给「吃掉」的机会。
///
/// 【委托必须用字段长期持有】
///   如果回调委托是临时 new 出来的局部变量，用完就被垃圾回收，钩子会在「用一会儿」后
///   莫名其妙失灵（这是这类 bug 的头号原因）。所以 _proc 必须是 static 字段。
///
/// 它和谁交互：
///   - App.xaml.cs：启动时 Install，收到 HookEventMessage 时 Drain 后喂给 HotkeyState / 状态机；
///   - 退出时 Uninstall。
/// </summary>
internal static class LowLevelKeyboardHook
{
    /// <summary>回调委托的强引用。绝不能让它被 GC 回收，否则钩子会静默失灵。</summary>
    private static NativeMethods.LowLevelKeyboardProc? _proc;

    private static IntPtr _hookHandle = IntPtr.Zero;
    private static IntPtr _notifyWindow = IntPtr.Zero;

    /// <summary>待处理事件队列（钩子线程写、主线程读）。</summary>
    private static readonly ConcurrentQueue<KeyHookEvent> Pending = new();

    /// <summary>钩子是否处于已安装状态。</summary>
    public static bool IsInstalled => _hookHandle != IntPtr.Zero;

    /// <summary>
    /// 安装键盘钩子。必须在有消息循环的线程上调用（UI 主线程）。
    /// notifyWindow：收到消息后被唤醒去取事件的那个窗口（主窗口）。
    /// 返回 false 表示装不上，调用方必须报错并停止，别装作成功。
    /// </summary>
    public static bool Install(IntPtr notifyWindow)
    {
        if (IsInstalled) return true;

        _notifyWindow = notifyWindow;
        _proc = KeyboardCallback;

        // dwThreadId = 0：全局钩子（整个系统），这正是我们要的。
        // hMod 传 IntPtr.Zero：本进程自己的回调不需要模块句柄。
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            AppLogger.Error($"安装键盘钩子失败，错误码={err}（常见原因：权限不足）");
            _proc = null;
            return false;
        }

        AppLogger.Info($"键盘钩子已安装（句柄=0x{_hookHandle.ToInt64():X}），监听 Ctrl / Alt / Esc");
        return true;
    }

    /// <summary>卸载键盘钩子。程序退出前必须调。</summary>
    public static void Uninstall()
    {
        if (_hookHandle == IntPtr.Zero) return;

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        AppLogger.Info("键盘钩子已卸载");
        _hookHandle = IntPtr.Zero;
        _proc = null;
        Pending.Clear();
    }

    /// <summary>取走队列里所有待处理的按键事件（只在主线程调用）。</summary>
    public static void Drain(Action<KeyHookEvent> handler)
    {
        while (Pending.TryDequeue(out KeyHookEvent evt))
        {
            handler(evt);
        }
    }

    /// <summary>队列里还有没有事件没处理（调试用）。</summary>
    public static int PendingCount => Pending.Count;

    /// <summary>
    /// 键盘钩子的回调。系统在线程上调用它（我们装在 UI 主线程，所以就是主线程）。
    /// 【这里只有 5 行是有意为之的：越短越好，越快返回越好】
    /// </summary>
    private static IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // nCode < 0 说明出错了，直接放行
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool isUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    var info = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHookData>(lParam);
                    if (IsWatchedKey((int)info.vkCode))
                    {
                        Pending.Enqueue(new KeyHookEvent((int)info.vkCode, isDown));
                        if (_notifyWindow != IntPtr.Zero)
                        {
                            NativeMethods.PostMessageForWake(_notifyWindow);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 回调里绝不能抛出去，异常穿回系统会引发不可控的后果。这里吞掉并记日志。
            AppLogger.Error(ex, "键盘钩子回调内部出错（已忽略）");
        }

        // 永远放行：不吃用户的任何按键
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>只监听这几个键，其它键连队列都不进（越少越快）。</summary>
    ///
    /// 🔴【2026-10-10 修：`Ctrl+Alt+D` 按了没反应】
    ///
    ///   **原来这里漏了 `VK_D`（0x44）**：
    ///   `App.xaml.cs` 里判断的是 `evt.VirtualKey == 0x44`，
    ///   但 **D 键的按下事件在这个名单里，连队列都进不去**，
    ///   所以那个判断**永远不可能为真**——按多少次都没用，**日志里连一行都没有**。
    ///
    ///   **典型的「代码写了、看起来没问题、就是永远执行不到」**：
    ///   判断语句本身是对的，加载名单的时候忘了它。
    ///
    /// 【为什么要单独盯 D】
    ///   `D` 是调试截图热键 `Ctrl+Alt+D` 的最后一个键，
    ///   只有「前面判断 `Hotkeys.IsComboDown`」成立时才会被当回事。
    ///   **光按 D（不按 Ctrl+Alt）不会触发任何操作**，
    ///   所以这里多盯一个键，**不会影响用户正常打字**。
    private static bool IsWatchedKey(int vkCode) => vkCode
        is NativeMethods.VK_CONTROL
        or NativeMethods.VK_LCONTROL
        or NativeMethods.VK_RCONTROL
        or NativeMethods.VK_MENU
        or NativeMethods.VK_LMENU
        or NativeMethods.VK_RMENU
        or NativeMethods.VK_ESCAPE
        or NativeMethods.VK_D;

    /// <summary>
    /// 钩子回调里用的键盘事件结构体副本。
    /// 单独定义一份（和 NativeMethods 里的 public 版本字段一致），回调里少一层跨程序集访问。
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KBDLLHookData
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}