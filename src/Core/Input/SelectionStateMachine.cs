using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

// RECT 是 NativeMethods 内部嵌套的结构体，using 命名空间引不到嵌套类型，要写别名。
using RECT = TranslatorApp.Core.Native.NativeMethods.RECT;

namespace TranslatorApp.Core.Input;

/// <summary>框选状态机的三个状态。</summary>
internal enum SelectionState
{
    /// <summary>空闲：没有在框选。</summary>
    Idle,

    /// <summary>已按下：Ctrl+Alt 按着、左键也按下了，但还只按着一个点、没拖动开。</summary>
    Armed,

    /// <summary>拖拽中：已经拖动超过阈值，屏幕上正在画选框。</summary>
    Dragging
}

/// <summary>
/// SelectionStateMachine —— 判断「用户是不是在框选、框到哪儿了、什么时候算完成」（阶段 2 的核心）
///
/// 【状态怎么走】
///   空闲 --(Ctrl+Alt 按着 + 左键按下)--> 已按下 --(移动超过 3 像素)--> 拖拽中
///                                                                  --(左键抬起)--> 完成
///   任意状态 --(松开 Ctrl 或 Alt / 按 Esc / 拖太久)--> 空闲（取消，什么都不留下）
///
/// 【为什么要这样设计 —— 每一条都是踩过的坑】
///   1. 「已按下」和「拖拽中」要分开：否则用户只是 Ctrl+Alt 点一下（没拖）也会闪一个框。
///      规格要求框不到 10×10 像素就算误触、什么都不发生。
///   2. 松开 Ctrl/Alt 必须**立即取消**，不能等松鼠标。否则用户以为取消了，
///      松手后却照样弹出结果（规格异常表第一条）。
///   3. 抬起时用**抬起那一刻的坐标**当终点，不能假设鼠标移动事件都收到了：
///      快速甩鼠标时系统会合并/丢弃移动事件（规格第 23 条）。
///   4. 拖拽超过 60 秒强制回空闲：防止用户按下 Ctrl+Alt+左键后去泡茶，
///      回来松手时突然弹出一个巨大的选区（规格第 25 条）。
///   5. 坐标一律钳制在屏幕内：鼠标拖出屏幕边缘时不能算出负数宽高把界面搞崩。
///
/// 【线程】只在 UI 主线程调用。钩子回调里绝对不许直接调用本类。
///
/// 它和谁交互：
///   - App.xaml.cs：从钩子队列取出事件后喂进来，并定时调 Tick()；
///   - SelectionMaskWindow：通过下面四个事件显示/更新/隐藏选框；
///   - SelectionCoordLog：完成时写一行坐标日志。
/// </summary>
internal sealed class SelectionStateMachine
{
    /// <summary>小于这个尺寸认为是误触（规格：小于 10×10 像素忽略）。</summary>
    public const int MinSelectionPixels = 10;

    /// <summary>移动超过这么多像素才算「开始拖拽」（3 像素，用来过滤手抖）。</summary>
    public const int DragStartThresholdPixels = 3;

    /// <summary>拖拽超过这么久强制取消（防状态卡死）。</summary>
    public static readonly TimeSpan MaxDragDuration = TimeSpan.FromSeconds(60);

    private readonly HotkeyState _hotkeys;

    private SelectionState _state = SelectionState.Idle;
    private int _startX;
    private int _startY;
    private int _lastX;
    private int _lastY;
    private DateTime _armedAt;

