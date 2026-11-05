# T3b 真机验收的 UI 自动化驱动（2026-11-04）。
#
# 为什么用 UI Automation 而不是模拟鼠标坐标：
#   本轮要验的是「勾上/取消之后，另一处的状态对不对」。坐标点击一旦窗口位置变了就点空，
#   而 UIA 是按控件语义操作的，窗口挪到哪都能点到，也不会因为 DPI 缩放点偏。
#
# 用法：powershell -File _t3b_uia.ps1 -Step <步骤> [-Value <值>]
#   步骤：read-main / check / uncheck / tray-open / tray-toggle / tray-close
#         / lang-select <显示名子串> / lang-type <文本>
# 输出一律 UTF-8 写文件，不靠控制台（PS 5.1 控制台是 GBK，中文会乱）。

param(
    [Parameter(Mandatory = $true)][string]$Step,
    [string]$Value = "",
    [string]$OutFile = ""
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$UIA = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$True_ = [System.Windows.Automation.Condition]::TrueCondition

function Get-AppProc {
    $p = Get-Process TranslatorApp -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw 'NO_PROC：进程没在跑' }
    return $p
}

# 【为什么要按名字挑，不能取第一个】同一个进程里有好几扇顶层窗口：
#   主窗口、托盘菜单弹层、右下角提示卡、框选遮罩。按进程号 FindFirst 拿到的可能是任意一扇，
#   上一版就拿到过菜单弹层，然后在里面找 CheckBox 找不到。这里只认「窗口 + 标题含『翻译软件』」。
function Get-MainWin($proc) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)
    $wins = $UIA::RootElement.FindAll($TS::Children, $cond)
    foreach ($w in $wins) {
        if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and
            $w.Current.Name -like '翻译软件*') { return $w }
    }
    throw 'NO_WINDOW：找不到主窗口（可能已缩到托盘，先用 show-main 叫出来）'
}

function Find-ById($root, $autoId) {
    $c = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $autoId)
    return $root.FindFirst($TS::Descendants, $c)
}

function Get-Checked($win) {
    $cb = Find-ById $win 'TranslateCheck'
    if (-not $cb) { throw 'NO_CHECKBOX' }
    $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
}

function Get-LangText($win) {
    $cb = Find-ById $win 'LangBox'
    if (-not $cb) { throw 'NO_COMBO' }
    $sel = $null
    try {
        $sp = $cb.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        if ($sp.Current.GetSelection().Count -gt 0) { $sel = $sp.Current.GetSelection()[0].Current.Name }
    } catch { }
    $txt = $null
    try {
        $vp = $cb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $txt = $vp.Current.Value
    } catch { }
    if (-not $txt) {
        $ed = Find-ById $win 'PART_EditableTextBox'
        if ($ed) {
            try {
                $vp2 = $ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                $txt = $vp2.Current.Value
            } catch { }
        }
    }
    return (@{ selected = $sel; text = $txt })
}

# 托盘菜单：给主窗口发 WM_RBUTTONUP（msg = WM_USER+1 = 0x401，lParam = 0x0205），
# 走的是程序里那条真路径（App.MainWindowWndProc → TrayIcon.HandleMouseMessage → ShowMenu）。
Add-Type -Namespace Win32 -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetCursor();
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern IntPtr GetCursorInfo(ref CURSORINFO pci);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

# 真注入一次鼠标左键点击（2026-11-04 T3b-补：验「点菜单外面能不能关掉」，
# 必须是真的鼠标消息，UIA 里没有「点空白处」这种动作）。
function Click-At([int]$x, [int]$y) {
    [void][Win32.U]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [Win32.U]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)   # LEFTDOWN
    Start-Sleep -Milliseconds 60
    [Win32.U]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)   # LEFTUP
}

function Open-TrayMenu($proc) {
    [void][Win32.U]::SetCursorPos(1700, 20)      # 菜单弹在光标处，放屏幕右上角，别挡住主窗口
    # 【不能用 $proc.MainWindowHandle】主窗口 Hide() 之后它变成 0，
    #   而「点显示主窗口」这条要验的正是「藏起来之后还叫得回来」。
    #   窗口藏了句柄还在，按类名找，不看可见性。
    $h = Find-AppHwnd $proc
    if ($h -eq [IntPtr]::Zero) { throw 'NO_APP_HWND' }
    [void][Win32.U]::PostMessage($h, 0x401, [IntPtr]::Zero, [IntPtr](0x0205))
    Start-Sleep -Milliseconds 700
}

