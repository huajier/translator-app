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
'@

function Open-TrayMenu($proc) {
    [void][Win32.U]::SetCursorPos(1700, 20)      # 菜单弹在光标处，放屏幕右上角，别挡住主窗口
    [void][Win32.U]::PostMessage($proc.MainWindowHandle, 0x401, [IntPtr]::Zero, [IntPtr](0x0205))
    Start-Sleep -Milliseconds 700
}

function Find-MenuWindow($proc) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)
    $wins = $UIA::RootElement.FindAll($TS::Children, $cond)
    foreach ($w in $wins) {
        if ($w.Current.ControlType -eq [System.Windows.Automation.ControlType]::Menu) { return $w }
    }
    return $null
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
        'lang-select' {
            # 【不要 ExpandCollapsePattern】WPF 的可编辑 ComboBox 在 UIA 里不支持它
            #   （会抛「不支持的模式」，实测 2026-11-04）。下拉项直接以 ListItem 的形式
            #   挂在 ComboBox 的子树里，拿到就能 Select —— 效果和用户从列表里点一项一样。
            $win = Get-MainWin $proc
            $box = Find-ById $win 'LangBox'
            if (-not $box) { throw 'NO_COMBO' }
            $target = $null
            foreach ($e in $box.FindAll($TS::Descendants, $True_)) {
                if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
                    $e.Current.Name -like ("*" + $Value + "*")) { $target = $e; break }
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
            $menu = Find-MenuWindow $proc
            if ($menu) {
                $r = $menu.Current.BoundingRectangle
                Say ("MENURECT x=" + [int]$r.X + " y=" + [int]$r.Y + " w=" + [int]$r.Width + " h=" + [int]$r.Height)
            }
            Say ("TRAYSHOT " + $Value)
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