    /// <summary>
    /// 左键按下且组合键按着（遮罩该立刻铺上了，但还没画出选框）。
    ///
    /// 【2026-10-09 用户报的 bug：因为缺这个事件，遮罩晚铺了 3 像素】
    ///
    ///   用户原话：「当你摁住 ctrl+alt+左键框选识别后不松开 ctrl 和 alt，
    ///   再次摁左键再次框选，鼠标会错误的可以选中文本，
    ///   应该是没有灰色遮罩导致的。」
    ///
    ///   **用户的判断完全正确。** 本程序有一条铁律：
    ///   `LowLevelMouseHook` **永远放行，一个鼠标事件都不吃**。
    ///   所以「不让下面的软件选中文字」这件事，**全靠遮罩窗口挡着**。
    ///
    ///   而原来遮罩只在 `DragStarted` 里铺（也就是**鼠标移动超过 3 像素之后**）：
    ///
    ///     按下左键 ──── 遮罩还没铺 ──── 移动 3 像素 ──── 遮罩铺上
    ///                    ↑
    ///             这个空档里点击直接落到下面的软件
    ///             → 双击选中 / 拖到文字上选中
    ///
    ///   第一次框选之所以没暴露，是因为**用户通常会先动一下鼠标**，
    ///   空档被掩盖了。而「不松开 Ctrl+Alt 直接框第二次」时，
    ///   用户的手本来就停在原地不动，**空档必然被撞上**。
    ///
    ///   修法：**左键一按下去、确认组合键按着，就立刻把遮罩铺上**
    ///   （此时不画选框，等拖开 3 像素再画）。
    /// </summary>
    public event Action? Armed;

    /// <summary>进入拖拽（遮罩该出现了）。参数是物理像素矩形。</summary>
    public event Action<RECT>? DragStarted;

    /// <summary>拖拽中选框移动了（参数是物理像素矩形）。</summary>
    public event Action<RECT>? DragUpdated;

    /// <summary>取消了（遮罩该消失了）。</summary>
    public event Action? Cancelled;

    /// <summary>框选完成。</summary>
    public event Action<SelectionSession>? Completed;

    public SelectionStateMachine(HotkeyState hotkeys)
    {
        _hotkeys = hotkeys;
    }

    /// <summary>当前状态（给日志和界面用）。</summary>
    public SelectionState State => _state;

    /// <summary>喂一个按键事件进来（主线程）。</summary>
    public void HandleKey(int virtualKey, bool isDown)
    {
        // 【2026-10-08 用户反馈】用户把「取消」从 Esc 改成了**右键**（见 HandleMouse）。
        //
        //   Esc 这里**仍然保留**为备用。用户说的是「改成右键」，没说「Esc 不许用」；
        //   多留一条取消途径不碍事，万一右手离开鼠标时 Esc 反而更方便。
        //   要彻底去掉的话，把下面这段删掉即可。
        if (isDown && virtualKey == NativeMethods.VK_ESCAPE)
        {
            if (_state != SelectionState.Idle)
            {
                Cancel("按下 Esc 取消框选");
            }
            return;
        }

        _hotkeys.ApplyKey(virtualKey, isDown);

        // ==========================================================
        // 【2026-10-09 用户反馈后改：拖到一半松开 Ctrl / Alt 不再取消】
        //
        //   原来这里是「规格异常表」里的一条，一松开就立刻 Cancel。
        //   听起来合理，实际用起来非常难受——用户 2026-10-09 实测时说：
        //     「识别变得不灵敏了，E3、E4 那边识别了两次才识别出来，
        //       第一次就是直接没弹识别框」。
        //
        //   日志证实了原因：
        //     `框选取消：中途松开了 Ctrl 或 Alt`   ← 整个这一轮只出现了 2 次，
        //     而这两次就是 E3、E4 的「第一次」。
        //
        //   【为什么这不是识别的问题，而是手势判定太苛刻】
        //     人的手指在拖框过程中**不可避免地会有一点点抖动**，
        //     Ctrl/Alt 是**按住 + 拖鼠标**两个动作同时做的，
        //     一边拖一边松是很自然的事。
        //     把这种抖动判成「取消」，用户只会觉得「不灵敏」，
        //     **根本想不到是自己手指抖了一下**。
        //
        //   【现在怎么取消】
        //     ① **右键**（用户 2026-10-08 亲自定的，见 HandleMouse）
        //     ② **Esc**（下面那段保留着）
        //     ③ **松开鼠标左键**但框太小（本来就有的规则）
        //     ④ 现在还多一条：**左键先松、且框选区域过小** → 取消
        //
        //     四条取消途径一条不少，**松手指不再算取消**。
        //
        //   【注意：这里只删「松开组合键就取消」这一条】
        //     `_hotkeys.ApplyKey` 仍然照常调用——组合键的状态还是要记的，
        //     否则左键按下时判断不出「此刻组合键是不是按着的」。
        // ==========================================================
    }

