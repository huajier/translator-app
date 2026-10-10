using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TranslatorApp.Core.Input;
using TranslatorApp.Core.Native;

// RECT 是 NativeMethods 内部嵌套的结构体，using 命名空间引不到嵌套类型，要写别名。
using RECT = TranslatorApp.Core.Native.NativeMethods.RECT;

namespace TranslatorApp.UI.SelectionOverlay;

/// <summary>
/// SelectionMaskWindow —— 覆盖所有显示器的半透明遮罩 + 亮色选框（阶段 2）
///
/// 这个类干嘛的：
///   用户按住 Ctrl+Alt 拖鼠标时，本窗口盖满整个「虚拟屏幕」（多显示器时是一整块大矩形，
///   可能包含负坐标），上面画一个跟着鼠标走的亮色框；松手或取消时立刻隐藏。
///
/// 【三条必须做到的】
///   1. **绝不能挡住用户操作**：加 WS_EX_TRANSPARENT（鼠标穿透）+ WS_EX_NOACTIVATE（不抢焦点）
///      + WS_EX_TOOLWINDOW（不进 Alt-Tab）+ WS_EX_LAYERED（透明）。
///      漏了任何一条，用户会发现「框选之后鼠标点不动别的软件了」——这是灾难级 bug。
///   2. **必须盖住虚拟屏而不是主屏**：用户在第二块屏上框选时，遮罩也得在那块屏上。
///   3. **用 Show() 不用 Activate()**：Activate 会把焦点抢走，用户没法继续打字。
///
/// 【坐标系：这是最容易出错的地方】
///   - 钩子给的矩形是「物理像素」，原点在主屏左上角；
///   - WPF 定位窗口用的是「DIP」；
///   - 所以：窗口本身按虚拟屏左上角（DIP）定位；选框的位置要减去窗口左上角，再按 DPI 换算。
///   全部换算走 DpiHelper，业务代码不许手算比例。
///
/// 它和谁交互：SelectionStateMachine 通过 App.xaml.cs 订阅 DragStarted / DragUpdated / Cancelled。
/// </summary>
public partial class SelectionMaskWindow : Window
{
    /// <summary>虚屏左上角（DIP），选框相对位置用它做基准。</summary>
    private double _virtualLeftDip;
    private double _virtualTopDip;
    private double _dpi = DpiHelper.DefaultDpi;

    /// <summary>构造：先把自己藏起来，没被要求显示之前绝不出现。</summary>
    public SelectionMaskWindow()
    {
        InitializeComponent();

        // 一上来就藏好：没人在框选时，这个窗口必须对用户完全不存在
        Root.Visibility = Visibility.Collapsed;
    }

    /// <summary>窗口句柄（OnSourceInitialized 之后才有值，之前是 0）。</summary>
    private IntPtr _hwnd;

    /// <summary>当前是否处于「接管鼠标」状态。</summary>
    private bool _mouseBlocking;

