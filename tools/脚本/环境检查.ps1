# ============================================================================
#  环境检查.ps1 —— 一键体检这台开发机，判断「能不能开工做翻译软件」
#
#  这个脚本干嘛的：
#    把散落各处的环境参数（系统版本、.NET SDK、磁盘、屏幕缩放、IDE 有没有）
#    一次性采集出来，判断哪些达标、哪些缺货。
#
#    阶段 0（开工准备）、阶段 2（坐标换算）、阶段 9（打包体积）、
#    阶段 11（本地模型能不能跑）都要回看这份结果。
#
#  怎么用：
#    1. 右键本文件 →「使用 PowerShell 运行」
#    2. 如果双击闪退，改在「开始菜单 → 搜索 PowerShell → 右键「Windows PowerShell」
#       → 以管理员身份运行」，然后粘贴这一行回车：
#          & "D:\111Desktop\deepseek\翻译软件\tools\脚本\环境检查.ps1"
#    3. 若提示「禁止运行脚本」，先执行一次（只需一次）：
#          Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
#
#  不做什么：
#    - 不安装任何东西，不修改任何系统设置
#    - 不碰 API Key，不碰密码
#    - 全部只读
#
#  写法约定（对齐 AI版\02-技术架构.md 的编码硬约束）：
#    - 不出现魔法数字（阈值统一写成具名常量，改一处就够）
#    - 任何一步失败都不许让脚本整体崩掉（体检脚本崩了等于什么都没测到）
# ============================================================================

[CmdletBinding()]
param(
    # 项目根目录，默认由本文件位置反推（tools\脚本\ → 上两级 = 项目根）
    [string] $ProjectRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),

    # 是否把结果追加进 环境信息.md
    [switch] $NoAppend
)

$ErrorActionPreference = 'Continue'   # 单项检测失败不中断整体体检

# ---- 具名常量（改阈值只改这里） -------------------------------------------
$script:ThresholdFreeDiskGB = 20       # 大纲要求：C 盘剩余至少 20 GB
$script:ThresholdMemoryGB = 8          # 阶段 11 跑本地模型建议至少 8 GB
$script:RequiredSdkMajor  = 10        # 决策 0-2：.NET 10 LTS
$script:TargetFramework   = 'net10.0-windows'

# ---- 结果收集器 ------------------------------------------------------------
$script:Report  = New-Object System.Collections.Generic.List[string]
$script:Problems = New-Object System.Collections.Generic.List[string]

function Add-Section {
    param([string] $Title)
    $script:Report.Add('')
    $script:Report.Add("## $Title")
    $script:Report.Add('')
}

function Add-Line {
    param([string] $Text)
    $script:Report.Add($Text)
}

function Add-Result {
    # 三态判定：OK / WARN / FAIL，统一带图标，避免自己瞎判断
    param([string] $Item, [string] $Value, [string] $Status, [string] $Note = '')
    $icon = switch ($Status) { 'OK'   { '[通过]' } 'WARN' { '[注意]' } default { '[不达标]' } }
    $line = if ($Note) { "- $icon $Item：$Value  ← $Note" } else { "- $icon $Item：$Value" }
    Add-Line $line
    if ($Status -ne 'OK') { $script:Problems.Add("$Item — $Value" + $(if ($Note) { "（$Note）" } else { '' })) }
}

# ============================================================================
#  1. 系统版本与位数
# ============================================================================
Add-Section '一、系统'

try {
    $rk = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
    $osName  = $rk.ProductName
    $osVer   = "$($rk.DisplayVersion) (内部版本 $($rk.CurrentBuild).$($rk.UBR))"
    Add-Result '操作系统' "$osName  $osVer" 'OK'
} catch {
    Add-Result '操作系统' "读取失败：$($_.Exception.Message)" 'FAIL' '用 Win+R → winver 手动确认'
}

# 位数：优先看环境变量（判断 dotnet 该用哪个架构）
$is64 = [Environment]::Is64BitOperatingSystem
Add-Result '系统位数' $(if ($is64) { '64 位（x64）' } else { '32 位（x86）' }) $(if ($is64) { 'OK' } else { 'FAIL' }) $(if ($is64) { '' } else { '本软件不支持 32 位，项目必须停止' })

