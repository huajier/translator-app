using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TranslatorApp.Core.Logging;

namespace TranslatorApp.Core.Native;

/// <summary>
/// 把一个原生 DLL 按**绝对路径**预先加载进进程。
///
/// 【这是干什么的，为什么需要它 —— 阶段 3 加 Tesseract 时踩出来的】
///   Tesseract 的 .NET 封装（InteropDotNet）在找它的原生库时，
///   用的是「**当前程序集所在目录**」拼文件名。
///   而本项目是**单文件打包**（PublishSingleFile），
///   单文件下程序集的 `Location` 是**空字符串**，
///   于是拼出来的路径为 null，直接抛 `ArgumentNullException: path1`，
///   Tesseract 一初始化就崩。
///
///   绕开的办法：趁它还没去找之前，我们自己用**绝对路径**把 DLL 加载进进程。
///   Windows 的模块加载器会按「模块名」缓存——
///   只要 tesseract50.dll 已经躺在进程里了，
///   封装层再调 LoadLibrary("tesseract50.dll") 就能直接命中，
///   不会再走那条拼空路径的逻辑。
///
///   这样就**不用为了 Tesseract 放弃单文件打包**。
///
/// 【为什么写在本文件】
///   决策约束 C2：所有原生互操作一律集中在这里，业务类不许自己碰。
///
/// 【安全】
///   只在本进程内加载，不注入别的程序，也不联网下载任何东西。
/// </summary>
public static class NativeLibraryPreloader
{
    /// <summary>已经加载过的记一下，避免重复加载。</summary>
    private static readonly HashSet<string> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>
    /// 预加载 <paramref name="fileName"/>（在 <paramref name="directory"/> 下找）。
    /// 找不到就返回 false，不在这里抛异常——由调用方决定要不要报错。
    /// </summary>
    public static bool Preload(string directory, string fileName)
    {
        string fullPath = System.IO.Path.Combine(directory, fileName);

        lock (Gate)
        {
            if (Loaded.Contains(fullPath)) return true;

            try
            {
                if (!System.IO.File.Exists(fullPath)) return false;

                NativeLibrary.Load(fullPath);
                Loaded.Add(fullPath);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"预加载原生库失败（不影响主流程）：{fileName} —— {ex.GetType().Name} {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 从 exe 所在目录的 x64\ 子目录里预加载 Tesseract 依赖的原生库。
    ///
    /// 【2026-10-10 换引擎 5.0 → 5.5.3：这里整个重写了】
    ///
    ///   **5.5.3 的依赖不是两个，是 28 个**：
    ///     5.0  只要 leptonica-1.82.0.dll + tesseract50.dll
    ///     5.5.3 libtesseract-5.dll 拖着一整条链——
    ///           libstdc++-6(25MB)、libgcc、libwinpthread、leptonica、
    ///           png/jpeg/tiff/webp/zlib/lzma/zstd/brotli、libarchive、libunistring…
    ///
    ///   **而且文件名全变了**：
    ///     5.0   tesseract50.dll       + leptonica-1.82.0.dll
    ///     5.5.3 libtesseract-5.dll    + libleptonica-6.dll
    ///
    ///   【为什么不能把顺序和名字写死】
    ///     ① 写死名字：升级一次引擎就要改一次代码，
    ///        而且**改漏一个就是启动失败**。
    ///     ② 写死顺序：28 个库的依赖顺序手写出来，
    ///        **早加载晚依赖的照样失败**（实测里 libgcc 就因为先于
    ///        libwinpthread 加载而失败过）。
    ///
    ///   【改成什么】
    ///     **扫目录，反复加载，直到一轮下来没有任何新进展为止。**
    ///     先能加载的（依赖都在的）先装上，装不上的下一轮再来，
    ///     **几轮之后剩下的就是真的缺依赖，会如实报出来**。
    ///     这样**加库、删库、升版本都不用改代码**。
    ///
    ///   【tesseract 必须改名】
    ///     .NET 封装把库名写死成 `tesseract50.dll`
    ///     （它自己调 `LoadLibrary("tesseract50.dll")`），
    ///     所以发布目录里那个文件**必须叫 tesseract50.dll**，
    ///     哪怕它里面装的是 5.5.3。
    ///     ——**文件名是给封装看的，不是给人看的**；
    ///       所以下面会**另外把引擎真实版本读出来写进日志**，
    ///       免得日志和文件名对不上，**自己骗自己**。
    /// </summary>
    public static bool PreloadTesseractChain(string exeDirectory)
    {
        string nativeDir = System.IO.Path.Combine(exeDirectory, "x64");

        if (!System.IO.Directory.Exists(nativeDir))
        {
            AppLogger.Warn($"预加载 Tesseract 原生库：目录不存在 {nativeDir}");
            return false;
        }

        // 【必须先加搜索路径】否则加载 libgcc 时找不到同目录下的 libwinpthread——
        //   实测踩过：LoadLibrary 返回错误 126（找不到模块），
        //   而报错**并不会告诉你是哪个依赖缺了**。
        SetDllDirectory(nativeDir);

        var pending = new List<string>(System.IO.Directory.GetFiles(nativeDir, "*.dll"));
        var loaded = new List<string>();
        var failed = new List<string>();

        // 反复扫，直到一轮下来没有任何新进展 = 剩下的都是真的装不上
        bool progress = true;
        while (progress && pending.Count > 0)
        {
            progress = false;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                string path = pending[i];
                try
                {
                    NativeLibrary.Load(path);
                    loaded.Add(System.IO.Path.GetFileName(path));
                    pending.RemoveAt(i);
                    progress = true;
                }
                catch
                {
                    // 这一轮还装不上，留到下一轮（依赖可能还没就位）
                }
            }
        }

        failed.AddRange(pending);

        // 关键的两个必须有
        bool tesseractOk = loaded.Any(n => n.Equals("tesseract50.dll", StringComparison.OrdinalIgnoreCase));
        bool leptonicaOk = loaded.Any(n => n.StartsWith("leptonica", StringComparison.OrdinalIgnoreCase)
                                        || n.StartsWith("libleptonica", StringComparison.OrdinalIgnoreCase));

        AppLogger.Info(
            $"预加载 Tesseract 原生库：成功 {loaded.Count} 个" +
            $"（tesseract={Describe(tesseractOk)} leptonica={Describe(leptonicaOk)}，" +
            $"目录 {nativeDir}）" +
            (failed.Count > 0
                ? $"；⚠️ 装不上 {failed.Count} 个：{string.Join("、", failed.Select(System.IO.Path.GetFileName).Take(8))}"
                : "；全部成功"));

        // 装上了就把引擎的真实版本读出来 —— 文件名写着 50，日志得说真话
        try
        {
            if (NativeLibrary.TryGetExport(
                    NativeLibrary.Load(System.IO.Path.Combine(nativeDir, "tesseract50.dll")),
                    "TessVersion", out IntPtr verFn))
            {
                var versionFunc = Marshal.GetDelegateForFunctionPointer<TessVersionFn>(verFn);
                IntPtr verPtr = versionFunc();
                string real = verPtr == IntPtr.Zero
                    ? "(读不到)"
                    : (Marshal.PtrToStringAnsi(verPtr) ?? "(读不到)");
                AppLogger.Info($"Tesseract 引擎真实版本：{real}（文件名 tesseract50.dll 只是为了迎合 .NET 封装的固定库名）");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"读引擎版本失败（不影响识别）：{ex.GetType().Name} {ex.Message}");
        }

        return tesseractOk && leptonicaOk;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TessVersionFn();

    /// <summary>
    /// 把目录加进本进程的 DLL 搜索路径。
    ///
    /// 【为什么需要 —— 2026-10-10 换 5.5.3 时实测踩到】
    ///   5.5.3 的原生库是一串（28 个），互相依赖。
    ///   `LoadLibrary("完整路径\libgcc_s_seh-1.dll")` 成功与否，
    ///   取决于**它自己要的 libwinpthread 能不能被找到**——
    ///   而默认搜索路径里**没有我们这个目录**，
    ///   于是报错误 126（找不到模块），
    ///   **而且报错不会告诉你是哪个依赖缺了**，排查起来非常费劲。
    ///
    ///   把目录加进搜索路径之后，整串库就都能自己找到彼此了。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    private static string Describe(bool ok) => ok ? "成功" : "失败";
}

/// <summary>
/// NativeMethods.cs —— 全项目唯一的 P/Invoke 声明处（阶段 1）
///
/// 这个文件干嘛的：
///   1) 单实例的第二步：第二个实例发消息请原进程显示窗口（见 NotifyExistingInstanceToShow）；
///   2) 托盘图标：直接调 Windows 官方 API Shell_NotifyIcon 把图标挂到通知区域（见 TrayAdd / TrayRemove）。
///
/// 【为什么托盘不用 System.Windows.Forms.NotifyIcon —— 阶段 1 踩过的坑】
///   WinForms 的 NotifyIcon 把 Shell_NotifyIcon 的返回值吞掉了：即使挂载失败，
///   Visible 属性照样返回 true，日志里也看不出任何异常，结果就是「代码说成功了，
///   通知区域里根本没有图标」。所以这里改成直接调 Shell_NotifyIcon，
///   把返回值交给调用方写进日志；挂上之后再调 Shell_NotifyIconGetRect 反查图标真实位置，
///   拿到位置才算真的挂上了（这才是可验证的成功）。
///
/// 【为什么不能用 ShowWindow 显示窗口 —— 阶段 1 的黑框就是这么来的】
///   主窗口是 WPF 窗口。别的进程用 Win32 的 ShowWindow 把它「硬显示」出来时，
///   本进程的 WPF 仍以为窗口是隐藏的，于是永远不绘制界面内容，
///   用户看到的就是一块黑色空壳。正确做法是发消息让原进程自己在 WPF 里调 Window.Show()。
///
/// 它和谁交互：
///   - App.xaml.cs   启动时调 NotifyExistingInstanceToShow()，并用 TrayCallbackMessage 分发托盘鼠标动作；
///   - TrayIcon.cs   调 TrayAdd / TrayRemove。
///
/// 要改它该动哪里：
///   - 托盘图标的编号/消息号 → TrayIconId / TrayCallbackMessage（改了要同步 TrayIcon.cs）；
///   - 阶段 2 起新增的 Win32 API 一律加在本文件，不要在业务类里自己写 DllImport（决策约束 C2）。
/// </summary>
internal static class NativeMethods
{
    // ==================================================================== 常量

    private const uint WM_USER = 0x0400;

    /// <summary>托盘鼠标动作的回调消息号：托盘上的操作会以这条消息发到主窗口。</summary>
    public const uint TrayCallbackMessage = WM_USER + 1;

    /// <summary>托盘图标在本程序内的编号（只挂一个，固定 1）。</summary>
    public const uint TrayIconId = 1;

    /// <summary>
    /// 阶段 2：钩子事件唤醒消息。
    /// 钩子回调只做「把事件排队 + 发这条消息」，主窗口收到后立刻把排队事件取出来处理。
    /// 这样回调能在 1 毫秒内返回（规格硬指标 P99 &lt; 1ms），不会卡住系统、不会被系统掐掉钩子。
    /// </summary>
    public const uint HookEventMessage = WM_USER + 2;

    /// <summary>系统广播的电源消息（阶段 2 用来在睡眠唤醒后重装钩子）。</summary>
    public const uint WM_POWERBROADCAST = 0x0218;

    /// <summary>「自动从睡眠恢复」的广播值 —— 判断钩子可能已被系统摘掉的可靠信号。</summary>
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_SHOWTIP = 0x00000080;

    /// <summary>系统自带的「应用程序」图标（IDI_APPLICATION = 32512）：永远有效，且不需要我们释放。</summary>
    private static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);

    private const string ShowMainWindowMessageName = "TranslatorApp.ShowMainWindow.v1";

    /// <summary>自定义窗口消息号：请原进程把主窗口显示出来。两个进程各自注册同名消息，拿到同一个号。</summary>
    public static uint ShowMainWindowMessage { get; } = RegisterWindowMessage(ShowMainWindowMessageName);

    // ==================================================================== 结构体

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    /// <summary>NOTIFYICONDATAW（x64 下 Marshal.SizeOf = 976 字节）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    // ==================================================================== 托盘

    /// <summary>
    /// 把托盘图标挂到通知区域。返回 true 才算真的挂上。
    /// iconHandle 传 0 表示用系统默认图标（IDI_APPLICATION）。
    /// diag 里带上完整证据（结构体大小、NIM_ADD 结果、Windows 反查到的图标位置），由调用方写进日志。
    /// </summary>
    public static bool TrayAdd(IntPtr ownerHwnd, string tipText, IntPtr iconHandle, out string diag)
    {
        var nid = NewNotifyIconData(ownerHwnd);
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        nid.uCallbackMessage = TrayCallbackMessage;
        nid.hIcon = iconHandle != IntPtr.Zero ? iconHandle : LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        nid.szTip = tipText ?? string.Empty;

        bool added = Shell_NotifyIcon(NIM_ADD, ref nid);
        int lastError = Marshal.GetLastWin32Error();

        string rectText = "（挂载失败，未反查）";
        if (added)
        {
            var id = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER)),
                hWnd = ownerHwnd,
                uID = TrayIconId
            };
            int hr = Shell_NotifyIconGetRect(ref id, out RECT r);
            rectText = hr == 0
                ? $"Windows 反查到的图标位置=({r.Left},{r.Top})-({r.Right},{r.Bottom})"
                : $"反查图标位置失败 hr=0x{hr:X8}";
        }

