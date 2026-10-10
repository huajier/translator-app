using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

namespace TranslatorApp.Core.Input;

/// <summary>
/// HotkeyState —— 「Ctrl 和 Alt 此刻按着没有」的唯一说了算的地方（阶段 2）
///
/// 这个类干嘛的：
///   判断「用户是不是正在用 Ctrl+Alt 框选」，需要知道两个修饰键的实时状态。
///   状态有两个来源：
///     1. 键盘钩子告诉我们的「刚按下了 / 刚松开了」—— 准，但万一漏了一个事件就会一直错下去；
///     2. GetAsyncKeyState 直接问系统「此刻按着没有」—— 永远准，但只能问当下、拿不到事件。
///   所以做法是：用钩子事件实时更新，同时每隔一段时间用 GetAsyncKeyState 校一遍，
///   万一状态跑偏（漏事件、锁屏、切窗口）能自动纠正回来，不会出现「Ctrl 卡在按着」导致一直乱框选。
///
/// 重要：只在 UI 主线程读写（本类不做线程同步）。
///   钩子回调里**不许**直接调本类 —— 钩子只负责把事件排队，由主线程取出后再喂进来。
///
/// 它和谁交互：
///   - LowLevelKeyboardHook：把键事件排好队，主线程取出后调用 ApplyKey；
///   - SelectionStateMachine：问 IsComboDown 决定要不要进入框选；
///   - App.xaml.cs：定时调 CorrectFromSystem 做校正。
/// </summary>
internal sealed class HotkeyState
{
    private bool _ctrlDown;
    private bool _altDown;

    /// <summary>Ctrl 是否按着（左右 Ctrl 都算）。</summary>
    public bool IsCtrlDown => _ctrlDown;

    /// <summary>Alt 是否按着（左右 Alt 都算）。</summary>
    public bool IsAltDown => _altDown;

    /// <summary>Ctrl 和 Alt 同时按着 —— 这才是「准备框选」的信号。</summary>
    public bool IsComboDown => _ctrlDown && _altDown;

    /// <summary>
    /// 喂一个按键事件进来（由主线程调用，不是钩子线程）。
    /// 非修饰键直接忽略——本类只关心 Ctrl / Alt / Shift。
    /// </summary>
    public void ApplyKey(int virtualKey, bool isDown)
    {
        switch (virtualKey)
        {
            case NativeMethods.VK_CONTROL:
            case NativeMethods.VK_LCONTROL:
            case NativeMethods.VK_RCONTROL:
                _ctrlDown = isDown;
                break;

            case NativeMethods.VK_MENU:
            case NativeMethods.VK_LMENU:
            case NativeMethods.VK_RMENU:
                _altDown = isDown;
                break;

            case NativeMethods.VK_SHIFT:
            case NativeMethods.VK_LSHIFT:
            case NativeMethods.VK_RSHIFT:
                // Shift 暂不参与判断，记一下是为了以后要加「Ctrl+Shift+Alt」时不用改结构
                break;

            default:
                // 其它按键与本类无关
                break;
        }
    }

    /// <summary>
    /// 用系统实时状态校正一遍，防止状态跑偏。
    /// 返回 true 表示发现并纠正了不一致（会写一行日志，方便排查）。
    /// </summary>
    public bool CorrectFromSystem()
    {
        bool ctrlNow = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        bool altNow = NativeMethods.IsKeyDown(NativeMethods.VK_MENU);

        bool changed = false;
        if (ctrlNow != _ctrlDown)
        {
            AppLogger.Info($"修饰键状态校正：Ctrl {_ctrlDown} → {ctrlNow}（说明漏了一次按键事件）");
            _ctrlDown = ctrlNow;
            changed = true;
        }
        if (altNow != _altDown)
        {
            AppLogger.Info($"修饰键状态校正：Alt {_altDown} → {altNow}（说明漏了一次按键事件）");
            _altDown = altNow;
            changed = true;
        }
        return changed;
    }

    /// <summary>一键清空（程序退出、或者发生取消时用，防止残留状态）。</summary>
    public void Reset()
    {
        _ctrlDown = false;
        _altDown = false;
    }

    /// <summary>给日志看的状态快照。</summary>
    public override string ToString()
        => $"Ctrl={(_ctrlDown ? "按着" : "松开")}, Alt={(_altDown ? "按着" : "松开")}";
}