    /// <summary>喂一个鼠标事件进来（主线程）。</summary>
    public void HandleMouse(MouseHookEvent evt)
    {
        switch (evt.Kind)
        {
            case MouseHookKind.LeftDown:
                OnLeftDown(evt.X, evt.Y);
                break;

            case MouseHookKind.Move:
                OnMouseMove(evt.X, evt.Y);
                break;

            case MouseHookKind.LeftUp:
                OnLeftUp(evt.X, evt.Y);
                break;

            // 【2026-10-08 用户反馈】「拖到一半摁 Esc 太反人类了，把 Esc 改成右键」
            //   右手本来就在鼠标上，取消不用挪到键盘——这个反馈很对，采纳。
            case MouseHookKind.RightDown:
                if (_state != SelectionState.Idle)
                {
                    Cancel("按下右键取消框选");
                }
                break;
        }
    }

    /// <summary>定时自检：防止状态卡死。由主线程的定时器每秒调一次。</summary>
    public void Tick()
    {
        if (_state == SelectionState.Idle) return;

        if (DateTime.Now - _armedAt > MaxDragDuration)
        {
            Cancel("拖拽超时（超过 60 秒）");
        }
    }

    // ==================================================================== 内部

    private void OnLeftDown(int x, int y)
    {
        if (!_hotkeys.IsComboDown)
        {
            // 没按 Ctrl+Alt：这是一次普通的左键点击，本程序**什么都不做**，
            // 事件会原样传给下面的软件（鼠标钩子已经放行了）。
            _state = SelectionState.Idle;
            return;
        }

        var (cx, cy) = ScreenGeometry.ClampToVirtualScreen(x, y);
        _startX = cx;
        _startY = cy;
        _lastX = cx;
        _lastY = cy;
        _armedAt = DateTime.Now;
        _state = SelectionState.Armed;

        // 【2026-10-09】遮罩在这一刻就铺上，不要等移动 3 像素之后。
        // 详细原因见 Armed 事件的说明。少了这一句，
        // 「不松开 Ctrl+Alt 直接框第二次」时鼠标会漏给下面的软件去选中文字。
        Armed?.Invoke();
    }

    private void OnMouseMove(int x, int y)
    {
        if (_state == SelectionState.Idle) return;

        var (cx, cy) = ScreenGeometry.ClampToVirtualScreen(x, y);
        _lastX = cx;
        _lastY = cy;

        if (_state == SelectionState.Armed)
        {
            int dx = Math.Abs(cx - _startX);
            int dy = Math.Abs(cy - _startY);
            if (dx < DragStartThresholdPixels && dy < DragStartThresholdPixels)
            {
                // 只是手抖，还没拖开，继续等
                return;
            }
            _state = SelectionState.Dragging;
            DragStarted?.Invoke(BuildRect(_startX, _startY, cx, cy));
        }

        DragUpdated?.Invoke(BuildRect(_startX, _startY, cx, cy));
    }