        diag = $"结构体={nid.cbSize}字节, hwnd=0x{ownerHwnd.ToInt64():X}, hIcon=0x{nid.hIcon.ToInt64():X}, " +
               $"NIM_ADD={(added ? "成功" : "失败")}, GetLastError={lastError}, {rectText}";
        return added;
    }

    /// <summary>把托盘图标从通知区域摘掉。程序退出前必须调，否则会留下点不掉的幽灵图标。</summary>
    public static void TrayRemove(IntPtr ownerHwnd)
    {
        if (ownerHwnd == IntPtr.Zero) return;
        var nid = NewNotifyIconData(ownerHwnd);
        Shell_NotifyIcon(NIM_DELETE, ref nid);
    }

    private static NOTIFYICONDATA NewNotifyIconData(IntPtr ownerHwnd)
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = ownerHwnd,
            uID = TrayIconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    // ==================================================================== 环境诊断（挂托盘失败时才调用）

    /// <summary>
    /// 挂托盘失败时输出一次环境证据：本程序的权限等级 与 系统任务栏的权限等级，谁高谁低。
    ///
    /// 【阶段 1 真实故障记录 —— 这就是「通知区域里没有图标」的根因】
    ///   Windows 有一条规则（UIPI，用户界面特权隔离）：权限低的一方，不能给权限高的一方
    ///   发送「编号大于 WM_USER(0x0400)」的窗口消息，系统直接拒绝访问（错误码 5）。
    ///   挂托盘图标走的正是这种消息。所以只要
    ///       本程序权限 &lt; 任务栏权限   （例如本程序"中(Medium)"、任务栏"高(High)"）
    ///   Shell_NotifyIcon 就会返回失败，通知区域里什么也不会出现。
    ///
    ///   本机实测触发过两次：
    ///     1) 发布出来的 exe 被沙箱打上「低(Low)」标签 → 从低标签文件启动的程序一律是低权限 → 必然失败；
    ///     2) 任务栏跑在"高(High)"而本程序是"中(Medium)" → 同样被 UIPI 拒绝。
    ///
    /// 本方法只读信息、不增删任何图标，不会干扰正常流程。
    /// </summary>
    public static string DiagnoseTrayEnvironment()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);

        string shellLevel = tray == IntPtr.Zero ? "取不到任务栏窗口" : IntegrityOfWindow(tray);
        string ownLevel = IntegrityOfPid((int)GetCurrentProcessId());

        long ownValue = IntegrityValue(ownLevel);
        long shellValue = IntegrityValue(shellLevel);

        string conclusion;
        if (ownValue < 0 || shellValue < 0)
        {
            conclusion = "；两者无法比较";
        }
        else if (ownValue < shellValue)
        {
            conclusion = "；原因：本程序权限低于系统任务栏，Windows 拒绝挂载（exe 的强制标签或启动方式有问题）";
        }
        else
        {
            conclusion = "；权限不低，问题在别处";
        }

        return $"本程序权限={LevelName(ownLevel)}，系统任务栏权限={LevelName(shellLevel)}{conclusion}";
    }

    /// <summary>取某个窗口所属进程的权限等级（形如 "S-1-16-12288"）。</summary>
    private static string IntegrityOfWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return "取不到任务栏的进程";
        return IntegrityOfPid((int)pid);
    }

    /// <summary>取某个进程的权限等级 SID 字符串；完整性 SID 的最后一个数字就是等级值。</summary>
    private static string IntegrityOfPid(int pid)
    {
        IntPtr process = OpenProcess(0x00100000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (process == IntPtr.Zero) return "取不到(OpenProcess错误码=" + Marshal.GetLastWin32Error() + ")";

        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, 0x0008 /*TOKEN_QUERY*/, out token)) return "取不到(OpenProcessToken错误码=" + Marshal.GetLastWin32Error() + ")";

            GetTokenInformation(token, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out int length);
            if (length <= 0) return "取不到(TokenIntegrityLevel长度)";

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, 25, buffer, length, out length)) return "取不到(TokenIntegrityLevel读取失败)";

            // TOKEN_MANDATORY_LABEL 的第一个字段就是 SID 指针
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (!ConvertSidToStringSid(sid, out IntPtr sidString)) return "取不到(SID转字符串失败)";

            string text = Marshal.PtrToStringUni(sidString) ?? "取不到";
            LocalFree(sidString);
            return text;
        }
        catch (Exception ex)
        {
            return "取不到(" + ex.Message + ")";
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    /// <summary>从 "S-1-16-12288" 这类完整性 SID 里取出等级值；取不到返回 -1。</summary>
    private static long IntegrityValue(string integritySid)
    {
        if (integritySid == null) return -1;
        int idx = integritySid.LastIndexOf('-');
        if (idx < 0 || !long.TryParse(integritySid[(idx + 1)..], out long value)) return -1;
        return value;
    }

    private static string LevelName(string integritySid)
    {
        return IntegrityValue(integritySid) switch
        {
            0x1000 => $"{integritySid}=低(Low)",
            0x2000 => $"{integritySid}=中(Medium)",
            0x3000 => $"{integritySid}=高(High)",
            0x4000 => $"{integritySid}=系统(System)",
            _ => integritySid
        };
    }

    // ==================================================================== 实测期：前台窗口的进程名

    /// <summary>
    /// 取**前台窗口所属进程的名字**（如 `chrome.exe`），给取词记录对号用。
    ///
    /// 【为什么要这个（2026-10-17 用户批的）】
    ///   取词记录里原本只有时间/通道/语言/耗时/说明/字数/原文，
    ///   **没有「用户当时在看哪个网站」**。用户截图报错例时，
    ///   只能靠时间戳去对 —— 同一天测几十次，光靠时间对号很容易对错。
    ///   加一个进程名（`chrome.exe` / `Code.exe` / `WINWORD.EXE`），
    ///   对号就多一条硬线索。
    ///
    /// 【只记进程名，不记网址、不记窗口标题】
    ///   网址和标题是用户的内容，没必要记；进程名只说明「用的是哪个软件」。
    ///
    /// 【绝不抛异常、绝不影响取词】
    ///   管理员权限的窗口（任务管理器之类）`OpenProcess` 会失败，
    ///   这时返回「取不到」就行 —— 记录少一行线索，远好过取词崩掉。
    /// </summary>
    public static string ForegroundProcessName()
    {
        IntPtr process = IntPtr.Zero;
        try
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return "取不到";

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return "取不到";

            process = OpenProcess(0x00100000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, (int)pid);
            if (process == IntPtr.Zero) return "取不到";

            // 先要路径（QueryFullProcessImageName 只给完整路径，进程名得自己取最后一段）
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageName(process, 0, buffer, ref size)) return "取不到";

            string path = buffer.ToString();
            if (path.Length == 0) return "取不到";

            string name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? "取不到" : name;
        }
        catch (Exception ex)
        {
            // 记录线索而已，绝不能让取词因为这件事失败
            return "取不到(" + ex.GetType().Name + ")";
        }
        finally
        {
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    // ==================================================================== 单实例

    /// <summary>
    /// 找到已有的那个实例，请它显示主窗口。
    /// 找到并投递成功返回 true；找不到窗口或投递失败返回 false（不抛异常、不弹框）。
    /// </summary>
    public static bool NotifyExistingInstanceToShow(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return false;

        IntPtr hWnd = FindWindow(null, windowTitle);
        if (hWnd == IntPtr.Zero) return false;

        // PostMessage（投递后立刻返回）而不是 SendMessage（会等对方处理完）：对方忙时不会把我们卡住。
        return PostMessage(hWnd, ShowMainWindowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    // ==================================================================== 阶段 2：全局钩子（组合键 + 拖拽监听）

    /// <summary>
    /// 阶段 2 新增的 Win32 API 也全部声明在本文件（决策约束 C2：P/Invoke 只许出现在这一个文件里）。
    ///
    /// 【低级钩子（LowLevel Hooks）是什么】
    ///   Windows 提供两种全局监听方式：
    ///     - SetWindowsHookEx(WH_KEYBOARD_LL / WH_MOUSE_LL)「低级钩子」：系统把每一次按键/鼠标事件
    ///       先交给本程序的一个回调函数，我们处理完再决定「放行还是吃掉」。范围是整个系统。
    ///     - 轮询 GetAsyncKeyState：只能问「某个键此刻按着没有」，拿不到「刚才发生了什么」。
    ///   决策 2-1 选了 A（手写低级钩子），所以这里声明它们。
    ///
    /// 【三条必须遵守的铁律 —— 都来自规格，踩了就出大问题】
    ///   1. 回调必须极快：里面不许写文件、不发网络、不 Sleep、不 lock。需要干的活一律 PostMessage
    ///      丢给主线程去做。回调太慢系统会直接掐掉钩子（LowLevelHooksTimeout）。
    ///   2. 回调委托必须用字段长期持有（存起来防 GC），否则被回收后钩子莫名其妙就失灵了。
    ///   3. 不该吃的事件必须 CallNextHookEx 原样放行，否则会干扰用户正常点鼠标、拖文件。
    ///
    /// 它和谁交互：
    ///   - LowLevelKeyboardHook.cs / LowLevelMouseHook.cs 在这里取到回调委托、装/卸钩子；
    ///   - DpiHelper / ScreenGeometry 在这里取坐标、缩放、虚拟屏尺寸。
    /// </summary>

    // ---- 钩子类型 ----
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    // ---- 键盘消息 ----
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    // ---- 鼠标消息 ----
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;

    /// <summary>右键按下。阶段 3 用来「拖到一半右键取消」（用户 2026-10-08 要求）。</summary>
    public const int WM_RBUTTONDOWN = 0x0204;

    // ---- 虚拟键（只看 VK_CONTROL / VK_MENU / VK_LCONTROL 等修饰键和 Esc）----
    public const int VK_CONTROL = 0x11;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_MENU = 0x12;      // Alt
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;
    public const int VK_SHIFT = 0x10;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_ESCAPE = 0x1B;

    /// <summary>字母 D 的虚拟键码。调试截图热键 `Ctrl+Alt+D` 的最后一个键。</summary>
    public const int VK_D = 0x44;

    // ---- 多显示器 ----
    /// <summary>MonitorFromPoint 的常量：取「离这个点最近」的那块显示器。</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    /// <summary>GetDpiForMonitor 的常量：取「实际生效的」DPI（而不是系统设定值）。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>一块显示器的位置与工作区（坐标都是物理像素，原点可能是负数 —— 多显示器时副屏常在左边/上边）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;   // 这块屏的整个范围
        public RECT rcWork;      // 这块屏的「工作区」（排除了任务栏）
        public uint dwFlags;
    }

    // ---- 屏幕度量（多显示器虚拟屏）----
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_XSCREEN = 0;
    public const int SM_YSCREEN = 1;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    /// <summary>扩展窗口样式：让遮罩窗口不吃鼠标、不抢焦点、不进 Alt-Tab（见 SelectionMaskWindow）。</summary>
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;   // 鼠标穿透（点击落到下面的程序）
    public const int WS_EX_TOOLWINDOW = 0x00000080;   // 不出现在 Alt-Tab
    public const int WS_EX_NOACTIVATE = 0x08000000;   // 不抢走焦点

    /// <summary>坐标结构：GetCursorPos 用的就是它（两个 int，一行一个 x / y）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// 键盘钩子每次回调会带这个结构，说明「哪个键、什么动作」。
    /// 业务只关心 vkCode（键码）和 flags，这里如实映射，字段不省略。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>鼠标钩子每次回调会带这个结构，说明「鼠标动了到哪、哪个键、按了什么状态」。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;          // 鼠标所在的屏幕坐标（物理像素）
        public uint mouseData;    // 滚轮等额外信息
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>低级键盘钩子的回调原型。返回 IntPtr.Zero 表示「吃掉」，否则调用 CallNextHookEx 放行。</summary>
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>低级鼠标钩子的回调原型。返回 IntPtr.Zero 表示「吃掉」，否则调用 CallNextHookEx 放行。</summary>
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 装一个低级钩子。返回 IntPtr.Zero 表示失败（错误码见 GetLastWin32Error，例如权限不够）。
    /// 调用方必须判空，不许装不上还往下跑。
    /// </summary>
    public static IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId)
        => SetWindowsHookExRaw(idHook, lpfn, hMod, dwThreadId);

    /// <summary>卸钩子。进程退出前必须调。</summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    public static bool UnhookWindowsHookEx(IntPtr hhk)
        => UnhookWindowsHookExRaw(hhk);

    /// <summary>把事件放行给下一个程序。不该吃的事件必须走这一步，否则会干扰用户正常操作。</summary>
    public static IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam)
        => CallNextHookExRaw(hhk, nCode, wParam, lParam);

    /// <summary>
    /// 给指定窗口发一条「钩子事件到了，请来取」的消息。
    /// 用 PostMessage 而不是 SendMessage：投递完立刻返回，不会因为主窗口正忙而把系统回调卡住。
    /// </summary>
    public static bool PostMessageForWake(IntPtr hwnd)
        => PostMessage(hwnd, HookEventMessage, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// 查某个键此刻按着没有（不看历史，只看当下状态）。
    /// 用来给钩子维护的状态做「定期校正」——万一漏了一个抬起事件，状态就不会卡在「一直按着」。
    /// 返回值最低位 0x8000 那一位非零 = 当前按着。
    /// </summary>
    public static bool IsKeyDown(int vKey)
        => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>取鼠标当前屏幕坐标（物理像素，左上角为 0,0）。</summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    public static bool GetCursorPos(out POINT lpPoint)
        => GetCursorPosRaw(out lpPoint);

    /// <summary>取屏幕/虚拟屏的度量值（多显示器用 SM_XVIRTUALSCREEN 等）。</summary>
    public static int GetSystemMetrics(int nIndex)
        => GetSystemMetricsRaw(nIndex);

    /// <summary>取某个窗口所在显示器的 DPI（96 = 100%）。用于物理像素 ↔ DIP 换算。</summary>
    public static uint GetDpiForWindow(IntPtr hwnd)
        => GetDpiForWindowRaw(hwnd);

    /// <summary>给窗口设置扩展样式（遮罩窗口要设穿透/不抢焦点，全在这里做）。</summary>
    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    /// <summary>读窗口当前的扩展样式。要先读再改，不然会把系统/WPF 已有的样式冲掉。</summary>
    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    /// <summary>取「离这个点最近」的那块显示器。多显示器时鼠标在副屏上也要知道是哪块。</summary>
    public static IntPtr MonitorFromPoint(POINT pt, uint dwFlags)
        => MonitorFromPointRaw(pt, dwFlags);

    /// <summary>取这块显示器的范围与工作区。</summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    public static bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi)
        => GetMonitorInfoRaw(hMonitor, ref lpmi);

    /// <summary>取这块显示器实际生效的 DPI（96 = 100%，120 = 125%，144 = 150%）。拿不到时返回 96。</summary>
    public static uint GetDpiForMonitor(IntPtr hMonitor)
    {
        if (GetDpiForMonitorRaw(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX;
        }
        return 96; // 取不到就按标准 100% 处理，最坏结果是坐标比例差一点，不会崩
    }

    // ---- 上面这些薄封装对应的真实 P/Invoke（隐藏起来，业务不直接碰）----

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern IntPtr MonitorFromPointRaw(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoRaw(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    private static extern int GetDpiForMonitorRaw(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- 上面这些薄封装对应的真实 P/Invoke（隐藏起来，业务不直接碰）----

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookExRaw(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "UnhookWindowsHookEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookExRaw(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CallNextHookEx")]
    private static extern IntPtr CallNextHookExRaw(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPosRaw(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static extern int GetSystemMetricsRaw(int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForWindowRaw(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    // ==================================================================== Win32 声明

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    // ---- 环境诊断用的 API（对比「本程序权限」和「任务栏权限」）----

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---- 实测期对号用：取前台窗口的进程名 ----

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ==================================================================== 阶段 3：模拟按键与剪贴板

    public const uint INPUT_KEYBOARD = 1;
    public const uint INPUT_MOUSE = 0;

    public const uint KEYEVENTF_KEYUP = 0x0002;      // 松开（不写就是按下）
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    /// <summary>按联合体布局对齐的输入事件体（键盘与鼠标共用一块内存）。</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    /// <summary>SendInput 的一次输入。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// 往系统里注入一次输入（按键或鼠标）。
    ///
    /// 【为什么用 SendInput 而不是 keybd_event】
    ///   keybd_event 是老 API，x64 下有已知缺陷，且拿不到错误码。
    ///   SendInput 能拿到返回值——注入失败时它是 0，能立刻知道，
    ///   不然「复制没生效」和「对方不让复制」这两种情况根本分不开。
    ///
    /// 【注入失败排查】
    ///   最常见原因是**权限不够**：低级钩子能装上不代表能注入输入，
    ///   中途会弹 UAC 提权对话框把权限截断。
    /// </summary>
    public static uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize)
        => SendInputRaw(nInputs, pInputs, cbSize);

    /// <summary>剪贴板序号（每次剪贴板内容变化都会 +1）。</summary>
    public static uint GetClipboardSequenceNumber() => GetClipboardSequenceNumberRaw();

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInputRaw(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint GetClipboardSequenceNumberRaw();
}
