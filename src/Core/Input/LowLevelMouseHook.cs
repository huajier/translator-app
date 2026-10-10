using System.Collections.Concurrent;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Input;

/// <summary>一次鼠标事件（主线程从队列取出后处理）。</summary>
internal readonly record struct MouseHookEvent(MouseHookKind Kind, int X, int Y);

/// <summary>
/// 我们关心的鼠标动作。
///
/// 【为什么多了 RightDown —— 2026-10-08 用户反馈】
///   用户原话：「拖到一半摁 Esc 太反人类了，把 Esc 改成右键，
///   这样拖到一半不要了还可以右键取消选中。」
///   在选区半途按右键取消，确实比去按 Esc 顺手得多：
///   右手本来就在鼠标上，不用挪到键盘。
///
///   注意：本钩子**依然什么都不吃**，右键事件照样 CallNextHookEx 放行。
///   之所以能取消成功，是因为拖到一半时屏幕上盖着遮罩层，
///   右键根本到不了下面的软件。
/// </summary>
internal enum MouseHookKind
{
    Move,
    LeftDown,
    LeftUp,
    RightDown
}

/// <summary>
/// LowLevelMouseHook —— 全局鼠标监听（阶段 2）
///
/// 这个类干嘛的：
///   用 Windows 的低级鼠标钩子（WH_MOUSE_LL）监听左键按下 / 移动 / 抬起，
///   交给状态机去判断「这是不是一次 Ctrl+Alt 框选」，以及「框选到哪儿了」。
///
/// 【和键盘钩子一样的铁律】
///   1. 回调里只排队 + 发唤醒消息 + 立刻放行（P99 &lt; 1 毫秒）；
///   2. 回调委托用 static 字段强引用，防 GC（否则用一会儿就失灵）；
///   3. 平时永远 CallNextHookEx 放行 —— 本程序**平时一个鼠标事件都不许吃**。
///      用户平时点按钮、拖文件、拉滚动条、选中文字，必须完全不受影响（规格异常表明确要求）。
///   4. **唯一的例外（2026-10-09 加）**：
///      **Ctrl 和 Alt 都按着时的左键按下**，事件在这一步终止，不交给下面的软件。
///      原因写在 MouseCallback 里那一大段注释里——
///      不吃这一次点击，底下的软件就会立刻开始选中文字，
///      而我们的遮罩是**事后才铺的**，追不上已经发出去的事件。
///
/// 【坐标是什么】
///   MSLLHOOKSTRUCT.pt 是**物理像素**、以主显示器左上角为原点、y 轴向下。
///   多显示器时副屏在左边可能出现负坐标，这是正常的，不要当成异常去「修正」。
///   WPF 用的是 DIP，两者换算统一走 DpiHelper（决策约束：业务代码不许手算比例）。
///
/// 它和谁交互：
///   - App.xaml.cs：启动时 Install，收到唤醒消息后 Drain 出来喂状态机；
///   - SelectionStateMachine：处理按下/移动/抬起，推进状态。
/// </summary>
internal static class LowLevelMouseHook
{
    /// <summary>回调委托的强引用，防 GC（极重要，删了这行钩子会「用一会儿就失灵」）。</summary>
    private static NativeMethods.LowLevelMouseProc? _proc;

    private static IntPtr _hookHandle = IntPtr.Zero;
    private static IntPtr _notifyWindow = IntPtr.Zero;

    private static readonly ConcurrentQueue<MouseHookEvent> Pending = new();

    public static bool IsInstalled => _hookHandle != IntPtr.Zero;

    /// <summary>安装鼠标钩子（必须在有消息循环的线程上调用）。</summary>
    public static bool Install(IntPtr notifyWindow)
    {
        if (IsInstalled) return true;

        _notifyWindow = notifyWindow;
        _proc = MouseCallback;

        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _proc, IntPtr.Zero, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            AppLogger.Error($"安装鼠标钩子失败，错误码={err}（常见原因：权限不足）");
            _proc = null;
            return false;
        }