function Find-AppHwnd($proc) {
    # 按类名前缀找本程序的顶层窗口，**不管可见不可见**（藏起来的窗口句柄照样能收消息）。
    $script:foundApp = [IntPtr]::Zero
    $targetPid = [uint32]$proc.Id
    $cb = [Win32.U+EnumWindowsProc]{
        param($h, $l)
        $sb = New-Object System.Text.StringBuilder 256
        [void][Win32.U]::GetClassNameW($h, $sb, 256)
        if (-not $sb.ToString().StartsWith('HwndWrapper[TranslatorApp')) { return $true }
        $wpid = [uint32]0
        [void][Win32.U]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -ne $targetPid) { return $true }
        # 【必须挑对那一扇】本进程里类名以 HwndWrapper[TranslatorApp 开头的顶层窗口有好几扇：
        #   主窗口、托盘图标的隐藏宿主窗口(136x39)、SystemResourceNotifyWindow(0x0)…
        #   2026-11-05 实测：按枚举顺序取第一个拿到的是 136x39 那扇隐藏宿主，
        #   把 0x401 托盘消息贴给它 → 程序照收但 TrayIcon 收不到 → 菜单根本不弹，
        #   于是 Find-MenuHwnd 一直返回 0，看起来像「菜单弹不出来」。
        #   判据：标题非空的那扇就是主窗口（标题由 App.WindowTitle 统一给）。
        $tb = New-Object System.Text.StringBuilder 256
        [void][Win32.U]::GetWindowTextW($h, $tb, 256)
        if ($tb.ToString().Length -eq 0) { return $true }
        $script:foundApp = $h
        return $false
    }
    [void][Win32.U]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:foundApp
}

function Find-MenuHwnd($proc) {
    # 【为什么按类名找，不按 UIA 的 ControlType=Menu】
    #   WinForms 的 ContextMenuStrip 在 UIA 里并不暴露成 ControlType=Menu 的顶层窗口，
    #   RootElement.FindAll(Children, ProcessId=...) 拿不到它 —— 2026-11-04 实测返回 null，
    #   于是 click-away 的 BEFORE 就写成 0，看起来像「菜单压根没弹」，其实弹了。
    #   实测类名是 WindowsForms10.Window.<n>.app...（n 每次进程都不同，只能按前缀匹配）。
    $script:foundHwnd = [IntPtr]::Zero
    $targetPid = [uint32]$proc.Id
    $cb = [Win32.U+EnumWindowsProc]{
        param($h, $l)
        if (-not [Win32.U]::IsWindowVisible($h)) { return $true }
        $sb = New-Object System.Text.StringBuilder 256
        [void][Win32.U]::GetClassNameW($h, $sb, 256)
        if (-not $sb.ToString().StartsWith('WindowsForms10.Window.')) { return $true }
        $wpid = [uint32]0
        [void][Win32.U]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -ne $targetPid) { return $true }
        $script:foundHwnd = $h
        return $false
    }
    [void][Win32.U]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:foundHwnd
}

function Find-MenuWindow($proc) {
    $h = Find-MenuHwnd $proc
    if ($h -eq [IntPtr]::Zero) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($h)
}

function Get-TrayItem($proc) {
    $menu = Find-MenuWindow $proc
    if (-not $menu) { throw 'NO_MENU：托盘菜单没弹出来' }
    # 菜单项名字里带「翻译」的那一项（**不写死整串**：那串文字是锁定的披露文案，
    # 验收脚本跟着它变会让「文字被改了」这种事故看不出来；这里只按关键字定位）
    $items = $menu.FindAll($TS::Descendants, $True_)
    foreach ($i in $items) {
        if ($i.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -and
            $i.Current.Name -like '*翻译*') { return $i }
    }
    throw 'NO_TRAY_ITEM'
}

function AppSettingsBeforeToggle {
    $cfg = Join-Path $env:APPDATA 'TranslatorApp\settings.json'
    $j = Get-Content $cfg -Raw -Encoding UTF8 | ConvertFrom-Json
    return $j.translateEnabled
}

function Shot($path) {
    $proc = Get-AppProc
    $win = Get-MainWin $proc
    $r = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return @{ x = [int]$r.X; y = [int]$r.Y; w = [int]$r.Width; h = [int]$r.Height; path = $path }
}

$lines = New-Object System.Collections.ArrayList
function Say($s) { [void]$lines.Add($s) }

