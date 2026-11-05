using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TranslatorApp.Core.Capture;
using TranslatorApp.Core.Input;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;
using TranslatorApp.UI.SelectionOverlay;
using TranslatorApp.UI.Shared;

namespace TranslatorApp;

/// <summary>
/// App.xaml.cs —— 程序入口（阶段 2 · v0.2 组合键 + 拖拽框选）
///
/// 这个文件干嘛的：
///   程序的总开关，做六件事：
///   1) 单实例：全局互斥体，双击 exe 只允许跑一个进程；
///   2) 托盘：调用 TrayIcon.Initialize() 把通知区域图标挂上；
///   3) 主窗口：创建空壳窗口，点右上角 × 只缩到托盘、不退出程序；
///   4) 全局监听（阶段 2 新增）：安装键盘/鼠标低级钩子，把事件喂给框选状态机；
///   5) 框选反馈（阶段 2 新增）：拖动时显示遮罩选框，松手写日志 + 弹坐标提示框；
///   6) 兜底：三个全局异常事件全部写日志，不弹英文崩溃框。
///
/// 【阶段 2 的事件是怎么流动的 —— 改代码前先看懂这条链】
///   系统事件 → 低级钩子回调（只排队，1 毫秒内返回）
///            → 发 HookEventMessage 给主窗口
///            → 主窗口收到消息（MainWindowWndProc）
///            → 把队列里的事件取出来（Drain）
///            → 喂给 SelectionStateMachine
///            → 状态变化触发遮罩显示/移动/隐藏，完成时写日志 + 弹提示框
///   为什么不让钩子回调直接干活：回调慢了整个系统都会卡（鼠标发顿），
///   Windows 会直接掐掉钩子（LowLevelHooksTimeout）。所以回调只做「排队 + 发消息」。
///
/// 【重要 · 阶段 1 踩过的两个坑，改代码时不要踩回去】
///   坑 1：绝不能用 Win32 的 ShowWindow 去显示本窗口。
///         第二次双击 exe 时，新进程只能「发消息」请原进程显示窗口。
///         因为窗口是 WPF 的，被人从外面用 Win32 硬显示时，WPF 内部仍以为窗口是隐藏的，
///         于是永远不绘制内容 → 界面上就是一块黑框。见 MainWindowWndProc。
///   坑 2：显示/隐藏窗口时绝不要改 ShowInTaskbar，也不要把 WindowState 设成 Minimized。
///         改 ShowInTaskbar 会让 WPF 重建窗口句柄，重建后内容不重绘 → 又是黑框；
///         设 Minimized 会让窗口缩到任务栏（左下角），而不是缩到托盘。
///         正确做法只有一个：Hide() / Show()。见 HideMainWindow / ShowOrCreateMainWindow。
///   （阶段 2 的遮罩窗口是新窗口，不受坑 1、坑 2 约束，但要守住另一条：
///     遮罩只能用 Show() 不用 Activate()，否则会把用户焦点抢走。）
///
/// 它和谁交互：
///   - TrayIcon.cs                 托盘图标 + 右键菜单；
///   - MainWindow.xaml             空壳主窗口；
///   - Core\Input\*.cs             键盘/鼠标钩子、状态机、坐标换算；
///   - UI\SelectionOverlay\        框选遮罩窗口；
///   - UI\Shared\DebugToastWindow  坐标提示框；
///   - Core\Logging\*.cs           写 tools\日志\ 下的日志。
///
/// 要改它该动哪里：
///   - 改窗口标题   → 本文件 WindowTitle（单实例查找也用它，只此一处）；
///   - 改退出策略   → OnMainWindowClosing + TrayIcon 的「退出」菜单；
///   - 换互斥体名字 → MutexName 常量（新旧版本必须错开，否则互相顶掉）。
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = "Global\\TranslatorApp.SingleInstance";

    private static Mutex? _mutex;

    /// <summary>true = 用户从托盘菜单选了「退出」，此时窗口才允许真正关闭。</summary>
    internal static bool IsExiting { get; private set; }

    public static string AppVersion { get; } = GetVersion();

    /// <summary>窗口标题。唯一且可预测：第二个实例要靠它 FindWindow 定位已有窗口。</summary>
    internal static string WindowTitle => $"翻译软件 {AppVersion} · 框选";

    // ------------------------------------------------------------------ 阶段 2：全局监听与框选

    /// <summary>修饰键状态（Ctrl/Alt 按着没有）。</summary>
    private static readonly HotkeyState Hotkeys = new();

    /// <summary>框选状态机：判断「这次拖拽是不是一次框选」。</summary>
    private static readonly SelectionStateMachine SelectionMachine = new(Hotkeys);

    /// <summary>框选时盖在所有显示器上的遮罩窗口（用到才创建；反复使用，所以一直不关闭）。</summary>
    private static SelectionMaskWindow? _maskWindow;

    // 注意：坐标提示框**故意不缓存**。它 3 秒后会自己 Close()，而 WPF 窗口关闭后不能再 Show()，
    // 复用一个实例会导致第二次框选起就再也弹不出来。详见 OnSelectionCompleted 里的说明。

    /// <summary>每秒跑一次：状态机超时自检 + 修饰键状态校正。</summary>
    private static readonly DispatcherTimer TickTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>每 30 秒跑一次：检查钩子还在不在，不在就重装（自愈）。</summary>
    private static readonly DispatcherTimer HookWatchdog = new() { Interval = TimeSpan.FromSeconds(30) };

    /// <summary>钩子被重装过多少次（写进日志，便于排查「用一会儿就失灵」）。</summary>
    private static int _hookReinstallCount;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLogger.Info($"===== 启动 {AppVersion} =====");

        // 0) 识别质量对照实验模式：`--ocr实验 "图片路径"`
        //    这是排查「读不全」的专用工具，不启动界面、不挂钩子、不占托盘。
        string[] args = e.Args ?? Array.Empty<string>();
        if (args.Length >= 2 && args[0] == "--ocr实验")
        {
            AppLogger.Info("进入识别质量对照实验模式");
            try
            {
                OcrExperiment.Run(args[1]);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "对照实验运行失败");
            }
            Shutdown();
            return;
        }

        // 0.7) 引擎对比实验：`--引擎对比 "输出目录"`
        //     用户 2026-10-08 问「是不是已经到引擎极限了」。
        //     这个问题不能靠看图回答——看图只能看出好坏，看不出还能提升多少。
        //     所以用「自己画的已知原文的图」当标尺，
        //     让 4 个引擎 × 5 种预处理去认，再逐字算准确率，用数字回答。
        if (args.Length >= 1 && args[0] == "--引擎对比")
        {
            AppLogger.Info("进入引擎对比模式");
            string outDir = args.Length >= 2
                ? args[1]
                : global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "引擎对比");
            try
            {
                NativeLibraryPreloader.PreloadTesseractChain(
                    global::System.IO.Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty);
                EngineBenchmark.Run(outDir,
                    args.Length >= 3 ? args[2] : string.Empty,
                    args.Length >= 4 ? args[3] : "英俄");
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, "引擎对比实验运行失败");
            }
            Shutdown();
            return;
        }

        // 0.6) Tesseract 探针：`--tess探测 "图片路径"`
        //     临时诊断工具，用来确认第二个引擎到底能不能跑、准不准。
        if (args.Length >= 2 && args[0] == "--tess探测")
        {
            AppLogger.Info("进入 Tesseract 探针模式");
            try { TessProbe.Run(args[1]); }
            catch (Exception ex) { AppLogger.Error(ex, "探针运行失败"); }
            Shutdown();
            return;
        }

        // 1) 单实例

        // 1.1) 先把设置读进来。
        //      【为什么必须在这里读、而不是等到认字时才读】
        //      备用引擎 Tesseract 的开关是「读设置 → 看开没开」，
        //      如果先看开关再看设置，默认值 false 会直接把这条路堵死，
        //      设置文件永远读不到，开关也就永远打不开（这个坑踩过）。
        try
        {
            AppSettings.Load();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"读设置失败（不影响取词）：{ex.Message}");
        }

        // 1.2) 图库回放：`--回放 "清单.txt" "结果.txt"`
        //      不建窗口、不挂托盘、不碰屏幕，跑完直接退出。
        //
        //      【为什么放在「读设置之后」】识别要用设置里的 Tesseract 开关，
        //      放在前面会把那条路堵死（跟上面 1.1 是同一个坑）。
        //      【为什么放在「单实例检查之前」】回放是只读的：不截屏、不弹窗、
        //      不写用户配置，没理由因为主程序开着就跑不了。
        if (args.Length >= 3 && args[0] == "--回放")
        {
            AppLogger.Info("进入图库回放模式");
            _ = RunReplayAsync(args[1], args[2]);
            return;
        }

        if (!TryAcquireSingleInstance())
        {
            AppLogger.Info("已有实例在运行：请它显示主窗口，然后结束本次启动");
            bool notified = NativeMethods.NotifyExistingInstanceToShow(WindowTitle);
            AppLogger.Info(notified
                ? "已通知已有实例显示窗口，本次启动结束"
                : "没找到已有实例的窗口，本次启动直接结束");
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 2) 全局异常兜底（三个都要接，缺一个就会弹英文崩溃框）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 3) 主窗口（托盘在窗口 Loaded 之后再挂，原因见 CreateMainWindow 里的注释）
        CreateMainWindow();

        // 把「取词记录」存档的位置打出来。
        // 用户 2026-10-09 要求「提取出文字了软件里应该有记录的，你读取记录比对文本」，
        // 所以这个路径必须让他一眼看到，否则记录存了他也不知道。
        AppLogger.Info($"取词记录存档：{Core.Capture.CaptureRecord.CurrentFilePath}" +
                        "（每取一次词就追加一段，认错的原文会一字不截地记下来）");

        AppLogger.Info("启动流程完成");

        // 4) 翻译首次提示兜底（阶段四 T1a）。
        //    放在最后：提示卡片要摆位置，此时窗口和托盘都已经就位。
        //    这一段和托盘里点开关走的是同一个方法 —— 两条路只留一套逻辑。
        EnsureTranslateNotice();
    }

    /// <summary>
    /// 第一次打开翻译时，弹一次「文字会发到微软」的卡片；弹过就不再弹。
    ///
    /// 【为什么要有启动兜底这个入口】
    ///   托盘点开关会调它；但用户也可能直接手改 settings.json 打开翻译。
    ///   只在托盘回调里弹，手改的那次就漏掉了披露。两条路都进这里，就不会漏。
    ///
    /// 【为什么用卡片而不是确认对话框】
    ///   托盘菜单项文字已经写着「把文字发给微软」，用户点它就是知情同意；
    ///   再拦一个模态框只会挡路。⚠️ 一旦把菜单文字改成不带披露的说法，
    ///   必须同时换成弹窗确认，否则等于偷偷外发。
    /// </summary>
    internal static void EnsureTranslateNotice()
    {
        try
        {
            if (!AppSettings.TranslateEnabled || AppSettings.TranslateNoticeShown) return;

            const string title = "要开始翻译了";
            // 【别写「发完不留存」（2026-11-02 架构师 P0-C2）】
            //   微软那边保留多久、怎么处理，不是我们能担保的事，
            //   写上去就是替别人立保证。这里只陈述我们自己做什么、不做什么。
            //   关于微软侧的任何说法，必须把官方链接抄进注释，不许凭印象写。
            const string detail =
                "翻译已经开着：框到的文字会发到微软翻译服务。\n" +
                "只发文字，不发截图、不发窗口标题、不发进程名。关掉在托盘右键菜单。";

            // 8 秒：这条比原来长了一行，3 秒读不完，等于没提示。其它提示仍走默认 3 秒。
            ShowTransientNotice(title, detail, 8);

            AppSettings.SetTranslateNoticeShown();   // 存盘：下次启动不再弹
            AppLogger.Info("翻译首次提示已显示（之后不再弹）");
        }
        catch (Exception ex)
        {
            // 提示只是告知，它自己出问题绝不能影响取词
            AppLogger.Warn($"翻译首次提示没显示出来：{ex.Message}");
        }
    }

    /// <summary>
    /// 弹一条短提示，**永远新开一张卡片**。
    ///
    /// 【为什么不复用 _activeToast（2026-11-02 架构师 P0-B1）】
    ///   _activeToast 在框选后指向的是**刚出识别结果那张卡**。
    ///   复用它 = ShowNotice 把 MainText 清空并 Collapsed，
    ///   **用户正在读的识别结果当场消失**。少开一个窗口的代价是吃掉用户要的字，
    ///   这笔账怎么算都亏，所以宁可多一张。
    /// </summary>
    private static void ShowTransientNotice(string title, string detail, int seconds = 3)
    {
        try
        {
            var notice = new DebugToastWindow();
            notice.ShowNotice(title, detail, seconds);
            _activeToast = notice;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"屏幕提示没显示出来：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 单实例

    private static bool TryAcquireSingleInstance()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            return createdNew;
        }
        catch (AbandonedMutexException)
        {
            // 上一个进程异常退出留下的被遗弃互斥体：本次直接接手，继续启动
            AppLogger.Warn("发现被遗弃的互斥体（上次异常退出），本次接手继续启动");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "创建互斥体失败，跳过单实例检查继续启动");
            return true;
        }
    }

    // ------------------------------------------------------------------ 主窗口

    /// <summary>
    /// 创建主窗口、挂窗口消息钩子，并在窗口 Loaded 之后挂托盘图标。
    ///
    /// 【为什么托盘必须等 Loaded 之后再挂 —— 阶段 1 的坑】
    ///   App.OnStartup 阶段 WPF 的消息循环还没开始跑。此时调 Shell_NotifyIcon(NIM_ADD)，
    ///   Explorer 会回调本窗口，而本线程还没在派发消息，于是 Explorer 收不到回应，
    ///   直接把这次挂载判为失败（NIM_ADD 返回 false）。表现就是：
    ///   代码「执行成功」、日志无异常，但通知区域永远没有图标。
    ///   实测证据：同一个窗口句柄、同样的参数，等消息循环跑起来之后再调就成功（托盘数 19→20）。
    ///   所以这里挂到 Loaded 事件上 —— 那时消息循环已经在正常工作。
    /// </summary>
    private static void CreateMainWindow()
    {
        var win = new MainWindow();
        win.Closing += OnMainWindowClosing;
        Current.MainWindow = win;
        win.Show();

        IntPtr handle = new WindowInteropHelper(win).Handle;

        var source = HwndSource.FromHwnd(handle);
        if (source != null)
        {
            source.AddHook(MainWindowWndProc);
            AppLogger.Info($"已挂窗口消息钩子：托盘回调 msg=0x{NativeMethods.TrayCallbackMessage:X4}，" +
                           $"唤起窗口 msg=0x{NativeMethods.ShowMainWindowMessage:X4}");
        }
        else
        {
            AppLogger.Warn("取不到窗口消息源：第二个实例将无法唤起本窗口，托盘右键菜单也会失效");
        }

        AppLogger.Info($"主窗口已显示：Title=\"{win.Title}\"，hwnd=0x{handle.ToInt64():X}");

        // 立刻尝试挂托盘。若此刻 Explorer 还没准备接收（WPF 消息循环尚未开始跑），
        // TrayIcon 会自动重试若干次，日志里能看到是第几次成功的。
        TrayIcon.Initialize(handle);

        // 阶段 2：装全局监听钩子（同样要等消息循环跑起来，理由同托盘）
        InitializeSelection(handle);
    }

    /// <summary>
    /// 阶段 2：安装键盘/鼠标钩子，接上框选状态机和遮罩窗口。
    ///
    /// 【为什么钩子必须等主窗口有消息循环之后再装】
    ///   低级钩子靠本线程的消息循环派发回调。消息循环还没跑时装上的钩子收不到事件，
    ///   表现就是「代码说装上了，但按什么都没反应」——和阶段 1 托盘那个坑是同一类问题。
    ///
    /// 【钩子被系统摘掉怎么办】
    ///   Windows 不会告诉你「钩子掉了」，但睡眠唤醒后钩子通常会失效。
    ///   所以做了两层自愈：
    ///     1) 收到 WM_POWERBROADCAST 的「自动恢复」广播 → 立刻重装；
    ///     2) 每 30 秒自检一次句柄，发现没装上就重装。
    ///   每次重装都写进日志，出问题时能从日志看出「什么时候掉的」。
    /// </summary>
    private static void InitializeSelection(IntPtr notifyWindow)
    {
        SelectionMachine.Armed += OnSelectionArmed;

        // 【2026-10-10】阶段 3 期间默认开启 `Ctrl+Alt+D` 调试截图。
        //
        //   起因是 C1 连续五次都识别成「（空）」，光看日志查不出来——
        //   日志只能说「阈值 128、所有引擎 0 字」，**看不到当时截到了什么画面**。
        //   存一张图比再猜十轮都快。
        //
        //   【为什么必须在这里设，不能等到按快捷键时才设】
        //     反色后那张图是**框选当场就生成**的，
        //     如果开关是「按 `Ctrl+Alt+D` 时才打开」，
        //     **第一次框选时那张图就已经错过了**，怎么按都补不回来。
        //
        //   【安全说明，不会偷偷存】
        //     只有**主动按 `Ctrl+Alt+D`** 才会把截图写盘，
        //     平时框选一次都不存。
        //     存到程序目录下的 `debug-screenshots` 文件夹。
        //
        //   【阶段 4 之前会关掉】验收通过后就不再需要它了。
        DebugScreenshotSaver.Enabled = true;
        AppLogger.Info("调试截图已开启：框选后按 Ctrl+Alt+D 可把刚才那块区域存成 PNG");
        SelectionMachine.DragStarted += OnDragStarted;
        SelectionMachine.DragUpdated += OnDragUpdated;
        SelectionMachine.Cancelled += OnSelectionCancelled;
        SelectionMachine.Completed += OnSelectionCompleted;

        bool keyboardOk = LowLevelKeyboardHook.Install(notifyWindow);
        bool mouseOk = LowLevelMouseHook.Install(notifyWindow);

        if (!keyboardOk || !mouseOk)
        {
            AppLogger.Error($"全局监听未能全部装上（键盘={keyboardOk}，鼠标={mouseOk}）："
                          + "框选功能不可用。常见原因是本程序权限低于其它程序，请以管理员身份运行试试");
        }

        AppLogger.Info($"框选环境：{ScreenGeometry.DescribeVirtualScreen()}");
        AppLogger.Info("按住 Ctrl+Alt 再按住鼠标左键拖动即可框选；松开 Ctrl/Alt、按 Esc 或框太小都会取消");

        TickTimer.Tick += (_, _) =>
        {
            SelectionMachine.Tick();       // 超时自检（拖太久强制取消）
            Hotkeys.CorrectFromSystem();   // 修饰键状态校正（防漏事件导致状态卡住）
        };
        TickTimer.Start();

        HookWatchdog.Tick += (_, _) => EnsureHooksAlive();
        HookWatchdog.Start();
    }

    /// <summary>自检钩子是否还在，不在就重装（阶段 2 异常表要求「静默失灵」必须有自愈）。</summary>
    private static void EnsureHooksAlive()
    {
        if (LowLevelKeyboardHook.IsInstalled && LowLevelMouseHook.IsInstalled) return;

        _hookReinstallCount++;
        AppLogger.Warn($"检测到钩子不在岗（第 {_hookReinstallCount} 次自愈），正在重装");

        IntPtr hwnd = new WindowInteropHelper(Current.MainWindow!).Handle;
        LowLevelKeyboardHook.Install(hwnd);
        LowLevelMouseHook.Install(hwnd);
        Hotkeys.Reset();
        SelectionMachine.Tick(); // 状态机回空闲，避免拿着旧状态继续框选
    }

    /// <summary>把钩子队列里的事件取出来喂给状态机（只在这里做，不在钩子回调里）。</summary>
    private static void DrainHookEvents()
    {
        LowLevelKeyboardHook.Drain(evt =>
        {
            // 调试截图热键 Ctrl+Alt+D：只有按住 Ctrl+Alt 再按 D 才算，
            // 否则平时打字按到 D 也会被误当成热键。
            if (evt.IsDown && evt.VirtualKey == 0x44 && Hotkeys.IsComboDown)
            {
                TryDebugShortcut();
                return;
            }
            SelectionMachine.HandleKey(evt.VirtualKey, evt.IsDown);
        });

        LowLevelMouseHook.Drain(evt =>
        {
            // 托盘菜单弹着的时候，点菜单外面要把它收起来（2026-11-04 T3b-补）。
            // 本程序是纯 WPF 宿主，没有 WinForms 消息泵，菜单自己不收；
            // 反正鼠标钩子本来就看得到每一次按下，顺手判一下坐标就够了。
            // 收菜单的那一下点击不再喂给状态机——用户是想关菜单，不是想框选。
            bool isDown = evt.Kind is MouseHookKind.LeftDown or MouseHookKind.RightDown;
            if (isDown && TrayIcon.CloseMenuIfClickOutside(evt.X, evt.Y)) return;

            SelectionMachine.HandleMouse(evt);
        });

        // 关键顺序：事件喂完之后再看 Ctrl+Alt 状态，按状态切换遮罩。
        UpdateComboOverlay();
    }

    /// <summary>
    /// `Ctrl+Alt+D`：把最近一次框选区域的截图存下来。
    ///
    /// 【为什么需要它】
    ///   「没识别出文字」光看日志是查不出来的——
    ///   得看到当时截到的到底是什么画面（框歪了？全黑？有反光？）。
    ///   默认是**关**的：不主动开启就绝不会把屏幕内容写进磁盘。
    /// </summary>
    private static void TryDebugShortcut()
    {
        // 🔴【2026-10-10】**每一按都留痕**——
        //   用户反馈「按了没有任何反馈，也不知道截图成没成功」。
        //   以前成功与否全靠后面的日志去推断，
        //   **一旦中间哪个环节没走到（像漏监听 D 键那次），就一个字都看不到**，
        //   双方只能干瞪眼。所以这里先把「按到了」这件事本身记下来。
        AppLogger.Info("收到 Ctrl+Alt+D：准备保存刚才那块区域的截图");

        if (!DebugScreenshotSaver.Enabled)
        {
            AppLogger.Warn("调试截图未开启，已跳过");
            return;
        }

        // 用户主动按了，说明「他想看」——
        // 之后框选当场生成的中间图（比如反色后那张）也一并存下来。
        DebugScreenshotSaver.Requested = true;

        try
        {
            string? saved = DebugScreenshotSaver.SaveLastRegion();
            ShowDebugNotice(saved);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"调试截图失败：{ex.Message}");
            ShowDebugNotice(null);
        }
    }

    /// <summary>
    /// 把「调试截图成没成功」**在屏幕上告诉用户**。
    ///
    /// 【为什么必须有这一步：2026-10-10 用户反馈】
    ///   「按了没有任何反馈，也不知道截图成没成功」。
    ///   查下来图**其实存下来了**——**做了事但不吭声，等于没做**。
    ///   日志是给开发看的，用户不会去翻；**屏幕上看得见才算数**。
    ///
    /// 【不复用现成提示框，见 ShowTransientNotice 的注释】
    ///   以前这里复用 _activeToast，会把刚出的识别结果清掉。现在永远新开一张。
    /// </summary>
    private static void ShowDebugNotice(string? savedPath)
    {
        string title = savedPath is null ? "调试截图没成功" : "调试截图已保存";
        string detail = savedPath is null
            ? "请看日志里的原因（可能是还没框选过、或那块区域截不到）"
            : Path.GetFileName(savedPath);

        ShowTransientNotice(title, detail);
    }

    /// <summary>遮罩当前是不是因为「Ctrl+Alt 按着」而显示（避免反复 Show/Hide）。</summary>
    private static bool _comboOverlayShown;

    /// <summary>
    /// 按住 Ctrl+Alt：只把上一轮残留的提示框收掉，**不铺遮罩**；松开时若有遮罩就收回去。
    ///
    /// 【2026-10-15 用户反馈后改：遮罩不再一按组合键就冒出来】
    ///   用户原话：「我按住 Ctrl+Alt 的时候屏幕会闪一下灰罩，我不要这个，
    ///   等我真的按下左键开始拖了再出现。」
    ///
    ///   【为什么现在敢延后了】
    ///     原来必须在「组合键一按下」就铺遮罩，是因为**左键那一下会被下面的软件吃掉**，
    ///     遮罩事后才铺就追不上，窗口已经开始拖动了。
    ///     但 2026-10-09 之后 `src\Core\Input\LowLevelMouseHook.cs` 改了规矩：
    ///     **Ctrl+Alt 按着时的左键按下，在钩子里就被吃掉**（`eatThisClick`），
    ///     下面的软件根本收不到这一次点击。
    ///     「不让下面的软件选中文字」这件事已经由钩子负责，
    ///     遮罩现在只负责**给用户看选框**，所以可以等到真开始拖再铺。
    ///
    ///   【代价（如果有的话）】
    ///     组合键按着、左键点一下**不拖**（框太小不算）——这种点击本来就被钩子吃了，
    ///     下面软件收不到，不会选中文字，所以不铺也没事。
    /// </summary>
    private static void UpdateComboOverlay()
    {
        bool comboDown = Hotkeys.IsComboDown;
        if (comboDown == _comboOverlayShown) return;

        _comboOverlayShown = comboDown;

        if (comboDown)
        {
            // 一开始新一轮框选，先把上一轮还没消失的坐标提示框收掉。
            // 不收的话它会跟着新遮罩一起出现在画面上，
            // 用户看着就像「上一次的框又闪了一下」（2026-10-08 用户反馈的原话）。
            CloseActiveToast();
        }
        else
        {
            // 松开组合键：拖到一半松开也不打断（见 SelectionStateMachine），
            // 但遮罩该收了。
            _maskWindow?.EndOverlay();
        }
    }

    /// <summary>当前还开着的坐标提示框（可能为空，也可能已经自己关了）。</summary>
    private static DebugToastWindow? _activeToast;

    /// <summary>关掉还开着的坐标提示框，让新一轮框选从干净的画面开始。</summary>
    private static void CloseActiveToast()
    {
        _activeToast?.CloseIfOpen();
        _activeToast = null;
    }

    // ------------------------------------------------------------------ 框选反馈

    /// <summary>
    /// 左键刚按下去、组合键按着 —— **这里现在什么都不铺**，只记一条日志。
    ///
    /// 【历史：2026-10-09 这里必须铺遮罩，否则下面软件会选中文字】
    ///   用户说：「不松开 ctrl 和 alt 再次摁左键再次框选，
    ///   鼠标会错误的可以选中文本，应该是没有灰色遮罩导致的。」
    ///   当时的依据是「本程序不在钩子里吃鼠标事件（铁律），全靠遮罩挡」，
    ///   而遮罩要等移动 3 像素才铺，那个空档正好被撞上。
    ///
    /// 【2026-10-15 为什么不铺了】
    ///   那条依据**已经不成立**：`src\Core\Input\LowLevelMouseHook.cs` 现在
    ///   **Ctrl+Alt 按着时的左键按下，在钩子里就被吃掉**（`eatThisClick`，返回 1），
    ///   下面的软件根本收不到这一次点击，也就不可能开始选中文字。
    ///   遮罩不再承担「挡住下面的软件」这个职责，只剩下「给用户看选框」。
    ///
    ///   而用户要求「别一按组合键就闪灰罩，等他真开始拖了再出现」——
    ///   所以铺遮罩的时机统一挪到 `OnDragStarted`（拖过 3 像素）。
    /// </summary>
    private static void OnSelectionArmed()
    {
        AppLogger.Info(
            $"左键已按下（这次点击已在钩子里拦住，累计 {LowLevelMouseHook.SwallowedLeftDown} 次）；" +
            "遮罩等真开始拖动再铺 —— 用户要求不要一按 Ctrl+Alt 就闪灰罩");
    }

    private static void OnDragStarted(NativeMethods.RECT rect)
    {
        AppLogger.Info($"开始框选：({rect.Left},{rect.Top}) 起手");
        _maskWindow ??= new SelectionMaskWindow();
        _maskWindow.BeginOverlay();
        _maskWindow.UpdateSelection(rect);
    }

    private static void OnDragUpdated(NativeMethods.RECT rect)
        => _maskWindow?.UpdateSelection(rect);

    private static void OnSelectionCancelled()
    {
        _maskWindow?.EndOverlay();
    }

    private static void OnSelectionCompleted(SelectionSession session)
    {
        _maskWindow?.EndOverlay();

        // 记住这块区域，给 Ctrl+Alt+D 调试截图用
        DebugScreenshotSaver.RememberRegion(session.PhysicalRect);

        var request = new CaptureRequest
        {
            PhysicalRect = session.PhysicalRect,
            DipRect = session.DipRect,
            MonitorRect = session.MonitorRect,
            MonitorDpi = session.MonitorDpi,
            TriggerMode = session.TriggerMode,

            // 此刻遮罩已经收起、提示框不抢焦点，前台窗口仍然是用户正在看的那个软件。
            // 只记进程名（chrome.exe 之类），不记网址、不记窗口标题。
            SourceProcess = NativeMethods.ForegroundProcessName()
        };

        DebugToastWindow? toast = null;

        // 【踩过的坑 —— 只有连测 20 次才暴露出来的 bug】
        //   提示框 3 秒后会自动 Close()，而 WPF 窗口一旦关闭就**不能再 Show()**。
        //   原来写成「复用一个实例」（_toastWindow ??= ...），结果：
        //     第一次框选能看到坐标，第二次起全部抛 InvalidOperationException
        //     （关闭窗口后，无法设置可见性），界面上一个提示框都不出现，
        //     只有日志里躺着一串 ERROR —— 光看界面根本发现不了。
        //   改成：每次框选都新建一个提示框，让它自己 3 秒后消失，WPF 会自行回收。
        try
        {
            toast = new DebugToastWindow();
            toast.ShowFor(session);

            // 先显示坐标**是为了立刻给反馈**：取词要几百毫秒，
            // 干等着会让用户以为「没反应」。识别完再把这个卡片换成文字。
            _activeToast = toast;
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "显示坐标提示框失败（不影响框选本身）");
        }

        var card = toast;
        if (card is null) return;

        // 取词整个丢到后台（剪贴板走 UI 线程但全程 await，OCR 走专用线程）
        _ = RunCaptureAsync(request, card);
    }

    /// <summary>取词 + 把结果填回提示卡。所有异常都在这里兜住，绝不让它把程序带崩。</summary>
    /// <summary>
    /// 图库回放：跑完清单里所有图，然后关掉进程。
    /// 失败也要关——否则命令行会一直挂着，脚本那边等不到返回。
    /// </summary>
    private static async Task RunReplayAsync(string listPath, string resultPath)
    {
        try
        {
            await ReplayRunner.RunAsync(listPath, resultPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "图库回放失败");
        }
        finally
        {
            Current?.Shutdown();
        }
    }

    private static async Task RunCaptureAsync(CaptureRequest request, DebugToastWindow card)
    {
        CaptureResult result;
        try
        {
            result = await CaptureDispatcher.Shared.CaptureAsync(request, CancellationToken.None)
                                                  .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "取词流程异常（不影响程序运行）");
            result = new CaptureResult
            {
                Failure = CaptureFailure.Unknown,
                Detail = ex.Message
            };
        }

        // 认字成功才谈翻译。认字都没成，把原文（其实是错误说明）发出去没有任何意义。
        bool wantTranslate = result.Success && AppSettings.TranslateEnabled;
        Core.Translate.TranslationResult? translation = null;

        // ==========================================================
        // 【2026-11-05 第16轮 P0-J2：原文本来就是目标语言 → 连「翻译中…」都不画】
        //   原来只要开关开着就先进翻译态，而「本地判成中文就跳过」那个判断在
        //   TranslationService.TranslateAsync 里才发生 —— 那时卡片上已经画出了
        //   「翻译中…」，几十毫秒后才收起。用户框中文界面，每次都看到闪一下，
        //   像程序在干傻事。
        //
        //   这里提前判一次，命中就直接把原文交出去（只调一次 UpdateWith，
        //   和「翻译关着」那条路径长得一模一样）。
        //
        //   【为什么 TranslationService 里那个判断要留着，这不是重复】
        //     服务层那道是**最后防线**：万一这里判错、或者将来有人改坏这里、
        //     或者别的地方直接调 TranslateAsync —— 中文照样不会被发到必应。
        //     「不出网」这件事必须有两道锁，一道在调用方（省一次白等），
        //     一道在服务层（保底不发）。删掉任何一道都是拿隐私赌运气。
        // ==========================================================
        bool skipAsNative =
            Core.Translate.TranslationService.IsChineseTarget(AppSettings.TranslateTargetLang)
            && Core.Capture.LanguageDetector.IsMostlyChinese(result.Text);

        if (wantTranslate && skipAsNative)
        {
            // 原文本来就是目标语言，翻也翻不出东西 —— 直接把原文交出去，
            // **连「翻译中…」都不画**。translation 保持 null，下面是 else 分支同款收尾。
            AppLogger.Info("翻译：跳过（原文已经是目标语言，没有发出去）");
        }
        else if (wantTranslate)
        {
            try { card.UpdateWith(result, null, translating: true); }
            catch (Exception ex) { AppLogger.Warn($"显示「翻译中…」失败（不影响原文）：{ex.Message}"); }

            // ==========================================================
            // 【2026-11-03 预算改固定，删掉「从认字时间里扣」的减法】
            //   原来 budget = 2500 - 认字耗时。真机实测认字冷启动 1922~4031 ms，
            //   budget 直接 ≤0 → 翻译一个请求都不发 → 用户第一次用看到
            //   原文 +「翻译超时」，以为功能坏了。那是每个用户的第一印象，
            //   不是边缘情况。
            //
            //   现在翻译拿独立预算（HardTimeoutMs，从翻译开始算，认字不算在内）：
            //     · 用户此刻已经有原文可读，翻译不是阻塞项；
            //     · 删掉减法 = 删掉一整类边界情况（「只剩 300 ms 预算」彻底消失）。
            //   这个常数 2026-11-03 又从 2500 提到 5000，理由见
            //   TranslationService.HardTimeoutMs 上的注释（显示口径变了，用户不再盯空白）。
            // ==========================================================
            int budget = Core.Translate.TranslationService.HardTimeoutMs;
            try
            {
                translation = await Core.Translate.TranslationService.Shared
                    .TranslateAsync(result.Text, budget, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // TranslateAsync 自己说「永远不抛」，这里再兜一层：
                // 翻译整个崩了也必须走到下面那次 UpdateWith，原文才能显出来。
                AppLogger.Warn($"翻译出错（不影响原文显示）：{ex.GetType().Name} {ex.Message}");
            }
        }

        try
        {
            card.UpdateWith(result, translation);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"更新提示框失败（不影响取词结果）：{ex.Message}");
        }
    }

    /// <summary>
    /// 窗口消息处理：
    ///   1) 资源管理器重启广播 → 把托盘图标重挂回去；
    ///   2) 托盘上的鼠标动作 → 交给 TrayIcon；
    ///   3) 第二个实例发来的「显示主窗口」请求 → 用 WPF 方式显示窗口（绝不能用 Win32 硬显示 → 黑框）；
    ///   4) 阶段 2：钩子事件唤醒消息 → 取事件喂状态机；
    ///   5) 阶段 2：睡眠唤醒广播 → 重装钩子。
    /// </summary>
    private static IntPtr MainWindowWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 资源管理器重启（卡死、崩溃、被任务管理器结束）→ 通知区域被重建，所有托盘图标都没了。
        // Windows 广播 TaskbarCreated 就是叫有图标的程序赶紧重挂，这里必须接住，否则图标永久消失。
        if (NativeMethods.TaskbarCreatedMessage != 0 &&
            (uint)msg == NativeMethods.TaskbarCreatedMessage)
        {
            TrayIcon.ReAddAfterExplorerRestart();
            handled = true;
            return IntPtr.Zero;
        }

        if ((uint)msg == NativeMethods.TrayCallbackMessage)
        {
            TrayIcon.HandleMouseMessage(lParam.ToInt32());
            handled = true;
            return IntPtr.Zero;
        }

        if (NativeMethods.ShowMainWindowMessage != 0 &&
            (uint)msg == NativeMethods.ShowMainWindowMessage)
        {
            AppLogger.Info("收到「显示主窗口」请求（来自第二个实例）");
            ShowOrCreateMainWindow();
            handled = true;
        }

        if ((uint)msg == NativeMethods.HookEventMessage)
        {
            DrainHookEvents();
            handled = true;
        }

        if ((uint)msg == NativeMethods.WM_POWERBROADCAST &&
            wParam.ToInt32() == NativeMethods.PBT_APMRESUMEAUTOMATIC)
        {
            AppLogger.Info("收到「从睡眠自动恢复」广播：钩子可能已被系统摘掉，正在重装");
            _hookReinstallCount++;
            LowLevelKeyboardHook.Uninstall();
            LowLevelMouseHook.Uninstall();
            LowLevelKeyboardHook.Install(hwnd);
            LowLevelMouseHook.Install(hwnd);
            Hotkeys.Reset();
        }

        return IntPtr.Zero;
    }

    private static void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (IsExiting) return;   // 托盘「退出」→ 放行，真正关闭
        e.Cancel = true;         // 其他情况（点 ×）→ 只缩到托盘，程序继续常驻
        HideMainWindow();
    }

    internal static void HideMainWindow()
    {
        var win = Current?.MainWindow;
        if (win == null) return;
        try
        {
            // 只调 Hide()。窗口一隐藏，任务栏按钮自己就没了，不需要动 ShowInTaskbar
            //（动它会让 WPF 重建窗口句柄 → 内容不重绘 → 黑框，见文件头注释「坑 2」）。
            win.Hide();
            AppLogger.Info("主窗口已隐藏（任务栏按钮消失，托盘图标仍在）");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "隐藏主窗口失败");
        }
    }

    internal static void ShowOrCreateMainWindow()
    {
        var win = Current?.MainWindow;
        if (win == null)
        {
            CreateMainWindow();
            return;
        }

        try
        {
            win.Show();
            win.WindowState = WindowState.Normal;
            win.Activate();
            win.Topmost = true;
            win.Topmost = false;
            AppLogger.Info("主窗口已显示到前台");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "显示主窗口失败");
        }
    }

    /// <summary>
    /// 让主窗口的翻译开关跟着配置文件变（阶段四 T3b，2026-11-04）。
    ///
    /// 【为什么要有它】托盘菜单和主窗口各有一个开关，改一处不通知另一处，
    ///   用户会看到「托盘勾着、主窗口没勾」这种自相矛盾的界面。
    /// 【为什么窗口可能是 null】主窗口点 × 就藏起来，但对象还在；真正的 null 只出现在
    ///   还没建窗口的极短窗口期（比如托盘在 Loaded 里就挂上了）。所以这里必须判空。
    /// </summary>
    internal static void SyncTranslateUiFromSettings()
    {
        if (Current?.MainWindow is MainWindow win) win.SyncFromSettings();
    }

    internal static void ExitApplication()
    {
        IsExiting = true;
        AppLogger.Info("用户选择「退出」，开始关闭程序");
        try { Current?.Shutdown(); }
        catch (Exception ex) { AppLogger.Error(ex, "退出失败"); }
    }

    // ------------------------------------------------------------------ 异常兜底

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppLogger.Error(e.Exception, "[UI 线程未处理异常]");
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception
                 ?? new Exception(e.ExceptionObject?.ToString() ?? "未知异常");
        AppLogger.Error(ex, "[非 UI 线程未处理异常]");
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error(e.Exception, "[未观察的 Task 异常]");
        e.SetObserved();
    }

    // ------------------------------------------------------------------ 退出清理

    protected override void OnExit(ExitEventArgs e)
    {
        // 阶段 2：先摘钩子再退，不装钩子就退出会留下悬空的全局钩子
        try { TickTimer.Stop(); } catch { }
        try { HookWatchdog.Stop(); } catch { }
        try { LowLevelKeyboardHook.Uninstall(); } catch { }
        try { LowLevelMouseHook.Uninstall(); } catch { }
        try { _maskWindow?.Close(); } catch { }

        // 阶段 3：释放缓存的 OCR 引擎（WinRT 对象不释放会留句柄）
        try { OcrWorker.Shutdown(); } catch { }

        try { TrayIcon.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        AppLogger.Info("已退出");
        base.OnExit(e);
    }

    private static string GetVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        if (v == null) return "v0.3.2.10";

        // 【2026-10-10 顺带修的老毛病】
        //   原来只显示 `v{Major}.{Minor}`，也就是 **v0.3.1 和 v0.3.2 显示出来一模一样**。
        //   我们一版一版地改、一版一版地测，结果用户看到的版本号根本分不出来是哪一版——
        //   **测的是哪一版都说不清，出了问题也对不上号。**
        //   修订号（Patch）现在也显示出来。
        //
        // 【2026-10-13 再修一次：第 4 段从来没显示过】
        //   上一条只补到了第 3 段（`v.Build`），可我们的版本号有**四段**（第 4 段是「内部小更新号」）。
        //   结果 V0.3.2.3 ~ V0.3.2.9 的窗口标题、弹窗内容、任务栏标题、小图标标题
        //   **全都写着 `v0.3.2`，长得一模一样**（用户 2026-10-13 报的）。
        //   第 4 段在 .NET 里叫 `Revision`，现在一起显示出来。
        return $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }
}