# Windows 10 / 11 判定：内部版本号 >= 10240 即为 Win10 及以上
$build = [int]$rk.CurrentBuild
Add-Result '是否 Windows 10 及以上' $(if ($build -ge 10240) { "是（内部版本 $build）" } else { "否（内部版本 $build）" }) $(if ($build -ge 10240) { 'OK' } else { 'FAIL' })

# ============================================================================
#  2. 硬件
# ============================================================================
Add-Section '二、硬件'

Add-Line "- CPU：$env:PROCESSOR_IDENTIFIER（$env:NUMBER_OF_PROCESSORS 个逻辑处理器）"

try {
    Add-Type -AssemblyName Microsoft.VisualBasic -ErrorAction Stop
    $ci = New-Object Microsoft.VisualBasic.Devices.ComputerInfo
    $totalGB = [math]::Round($ci.TotalPhysicalMemory / 1GB, 1)
    Add-Result '内存' "$totalGB GB（当前可用 $([math]::Round($ci.AvailablePhysicalMemory / 1GB, 1)) GB）" `
        $(if ($totalGB -ge $script:ThresholdMemoryGB) { 'OK' } else { 'WARN' }) `
        $(if ($totalGB -ge $script:ThresholdMemoryGB) { '' } else { "低于 $($script:ThresholdMemoryGB) GB，阶段 11 本地模型可能吃力" })
} catch {
    Add-Result '内存' "读取失败：$($_.Exception.Message)" 'WARN'
}

try {
    [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady } | ForEach-Object {
        $freeGB  = [math]::Round($_.AvailableFreeSpace / 1GB)
        $totalGB = [math]::Round($_.TotalSize / 1GB)
        # 只对系统盘 C 强制限额；项目可能在 D 盘
        $isSystem = $_.Name -like 'C:*'
        $status   = if ($freeGB -ge $script:ThresholdFreeDiskGB) { 'OK' } else { 'FAIL' }
        $note     = if ($freeGB -ge $script:ThresholdFreeDiskGB) { '' } else { "低于 $($script:ThresholdFreeDiskGB) GB，VS 装不下" }
        if ($isSystem) {
            Add-Result "磁盘 $($_.Name)" "可用 $freeGB GB / 共 $totalGB GB（系统盘）" $status $note
        } else {
            Add-Line "- [通过] 磁盘 $($_.Name)：可用 $freeGB GB / 共 $totalGB GB"
        }
    }
} catch {
    Add-Result '磁盘' "读取失败：$($_.Exception.Message)" 'WARN'
}

# ============================================================================
#  3. 屏幕与缩放（阶段 2 坐标换算要用）
# ============================================================================
Add-Section '三、屏幕'

try {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    $screens = [System.Windows.Forms.Screen]::AllScreens
    foreach ($s in $screens) {
        $isPrimary = if ($s.Primary) { '（主显示器）' } else { '' }
        Add-Line "- $($s.DeviceName)$isPrimary：$($s.Bounds.Width) x $($s.Bounds.Height)，工作区 $($s.WorkingArea.Width) x $($s.WorkingArea.Height)，原点 ($($s.Bounds.X), $($s.Bounds.Y))"
    }
    Add-Result '显示器数量' "$($screens.Count) 块" 'OK' '阶段 2 会按实际块数写多显示器逻辑'
} catch {
    Add-Result '显示器' "读取失败：$($_.Exception.Message)" 'WARN'
}