try {
    $proc = Get-AppProc

    switch -Wildcard ($Step) {
    'qmarks' {
        # 定位两个「?」的中心坐标（给 tooltip 悬停用）。
        # 注意：这里不许用 return —— 在 try 块里 return 会跳过末尾的写文件和打印，
        # 一声不响地什么都不输出（2026-11-05 第一次就是这么踩的）。
        $win = Get-MainWin $proc
        if (-not $win) {
            Say "EMPTY no-main"
        } else {
            $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
            $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
            $i = 0
            foreach ($e in $all) {
                # 第 18 轮起问号换成圆圈内问号（U+E9CE），UIA 的 Name 就是那一个字符。
                # 这里按「长度 1 且不是普通问号也不是普通字母」的条件找，兼容新旧两种写法。
                $nm = $e.Current.Name
                $isGlyph = ($nm -eq [string][char]0xE9CE) -or ($nm -eq "?")
                if ($isGlyph) {
                    $r = $e.Current.BoundingRectangle
                    Say ("QMARK #" + $i + " cx=" + [int]($r.X + $r.Width / 2) + " cy=" + [int]($r.Y + $r.Height / 2))
                    $i++
                }
            }
            if ($i -eq 0) { Say "EMPTY no-qmark" }
        }
    }
        'read-main' {
            $win = Get-MainWin $proc
            Say ("OK checked=" + (Get-Checked $win))
            $lt = Get-LangText $win
            Say ("LANG selected=" + $lt.selected + " text=" + $lt.text)
        }
        'check' {
            $win = Get-MainWin $proc
            $cb = Find-ById $win 'TranslateCheck'
            $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
            Start-Sleep -Milliseconds 400
            Say ("OK checked=" + (Get-Checked $win))
        }
        'uncheck' {
            $win = Get-MainWin $proc
            $cb = Find-ById $win 'TranslateCheck'
            $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
            Start-Sleep -Milliseconds 400
            Say ("OK checked=" + (Get-Checked $win))
        }
        'tray-open' {
            Open-TrayMenu $proc
            $item = Get-TrayItem $proc
            $tp = $item.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            Say ("OK trayChecked=" + ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On))
            Say ("TRAYNAME=" + $item.Current.Name)
        }
        'tray-toggle' {
            # 【为什么不读 TogglePattern】WinForms 的菜单项只暴露 InvokePattern，
            #   勾选态在 UIA 里读不到（实测 GetSupportedPatterns 只有 Invoke）。
            #   所以「托盘上的勾」靠整屏截图肉眼确认（见 tray-shot），
            #   这里只负责「像用户一样点一下那一项」。
            Open-TrayMenu $proc
            $item = Get-TrayItem $proc
            $before = AppSettingsBeforeToggle
            $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 1000
            Say ("OK trayBefore=" + $before)
        }
        'tray-close' {
            [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
            Start-Sleep -Milliseconds 400
            Say "OK closed"
        }
        'show-main' {
            Open-TrayMenu $proc
            $menu = Find-MenuWindow $proc
            if (-not $menu) { throw 'NO_MENU' }
            $item = $null
            foreach ($i in $menu.FindAll($TS::Descendants, $True_)) {
                if ($i.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -and
                    $i.Current.Name -like '*显示主窗口*') { $item = $i; break }
            }
            if (-not $item) { throw 'NO_SHOW_ITEM' }
            $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 900
            $win = Get-MainWin $proc
            Say ("OK shown name=" + $win.Current.Name)
        }
        'lang-probe' {
            # 排查用：把 LangBox 子树里所有元素的 ControlType / Name / AutomationId 打出来。
            # 2026-11-05：第 17 轮 S5-1 跑 lang-select 日语 报了 NO_ITEM，
            # 先看清楚下拉项到底以什么形式挂在树上，再决定怎么选。
            $win = Get-MainWin $proc
            $box = Find-ById $win 'LangBox'
            if (-not $box) { Say "NO_COMBO" }
            else {
                Say ("COMBO name=" + $box.Current.Name + " class=" + $box.Current.ClassName)
                $i = 0
                foreach ($e in $box.FindAll($TS::Descendants, $True_)) {
                    Say ("  #" + $i + " type=" + $e.Current.ControlType.ProgrammaticName + " id=" + $e.Current.AutomationId + " name=" + $e.Current.Name)
                    $i++
                }
                Say ("TOTAL " + $i)
            }
        }
        'lang-select' {
            # 【不要 ExpandCollapsePattern】WPF 的可编辑 ComboBox 在 UIA 里不支持它
            #   （会抛「不支持的模式」，实测 2026-11-04）。下拉项直接以 ListItem 的形式
            #   挂在 ComboBox 的子树里，拿到就能 Select —— 效果和用户从列表里点一项一样。
            $win = Get-MainWin $proc
            $box = Find-ById $win 'LangBox'
            if (-not $box) { throw 'NO_COMBO' }
            # 【必须先展开】下拉项是「展开之后才挂到树上」的，没展开时 LangBox 子树里
            #   只有一个 PART_EditableTextBox（2026-11-05 lang-probe 实测 TOTAL 1）。
            #   展开的两条路：能拿到 ExpandCollapsePattern 就用它；拿不到就先 SetFocus
            #   再按 Alt+Down —— WPF 的可编辑 ComboBox 走的是后一条（实测报「不支持的模式」）。
            $target = $null
            foreach ($e in $box.FindAll($TS::Descendants, $True_)) {
                if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
                    $e.Current.Name -like ("*" + $Value + "*")) { $target = $e; break }
            }
            if (-not $target) {
                try {
                    $box.SetFocus()
                    Start-Sleep -Milliseconds 300
                    [System.Windows.Forms.SendKeys]::SendWait("%{DOWN}")
                    Start-Sleep -Milliseconds 700
                } catch {
                    Say ("  （Alt+Down 展开失败：" + $_.Exception.Message + "）")
                }
                foreach ($e in $box.FindAll($TS::Descendants, $True_)) {
                    if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
                        $e.Current.Name -like ("*" + $Value + "*")) { $target = $e; break }
                }
            }
            if (-not $target) { throw ("NO_ITEM：" + $Value) }
            $sp = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $sp.Select()
            Start-Sleep -Milliseconds 700
            $lt = Get-LangText $win
            Say ("OK picked=" + $target.Current.Name)
            Say ("LANG selected=" + $lt.selected + " text=" + $lt.text)
        }
        'lang-type' {
            $win = Get-MainWin $proc
            $ed = Find-ById $win 'PART_EditableTextBox'
            if (-not $ed) {
                $box = Find-ById $win 'LangBox'
                $ec = $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                $ec.Expand(); Start-Sleep -Milliseconds 500
                $ed = Find-ById $win 'PART_EditableTextBox'
            }
            if (-not $ed) { throw 'NO_EDIT' }
            $vp = $ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $vp.SetValue($Value)
            Start-Sleep -Milliseconds 300
            # 把焦点移走 → 触发 LostFocus → 写盘（和用户点别处是同一条路径）
            $cb = Find-ById $win 'TranslateCheck'
            $cb.SetFocus()
            Start-Sleep -Milliseconds 600
            $lt = Get-LangText $win
            Say ("OK typed=" + $Value)
            Say ("LANG selected=" + $lt.selected + " text=" + $lt.text)
        }

        'menu-probe' {
            Open-TrayMenu $proc
            $menu = Find-MenuWindow $proc
            if (-not $menu) { throw 'NO_MENU' }
            $items = $menu.FindAll($TS::Descendants, $True_)
            foreach ($i in $items) {
                $pats = @()
                try { foreach ($pp in $i.GetSupportedPatterns()) { $pats += $pp.ProgrammaticName } } catch { }
                Say ("ITEM|" + $i.Current.ControlType.ProgrammaticName + "|" + $i.Current.Name + "|" + ($pats -join ","))
            }
        }
        'tray-shot' {
            # 打开托盘菜单并截整屏，然后**保持菜单开着**（下一步用 tray-close 关）
            Open-TrayMenu $proc
            $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
            $bmp.Save($Value, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            $mh = Find-MenuHwnd $proc
            if ($mh -ne [IntPtr]::Zero) {
                # 用 GetWindowRect 量，不用 UIA 的 BoundingRectangle：
                # WinForms 菜单的 UIA 元素边界有时是空矩形，量出来 0x0 比量不到更误导。
                $rr = New-Object Win32.U+RECT
                [void][Win32.U]::GetWindowRect($mh, [ref]$rr)
                Say ("MENURECT x=" + $rr.Left + " y=" + $rr.Top + " w=" + ($rr.Right - $rr.Left) + " h=" + ($rr.Bottom - $rr.Top))
            } else {
                Say "MENURECT none"
            }
            Say ("TRAYSHOT " + $Value)
        }
        'icon-tip' {
            # 悬停到主窗口里那个圆圈问号上，等 tooltip 展开，然后整屏截图。
            # Value = 输出图片路径。
            $win = Get-MainWin $proc
            if (-not $win) {
                Say "FAIL NO_WINDOW"
            } else {
                $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
                $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
                $hit = $null
                foreach ($e in $all) {
                    $nm = $e.Current.Name
                    if (($nm -eq [string][char]0xE9CE) -or ($nm -eq "?")) { $hit = $e; break }
                }
                if (-not $hit) {
                    Say "FAIL NO_ICON"
                } else {
                    $r = $hit.Current.BoundingRectangle
                    $cx = [int]($r.X + $r.Width / 2)
                    $cy = [int]($r.Y + $r.Height / 2)
                    Say ("ICON cx=" + $cx + " cy=" + $cy)
                    # 【悬停必须分两步走，直接跳到目标点不会触发 tooltip】
                    #   WPF 的 ToolTip 靠鼠标 Enter 消息开；SetCursorPos 一次跨越大段距离时，
                    #   有的窗口收不到 Enter（2026-11-05 实测：直接跳到图标中心，等 2.3 秒也没 tooltip）。
                    #   先跳到旁边再挪进来，让系统产生一次真实的进入。
                    [void][Win32.U]::SetCursorPos($cx + 60, $cy + 40)
                    Start-Sleep -Milliseconds 250
                    [void][Win32.U]::SetCursorPos($cx, $cy)
                    Start-Sleep -Milliseconds 400
                    [void][Win32.U]::SetCursorPos($cx + 1, $cy)
                    Start-Sleep -Milliseconds 2300
                    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
                    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                    $g = [System.Drawing.Graphics]::FromImage($bmp)
                    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
                    $bmp.Save($Value, [System.Drawing.Imaging.ImageFormat]::Png)
                    $g.Dispose(); $bmp.Dispose()
                    Say ("TIPSHOT " + $Value)
                }
            }
        }
        'cursor-probe' {
            # 光标形状怎么量：
            #   GetCursor() 拿到当前线程的光标 —— 对别的窗口没用（那是本进程的）。
            #   正确做法是 GetCursorInfo()：它给的是**屏幕当前显示的那个**光标句柄，
            #   再和 LoadCursorW(0, IDC_*) 的五个标准句柄比。IDC_ARROW=32512、IDC_IBEAM=32513、
            #   IDC_HAND=32649、IDC_HELP=32651、IDC_WAIT=32514。
            #   Value = "x,y"：先把光标挪过去，等 600ms 再量。
            $xy = $Value.Split(',')
            [void][Win32.U]::SetCursorPos([int]$xy[0], [int]$xy[1])
            Start-Sleep -Milliseconds 700
            $ci = New-Object Win32.U+CURSORINFO
            $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($ci)
            [void][Win32.U]::GetCursorInfo([ref]$ci)
            $cur = $ci.hCursor
            $names = @{ 32512 = 'ARROW'; 32513 = 'IBEAM'; 32649 = 'HAND'; 32651 = 'HELP'; 32514 = 'WAIT'; 32650 = 'NO' }
            $found = 'OTHER'
            foreach ($id in $names.Keys) {
                $h = [Win32.U]::LoadCursorW([IntPtr]::Zero, [IntPtr]$id)
                if ($h -eq $cur) { $found = $names[$id]; break }
            }
            Say ("CURSOR x=" + $xy[0] + " y=" + $xy[1] + " shape=" + $found + " hwnd=" + $cur)
        }
        'icon-cursor-shot' {
            # 悬停到图标上，先量光标形状，再整屏截图（截图里能看到光标本体）。
            # Value = 输出图片路径。
            $win = Get-MainWin $proc
            $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
            $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
            $hit = $null
            foreach ($e in $all) {
                $nm = $e.Current.Name
                if (($nm -eq [string][char]0xE9CE) -or ($nm -eq "?")) { $hit = $e; break }
            }
            if (-not $hit) { Say "FAIL NO_ICON"; } else {
                $r = $hit.Current.BoundingRectangle
                $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
                [void][Win32.U]::SetCursorPos($cx + 60, $cy + 40)
                Start-Sleep -Milliseconds 250
                [void][Win32.U]::SetCursorPos($cx, $cy)
                Start-Sleep -Milliseconds 900
                $ci = New-Object Win32.U+CURSORINFO
                $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($ci)
                [void][Win32.U]::GetCursorInfo([ref]$ci)
                $names = @{ 32512 = 'ARROW'; 32513 = 'IBEAM'; 32649 = 'HAND'; 32651 = 'HELP'; 32514 = 'WAIT'; 32650 = 'NO' }
                $found = 'OTHER'
                foreach ($id in $names.Keys) {
                    $h = [Win32.U]::LoadCursorW([IntPtr]::Zero, [IntPtr]$id)
                    if ($h -eq $ci.hCursor) { $found = $names[$id]; break }
                }
                Say ("ICONCURSOR cx=" + $cx + " cy=" + $cy + " shape=" + $found)
                $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
                # 【为什么要把光标自己画上去】CopyFromScreen 拍不到光标（GDI 截屏本来就不含光标层），
                #   拍出来的图里没有鼠标，等于没证明「光标长什么样」。
                #   这里用 GetCursorInfo 拿到的**那个真实光标句柄**调 Icon.FromHandle 画到截图上 ——
                #   画的是系统此刻正在显示的光标本身，不是我们画的箭头。
                $icon = [System.Drawing.Icon]::FromHandle($ci.hCursor)
                $px = $ci.ptScreenPos.X - $b.X
                $py = $ci.ptScreenPos.Y - $b.Y
                $g.DrawIcon($icon, $px, $py)
                $bmp.Save($Value, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
                Say ("CURSORSHOT " + $Value + " drewCursorAt=" + $px + "," + $py)
            }
        }
        'cursor-save' {
            # 把「系统此刻显示的那个光标」单独存成一张 PNG（放大 12 倍，透明背景铺白）。
            # 为什么这样做：整屏截图里光标会和它下面的图标叠在一起，
            #   看图的人分不清「问号是光标带的」还是「问号是界面图标」——
            #   2026-11-05 就是这么被识图误导过一次。单独存光标本体，一眼定案。
            # Value = "x,y|输出路径"
            $parts = $Value.Split('|')
            $xy = $parts[0].Split(',')
            [void][Win32.U]::SetCursorPos([int]$xy[0], [int]$xy[1])
            Start-Sleep -Milliseconds 900
            $ci = New-Object Win32.U+CURSORINFO
            $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($ci)
            [void][Win32.U]::GetCursorInfo([ref]$ci)
            $names = @{ 32512 = 'ARROW'; 32513 = 'IBEAM'; 32649 = 'HAND'; 32651 = 'HELP'; 32514 = 'WAIT'; 32650 = 'NO' }
            $found = 'OTHER'
            foreach ($id in $names.Keys) {
                $h = [Win32.U]::LoadCursorW([IntPtr]::Zero, [IntPtr]$id)
                if ($h -eq $ci.hCursor) { $found = $names[$id]; break }
            }
            $icon = [System.Drawing.Icon]::FromHandle($ci.hCursor)
            $src = $icon.ToBitmap()
            $z = 12
            $out = New-Object System.Drawing.Bitmap(($src.Width * $z), ($src.Height * $z))
            $g = [System.Drawing.Graphics]::FromImage($out)
            $g.Clear([System.Drawing.Color]::White)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $g.DrawImage($src, 0, 0, $src.Width * $z, $src.Height * $z)
            $out.Save($parts[1], [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $out.Dispose(); $src.Dispose()
            Say ("CURSORSAVE shape=" + $found + " size=" + $icon.Width + "x" + $icon.Height + " -> " + $parts[1])
        }
        'cursor-compare' {
            # 【光标的决定性判据】把「当前屏幕光标」和「系统标准箭头 / 标准帮助光标」
            #   各自渲染成位图，逐像素比差异。谁的差异像素最少就是谁 ——
            #   这比看图猜、也比只比句柄可靠（句柄比较在共享光标表里有时会撞车）。
            # Value = "x,y"
            $xy = $Value.Split(',')
            [void][Win32.U]::SetCursorPos([int]$xy[0], [int]$xy[1])
            Start-Sleep -Milliseconds 900
            $ci = New-Object Win32.U+CURSORINFO
            $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($ci)
            [void][Win32.U]::GetCursorInfo([ref]$ci)
            function Render($hIcon) {
                $ic = [System.Drawing.Icon]::FromHandle($hIcon)
                return $ic.ToBitmap()
            }
            function Diff($a, $b) {
                $w = [Math]::Min($a.Width, $b.Width); $h = [Math]::Min($a.Height, $b.Height)
                $n = 0
                for ($y = 0; $y -lt $h; $y++) {
                    for ($x = 0; $x -lt $w; $x++) {
                        $c1 = $a.GetPixel($x, $y); $c2 = $b.GetPixel($x, $y)
                        if ($c1.ToArgb() -ne $c2.ToArgb()) { $n++ }
                    }
                }
                return $n
            }
            $cur = Render $ci.hCursor
            $std = @(
                @{ n = 'ARROW'; h = [Win32.U]::LoadCursorW([IntPtr]::Zero, [IntPtr]32512) },
                @{ n = 'HELP';  h = [Win32.U]::LoadCursorW([IntPtr]::Zero, [IntPtr]32651) },
                @{ n = 'IBEAM'; h = [Win32.U]::LoadCursorW([IntPtr]::Zero, [IntPtr]32513) },
                @{ n = 'HAND';  h = [Win32.U]::LoadCursorW([IntPtr]::Zero, [IntPtr]32649) }
            )
            Say ("CUR hCursor=" + $ci.hCursor + " size=" + $cur.Width + "x" + $cur.Height)
            foreach ($s in $std) {
                $b = Render $s.h
                Say ("DIFF vs " + $s.n + " (h=" + $s.h + ") = " + (Diff $cur $b))
            }
            $cur.Dispose()
        }
        'combo-probe' {
            $win = Get-MainWin $proc
            $box = Find-ById $win 'LangBox'
            $pats = @()
            foreach ($pp in $box.GetSupportedPatterns()) { $pats += $pp.ProgrammaticName }
            Say ("COMBO pats=" + ($pats -join ","))
            $kids = $box.FindAll($TS::Descendants, $True_)
            foreach ($k in $kids) {
                $kp = @()
                try { foreach ($pp in $k.GetSupportedPatterns()) { $kp += $pp.ProgrammaticName } } catch { }
                Say ("KID|" + $k.Current.ControlType.ProgrammaticName + "|" + $k.Current.Name + "|" + $k.Current.AutomationId + "|" + ($kp -join ","))
            }
        }
        'menu-exists' {
            $menu = Find-MenuWindow $proc
            Say ("OK menu=" + $(if ($menu) { 1 } else { 0 }))
        }
        'click-away' {
            # 【验的是「点菜单外面能不能关掉」】Value 形如 "60,900"。
            #   先弹菜单（走 PostMessage 那条真路径），确认它真的开着，
            #   再往屏幕空白处真点一下鼠标，最后再看它还在不在。
            Open-TrayMenu $proc
            $before = Find-MenuHwnd $proc
            Say ("BEFORE menu=" + $(if ($before -ne [IntPtr]::Zero) { 1 } else { 0 }))
            $parts = $Value.Split(',')
            Click-At ([int]$parts[0]) ([int]$parts[1])
            Start-Sleep -Milliseconds 800
            $after = Find-MenuHwnd $proc
            Say ("AFTER menu=" + $(if ($after -ne [IntPtr]::Zero) { 1 } else { 0 }) + " clickAt=" + $Value)
        }
        'click-away-shot' {
            # 【S1-① 的取证版】Value 形如 "60,900|D:\...\before.png|D:\...\after.png"
            #   弹菜单 → 截「弹出时」→ 真点屏幕空白处 → 截「点完后」→ 报菜单还在不在。
            #   两张图要能并排看出「菜单没了」，光有 BEFORE/AFTER 数字不算证据。
            $parts = $Value.Split('|')
            $xy = $parts[0].Split(',')
            Open-TrayMenu $proc
            Start-Sleep -Milliseconds 400
            $before = Find-MenuHwnd $proc
            $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
            $bmp.Save($parts[1], [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            Say ("BEFORE menu=" + $(if ($before -ne [IntPtr]::Zero) { 1 } else { 0 }))
            Click-At ([int]$xy[0]) ([int]$xy[1])
            Start-Sleep -Milliseconds 800
            $after = Find-MenuHwnd $proc
            $bmp2 = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
            $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
            $g2.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp2.Size)
            $bmp2.Save($parts[2], [System.Drawing.Imaging.ImageFormat]::Png)
            $g2.Dispose(); $bmp2.Dispose()
            Say ("AFTER menu=" + $(if ($after -ne [IntPtr]::Zero) { 1 } else { 0 }) + " clickAt=" + $parts[0])
        }
        'menu-hover' {
            # 【第16轮 P1-J2 试验】不抢前台时，鼠标移到菜单项上还有没有高亮？
            #   Value 形如 "1700,45|D:\...\shot.png"（坐标 + 整屏截图路径）。
            $parts = $Value.Split('|')
            $xy = $parts[0].Split(',')
            Open-TrayMenu $proc
            Start-Sleep -Milliseconds 600
            $mh = Find-MenuHwnd $proc
            Say ("MENU menu=" + $(if ($mh -ne [IntPtr]::Zero) { 1 } else { 0 }))
            [void][Win32.U]::SetCursorPos([int]$xy[0], [int]$xy[1])
            Start-Sleep -Milliseconds 800
            $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
            $bmp.Save($parts[1], [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            # 顺带把每一项的选中态读出来（能读就读，读不到写 ?）
            $menu = Find-MenuWindow $proc
            if ($menu) {
                foreach ($i in $menu.FindAll($TS::Descendants, $True_)) {
                    $sel = '?'
                    try {
                        $sp = $i.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                        $sel = $sp.Current.IsSelected
                    } catch { }
                    Say ("ITEM|" + $i.Current.ControlType.ProgrammaticName + "|" + $i.Current.Name + "|sel=" + $sel)
                }
            }
            Say ("HOVER " + $parts[1] + " at=" + $parts[0])
        }
        'menu-keys' {
            # 【第16轮 P1-J2 试验】不抢前台时，方向键能不能选菜单项？
            #   Value 形如 "DOWN|D:\...\shot.png"。只按两下，按完不再按别的，
            #   免得把按键送进别的窗口干出别的事。
            $parts = $Value.Split('|')
            Open-TrayMenu $proc
            Start-Sleep -Milliseconds 600
            $fg = [Win32.U]::GetForegroundWindow()
            $fgPid = [uint32]0
            [void][Win32.U]::GetWindowThreadProcessId($fg, [ref]$fgPid)
            Say ("FG beforekeys pid=" + $fgPid + " appPid=" + $proc.Id + " isApp=" + $(if ($fgPid -eq $proc.Id) { 1 } else { 0 }))
            [System.Windows.Forms.SendKeys]::SendWait("{" + $parts[0] + "}")
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait("{" + $parts[0] + "}")
            Start-Sleep -Milliseconds 700
            $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
            $bmp.Save($parts[1], [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            $menu = Find-MenuWindow $proc
            if ($menu) {
                foreach ($i in $menu.FindAll($TS::Descendants, $True_)) {
                    $sel = '?'
                    try {
                        $sp = $i.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                        $sel = $sp.Current.IsSelected
                    } catch { }
                    Say ("ITEM|" + $i.Current.ControlType.ProgrammaticName + "|" + $i.Current.Name + "|sel=" + $sel)
                }
            } else {
                Say "MENU gone-after-keys"
            }
            Say ("KEYS " + $parts[0] + " " + $parts[1])
        }
        'menu-tri' {
            # 【第16轮 P1-J2】Value 形如 "A.png|B.png|C.png"（基线 / 悬停 / 方向键）
            $p = $Value.Split('|')
            $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            function _Snap($path) {
                $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
                $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
            }

            # 1) 基线：光标在屏幕左下角（离菜单很远），弹菜单
            [void][Win32.U]::SetCursorPos(60, 900)
            Start-Sleep -Milliseconds 300
            Open-TrayMenu $proc
            Start-Sleep -Milliseconds 1300
            $m1 = Find-MenuHwnd $proc
            Say ("A menu=" + $(if ($m1 -ne [IntPtr]::Zero) { 1 } else { 0 }) + " appHwnd=" + (Find-AppHwnd $proc) + " procId=" + $proc.Id)
            _Snap $p[0]

            # 2) 悬停：光标移到菜单第二项上
            #   【坐标必须实测】菜单 1646,20 274x82，里面 3 项 + 2 条分隔线，
            #   1700,45 落在分隔线附近（第一版就是这么误判成「悬停没高亮」的）。
            #   Value 第 4 段可覆盖悬停坐标，默认 1700,58 = 第二项中心。
            $hx = 1700; $hy = 58
            if ($p.Length -ge 4 -and $p[3]) { $hz = $p[3].Split(','); $hx = [int]$hz[0]; $hy = [int]$hz[1] }
            [void][Win32.U]::SetCursorPos($hx, $hy)
            Start-Sleep -Milliseconds 900
            Say ("B menu=" + $(if ((Find-MenuHwnd $proc) -ne [IntPtr]::Zero) { 1 } else { 0 }))
            _Snap $p[1]

            # 3) 方向键：光标先挪远（保证高亮只可能来自键盘），再按两下 DOWN
            [void][Win32.U]::SetCursorPos(60, 900)
            Start-Sleep -Milliseconds 500
            [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
            Start-Sleep -Milliseconds 350
            [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
            Start-Sleep -Milliseconds 800
            Say ("C menu=" + $(if ((Find-MenuHwnd $proc) -ne [IntPtr]::Zero) { 1 } else { 0 }))
            _Snap $p[2]

            Say ("SHOTS " + $Value)
        }
        'menu-trace' {
            # 【量具自查】弹菜单后连续采样 10 次（每 250ms），报 0/1 序列。
            #   Value 形如 "far"（先把光标挪到 60,900 再弹）或 "near"（不挪）。
            if ($Value -eq 'far') { [void][Win32.U]::SetCursorPos(60, 900); Start-Sleep -Milliseconds 400 }
            Open-TrayMenu $proc
            $seq = ''
            for ($i = 0; $i -lt 10; $i++) {
                $h = Find-MenuHwnd $proc
                $seq += $(if ($h -ne [IntPtr]::Zero) { '1' } else { '0' })
                Start-Sleep -Milliseconds 250
            }
            Say ("TRACE " + $Value + " " + $seq)
            $h = Find-MenuHwnd $proc
            if ($h -ne [IntPtr]::Zero) {
                $rr = New-Object Win32.U+RECT
                [void][Win32.U]::GetWindowRect($h, [ref]$rr)
                Say ("RECT x=" + $rr.Left + " y=" + $rr.Top + " w=" + ($rr.Right - $rr.Left) + " h=" + ($rr.Bottom - $rr.Top))
            }
        }
        'shot' {
            $r = Shot $Value
            Say ("SHOT " + $r.path + " x=" + $r.x + " y=" + $r.y + " w=" + $r.w + " h=" + $r.h)
        }
        'screen' {
            # 整屏截图（托盘菜单、卡片这些不在主窗口里的东西要用它）
            $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
            $bmp.Save($Value, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            Say ("SCREEN " + $Value + " " + $b.Width + "x" + $b.Height)
        }
        'winrect' {
            $win = Get-MainWin $proc
            $r = $win.Current.BoundingRectangle
            Say ("RECT x=" + [int]$r.X + " y=" + [int]$r.Y + " w=" + [int]$r.Width + " h=" + [int]$r.Height)
        }
        'fg' {
            # 当前前台窗口是不是本程序（S1-② 「点显示主窗口真的弹到前面」的判据）。
            # 只用「窗口可见」不够 —— 主窗口一直可见，只是可能被别的窗口盖住；
            # 也不用 UIA 的 NativeWindowHandle 比句柄（WPF 根元素给的句柄和
            # 程序日志里那个不一定同一个），比「前台窗口属于哪个进程」直接。
            $fg = [Win32.U]::GetForegroundWindow()
            $fgPid = [uint32]0
            [void][Win32.U]::GetWindowThreadProcessId($fg, [ref]$fgPid)
            Say ("FG hwnd=" + $fg + " pid=" + $fgPid + " appPid=" + $proc.Id + " isApp=" + $(if ($fgPid -eq $proc.Id) { 1 } else { 0 }))
        }
        'hide-main' {
            # 模拟用户点 ×（Closing 被取消 → Hide()），把窗口藏起来，
            # 这样下一步 show-main 才有东西可验。
            # 判据用 Win32 的 IsWindowVisible（不靠 UIA —— UIA 对隐藏窗口的行为
            # 实测不稳定，2026-11-04 第一次跑直接把进程搞没了）。
            $win = Get-MainWin $proc
            $h = [IntPtr]$win.Current.NativeWindowHandle
            Say ("BEFORE visible=" + $(if ([Win32.U]::IsWindowVisible($h)) { 1 } else { 0 }))
            $pat = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $pat.Close()
            Start-Sleep -Milliseconds 900
            Say ("AFTER visible=" + $(if ([Win32.U]::IsWindowVisible($h)) { 1 } else { 0 }) + " alive=" + $(if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) { 1 } else { 0 }))
        }
        default { throw ("未知步骤：" + $Step) }
    }
}
catch {
    Say ("FAIL " + $_.Exception.Message)
}

if (-not $OutFile) { $OutFile = Join-Path $env:TEMP '_t3b_uia_out.txt' }
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($OutFile, $lines.ToArray(), $utf8)
foreach ($l in $lines) { $l }