    private void OnLeftUp(int x, int y)
    {
        if (_state == SelectionState.Idle) return;

        // 规格第 23 条：抬起时以抬起点为准。快速甩鼠标时移动事件会被系统合并/丢弃，
        // 用最后一次收到的移动位置会算错。
        var (cx, cy) = ScreenGeometry.ClampToVirtualScreen(x, y);
        _lastX = cx;
        _lastY = cy;

        RECT rect = BuildRect(_startX, _startY, _lastX, _lastY);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        bool bigEnough = width >= MinSelectionPixels && height >= MinSelectionPixels;

        _state = SelectionState.Idle;

        if (!bigEnough)
        {
            Cancelled?.Invoke();
            SelectionCoordLog.RecordNote($"忽略一次太小的框选：{width}×{height}（小于 {MinSelectionPixels}×{MinSelectionPixels}，判定为误触）");
            return;
        }

        // ==========================================================
        // 【2026-10-09 用户反馈后补：抬起左键时，组合键必须还按着】
        //
        //   背景是两步走：
        //     · 上一轮用户说「不灵敏，E3、E4 第一次没弹框」，
        //       查日志是 `框选取消：中途松开了 Ctrl 或 Alt` ——**取消发生在松键那一刻**，
        //       用户只是手指抖了一下，框就白拖了。
        //       于是把「松 Ctrl/Alt 就取消」这条整个删掉了。
        //     · 这一轮用户说：**「我框选后松开 Ctrl 或者 Alt 键，
        //       按道理来说应该不识别的，但是松开左键后却识别出来了，这是个 bug。」**
        //
        //   上一轮改过头了：删得太干净，把「松键就该作废」这条规矩也一起丢了。
        //
        //   【正确的规矩是什么】
        //     用户的意思很清楚——**松 Ctrl/Alt 就是「我不要这次框选」的意思**，
        //     只是**取消这件事应该等到松开左键那一刻再执行**，而不是立刻打断拖框。
        //     换句话说：**判断的时机错了，判断本身没错。**
        //
        //     · 拖框过程中松 Ctrl/Alt → **先不打断**（免得手指一抖就废掉一次拖框）
        //     · 松开左键的那一刻看：组合键还在吗？
        //         在   → 正常识别
        //         不在 → 取消，不识别
        //
        //   这样两头都照顾到了：
        //     手指抖一下但马上扶回去 → 不影响；
        //     真的松开了 → 用户明确表达的「作废」被尊重。
        //
        //   日志证据（这一轮的取词记录里混进了两条没用的）：
        //     `KESL`                       ← 本该作废的那次框选，结果照跑了
        //     `workbuddy cpenccée`         ← 同上
        // ==========================================================
        if (!_hotkeys.IsComboDown)
        {
            Cancelled?.Invoke();
            SelectionCoordLog.RecordNote(
                "松开左键时 Ctrl/Alt 已经不在了（拖框过程中松开过）——按取消处理，不识别");
            return;
        }

        var (monitorRect, dpi) = ScreenGeometry.GetMonitorAt(rect.Left, rect.Top);
        double dipLeft = DpiHelper.PhysicalToDip(rect.Left, dpi);
        double dipTop = DpiHelper.PhysicalToDip(rect.Top, dpi);
        double dipWidth = DpiHelper.PhysicalToDip(width, dpi);
        double dipHeight = DpiHelper.PhysicalToDip(height, dpi);

        var session = new SelectionSession
        {
            StartedAt = _armedAt,
            EndedAt = DateTime.Now,
            PhysicalRect = rect,
            DipRect = (dipLeft, dipTop, dipWidth, dipHeight),
            MonitorRect = monitorRect,
            MonitorDpi = dpi,
            TriggerMode = "Ctrl+Alt 拖拽"
        };

        SelectionCoordLog.Record(new SelectionCoordInfo
        {
            Timestamp = DateTime.Now,
            TriggerMode = session.TriggerMode,
            PhysicalLeft = rect.Left,
            PhysicalTop = rect.Top,
            PhysicalRight = rect.Right,
            PhysicalBottom = rect.Bottom,
            PhysicalWidth = width,
            PhysicalHeight = height,
            DipLeft = dipLeft,
            DipTop = dipTop,
            DipWidth = dipWidth,
            DipHeight = dipHeight,
            MonitorLeft = monitorRect.Left,
            MonitorTop = monitorRect.Top,
            MonitorRight = monitorRect.Right,
            MonitorBottom = monitorRect.Bottom,
            Dpi = dpi,
            DurationMs = session.DurationMs
        });

        AppLogger.Info($"框选完成：{session}");
        Completed?.Invoke(session);
    }

    private void Cancel(string reason)
    {
        bool wasActive = _state != SelectionState.Idle;
        _state = SelectionState.Idle;

        if (wasActive)
        {
            AppLogger.Info($"框选取消：{reason}");
            Cancelled?.Invoke();
        }
    }

    private static RECT BuildRect(int x1, int y1, int x2, int y2)
        => DpiHelper.Normalize(new RECT { Left = x1, Top = y1, Right = x2, Bottom = y2 });
}