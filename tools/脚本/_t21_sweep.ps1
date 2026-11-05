$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Namespace W7 -Name M -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
'@
$pr = Get-Process TranslatorApp
$h = $pr.MainWindowHandle
$r = New-Object W7.M+RECT
[void][W7.M]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L
$left = $r.L; $top = $r.T
foreach ($hh in 156,158,160,162,164,166,168,170,172) {
  [void][W7.M]::SetWindowPos($h, [IntPtr]::Zero, $left, $top, $w, $hh, 0x0004)
  Start-Sleep -Milliseconds 450
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
  $sv = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, "ScrollViewer")))
  $scroll = "n/a"; $view = "n/a"
  if ($sv -ne $null) {
    $pat = $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scroll = $pat.Current.VerticallyScrollable
    $view = [math]::Round($pat.Current.VerticalViewSize, 2)
  }
  $bb = $sv.Current.BoundingRectangle
  $rr = New-Object W7.M+RECT
  [void][W7.M]::GetWindowRect($h, [ref]$rr)
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  $txts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  $lastBottom = 0
  foreach ($t in $txts) {
    $b = [int]$t.Current.BoundingRectangle.Bottom
    if ($b -gt $lastBottom) { $lastBottom = $b }
  }
  Write-Output ("H=" + $hh + " winBottom=" + $rr.B + " scrollable=" + $scroll + " viewSize=" + $view + " svH=" + [int]$bb.Height + " lastTextBottom=" + $lastBottom + " gap=" + ($rr.B - $lastBottom))
}
