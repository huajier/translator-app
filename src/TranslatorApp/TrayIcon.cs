using System;
using System.IO;
using System.Windows.Threading;
using System.Windows.Forms;
using TranslatorApp.Core.Logging;
using TranslatorApp.Core.Native;

namespace TranslatorApp;

/// <summary>
/// TrayIcon.cs —— 托盘图标（阶段 1）
///
/// 这个文件干嘛的：
///   在屏幕右下角通知区域挂一个图标；右键弹出菜单「显示主窗口 / 退出」；双击图标唤起主窗口。
///
/// 它和谁交互：
///   - Core\Native\NativeMethods.cs 真正干活的 Win32 调用（Shell_NotifyIcon）；
///   - App.xaml.cs                  创建主窗口后调用 Initialize()，并把托盘鼠标消息转发给 HandleMouseMessage()；
///   - Core\Logging\AppLogger.cs    每次挂载尝试的结果与每个 catch 都写日志。
///
/// 【阶段 1 踩的两个坑，都在这个文件里解决】
///   坑 1：WinForms 的 NotifyIcon 会把 Shell_NotifyIcon 的失败吞掉 —— 挂载失败时 Visible 仍是 true，
///         日志里也看不出异常，于是「代码说成功了、通知区域里没图标」。
///         解决：改调 NativeMethods.TrayAdd()，把返回值和 Windows 反查到的图标位置写进日志。
///   坑 2：程序刚启动时 WPF 的消息循环还没开始跑，此刻 Shell_NotifyIcon(NIM_ADD) 会被 Explorer 拒掉。
///         解决：失败后按固定间隔自动重试若干次（日志显示第几次成功），成功即停。
///
/// 要改它该动哪里：
///   - 菜单文字、项数   → EnsureMenu()；
///   - 重试次数与间隔   → MaxAttempts / RetryInterval；
///   - 图标来源与提示语 → AttemptAdd()（图标本体在 NativeMethods 的 IDI_APPLICATION，阶段 9 换成 assets\tray.ico）。
/// </summary>
public static class TrayIcon
{
    private const int MaxAttempts = 6;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(400);

    private static ContextMenuStrip? _menu;
    private static DispatcherTimer? _retryTimer;
    private static System.Drawing.Icon? _icon;
    private static IntPtr _ownerHwnd = IntPtr.Zero;
    private static bool _added;
    private static int _attempts;

    /// <summary>
    /// 挂托盘图标。必须传入一个已经存在的窗口句柄（本程序用主窗口），
    /// 托盘上的鼠标动作会以消息形式发到那个窗口。
    /// </summary>
    public static void Initialize(IntPtr ownerHwnd)
    {
        if (_added) return;

        if (ownerHwnd == IntPtr.Zero)
        {
            AppLogger.Error("托盘挂载失败：没有可用的窗口句柄");
            return;
        }

        _ownerHwnd = ownerHwnd;
        EnsureMenu();
        AttemptAdd();
    }

    private static void EnsureMenu()
    {
        if (_menu != null) return;

        _menu = new ContextMenuStrip();
        _menu.Items.Add("显示主窗口", null, (_, _) => App.ShowOrCreateMainWindow());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("退出", null, (_, _) => Exit());
    }

    /// <summary>
    /// 取托盘图标。图标文件的位置用 Environment.ProcessPath 定位 exe 所在目录，再 Path.Combine 到
    /// assets\tray.ico（阶段 1 技术要点 9：不许用字符串拼路径，中文/空格路径会炸）。
    /// 文件缺失或损坏一律回退到系统默认图标并记日志，绝不因为图标出事就启动失败。
    /// </summary>
    private static IntPtr EnsureIcon()
    {
        try
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;
            string iconPath = Path.Combine(exeDir, "assets", "tray.ico");

            if (!File.Exists(iconPath))
            {
                AppLogger.Warn($"托盘图标文件不存在，改用系统默认图标：{iconPath}");
                return IntPtr.Zero;
            }

            if (_icon == null)
            {
                _icon = new System.Drawing.Icon(iconPath);
                AppLogger.Info($"托盘图标来源：{iconPath}");
            }

            return _icon.Handle;
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "读取托盘图标失败，改用系统默认图标");
            return IntPtr.Zero;
        }
    }

    private static void AttemptAdd()
    {
        _attempts++;

        try
        {
            bool ok = NativeMethods.TrayAdd(_ownerHwnd, $"翻译软件 {App.AppVersion}", EnsureIcon(), out string diag);

            if (ok)
            {
                _added = true;
                StopRetry();
                AppLogger.Info($"托盘图标已挂上通知区域（第 {_attempts} 次尝试）：" + diag);
                return;
            }

            // 第一次失败时补一句环境诊断（本程序权限 vs 任务栏权限），日志里能直接看到根因
            if (_attempts == 1)
            {
                AppLogger.Error("托盘环境诊断：" + NativeMethods.DiagnoseTrayEnvironment());
            }

            AppLogger.Error($"托盘图标第 {_attempts} 次挂载失败：" + diag);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, $"托盘图标第 {_attempts} 次挂载异常");
        }

        if (_attempts >= MaxAttempts)
        {
            StopRetry();
            AppLogger.Error($"托盘图标重试 {MaxAttempts} 次仍失败，通知区域不会有图标（右键菜单与双击唤起都不可用）");
            return;
        }

        StartRetry();
    }

    private static void StartRetry()
    {
        if (_retryTimer != null) return;

        _retryTimer = new DispatcherTimer(RetryInterval, DispatcherPriority.Normal, (_, _) => AttemptAdd(), App.Current.Dispatcher);
        _retryTimer.Start();
    }

    private static void StopRetry()
    {
        if (_retryTimer == null) return;
        _retryTimer.Stop();
        _retryTimer = null;
    }

    /// <summary>由主窗口消息钩子转发过来的托盘鼠标动作（lParam 就是鼠标消息号）。</summary>
    public static void HandleMouseMessage(int mouseMessage)
    {
        const int WM_RBUTTONUP = 0x0205;      // 右键抬起 → 弹菜单
        const int WM_LBUTTONDBLCLK = 0x0203;  // 左键双击 → 显示主窗口

        if (mouseMessage == WM_RBUTTONUP) ShowMenu();
        else if (mouseMessage == WM_LBUTTONDBLCLK) App.ShowOrCreateMainWindow();
    }

    private static void ShowMenu()
    {
        try
        {
            if (_menu == null) return;
            _menu.Show(Cursor.Position);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "显示托盘菜单失败");
        }
    }

    /// <summary>摘掉托盘图标。程序退出时调用；菜单里的「退出」也走这里。</summary>
    public static void Dispose()
    {
        try
        {
            StopRetry();

            bool hadIcon = _added;

            if (_added && _ownerHwnd != IntPtr.Zero)
            {
                NativeMethods.TrayRemove(_ownerHwnd);
                _added = false;
            }

            _menu?.Dispose();
            _menu = null;
            _icon?.Dispose();
            _icon = null;
            _ownerHwnd = IntPtr.Zero;

            // 没挂过就别记「已移除」，否则第二个实例退出的日志会让人误以为它也有托盘
            if (hadIcon) AppLogger.Info("托盘图标已移除");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "移除托盘图标失败");
        }
    }

    private static void Exit()
    {
        Dispose();
        App.ExitApplication();
    }
}
