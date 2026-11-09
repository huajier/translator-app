using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TranslatorApp.Core.Input;
using TranslatorApp.Core.Native;
// 为了让 translation.Failure.ToUserMessage() 这个扩展方法找得到（T3a 加的，别删）
using TranslatorApp.Core.Translate;

// RECT 是嵌套在 NativeMethods 里的结构体，得用 using 别名单独引进来
using RECT = TranslatorApp.Core.Native.NativeMethods.RECT;

namespace TranslatorApp.UI.Shared;

/// <summary>
/// DebugToastWindow —— 框选完成后弹出来、显示坐标的小提示框（阶段 2）
///
/// 这个类干嘛的：
///   把 SelectionStateMachine 算出来的坐标显示在屏幕上看一眼，方便人核对对不对。
///   3 秒自动消失，不打扰用户。
///
/// 为什么用 VisualTreeHelper 量尺寸：
///   窗口内容的大小要先把 Measure 跑一遍才知道，而 AutoSize 模式下
///   定位代码拿不到高度就没法把窗口放在「选框的右边/下边」。
///   这里先 Measure(0,0) 再 Arrange 一次拿到 DesiredSize，是 WPF 里量窗口内容的标准做法。
///
/// 它和谁交互：App.xaml.cs 在框选完成后调 ShowFor(session)。
/// </summary>
public partial class DebugToastWindow : Window
{
    /// <summary>提示框在屏幕上的边距（名字里带 Screen，避免和 FrameworkElement.Margin 撞名）。</summary>
    private const double ScreenMargin = 12;

    /// <summary>自动关闭的定时器（做成字段，更新文字时要能重新计时）。</summary>
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>
    /// 还在等结果（识别中 / 翻译中）。**为 true 时任何地方都不许启动关闭倒计时。**
    ///
    /// 【2026-11-09 加：修「24 秒识别、8 秒卡片就没了」】
    ///   以前三处都会启动倒计时（提示框显示、翻译中、鼠标移开），只要识别/翻译
    ///   超过 8 秒，用户就看不到结果。现在统一由一个标志位把关：
    ///   起点（ShowFor → 置 true）到终点（ApplyRecognition / 终态 → 置 false）之间，
    ///   卡片必须一直留着。
    /// </summary>
    private bool _waitingForResult;

    /// <summary>
    /// 「翻译中」那几个点的循环定时器（2026-11-06 第23轮）。
    ///
    /// 【为什么用 DispatcherTimer 改字符串，不用 Storyboard 做动画】
    ///   卡片的 UI 线程就是这个定时器的线程，改 Text 不会有跨线程问题；
    ///   Storyboard 要为「三个点」写三条关键帧动画，代码比这多一倍，
    ///   而用户要的只是「让人知道它还在动」。
    ///
    /// 【400ms 一跳，不是随手取的】200ms 像在闪，800ms 像卡住了。
    /// 【点用英文句号】中文省略号 '…' 是单个字符、宽度固定，跳不起来；
    ///   六个点会把卡片撑宽再缩回。'.' 一到三个，宽度变化最小。
    /// </summary>
    private DispatcherTimer? _hintTimer;

    /// <summary>当前提示后面有几个点（0~3，循环）。</summary>
    private int _hintDots;

    /// <summary>
    /// 现在这套跳点在给哪个控件打字（见 <see cref="StartHint"/>）。
    /// 【2026-11-06 第24轮：从「只能给 TransDetail」改成「给哪个都行」】
    ///   用户要求识别阶段也要「识别中.」，那行字在 DetailText（原文详情），
    ///   和 TransDetail 是两个不同的控件。原来三行代码把 TransDetail 写死，
    ///   现在改成记一个目标控件 + 一个前缀，谁要用谁传。
    /// </summary>
    private System.Windows.Controls.TextBlock? _hintTarget;

    /// <summary>跳点前面那截固定文字，例如「翻译中」「识别中」。</summary>
    private string _hintPrefix = string.Empty;

