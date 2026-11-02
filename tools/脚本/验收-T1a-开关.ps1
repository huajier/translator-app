param([string]$Exe = "release\v0.2\TranslatorApp.exe")

# 阶段四 T1a 验收：翻译开关 + 首次提示持久化（真机，非肉眼）
#
# 查三件事（对应 方案 9.6）：
#   S1 启动默认关 → 托盘勾开 → 卡片弹一次 + 存盘 → 菜单「退出」→ 重启：勾选态还在、卡片不再弹
#   S2 settings.json 正好 10 个键（Save() 是全量重写，少写一个下次保存就抹掉）
#   S3 手改 translateEnabled:true、translateNoticeShown:false → 启动弹一次并写回 true → 再启动不弹
#
# ⚠️ 本脚本会改写 %APPDATA%\TranslatorApp\settings.json。开跑前备份、跑完还原，
#    否则会吃掉用户自己配的 rapidOcr* 选项。
# ⚠️ 必须用正常完整性级别跑（沙箱低权限下 NIM_ADD 会 GetLastError=5，托盘挂不上，整轮无效）。

$ErrorActionPreference = 'Stop'
$OutPath = Join-Path $env:TEMP "t1a_result.txt"   # 别写进仓库：PowerShell 5.1 控制台读不回中文，只能落文件再 cat
$OUT = New-Object System.Collections.Generic.List[string]
function Say($s) { $OUT.Add([string]$s); Write-Host $s }
function Flush { [IO.File]::WriteAllLines($OutPath, $OUT, (New-Object Text.UTF8Encoding $false)) }

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static List<string> Visible(uint pid) {
    var list = new List<string>();
    EnumWindows((h, p) => {
      uint w; GetWindowThreadProcessId(h, out w);
      if (w == pid && IsWindowVisible(h)) {
        RECT r; GetWindowRect(h, out r);
        list.Add(string.Format("{0}x{1}", r.R - r.L, r.B - r.T));
      }
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
'@

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$SettingsPath = Join-Path $env:APPDATA "TranslatorApp\settings.json"
$BackupPath = "$SettingsPath.t1a-bak"

function Kill-App {
    Get-Process TranslatorApp -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
}

function Reset-Settings([bool]$enabled, [bool]$shown) {
    $dir = Split-Path $SettingsPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $o = [ordered]@{ rapidOcrKeepTrailingPunct = $true; rapidOcrPrimary = $true; rapidOcrSkipFirstPass = $true; rapidOcrRec = "en"; windowsOnly = $false }
    if ($enabled) { $o["translateEnabled"] = $true }
    if ($shown) { $o["translateNoticeShown"] = $true }
    ($o | ConvertTo-Json) | Set-Content -Path $SettingsPath -Encoding UTF8
    Start-Sleep -Milliseconds 400
}

function Read-Settings {
    for ($i = 0; $i -lt 15; $i++) {
        try { return (Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { Start-Sleep -Milliseconds 200 }
    }
    throw "settings.json unreadable"
}

function Start-App {
    $p = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
    Start-Sleep -Seconds 4
    return $p
}

function Menu-Items($proc) {
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    $all = $AE::RootElement.FindAll($TS::Descendants, $and)
    $res = New-Object System.Collections.ArrayList
    foreach ($x in $all) { [void]$res.Add($x) }
    return ,$res
}

function Open-Menu($proc) {
    $h = (Get-Process -Id $proc.Id).MainWindowHandle
    [W]::SetCursorPos(900, 500) | Out-Null
    Start-Sleep -Milliseconds 250
    for ($k = 0; $k -lt 4; $k++) {
        [W]::PostMessage($h, 0x0401, [IntPtr]1, [IntPtr]0x0205) | Out-Null   # WM_USER+1, NIN_SELECT 通路: 右键抬起
        Start-Sleep -Milliseconds 900
        $m = Menu-Items $proc
        if ($m.Count -gt 0) { return $m }
    }
    return (Menu-Items $proc)
}

function Toggle-State($item) {
    try { return [string]($item.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState) } catch { return "n/a" }
}

function Invoke-Item($item) {
    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Snapshot($tag, $proc) {
    $s = Read-Settings
    $keys = @($s.PSObject.Properties.Name)
    $en = if ($keys -contains 'translateEnabled') { [string]$s.translateEnabled } else { 'MISSING' }
    $sh = if ($keys -contains 'translateNoticeShown') { [string]$s.translateNoticeShown } else { 'MISSING' }
    $wins = [W]::Visible([uint32]$proc.Id)
    Say "$tag | keys=$($keys.Count) enabled=$en shown=$sh | windows=$($wins -join ',')"
    if ($keys.Count -eq 10) { Say "$tag | allkeys=$($keys -join ',')" }
}

function Dump-Menu($tag, $items) {
    Say "$tag | count=$($items.Count)"
    for ($i = 0; $i -lt $items.Count; $i++) {
        Say "$tag | [$i] text=[$($items[$i].Current.Name)] toggle=$(Toggle-State $items[$i])"
    }
}

$hadBackup = $false
try {
    Kill-App
    if (Test-Path $SettingsPath) { Copy-Item $SettingsPath $BackupPath -Force; $hadBackup = $true }

    # ---- S1
    Reset-Settings $false $false
    $p = Start-App
    Snapshot "S1-start-default-off" $p
    $items = Open-Menu $p
    Dump-Menu "S1-menu" $items
    Invoke-Item $items[1]
    Start-Sleep -Milliseconds 1800
    Snapshot "S1-after-toggle-on" $p
    $items3 = Open-Menu $p
    Invoke-Item $items3[2]
    Start-Sleep -Seconds 2
    Say "S1-exit-done | alive=$([bool](Get-Process -Id $p.Id -ErrorAction SilentlyContinue))"
    Start-Sleep -Milliseconds 600

    $p2 = Start-App
    Snapshot "S1-restart" $p2
    $items4 = Open-Menu $p2
    Dump-Menu "S1-restart-menu" $items4
    Kill-App

    # ---- S3
    Reset-Settings $true $false
    $p3 = Start-App
    Snapshot "S3-handedit-on-notshown" $p3
    Kill-App

    $p4 = Start-App
    Snapshot "S3-restart" $p4
    Kill-App
    Say "END"
}
finally {
    Flush
    Kill-App
    if ($hadBackup) { Copy-Item $BackupPath $SettingsPath -Force; Remove-Item $BackupPath -Force }
}