try {
    $dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction Stop).AppliedDPI
    $scale = [math]::Round($dpi / 96 * 100)
    Add-Result '缩放比例' "$scale%（AppliedDPI = $dpi）" 'OK' `
        $(if ($scale -eq 100) { '本机坐标换算最简单' } else { "非 100%，阶段 2 必须重点测 DPI 换算（截图偏移是划词软件的头号 bug）" })
} catch {
    Add-Result '缩放比例' "读取失败：$($_.Exception.Message)" 'WARN'
}

# ============================================================================
#  4. .NET SDK（阶段 0 的头号坑）
# ============================================================================
Add-Section '四、.NET 工具链'

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue

if (-not $dotnetCmd) {
    Add-Result 'dotnet 命令' '未找到' 'FAIL' '没装 SDK，去 dotnet.microsoft.com/download 下载 .NET 10 SDK'
    Add-Result '.NET SDK' '无法检测' 'FAIL' ''
} else {
    Add-Line "- dotnet 路径：$($dotnetCmd.Source)"

    # 注意：这条命令若被安全软件拦截会静默返回空，所以同时用目录做交叉验证
    $ver = (& dotnet --version 2>&1) -join ' '
    if ([string]::IsNullOrWhiteSpace($ver)) {
        Add-Result 'dotnet --version' '无输出' 'FAIL' '可能没装 SDK，也可能被杀毒拦截，用下面的目录法交叉验证'
    } else {
        $major = [int]($ver.Trim().Split('.')[0])
        Add-Result 'dotnet --version' $ver $(if ($major -eq $script:RequiredSdkMajor) { 'OK' } else { 'WARN' }) `
            $(if ($major -eq $script:RequiredSdkMajor) { '' } else { "决策 0-2 要求 .NET $($script:RequiredSdkMajor)" })
    }

    # 目录法：真正有没有 SDK，看有没有 sdk 目录
    $dotnetRoot = Split-Path -Parent $dotnetCmd.Source
    $sdkDirs = @()
    if (Test-Path (Join-Path $dotnetRoot 'sdk')) {
        $sdkDirs = (Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Directory | Select-Object -ExpandProperty Name)
    }
    if ($sdkDirs.Count -gt 0) {
        Add-Result '已安装的 SDK' ($sdkDirs -join '、') 'OK'
    } else {
        Add-Result '.NET SDK' '未安装（只有 Runtime）' 'FAIL' '去 dotnet.microsoft.com/download 下载文件名带 SDK 的安装包'
    }

    # Runtime 列表（WPF 需要 WindowsDesktop 运行时）
    if (Test-Path (Join-Path $dotnetRoot 'shared')) {
        # 必须带上框架名，否则 ASP.NET / .NET Core / WindowsDesktop 三组版本号会混成一团
        $runtimes = @()
        Get-ChildItem (Join-Path $dotnetRoot 'shared') -Directory | ForEach-Object {
            $fxName = $_.Name
            Get-ChildItem $_.FullName -Directory | ForEach-Object {
                $runtimes += "$fxName $($_.Name)"
            }
        }
        Add-Line "- 已装运行时（框架）：$($runtimes -join '、')"

        # WPF 依赖 WindowsDesktop 运行时，缺了它就算装了 SDK 也编译不出 WPF 程序
        $wpf = @($runtimes | Where-Object { $_ -like 'Microsoft.WindowsDesktop.App *' })
        Add-Result 'WPF 运行时' $(if ($wpf.Count) { $wpf -join '、' } else { '缺失' }) `
            $(if ($wpf.Count) { 'OK' } else { 'FAIL' }) `
            $(if ($wpf.Count) { '' } else { '编译 WPF 必须有它，装 SDK 时会自动带上' })
    }
}

# ============================================================================
#  5. 开发工具（决策 0-1 = Visual Studio 2022 Community）
# ============================================================================
Add-Section '五、开发工具'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    try {
        $vsList = & $vswhere -products * -format value -property displayName 2>$null
        if ($vsList) {
            foreach ($v in $vsList) { Add-Line "- [通过] Visual Studio：$v" }
        } else {
            Add-Result 'Visual Studio' '装了安装器但没装任何版本' 'WARN'
        }
    } catch {
        Add-Result 'Visual Studio' "检测失败：$($_.Exception.Message)" 'WARN'
    }
} else {
    Add-Result 'Visual Studio 2022' '未安装' 'FAIL' '决策 0-1 选的是它，需下载 Community 版并只勾「.NET 桌面开发」'
}

$codePaths = @("$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe", "$env:ProgramFiles\Microsoft VS Code\Code.exe")
$codeFound = $codePaths | Where-Object { Test-Path $_ }
Add-Line "- Visual Studio Code：$(if ($codeFound) { '已安装' } else { '未安装（决策没选它，不影响）' })"

$gitCmd = Get-Command git -ErrorAction SilentlyContinue
if ($gitCmd) {
    Add-Line "- git 路径：$($gitCmd.Source)"
    $gitVer = (& git --version 2>&1) -join ' '
    # 找到了 git 命令但读不出版本号，通常是被安全软件拦了——不能当成装好了
    if ([string]::IsNullOrWhiteSpace($gitVer)) {
        Add-Result 'Git（决策 0-3 需要）' '找到 git 命令，但读不出版本号' 'WARN' '多半被安全软件/权限拦截，请手动执行 git --version 确认'
    } else {
        Add-Result 'Git（决策 0-3 需要）' $gitVer 'OK'
    }
} else {
    Add-Result 'Git（决策 0-3 需要）' '未安装' 'FAIL' '决策 0-3 选了本地 Git 仓库，需装 Git for Windows'
}