    /// <summary>
    /// 提示色：卡片里本来就有的蓝（原文文字和边框用的就是它）。
    /// 【为什么不硬编码在四个地方】翻译中、边翻边长、要恢复灰色，三处以上都用它，
    ///   散着写迟早改一处漏三处。两个色都取自卡片既有配色，不引入新颜色。
    /// 【为什么写全名 System.Windows.Media.Brush】本工程同时 using System.Drawing
    ///   （Bitmap/Screen），`Brush` 会被判成不明确引用 CS0104。
    /// </summary>
    private static readonly System.Windows.Media.Brush HintBrush =
        (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#FF38BDF8")!;

    /// <summary>详情行的灰色（和 XAML 里 TransDetail 的 Foreground 一致）。</summary>
    private static readonly System.Windows.Media.Brush DetailBrush =
        (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#FF94A3B8")!;

    /// <summary>
    /// DetailText 的原色（识别中踢蓝以后要还原成它）。
    /// 【为什么要单独一个】XAML 里 DetailText 的 Foreground 是 #FFCBD5E1，
    ///   和 TransDetail 的 #FF94A3B8 不是一个灰。用 DetailBrush 还原会让原文详情行
    ///   悄悄变浅一档 —— 用户不会说「颜色错了」，只会觉得「哪里不对」。
    /// </summary>
    private static readonly System.Windows.Media.Brush RecognizeBrush =
        (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#FFCBD5E1")!;

    /// <summary>这一轮有没有框选过（决定短提示摆哪儿，见 <see cref="ShowNotice"/>）。</summary>
    private bool _hasSelection;

    /// <summary>
    /// 「还没出结果」阶段的兜底时限：**8 秒**。
    ///
    /// 【2026-10-09 用户反馈后加】
    ///   用户原话：「识别文字延迟大了后还没显示识别了啥文字就关掉了，
    ///             这个要改成识别出结果后 3 秒后关掉」。
    ///
    ///   原来 `ShowFor()` 一弹出就启动 3 秒倒计时，**识别还在跑**就开始倒数了。
    ///   实测里识别最慢的一次是 **907 毫秒**（E3 那段），
    ///   复杂背景再慢一点就超过 3 秒——
    ///   窗口先关了，随后 `UpdateWith()` 因为 `IsVisible == false` 直接 return，
    ///   **用户永远看不到识别出来的字**。
    ///
    ///   【现在的规则：倒计时一律从「出结果那一刻」开始算】
    ///     · 识别中  → 卡片上写「识别中…」，**倒计时给 8 秒兜底**，
    ///                 正常情况下根本用不到（识别都远快于 8 秒）；
    ///                 万一引擎卡死，也不会留下一个关不掉的框。
    ///     · 出结果  → 换成识别到的字，**从这一刻重新计 3 秒**。
    /// </summary>
    private static readonly TimeSpan PendingCloseDelay = TimeSpan.FromSeconds(8);

    /// <summary>
    /// **出结果之后**，卡片停留几秒再自动关闭。
    /// 用户 2026-10-09 明确要求：「识别出结果后 3 秒后关掉」。
    /// </summary>
    private const int CloseDelaySeconds = 3;

    /// <summary>
    /// 鼠标位置轮询器 —— 用来判断「鼠标是不是停在卡片上」。
    ///
    /// 【2026-10-08 用户反馈】「停留时间太短了，改成鼠标移上去的话不消失，
    ///   鼠标没有移上去的话 3 秒后消失。」
    ///
    /// 【为什么用轮询鼠标位置，而不是 MouseEnter/MouseLeave 事件】
    ///   事件方案有个绕不开的坑：卡片必须能收到鼠标事件，
    ///   而一旦它能收到，它就**会挡住下面软件的点击**。
    ///   用户正想点卡片底下的某个按钮，却点了卡片一下——这是新引入的 bug。
    ///
    ///   轮询就没这个问题：卡片照样正常显示（不设穿透），
    ///   但我们只是**读**鼠标位置，不靠它接收任何输入。
    ///   代价是每 100 毫秒读一次光标位置，开销可以忽略。
    ///
    /// 【2026-10-15 用户反馈后：上面这条取舍被推翻了】
    ///   用户要求「弹框可以拖动」+「弹框里的文字可以选中」。
    ///   这两件事**都要求卡片能收到鼠标事件**，不然拖动和选字根本无从谈起。
    ///   所以卡片现在会吃掉落在它身上的点击，**不再穿透到下面的软件**。
    ///   这是需求本身要求的代价，如实记在这里。
    /// </summary>
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>
    /// 用户有没有**手动拖过**这张卡片。
    ///
    /// 【为什么要有这个标志】
    ///   识别结果出来时 `UpdateWith()` 会重新摆一次位置（贴到选区旁边）。
    ///   而用户拖走卡片，往往正是因为**卡片挡住了他要看的东西**——
    ///   这时若还把他拖走的位置拉回去，等于白拖。
    ///   所以：**一旦用户拖过，程序就再也不自动摆位置了。**
    /// </summary>
    private bool _userMoved;

    /// <summary>正在拖（左键按在卡片上、还没松开）。</summary>
    private bool _dragging;

    /// <summary>拖动起点：窗口位置（DIP）与光标位置（物理像素）。</summary>
    private double _dragStartLeft;
    private double _dragStartTop;
    private int _dragStartCursorX;
    private int _dragStartCursorY;

    /// <summary>记住摆放位置所需的显示器信息，换文字后要重新摆一次避免跑出屏幕。</summary>
    private RECT _monitorRect;
    private uint _monitorDpi;
    private double _anchorLeftDip;
    private double _anchorTopDip;

    /// <summary>选区的物理宽高。摆位置时用来判断「卡片有没有压住选区」。</summary>
    private double _selectionWidthPx;
    private double _selectionHeightPx;

    public DebugToastWindow()
    {
        InitializeComponent();
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };

        // 鼠标进出卡片时，暂停/恢复自动关闭
        _hoverTimer.Tick += (_, _) => UpdateHoverState();
        _hoverTimer.Start();

        // 【2026-10-15 用户要求「弹框可以拖动」】
        //   按下在卡片上（但不在要选字的那块文字上）→ 开始拖；
        //   移动 → 跟着光标挪；松开 → 收工并重新计 3 秒。
        MouseLeftButtonDown += OnCardMouseLeftButtonDown;
        MouseMove += OnCardMouseMove;
        MouseLeftButtonUp += OnCardMouseLeftButtonUp;

        // 卡片一关就把轮询停掉，别让一个看不见的定时器一直跑
        Closed += (_, _) =>
        {
            _hoverTimer.Stop();
            _closeTimer.Stop();
            StopHint();
        };
    }

    /// <summary>
    /// 【2026-10-15 用户要求「弹框可以拖动」】
    /// 左键按在卡片上 → 记下起点，开始拖。
    ///
    /// 按在**那段可以选中的文字上**（含它的滚动条）时不拖：
    /// 那一下是留给用户拖选文字的，抢过来就选不成了。
    /// </summary>
    private void OnCardMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (IsInsideTextArea(e.OriginalSource)) return;
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT p)) return;

        _dragging = true;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragStartCursorX = p.X;
        _dragStartCursorY = p.Y;

        // 拖着的时候不许自动关（复用「鼠标停在卡片上就不关」那套逻辑）
        _closeTimer.Stop();

        try { CaptureMouse(); }
        catch (Exception ex) { Core.Logging.AppLogger.Warn($"提示框捕获鼠标失败（不影响拖动）：{ex.Message}"); }

