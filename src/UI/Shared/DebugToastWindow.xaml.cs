using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TranslatorApp.Core.Input;
using TranslatorApp.Core.Native;

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
    /// </summary>
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

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

        // 卡片一关就把轮询停掉，别让一个看不见的定时器一直跑
        Closed += (_, _) =>
        {
            _hoverTimer.Stop();
            _closeTimer.Stop();
        };
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
        else if (!_closeTimer.IsEnabled)
        {
            // 刚移开：从头开始 3 秒
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
        MainText.Text = $"({session.PhysicalRect.Left}, {session.PhysicalRect.Top})  {session.PhysicalWidth} × {session.PhysicalHeight}";
        DetailText.Text = "正在识别，请稍候…";

        (_monitorRect, _monitorDpi) = ScreenGeometry.GetMonitorAt(session.PhysicalRect.Left, session.PhysicalRect.Top);
        _anchorLeftDip = session.DipRect.Left;
        _anchorTopDip = session.DipRect.Top;
        _selectionWidthPx = session.PhysicalWidth;
        _selectionHeightPx = session.PhysicalHeight;

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

        _closeTimer.Stop();
        _closeTimer.Interval = PendingCloseDelay;
        _closeTimer.Start();
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
    {
        if (!IsVisible) return;   // 用户已经等不及关掉了

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

            bool needScroll = result.Text.Length > 120;
            DetailText.Text =
                $"{ChannelNameOf(result.Channel)}    {Core.Capture.LanguageDetector.DisplayName(result.DetectedLanguage)}    " +
                $"用时 {result.ElapsedMs} ms" +
                (result.LineCount > 0 ? $"    {result.LineCount} 行" : string.Empty) +
                (needScroll ? $"    共 {result.Text.Length} 字，可滚动查看" : string.Empty);
        }
        else
        {
            // 失败要说清楚为什么，别让用户对着空气发呆
            TitleText.Text = "没取到文字";
            MainText.Text = result.ToUserMessage();
            DetailText.Text = result.Detail.Length > 90 ? result.Detail[..90] + "…" : result.Detail;
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
        //   现在必须把 Interval 显式改回 3 秒——
        //   ShowFor() 那里把它设成了 8 秒兜底，定时器是同一个，不能忘了改回来。
        // ==========================================================
        _closeTimer.Stop();
        _closeTimer.Interval = TimeSpan.FromSeconds(CloseDelaySeconds);
        _closeTimer.Start();
    }

    private static string ChannelNameOf(string channel) => channel switch
    {
        "Clipboard" => "来自复制（原始文字）",
        "WindowsOcr" => "来自文字识别",
        // 2026-10-09 引擎分工：英文/俄文由 Tesseract 认，必须如实告诉用户是谁认的，
        // 不能明明用了它却还显示「来自文字识别」
        "Tesseract" => "来自 Tesseract（英文/俄文专用引擎）",
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