# ============================================================================
#  6. 项目目录结构（测试项 T0-4）
# ============================================================================
Add-Section '六、项目目录结构'

$requiredDirs = @('src', 'docs', 'release', 'backup', 'assets',
                  'tools\安装包', 'tools\脚本', 'tools\素材', 'tools\日志', 'tools\归档')

$missing = @()
foreach ($d in $requiredDirs) {
    $full = Join-Path $ProjectRoot $d
    if (Test-Path $full) {
        Add-Line "- [通过] $d\"
    } else {
        Add-Line "- [不达标] $d\  ← 缺失"
        $missing += $d
    }
}

if ($missing.Count -eq 0) {
    Add-Result '目录结构合规性' '违规项 = 0' 'OK'
} else {
    Add-Result '目录结构合规性' "缺 $($missing.Count) 个目录" 'FAIL' ($missing -join '、')
}

# 根目录多余文件检查（T0-4）
# 清单来源：AI版\02-技术架构.md 第四节「翻译软件\ 根目录只允许出现」。
# .gitignore 是决策 0-3 选本地 Git 时必须存在的版本控制文件，属于放行项。
$allowedRoot = @('制作大纲.md', '用户版', 'AI版', 'src', 'docs', 'release', 'backup', 'assets', 'tools',
                 '.gitignore', '.gitattributes', '.editorconfig')
$extras = Get-ChildItem $ProjectRoot | Where-Object { $allowedRoot -notcontains $_.Name } | Select-Object -ExpandProperty Name
if ($extras) {
    Add-Result '根目录多余文件' ($extras -join '、') 'FAIL' '非软件本身的东西一律挪进 tools\ 对应子目录（铁律 R3）'
} else {
    Add-Result '根目录多余文件' '无' 'OK'
}

# ============================================================================
#  7. 结论
# ============================================================================
Add-Section '七、结论'

if ($script:Problems.Count -eq 0) {
    Add-Line '**全部达标，可以开工。**'
} else {
    Add-Line "**发现 $($script:Problems.Count) 项待处理：**"
    Add-Line ''
    foreach ($p in $script:Problems) { Add-Line "- $p" }
    Add-Line ''
    Add-Line '其中带「[不达标]」的是**必须解决才能开工**的；带「[注意]」的可以边做边处理。'
}

$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'

# ============================================================================
#  8. 输出
# ============================================================================
Write-Host ''
Write-Host ('=' * 60) -ForegroundColor Cyan
Write-Host "  环境检查结果  $stamp" -ForegroundColor Cyan
Write-Host ('=' * 60) -ForegroundColor Cyan

$reportText = $script:Report -join [Environment]::NewLine
Write-Host $reportText

if ($script:Problems.Count -eq 0) {
    Write-Host ''
    Write-Host '结论：全部达标，可以开工。' -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host "结论：有 $($script:Problems.Count) 项待处理（详见上面的 [不达标] / [注意] 行）" -ForegroundColor Yellow
}

# 追加进 环境信息.md（除非用户传了 -NoAppend）
if (-not $NoAppend) {
    try {
        $envFile = Join-Path $ProjectRoot 'tools\日志\环境信息.md'
        if (Test-Path $envFile) {
            $block = @(
                '',
                '---',
                '',
                "### 自动体检记录 · $stamp",
                '',
                '```',
                $reportText,
                '```',
                ''
            ) -join [Environment]::NewLine
            # UTF-8 追加，避免把原有中文写坏
            Add-Content -Path $envFile -Value $block -Encoding UTF8 -ErrorAction Stop
            Write-Host ''
            Write-Host "已追加结果到：$envFile" -ForegroundColor Green
        } else {
            Write-Host ''
            Write-Host "没找到 环境信息.md，跳过追加（路径：$envFile）" -ForegroundColor Yellow
        }
    } catch {
        Write-Host ''
        Write-Host "写入 环境信息.md 失败：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host '提示：本脚本只做只读检查，不会安装或修改任何东西。' -ForegroundColor DarkGray