        AppLogger.Info($"鼠标钩子已安装（句柄=0x{_hookHandle.ToInt64():X}），监听左键与移动");
        return true;
    }

    /// <summary>卸载鼠标钩子。程序退出前必须调。</summary>
    public static void Uninstall()
    {
        if (_hookHandle == IntPtr.Zero) return;

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        AppLogger.Info("鼠标钩子已卸载");
        _hookHandle = IntPtr.Zero;
        _proc = null;
        Pending.Clear();
    }

    /// <summary>取走队列里所有待处理的鼠标事件（只在主线程调用）。</summary>
    public static void Drain(Action<MouseHookEvent> handler)
    {
        while (Pending.TryDequeue(out MouseHookEvent evt))
        {
            handler(evt);
        }
    }

    public static int PendingCount => Pending.Count;

    /// <summary>
    /// 累计吃掉的左键按下次数（只在 Ctrl+Alt 都按着时才会吃）。
    /// 写进日志是为了能事后核对「这次到底拦住没有」，日志里看不见就说明没生效。
    /// </summary>
    public static int SwallowedLeftDown;

    /// <summary>鼠标钩子回调：只做「读坐标 + 排队 + 通知 + 放行」。</summary>
    private static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                MouseHookKind kind;
                switch (msg)
                {
                    case NativeMethods.WM_LBUTTONDOWN: kind = MouseHookKind.LeftDown; break;
                    case NativeMethods.WM_LBUTTONUP:   kind = MouseHookKind.LeftUp; break;
                    case NativeMethods.WM_RBUTTONDOWN: kind = MouseHookKind.RightDown; break;
                    case NativeMethods.WM_MOUSEMOVE:  kind = MouseHookKind.Move; break;
                    default: return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam); // 滚轮等：本程序不关心，直接放行
                }

                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<MouseHookData>(lParam);

                // ==========================================================
                // 【2026-10-09 用户报的 bug：唯一一种「吃掉事件」的情况】
                //
                //   用户原话（连着报了两次，第二次说得很准）：
                //     「bug 还在，遮罩是出现了，**但是鼠标也已经开始选中了**。」
                //
                //   上一轮我以为「遮罩晚铺 3 像素」是原因，加了 `Armed` 事件，
                //   结果**遮罩确实铺出来了，鼠标还是在选中**——**说明压根不是遮罩的问题。**
                //
                //   【真正的时序问题】
                //     钩子回调跑完之后事件**立刻**顺着 CallNextHookEx 交给下面的软件，
                //     它一收到 WM_LBUTTONDOWN 就开始画选中区了。
                //     我们是**收到事件之后**才通知主线程的，
                //     主线程再通知遮罩铺开——**遮罩铺好的时候，文字已经选中了。**
                //     遮罩是**事后响应**，追不上**已经发出去的事件**。
                //
                //   【唯一的可靠办法：这一次左键按下，不要交给下面的软件】
                //     返回非零 = 吃掉，事件到此为止，底下的软件根本不知道发生过。
                //
                //   【那「一个鼠标事件都不许吃」的铁律怎么办】
                //     那条铁律是为了保证**平时**完全不影响用户，
                //     条件是「不是本程序的手势」。而现在这个条件不成立：
                //     **Ctrl+Alt 按着的时候，这次点击本来就属于本程序**，
                //     用户按这个组合键的目的只有一个——框选取词。
                //     底下软件收到这次点击纯属误伤。
                //
                //     所以铁律**不是取消，是加一个例外**：
                //       · 平时                      → 永远放行，一件不变
                //       · **Ctrl+Alt 都按着 + 左键按下** → 吃掉
                //
                //   **只吃左键按下**，移动和抬起照常放行。
                //   · 只吃按下：文字选中是从「按下」开始的，不让它开始就足够了；
                //     而且漏一个抬起事件底下软件也不会乱（它只会收到一个没有来源的抬起）。
                //   · 不吃移动：不然用户平时按住 Ctrl+Alt 晃鼠标都会觉得卡。
                // ==========================================================
                bool comboDown = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL)
                                 && NativeMethods.IsKeyDown(NativeMethods.VK_MENU);
                bool eatThisClick = kind == MouseHookKind.LeftDown && comboDown;

                Pending.Enqueue(new MouseHookEvent(kind, data.pt.X, data.pt.Y));

                if (_notifyWindow != IntPtr.Zero)
                {
                    NativeMethods.PostMessageForWake(_notifyWindow);
                }

                if (eatThisClick)
                {
                    SwallowedLeftDown++;
                    return 1;   // 吃掉：下面的软件不会看到这次点击
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "鼠标钩子回调内部出错（已忽略）");
        }

        // 永远放行：绝不影响用户正常的鼠标操作
        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>鼠标事件结构体（字段与 Win32 MSLLHOOKSTRUCT 一致）。</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public PointData pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointData
    {
        public int X;
        public int Y;
    }
}