        e.Handled = true;
    }

    /// <summary>拖动中：窗口跟着光标走，并夹在虚拟屏幕内。</summary>
    private void OnCardMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT p)) return;

        double scale = _monitorDpi / DpiHelper.DefaultDpi;
        if (scale <= 0) scale = 1;

        double left = _dragStartLeft + (p.X - _dragStartCursorX) / scale;
        double top = _dragStartTop + (p.Y - _dragStartCursorY) / scale;

        // 不许拖出屏幕（拖丢了就找不回来了）
        Left = ClampAxis(left, SystemParameters.VirtualScreenLeft,
                         SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        Top = ClampAxis(top, SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);

        _userMoved = true;
    }

    /// <summary>
    /// 把一个坐标夹在 [lo, hi] 里。
    ///
    /// 【为什么 hi 可能比 lo 还小】显示器比卡片还窄/还矮的时候，
    /// `hi = 屏幕原点 + 屏幕尺寸 - 卡片尺寸` 会小于 `lo`。
    /// 这时夹回 `lo`，卡片老老实实待在屏幕左上角，
    /// 而不是因为 `Math.Min/Max` 的先后顺序被推到屏幕外。
    /// </summary>
    internal static double ClampAxis(double value, double lo, double hi)
        => Math.Min(Math.Max(value, lo), Math.Max(lo, hi));

    /// <summary>松开左键：结束拖动，重新计 3 秒自动关闭。</summary>
    private void OnCardMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        try { ReleaseMouseCapture(); }
        catch (Exception ex) { Core.Logging.AppLogger.Warn($"提示框释放鼠标失败（不影响使用）：{ex.Message}"); }

        _closeTimer.Stop();
        if (!_waitingForResult)
        {
            _closeTimer.Interval = TimeSpan.FromSeconds(CloseDelaySeconds);
            _closeTimer.Start();
        }
    }

    /// <summary>这次点击是不是落在「要选字的那块文字」里（含它的滚动条）。</summary>
    private bool IsInsideTextArea(object? source)
    {
        var d = source as DependencyObject;
        while (d is Visual && !ReferenceEquals(d, this))
        {
            if (ReferenceEquals(d, TextScroll)) return true;
            d = VisualTreeHelper.GetParent(d);
        }

        return false;
    }

    /// <summary>
    /// 每 100 毫秒调一次：鼠标在卡片上就取消倒计时，移开了就重新开始倒 3 秒。
    ///
    /// 【为什么要判断「倒计时是不是已经在跑」】
    ///   如果移开鼠标就无条件重启 3 秒，那用户把鼠标在卡片边上晃来晃去，
    ///   倒计时会被无限往后推，卡片永远不关。
    ///   只有「倒计时已经停了」（也就是刚才鼠标在上面）才重新开始，才是用户要的效果。
    /// </summary>
    private void UpdateHoverState()
    {
        if (!IsVisible) return;

        if (IsCursorInsideCard())
        {
            // 鼠标在卡片上：停掉倒计时，让卡片一直留着
            if (_closeTimer.IsEnabled) _closeTimer.Stop();
        }
        else if (!_closeTimer.IsEnabled && !_waitingForResult)
        {
            // 刚移开：从头开始 3 秒。
            // 【2026-11-09 加 _waitingForResult】：还在识别/翻译时，鼠标移开**不许**顺手启动
            //   倒计时 —— 否则用户一挪鼠标，8 秒后又把没出结果的卡片关掉了。
            _closeTimer.Stop();
            _closeTimer.Start();
        }
    }

    /// <summary>鼠标当前是否落在卡片矩形内（物理像素比较）。</summary>
    private bool IsCursorInsideCard()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT p)) return false;

        // 窗口的 Left/Top 是屏幕 DIP，先换算成物理像素再比，
        // 否则在缩放不是 100% 的屏幕上会判错位置。
        double scale = _monitorDpi / DpiHelper.DefaultDpi;
        double left = Left * scale;
        double top = Top * scale;
        double width = Width * scale;
        double height = Height * scale;

        return p.X >= left && p.X <= left + width
            && p.Y >= top && p.Y <= top + height;
    }

    /// <summary>
    /// 显示一次框选结果。**这一步只是「我收到了框」，识别还在跑。**
    ///
    /// 【2026-10-09 用户反馈后改：这里不再按 3 秒倒计时】
    ///   用户要求「识别出结果后 3 秒后再关」。
    ///   原来这个方法一弹出就启动 3 秒倒计时，识别慢的时候
    ///   窗口先关了，`UpdateWith()` 走 `if (!IsVisible) return;`，
    ///   **用户连识别出来的字都没看见**。
    ///
    ///   现在的行为：
    ///     · 卡片先显示「识别中…」，**给 8 秒兜底**（防止引擎卡死后框关不掉）；
    ///     · 识别结果一出来，`UpdateWith()` 换成识别到的字，
    ///       **从那一刻重新计 3 秒**。
    /// </summary>
    internal void ShowFor(SelectionSession session)
    {
        TitleText.Text = "框选完成";
        MainText.Visibility = Visibility.Visible;
        MainText.Text = $"({session.PhysicalRect.Left}, {session.PhysicalRect.Top})  {session.PhysicalWidth} × {session.PhysicalHeight}";
        // ==========================================================
        // 【2026-11-06 第24轮：识别阶段也给个跳点】
        //   用户原话：「识别也要加一个识别中，不然看着怪怪的，格式啥的就和翻译中一样就行。」
        //   改之前这里是一句静止的「正在识别，请稍候…」——
        //   认字要 0.7~3.6 秒，屏幕上一动不动，看着像卡死了。
        //   现在交给和翻译中同一套跳点（400ms 一跳、'.'、0~3 循环），
        //   只是目标是 DetailText（原文详情那行）、前缀是「识别中」。
        //   识别结束会在 UpdateWith 开头 StopHint + 还原颜色。
        // ==========================================================
        DetailText.Foreground = HintBrush;
        StartHint(DetailText, "识别中");
        // 和第23轮翻译中那条日志对称：验收脚本要靠它知道「这一刻」。
        // 只看「开始取词」会早几百毫秒，截图拍到的是还没切过去的那一帧。
        Core.Logging.AppLogger.Info("原文区已切到「识别中」（蓝色小字带跳点）");

        (_monitorRect, _monitorDpi) = ScreenGeometry.GetMonitorAt(session.PhysicalRect.Left, session.PhysicalRect.Top);
        _anchorLeftDip = session.DipRect.Left;
        _anchorTopDip = session.DipRect.Top;
        _selectionWidthPx = session.PhysicalWidth;
        _selectionHeightPx = session.PhysicalHeight;
        _hasSelection = true;

        // 先放一个肯定合法的位置（多显示器时负坐标也算合法），Show() 之后才知道真实大小。
        //
        // 【踩过的坑】原来先 Measure() 再把 DesiredSize 赋给 Width/Height，
        // 结果窗口是 **0×0**，界面上什么都看不见，日志里也一条错误都没有 ——
        // 这种「安静地什么都不发生」的 bug 最难查。
        // 现在改用 XAML 里的 SizeToContent="WidthAndHeight"：窗口在 Show() 时自己撑到内容大小，
        // 不用手算，也就不会算出 0。
        Left = 0;
        Top = 0;

        // 用 Show() 不用 Activate()：不能抢用户焦点
        Show();
        Topmost = true;

        PlaceBesideSelection();

        Core.Logging.AppLogger.Info(
            $"提示框已显示：位置=({Left:0},{Top:0}) 尺寸={Width:0}×{Height:0}，" +
            $"正在识别（识别期间不会自动关闭，出结果后才计 {CloseDelaySeconds} 秒）");

        if (Width <= 0 || Height <= 0)
        {
            Core.Logging.AppLogger.Warn(
                $"提示框尺寸异常（{Width}×{Height}），界面上可能看不到 —— 这是 SizeToContent 没生效");
        }

        // ==========================================================
        // 【2026-11-09 修】这里以前是「Stop → Interval=8 秒 → Start」。
        //   那句「识别期间不会自动关闭」的日志是**假的**：它启的是 8 秒倒计时，
        //   识别一超过 8 秒卡片就自己消失了 —— 用户实测 24 秒的长文本必然看不到结果。
        //   证据：tools\日志\app-20261109.log 09:12 那几次 811 字 / 23796 ms。
        //
        //   正确行为：识别期间**根本不启动计时器**。终态（识别结果 / 翻译结果）
        //   到达时各自会重新设置并启动（ApplyRecognition / ApplyTranslation）。
        // ==========================================================
        _waitingForResult = true;
        _closeTimer.Stop();
    }

    /// <summary>
    /// 显示一条**与框选无关**的短提示（例如「调试截图已保存」），3 秒后自动消失。
    ///
    /// 【为什么需要它：2026-10-10 用户反馈】
    ///   按下 `Ctrl+Alt+D` 之后「**一点反馈也没有**，也不知道截图成没成功」。
    ///   查下来图其实**存下来了**，只是**存完就静悄悄结束**——
    ///   **做了事，但不告诉人做了，等于没做。**
    ///   光有日志不够（日志是给开发看的，用户不会去翻），
    ///   所以这里复用同一个提示框，**屏幕上能看见**才算数。
    ///
    /// 【摆在哪】
    ///   · 刚框选过 → 摆在选区旁边（和识别结果卡片一个位置，不挡用户看内容）
    ///   · 从没框选过 → 摆在屏幕右下角
    ///
    /// 【seconds 为什么可调（2026-11-02 架构师 P1-B1）】
    ///   默认 3 秒对「已保存」这种几字提示够用，但隐私披露那条 44 个字，
    ///   3 秒读不完就关 = 等于没提示。只有隐私那条传 8，其它调用点一个字不改。
    /// </summary>
    internal void ShowNotice(string title, string detail, int seconds = 3)
    {
        TitleText.Text = title;
        MainText.Text = "";

        // ==========================================================
        // 【2026-11-06 第24轮：先把跳点停掉再写提示】
        //   短提示（「调试截图已保存」这类）复用同一个 DetailText。
        //   如果此刻识别中的跳点还在跑，下一次 Tick 就会把提示文字
        //   覆盖成「识别中.」—— 用户根本看不到自己那条提示。
        // ==========================================================
        StopHint();
        DetailText.Foreground = RecognizeBrush;
        DetailText.Text = detail;

        // 【2026-10-15】MainText 换成了 TextBox（为了能选字），而 TextBox 即使文本为空
        //   也占着一行高度，短提示卡片底下会多出一块空白。所以这里直接收起来。
        MainText.Visibility = Visibility.Collapsed;
        TextScroll.MaxHeight = 80;   // 短提示不需要很高
        // 短提示和识别结果是共用的窗口，不主动清掉上一张的译文会串台。
        HideTranslation();

        Left = 0;
        Top = 0;
        Show();
        Topmost = true;

        if (_hasSelection) PlaceBesideSelection();
        else
        {
            Left = Math.Max(0, SystemParameters.VirtualScreenWidth - Width - 40);
            Top = Math.Max(0, SystemParameters.VirtualScreenHeight - Height - 60);
        }

        _closeTimer.Stop();
        // 【2026-11-09】这条是「与框选无关的短提示」（如调试截图已保存），本来就不参与识别；
        //   但万一它被复用到识别流程上，_waitingForResult 这道锁能挡住提前关闭。
        if (!_waitingForResult)
        {
            _closeTimer.Interval = TimeSpan.FromSeconds(seconds);
            _closeTimer.Start();
        }

        Core.Logging.AppLogger.Info(
            $"短提示已显示（{seconds} 秒）：{title} —— {detail}");
    }

    /// <summary>
    /// 取词完成后，把卡片内容换成识别出来的文字。
    ///
    /// 【2026-10-09 用户反馈后改：3 秒倒计时从「这一刻」开始算】
    ///   用户要求：「识别出结果后 3 秒后关掉」。
    ///   原来倒计时是从 `ShowFor()` 那一刻开始的，识别慢就白关了。
    ///
    /// 【为什么要重新摆位置】
    ///   识别出的文字比坐标长得多，卡片会变宽。
    ///   沿用原来摆好的位置，文字一多就可能跑出屏幕右边——
    ///   所以换完文字必须重新摆一次。
    /// </summary>
    internal void UpdateWith(Core.Capture.CaptureResult result)
        => UpdateWith(result, null, translating: false);

    /// <summary>
    /// 把「原文 + 译文（或翻译态）」交进来。
    ///
    /// 【2026-11-03 用户拍板，显示时序改成两段 —— 旧的「必须一次 UpdateWith」硬要求已撤销】
    ///   用户原话：「识别后先把原文识别出来，然后等待翻译就显示[翻译中...]，
    ///             翻译出来后再显示全文。」
    ///
    ///   所以现在是**故意分两次调**：
    ///     1) OCR 完成 → UpdateWith(result, null, translating: true)   原文 + 「翻译中…」
    ///     2) 译文到手 → UpdateWith(result, translation)                译文补上
    ///
    ///   为什么当初禁止、现在可以：早先担心的「译文晚到只剩 1 秒能看」，
    ///   前提是两次都在同一个 3 秒倒计时里倒着走。但方法末尾每次都会
    ///   `_closeTimer.Stop(); Interval=…; Start()` 重置计时器，
    ///   第二次进来用户就重新拿到完整倒计时，那个担心不成立。
    ///   **不要看到旧记录（第九轮 P1-C2）就把这里改回一次调用。**
    ///
    /// 【翻译失败绝不动原文】
    ///   MainText 永远只放 `result.Text`，译文单独一个控件。
    ///   失败时译文区保持隐藏，只在 DetailText 末尾追加一句人话，
    ///   用户看到的字和翻译关着时一模一样，只多一行说明。
    ///
    /// <param name="translating">true = 译文还在路上，译文区显示「翻译中…」，
    /// 倒计时用长的 PendingCloseDelay，别在译文到达前把卡片关掉。</param>
    /// </summary>
    internal void UpdateWith(Core.Capture.CaptureResult result,
                            Core.Translate.TranslationResult? translation,
                            bool translating = false)
    {
        if (!IsVisible) return;   // 用户已经等不及关掉了

        // ==========================================================
        // 【2026-11-06 第24轮：识别结束了，停表并把颜色还原】
        //   放在最前面（`if (!IsVisible)` 之后、判成功之前），是因为
        //   识别失败也会走到这里 —— 失败时同样要把「识别中.」停掉，
        //   否则那行蓝字会一直跳到卡片关闭。
        //   还原用的是 RecognizeBrush（#FFCBD5E1，DetailText 的原色），
        //   不是 DetailBrush（那是 TransDetail 的灰）。
        // ==========================================================
        StopHint();
        DetailText.Foreground = RecognizeBrush;

        // 【2026-11-09】结果到了，解除「等结果」锁 —— 后面的 ApplyRecognition /
        //   ApplyTranslation 才允许启动关闭倒计时。识别失败也走到这里，同样要解锁，
        //   否则失败的卡片会一直挂着不关。
        _waitingForResult = false;

        if (result.Success)
        {
            TitleText.Text = "识别结果";

            // 【2026-10-08 用户反馈】「太长的文字超出对话框大小就被省略掉了，我要完整显示」
            //   原来这里调了 Truncate(120)，超过 120 字直接砍掉加省略号——用户要看全文。
            //   现在**原文照登，一个字都不删**。
            //   但 500 字不可能全塞进一屏（那样卡片会顶出屏幕），
            //   所以超出部分由 ScrollViewer 负责滚动，
            //   并在下面明确告诉用户「可以滚」，免得以为又被省略了。
            MainText.Text = result.Text;
            // ShowNotice 把 MainText 藏起来过，这里显式恢复，免得复用同一个窗口时原文是空的。
            MainText.Visibility = Visibility.Visible;

            // ==========================================================
            // 【2026-11-04 v2.0 详情行改格式，用户实测截图后定的】
            //   旧格式：`来自 RapidOCR（外文主通道）  外文（拉丁字母）  用时 94 ms  3 行  共 12 字，可滚动查看`
            //   太长、太密，一个 400px 宽的卡片上要折成两行，
            //   而「3 行」「外文（拉丁字母）」对用户没有任何用处（他看得见原文长什么样）。
            //   新格式只留三段有用的：**谁认的 · 花了多久 · 认了多少字**。
            //
            // 【2026-11-05 第16轮 P1-J1：删掉「· 可滚动」，它现在会撒谎】
            //   旧世界有个 Truncate(120) 会把字**砍掉**，所以必须提示「去滚动看剩下的」。
            //   现在全文都塞进 TextScroll 了，而且它的 VerticalScrollBarVisibility="Auto"
            //   —— 真滚得动的时候滚动条自己会出来，不需要再说一句。
            //   那条 120 字阈值本身也不成立：MaxHeight=300 / MaxWidth=420 / FontSize=13 下，
            //   英文每行约 64 字符（444 字只占 ~7 行 ≈133px，压根不滚），
            //   中文每行约 32 字（触发线要 500 字左右），差了 4 倍，一个 120 谁都不对。
            //   444 字长文实测卡片 442×295、滚动条没出来、文字却写着「可滚动」，
            //   等于程序在说谎。用户要的格式就是「引擎 · 用时 · 字数」三项，多一句都别加。
            // ==========================================================
            DetailText.Text =
                $"{ChannelNameOf(result.Channel)} · {result.ElapsedMs} ms · {result.Text.Length} 字" +
                // 【2026-10-29 阶段三 S4】整块没认好时加一行小字，引导逐行框。
                //   只加提示，不改上面任何文字，也不阻断翻译。
                (result.BlockLooksBad ? Environment.NewLine + "这块字没认好，切成一行一行框会更准" : string.Empty);

            ApplyTranslation(translation, translating);
        }
        else
        {
            // 失败要说清楚为什么，别让用户对着空气发呆
            TitleText.Text = "没取到文字";
            MainText.Text = result.ToUserMessage();
            MainText.Visibility = Visibility.Visible;
            DetailText.Text = result.Detail.Length > 90 ? result.Detail[..90] + "…" : result.Detail;
            // 认字都没成，不可能有译文；万一传进来了也不显示，免得两张卡讲矛盾的话。
            HideTranslation();
        }

        PlaceBesideSelection();
        Core.Logging.AppLogger.Info(
            $"提示框已更新为取词结果：位置=({Left:0},{Top:0}) 尺寸={Width:0}×{Height:0}，" +
            $"文字 {result.Text.Length} 字");

        // ==========================================================
        // 【2026-10-09 用户反馈后改：3 秒从「这一刻」开始算】
        //   用户原话：「这个要改成识别出结果后 3 秒后关掉」。
        //
        //   原来倒计时是在 `ShowFor()`（框选刚完成、识别还没跑完）就启动的，
        //   识别慢的时候窗口先关掉，这里走 `if (!IsVisible) return;`，
        //   **用户连识别出来的字都没看见**。
        //   现在必须把 Interval 显式改回来——
        //   ShowFor() 那里把它设成了 8 秒兜底，定时器是同一个，不能忘了改回来。
        //
        // 【2026-11-03 翻译态必须是 8 秒，不能用 3 秒】
        //   原文出来就 3 秒关，而翻译最坏 2.5 秒 —— 余量只剩 0.5 秒，
        //   I/O 一抖就关在译文到达之前，用户永远看不到译文。
        //   8 秒的意义是「有个东西还在路上」：认字期间是这个语义，翻译期间同理，
        //   所以复用同一个 PendingCloseDelay，不新造常数。
        //   译文真正落地时会再进一次 UpdateWith(translating:false)，
        //   那时才换回 3 秒 —— 用户重新拿到完整 3 秒看译文。
        // ==========================================================
        // 【2026-11-09 改：待翻译期间也不启动计时器】
        //   以前 translating=true 时启 8 秒兜底。那同样会在「翻译还在路上」时把卡片关掉
        //   （长文本翻译 2.5 秒是常态，I/O 一抖就超）。
        //   现在：待翻译 = 只停表，等译文真正落地时再启动 3 秒（译文本就只有 3 秒可看）。
        _closeTimer.Stop();
        if (!translating)
        {
            _closeTimer.Interval = TimeSpan.FromSeconds(CloseDelaySeconds);
            _closeTimer.Start();
        }
    }

    /// <summary>
    /// 开始「翻译中」的跳点（2026-11-06 第23轮）。
    ///
    /// 【用户原话】「这个翻译中能不能换个样式，不要和翻译出来的文字一个颜色、大小，
    ///   换个明显点的样式，然后翻译完成后再消失。」
    ///   改之前「翻译中…」是写进 TranslationText 的，那个控件是译文正文
    ///   （13 号、近白 #FFE2E8F0），于是提示和译文长得一模一样，
    ///   用户分不清「这行字是它翻的，还是它还在翻」。
    ///   现在改写到译文下面那行小字（TransDetail，11 号），颜色临时换成蓝色。
    /// </summary>
    /// <summary>
    /// 起一套跳点：往指定控件里循环写「前缀 + 0~3 个点」。
    ///
    /// 【2026-11-06 第24轮：从「只服务翻译」改成「谁要谁传」】
    ///   原来叫 StartTranslatingHint()，里面把 TransDetail 写死了。
    ///   用户要求识别阶段也来一套，那行字在 DetailText，于是改成带参数。
    ///   名字也改短了 —— 它现在不只服务翻译。
    /// 【400ms 一跳，不是随手取的】200ms 像在闪，800ms 像卡住了。
    /// 【点用英文句号】中文省略号 '…' 是单个字符、宽度固定，跳不起来。
    /// </summary>
    private void StartHint(System.Windows.Controls.TextBlock target, string prefix)
    {
        _hintTarget = target;
        _hintPrefix = prefix;
        _hintDots = 0;
        _hintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hintTimer.Tick -= OnHintTick;
        _hintTimer.Tick += OnHintTick;
        // 【顺序不能反：先 Start，再写第一帧。】ApplyHintText 里有一道
        //   "if (!_hintTimer.IsEnabled) return;" 的护栏（挡住 Stop() 之后
        //   已经排进队列的那一次 Tick）。原来先 ApplyHintText 后 Start，
        //   于是这一次调用什么字都没写出去 —— 翻译分支因为在调用前手写了
        //   TransDetail.Text 看不出来，识别分支只靠这里，结果卡片前 400ms
        //   是空的，用户看到的就是「点了框选没反应」。
        _hintTimer.Start();
        ApplyHintText();
    }

    private void OnHintTick(object? sender, EventArgs e)
    {
        _hintDots = (_hintDots + 1) % 4;
        ApplyHintText();
    }

    /// <summary>
    /// 只在定时器活着的时候写文字。
    /// 【为什么要判 IsEnabled】Stop() 之后 Tick 不会再触发，但已经在排队的
    ///   那一次仍可能进来，进了就会把已经写好的「翻译中.」或识别结果覆盖掉，
    ///   看着像倒退。
    /// </summary>
    private void ApplyHintText()
    {
        if (_hintTarget is null) return;
        if (_hintTimer is null || !_hintTimer.IsEnabled) return;
        _hintTarget.Text = _hintPrefix + new string('.', _hintDots);
    }

    private void StopHint()
    {
        _hintTimer?.Stop();
        _hintTarget = null;
    }

    /// <summary>
    /// 把译文塞进卡片下半截，或者藏起来并在详情里说一句人话。
    ///
    /// 【什么时候藏】没有译文 / 翻译关着 / 判成中文不翻（都是 Disabled）—— 完全静默，
    ///   用户看不出程序「本来打算翻」，卡片和阶段三一模一样。
    /// 【什么时候只加一行字】真失败了（超时、断网、服务端不认）——
    ///   原文照旧在上面，详情末尾多一句「翻译超时，原文已保留」之类。
    /// 【什么时候显示译文】只有 Success。
    /// </summary>
    private void ApplyTranslation(Core.Translate.TranslationResult? translation, bool translating = false)
    {
        // ==========================================================
        // 【翻译态】原文已经显示出来了，这里只是把「还有一个东西在路上」说出来。
        //   只动译文区，**不碰原文和详情** —— 用户此刻正在读原文。
        //   措辞「翻译中…」：用户原话写的是「翻译中...」（三个点），
        //   项目里其它文案一律用中文省略号，跟着项目走。
        //
        //   【2026-11-06 第22轮：长文本分段后，翻译态也可能已经有字了】
        //     分段翻译每翻好一段就回调一次，此时 translating 仍是 true
        //     （后面还有段没翻完，计时器要继续拿 8 秒）。那种情况走下面那个
        //     「边翻边长」分支，这里只管「手上一个字都还没有」。
        // ==========================================================
        if (translating && (translation is null || !translation.Success))
        {
            // ==========================================================
            // 【2026-11-06 第23轮：提示从小字里出，不再占着译文正文】
            //   TranslationText 是译文正文（13 号近白），"翻译中" 写进去就跟
            //   译文长得一样；改写到下面那行 11 号小字，临时刷成蓝色。
            //   TransScroll 藏起来：此刻译文区一个字都没有，留着空滚动区
            //   会白白撑出 200px 高度（MaxHeight=200，空的时候也占位）。
            //   TransSep 那根横线留着 —— 它把「原文」和「正在进行的翻译」分开。
            // ==========================================================
            TranslationText.Text = string.Empty;
            TransScroll.Visibility = Visibility.Collapsed;
            TransSep.Visibility = Visibility.Visible;
            TransDetail.Foreground = HintBrush;
            TransDetail.Text = "翻译中";
            TransDetail.Visibility = Visibility.Visible;
            StartHint(TransDetail, "翻译中");
            Core.Logging.AppLogger.Info("译文区已切到「翻译中」（蓝色小字带跳点）");
            return;
        }

        // ==========================================================
        // 【2026-11-06 第22轮：还在翻，但已经有译文了 —— 只让字长出来】
        //   用户原话：「不写进度，只让译文长出来」。
        //   所以这里画译文正文，**刻意不显示**「引擎 · 耗时 · 字数」那一行：
        //   分段期间那个数字每段都变一次，一直跳，比没有更烦人。
        //   等最后一段翻完，App 会带着 translating=false 再调一次，
        //   那时候才由下面的成功分支补上详情行。
        // ==========================================================
        if (translating && translation is not null && translation.Success)
        {
            TranslationText.Text = translation.Text;
            TransScroll.Visibility = Visibility.Visible;
            TransSep.Visibility = Visibility.Visible;
            // ==========================================================
            // 【2026-11-06 第24轮：不再写「正在翻译剩余段落」】
            //   用户原话：「翻译中会被正在翻译剩余段落顶掉，这俩重复了，
            //   把翻译剩余段落去掉。」
            //   确实重复 —— 上一段刚显示「翻译中.」，这里换个说法说同一件事，
            //   还把跳点停了，看着像卡住。现在什么都不换：
            //   跳点继续跳，前缀还是「翻译中」，直到整块翻完才变灰色详情行。
            // ==========================================================
            TransDetail.Foreground = HintBrush;
            StartHint(TransDetail, "翻译中");
            TransDetail.Visibility = Visibility.Visible;
            return;
        }

        // ==========================================================
        // 【2026-11-03 P0-E2 真机验收时发现的真 bug，务必记住】
        //   原来第一行是把 `Failure is None or Disabled` 当作「没译文」直接隐藏——
        //   可是**成功时 Failure 恰恰就等于 None**（见 TranslationResult.Success）。
        //   结果就是：译文永远不显示，翻译跑得好好的、日志里全是「翻译：成功」，
        //   卡片上却一个字都没有。
        //
        //   这个 bug 从 T3a 起就存在，之前的 S2/S3/S4/S5/S6 全是走 `--回放`，
        //   **回放不建卡片窗口**，所以六条验收全绿也没人看见。
        //   教训：只验日志不验界面，等于没验界面。以后翻译相关的改动必须在真机上
        //   确认卡片上真的出现了译文那一块。
        //
        //   正确顺序：**先判成功**，再判「本来就不该有译文」，最后才当失败处理。
        // ==========================================================
        if (translation is not null && translation.Success)
        {
            TranslationText.Text = translation.Text;
            TransScroll.Visibility = Visibility.Visible;
            TransSep.Visibility = Visibility.Visible;
            // ==========================================================
            // 【2026-11-04 v2.0 译文详情行】跟原文详情行同一个格式。
            //   缓存命中那一支的耗时写 0 ms 并注明「上次的结果」——
            //   它确实没花时间，也没再发出去，写真实数字才不会被当成「怎么这么快」。
            //   原来那句「这段字刚翻过，直接用了上次的结果（没有再发出去）」
            //   从 DetailText 里删掉：同一件事不再两处都说。
            // ==========================================================
            // ==========================================================
            // 【2026-11-06 第22轮：分段失败要写在详情行末尾】
            //   LongTextTranslator 会把「第 2、4 段没翻出来」放进 Detail。
            //   用户拍板：已翻好的照样显示，只在末尾注明哪几段没出来。
            //   一段都没失败时 Detail 是空串，括号整个不加。
            // ==========================================================
            string failedNote = translation.Detail.Length == 0 ? string.Empty : $"（{translation.Detail}）";
            // 2026-11-06 第23轮：译文落地，跳点必须停，颜色换回详情行的灰。
            StopHint();
            TransDetail.Foreground = DetailBrush;
            TransDetail.Text = translation.FromCache
                ? $"{ProviderNameOf(translation.Provider)} · 0 ms（上次的结果） · {translation.Text.Length} 字{failedNote}"
                : $"{ProviderNameOf(translation.Provider)} · {translation.ElapsedMs} ms · {translation.Text.Length} 字{failedNote}";
            TransDetail.Visibility = Visibility.Visible;
            return;
        }

        // Disabled = 开关关着，或原文本来就是目标语言；None 但 Text 空 = 服务端没回译文。
        // 这两种都是正常状态，不该在卡片上留一行解释去打扰用户。
        if (translation is null
            || translation.Failure is Core.Translate.TranslationFailure.None
                                                   or Core.Translate.TranslationFailure.Disabled)
        {
            HideTranslation();
            return;
        }

        // ==========================================================
        // 【2026-11-03 P1-G1：失败时不要收起译文区，卡片会跳】
        //   原来这里先 HideTranslation() 再往详情行追加一句。可是译文区此刻已经
        //   因为「翻译中…」撑开了约 213px，失败一瞬间整块收起 —— 用户眼前就是
        //   卡片先长高再缩回去，像闪了一下。
        //   原因本来就该出现在「本该有译文的地方」，用户的眼睛在那里。
        //   所以：失败原因写进译文块，保留可见；不再往详情行重复一遍。
        //   以后要往译文区加东西（比如「重试」按钮），失败态就是这一支，别忘了一起改。
        // ==========================================================
        string msg = translation.Failure.ToUserMessage();
        if (msg.Length == 0)
        {
            HideTranslation();
            return;
        }
        TranslationText.Text = msg;
        TransScroll.Visibility = Visibility.Visible;
        TransSep.Visibility = Visibility.Visible;
        // 2026-11-06 第23轮：失败了就别让蓝点继续跳 —— 它看着像「还在努力」。
        StopHint();
        // 失败时译文详情行藏起来：下面那句原因就是全部信息，
        // 再挂一个「Bing 网页翻译 · 2401 ms」反而像在说「它翻成了这样」。
        TransDetail.Visibility = Visibility.Collapsed;
    }

    private void HideTranslation()
    {
        // 2026-11-06 第23轮：这里是所有「本来就不该有译文」的出口（翻译关着、
        // 判成中文不翻、服务端没回字）。第24轮起跳点改成通用版（StopHint），
        // 顺手把目标控件也清掉，免得指向一个已经藏起来的控件。
        StopHint();
        TranslationText.Text = string.Empty;
        TransScroll.Visibility = Visibility.Collapsed;
        TransSep.Visibility = Visibility.Collapsed;
        TransDetail.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 译文详情行上的提供方短名（2026-11-04 v2.0）。
    ///
    /// 【为什么只砍掉卡片上的括号后缀】`TranslationResult.Provider` 现在是
    ///   「Bing 网页翻译（免密钥）」。卡片宽 400px，那一行还有耗时和字数，
    ///   括号里那半截会把它挤到换行。**日志里仍然是全名**——日志是给排查用的，
    ///   「免密钥」这三个字恰恰是排查时要看的信息。
    /// </summary>
    private static string ProviderNameOf(string provider) =>
        provider.Replace("（免密钥）", string.Empty).Trim();

    /// <summary>
    /// 详情行上的通道短名（2026-11-04 v2.0 改短）。
    ///
    /// 【为什么改短】详情行现在是「通道 · 耗时 · 字数」三段拼一行，
    ///   原来那句「来自 Tesseract（英文/俄文专用引擎）」太长，把整行挤到换行，
    ///   看着像两件事。是谁认的这条信息要留，但不必占半行。
    /// 【RapidOCR 这支不能漏】阶段三起外文默认走它，漏了卡片上会直接打英文类名，
    ///   用户分不清那是引擎名还是出错信息。
    /// </summary>
    private static string ChannelNameOf(string channel) => channel switch
    {
        "Clipboard" => "复制",
        "WindowsOcr" => "Windows 识别",
        // 2026-10-09 引擎分工：英文/俄文由 Tesseract 认，必须如实告诉用户是谁认的。
        "Tesseract" => "Tesseract",
        // 2026-11-02 阶段四 T3a：阶段三起外文默认走 RapidOCR 主通道，这支不能漏。
        "RapidOCR" => "RapidOCR",
        _ => channel
    };

    /// <summary>
    /// 把卡片摆到选框**外面**，且保证**永远不会跑出屏幕**。
    ///
    /// 【踩过的坑 · 2026-10-08 用户反馈「单独框选并不能读全」的真正原因】
    ///   原来卡片摆的是「选框左上角往右挪一点」——也就是**压在选区里面**。
    ///   更要命的是弹出时机：OnSelectionCompleted 里是
    ///   「**先弹卡片，再截图识别**」。
    ///   于是卡片自己盖住了正要识别的那块画面，
    ///   OCR 认出来的是**卡片上写的坐标**，用户框的那段文字反而读不到。
    ///
    ///   实测证据：框一片小字，卡片正好盖在上面时，识别结果是
    ///   「( 300 ， 278 ) 400 x 34 / 显示 DPI…」——全是卡片自己的内容。
    ///
    /// 【现在的规矩】
    ///   卡片**必须完全在选区外面**：优先选区右边，放不下换左边，
    ///   再放不下换下边、上边。四个方向都试过仍放不下才允许压住（极端情况）。
    ///
    /// 【文字加长后的新问题】
    ///   用户要求「长文字完整显示」，卡片就会变得很高。
    ///   如果不管它，屏幕上放不下的那部分就看不到了——那等于还是没完整显示。
    ///   所以摆位置之前先按**可用空间**给文字区定一个上限高度，
    ///   超出的部分交给滚动条。宁可滚动，也要保证一个字都不丢。
    /// </summary>
    private void PlaceBesideSelection()
    {
        // 【2026-10-15】用户手动拖过卡片 → 位置由他做主，程序不再自动挪。
        //   识别结果出来时卡片会变长、变宽，但用户拖它往往就是为了不挡住某块内容，
        //   把他拖走的位置拉回去等于白拖一次。
        if (_userMoved) return;

        double scale = _monitorDpi / DpiHelper.DefaultDpi;
        double screenWidthDip = (_monitorRect.Right - _monitorRect.Left) / scale;
        double screenHeightDip = (_monitorRect.Bottom - _monitorRect.Top) / scale;

        double margin = ScreenMargin / scale;

        // 选区的四条边（换算成屏幕 DIP）。
        // 注意：_anchorLeftDip 已经是**整个虚拟屏幕**的绝对 DIP 坐标，
        // 所以不能再减显示器原点，否则会算错（这里踩过一次）。
        double selLeft = _anchorLeftDip;
        double selTop = _anchorTopDip;
        double selRight = selLeft + (_selectionWidthPx / scale);
        double selBottom = selTop + (_selectionHeightPx / scale);

        // ---- 第一步：按「四个候选位置里最宽的那个」给文字区定上限高度 ----
        // 摆在右边/下边时可用高度最多；摆在左边/上边时高度看屏幕顶部。
        double maxUsefulHeight = Math.Max(
            screenHeightDip - margin * 2,
            screenHeightDip - selBottom - margin * 2);   // 下边那一档最省事

        // 减去标题、明细、内外边距这些固定开销，剩下的才是正文能占的高度
        const double chromeHeight = 70;   // 标题 + 明细 + Padding + 间距，经验值
        double textCap = Math.Max(80, maxUsefulHeight - chromeHeight);
        TextScroll.MaxHeight = Math.Min(textCap, 600);

        // 高度变了要重新量一次，否则摆位置用的还是旧高度
        UpdateLayout();

        // 四个候选位置，顺序就是优先级：右边 → 左边 → 下边 → 上边
        var candidates = new (double X, double Y)[]
        {
            (selRight + margin,  selTop),
            (selLeft - Width - margin, selTop),
            (selLeft,  selBottom + margin),
            (selLeft,  selTop - Height - margin),
        };

        foreach (var (x, y) in candidates)
        {
            double left = ClampToScreen(x, Width, screenWidthDip);
            double top = ClampToScreen(y, Height, screenHeightDip);

            // 关键判定：这张卡片**一点都不能**压到选区上
            bool overlaps = left < selRight + margin && left + Width + margin > selLeft
                         && top < selBottom + margin && top + Height + margin > selTop;

            if (!overlaps)
            {
                Left = left;
                Top = top;
                return;
            }
        }

        // 四个方向都放不下（选区几乎占满整屏）：至少别压住选区左上角
        Left = ClampToScreen(selRight + margin, Width, screenWidthDip);
        Top = ClampToScreen(selBottom + margin, Height, screenHeightDip);
    }

    /// <summary>把某个值夹到屏幕内，返回夹好之后的位置。</summary>
    private static double ClampToScreen(double value, double size, double screenSize)
    {
        if (value + size > screenSize) value = screenSize - size;
        return value < 0 ? 0 : value;
    }

    /// <summary>
    /// 还开着就关掉（新一轮框选开始时调用）。
    ///
    /// 【为什么需要这个】
    ///   用户反馈：「框选完成后再次框选的时候上一次的框会短暂出现零点几秒」。
    ///   查下来不是选框残影（那个在 EndOverlay 里已经置为隐藏了），
    ///   而是**上一轮的坐标提示框还挂在屏幕上**——它要活 3 秒，
    ///   这 3 秒里用户开始下一次框选，它就跟着新遮罩一起出现在画面上，看着像「上一次的框又冒出来了」。
    ///
    /// 【为什么不能直接 Close()】
    ///   WPF 的 Window 一旦 Close() 就不能再操作，对关过的窗口再调一次 Close() 会抛
    ///   InvalidOperationException。所以要先看 IsVisible，再包一层 try。
    /// </summary>
    internal void CloseIfOpen()
    {
        try
        {
            if (!IsVisible) return;
            Close();
            Core.Logging.AppLogger.Info("已关闭上一轮残留的坐标提示框（开始新一轮框选）");
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.Warn($"关闭残留提示框失败（不影响框选）：{ex.Message}");
        }
    }
}