    /// <summary>
    /// 窗口句柄创建之后：先按「平时」的样式设置好（穿透、不抢焦点）。
    ///
    /// 【重要 · 阶段 2 验收后按用户要求改过】
    ///   原来遮罩**一直**带 WS_EX_TRANSPARENT（鼠标穿透），结果框选时下面的窗口照样能拖、能选文字。
    ///   用户的原话：「我要像截图一样，框选时下方的窗口不能互动但是看得见」。
    ///   改成**按需切换**：
    ///     - 没在框选时：保持穿透，用户平时的操作一点不受影响；
    ///     - 正在框选时：摘掉穿透位，遮罩接管鼠标，下面的窗口看得见但点不动。
    ///   关于规格冲突：规格「不许做的事」写的是遮罩绝不能吃掉鼠标事件，
    ///   那条的**本意是「不按 Ctrl+Alt 时不许干扰用户」**；现在改成「只有正在框选的那一瞬间才接管」，
    ///   两者并不冲突。已按 R5 记进 tools\日志\决策执行记录.md 的偏差表。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyMouseBlocking(mouseBlocking: false);
    }

    /// <summary>
    /// 切换遮罩要不要接管鼠标。
    ///   true  = 摘掉 WS_EX_TRANSPARENT → 遮罩吃点击，下面的窗口拖不动、选不了字（框选中）
    ///   false = 加回 WS_EX_TRANSPARENT → 鼠标穿透，绝不影响用户（平时 / 框选结束）
    /// </summary>
    private void ApplyMouseBlocking(bool mouseBlocking)
    {
        if (_hwnd == IntPtr.Zero) return;

        _mouseBlocking = mouseBlocking;

        // 读出当前样式，改完再写回（不能直接覆盖，会把 WPF / 系统已设的样式冲掉）
        IntPtr current = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        long style = current.ToInt64()
            | NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TOOLWINDOW   // 不出现在 Alt-Tab
            | NativeMethods.WS_EX_NOACTIVATE;  // 不抢走焦点（用户正在打字也不受影响）

        style = mouseBlocking
            ? style & ~NativeMethods.WS_EX_TRANSPARENT   // ← 关键：摘掉穿透，遮罩接管点击
            : style | NativeMethods.WS_EX_TRANSPARENT;   // ← 平时穿透

        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        Core.Logging.AppLogger.Info(mouseBlocking
            ? "遮罩已接管鼠标（框选中：下面的窗口看得见但点不动）"
            : "遮罩已释放鼠标（平时：完全穿透，不影响任何操作）");
    }

    /// <summary>按当前虚拟屏和 DPI 重新铺满整个屏幕。每次显示前都要调（用户可能改了分辨率/缩放）。</summary>
    public void AlignToVirtualScreen()
    {
        var rect = ScreenGeometry.GetVirtualScreen();
        var (monitorRect, dpi) = ScreenGeometry.GetMonitorAt(rect.Left, rect.Top);
        _dpi = dpi;

        Left = DpiHelper.PhysicalToDip(rect.Left, dpi);
        Top = DpiHelper.PhysicalToDip(rect.Top, dpi);
        Width = DpiHelper.PhysicalToDip(rect.Right - rect.Left, dpi);
        Height = DpiHelper.PhysicalToDip(rect.Bottom - rect.Top, dpi);

        _virtualLeftDip = Left;
        _virtualTopDip = Top;

        Core.Logging.AppLogger.Info(
            $"遮罩已铺满：屏幕=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})，"
            + $"窗口 Left={Left:0.##} Top={Top:0.##} 宽={Width:0.##} 高={Height:0.##}，{DpiHelper.Describe(dpi)}");
    }

    /// <summary>把选框彻底清空：不但隐藏，连位置和尺寸一起归零。</summary>
    private void ClearSelectionBox()
    {
        SelectionBox.Visibility = Visibility.Collapsed;
        SizeHint.Visibility = Visibility.Collapsed;

        // 宽高位置也一起清掉：只隐藏的话，旧框的几何还留在那儿。
        // 用户反馈的正是这个——「再次框选时上一次的蓝框在原位置闪一下，约 0.2 秒」。
        SelectionBox.Width = 0;
        SelectionBox.Height = 0;
        Canvas.SetLeft(SelectionBox, 0);
        Canvas.SetTop(SelectionBox, 0);
        SizeHintText.Text = string.Empty;
    }

    /// <summary>遮罩当前是否显示中。注意：**不能用 IsVisible**，那个代表窗口句柄本身。</summary>
    private bool _overlayActive;

    /// <summary>窗口句柄是否已经创建过——只 Show() 一次，之后永远不 Hide()。</summary>
    private bool _windowCreated;

    /// <summary>
    /// 显示遮罩并接管鼠标（正在框选）。
    ///
    /// 【为什么不再用 Hide() / Show() 开关遮罩 —— 这是修「旧蓝框闪现」的关键】
    ///   原来 EndOverlay 用 Hide()、BeginOverlay 用 Show()。窗口一隐藏再显示，
    ///   分层窗口（AllowsTransparency=True）的画面表面会被重建，
    ///   **上一次画上去的东西有机会被重新摆到屏幕上**——
    ///   用户看到的正是这个：再次框选时，上一次的蓝框在原位置闪一下，约 0.2 秒。
    ///
    ///   期间查过日志、加过诊断、让程序自己渲染那一刻的画面：
    ///   逻辑侧全是干净的（第二次框选第一个画的就是新位置的小框，从没画过旧框），
    ///   所以问题不在「谁画了旧框」，而在「旧画面被重新摆了出来」。
    ///   前面试过「先隐藏再 UpdateLayout 再显示」，**用户实测依然复现**，
    ///   说明这个方向不对，于是换成根本不会犯这类毛病的做法。
    ///
    ///   现在：**窗口句柄只创建一次、之后永不隐藏**，
    ///   显示/隐藏只切根节点 Root 的 Visibility——
    ///     Root 显示 → 灰罩和选框照常出现；
    ///     Root 折叠 → 整个窗口渲染成全透明，配合 WS_EX_TRANSPARENT 鼠标照常穿透，
    ///                 用户看到的画面和「窗口不存在」完全一样。
    ///   没有画面表面被重建这个环节，这类闪现从根上消失。
    /// </summary>
    public void BeginOverlay()
    {
        AlignToVirtualScreen();

        // 顺序很重要：先清空旧框，再把内容显示出来。反过来的话，
        // 会带着上一次的位置和大小先渲染一帧。
        ClearSelectionBox();

        // 窗口句柄只在这里 Show() 一次，之后永远不隐藏。
        if (!_windowCreated)
        {
            // 用 Show() 不用 Activate()：Activate 会抢走焦点，用户就没法继续打字了
            Show();
            _windowCreated = true;
        }

        Root.Visibility = Visibility.Visible;
        _overlayActive = true;

        // 只有状态真的变了才动窗口样式并写日志，避免一次框选刷出好几行重复记录
        if (!_mouseBlocking) ApplyMouseBlocking(mouseBlocking: true);
    }
    /// <summary>更新选框位置与尺寸（参数是物理像素矩形）。</summary>
    /// <summary>更新选框位置与尺寸（参数是物理像素矩形）。</summary>
    internal void UpdateSelection(RECT rect)
    {
        if (!_overlayActive || !_mouseBlocking) BeginOverlay();

        // 物理像素 → 窗口内的 DIP（减掉窗口原点，再按 DPI 换算）
        double x = DpiHelper.PhysicalToDip(rect.Left, (uint)_dpi) - _virtualLeftDip;
        double y = DpiHelper.PhysicalToDip(rect.Top, (uint)_dpi) - _virtualTopDip;
        double w = DpiHelper.PhysicalToDip(rect.Right - rect.Left, (uint)_dpi);
        double h = DpiHelper.PhysicalToDip(rect.Bottom - rect.Top, (uint)_dpi);

        Canvas.SetLeft(SelectionBox, x);
        Canvas.SetTop(SelectionBox, y);
        SelectionBox.Width = w;
        SelectionBox.Height = h;
        SelectionBox.Visibility = Visibility.Visible;

        int pxWidth = rect.Right - rect.Left;
        int pxHeight = rect.Bottom - rect.Top;
        SizeHintText.Text = $"{pxWidth} × {pxHeight}";

        // 提示框放在选框下方；顶到屏幕底部就改放上方，避免跑出屏幕看不见
        double hintY = y + h + 4;
        if (y + h + 26 > Height) hintY = Math.Max(0, y - 24);
        Canvas.SetLeft(SizeHint, x);
        Canvas.SetTop(SizeHint, hintY);
        SizeHint.Visibility = Visibility.Visible;
    }

    /// <summary>隐藏遮罩、释放鼠标（框选完成或取消）。</summary>
    public void EndOverlay()
    {
        // 清掉旧框，再把整个内容折叠起来（窗口本身不隐藏，理由见 BeginOverlay 的说明）
        ClearSelectionBox();
        Root.Visibility = Visibility.Collapsed;
        _overlayActive = false;

        // 内容收起之后立刻恢复鼠标穿透：万一有残留，下一次框选之前也不会挡住用户
        if (_mouseBlocking) ApplyMouseBlocking(mouseBlocking: false);
    }
}