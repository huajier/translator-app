# ============================================================
# 把三份验收材料里的每一段，自动截成图片灌进图库
#
#   用法：
#     & ".\tools\脚本\截图语料.ps1" -Mat 1        # 只做材料1（11 段）
#     & ".\tools\脚本\截图语料.ps1"               # 三份全做（56 段）
#
#   前提：Chrome 开着远程调试，且 CDP 代理在 127.0.0.1:3456 跑着：
#     cd "D:\deepseek harness new\dsh-home\skills\web-access\scripts"
#     node cdp-proxy.mjs --browser chrome
#
#   怎么截的：
#     材料 HTML 里每一段都是 <span class="tag">A1 · 说明</span><p>正文</p>
#     所以按 .tag 找，取它后面那个 <p>，滚动到屏幕中间，截整个视口，再按
#     <p> 的坐标裁出那一块 —— 跟人手框选「只框标签下面的字」是同一个区域。
#
#   为什么不用人手框：56 段 × 每段要框准，纯体力活，而且每次框的松紧都不一样，
#     图不一致就没法拿来做回归对比。
# ============================================================
param(
    [int]$Mat = 0,      # 0 = 三份全做；1/2/3 = 只做那一份
    [string]$Only = '', # 只做这几段，逗号分隔（如 "P1,P2,P3"）；空 = 全做
    [switch]$KeepTemp   # 保留中间产物（整屏截图和裁出来的小图）
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Proxy = 'http://127.0.0.1:3456'
$root  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$corpusScript = Join-Path $PSScriptRoot '语料.ps1'

$materials = @{
    1 = @{ Html = 'tools\素材\验收材料.html';          Name = '材料1' }
    2 = @{ Html = 'tools\素材\验收材料2-实战版.html';  Name = '材料2' }
    3 = @{ Html = 'tools\素材\验收材料3-实战版.html';  Name = '材料3' }
}

$temp = Join-Path $env:TEMP '语料截图'
if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
New-Item -ItemType Directory -Path $temp -Force | Out-Null

# ---------------------------- HTTP 小工具 ----------------------------
function Invoke-Proxy([string]$url, [string]$body, [string]$method = 'POST') {
    $p = @{ Uri = $url; UseBasicParsing = $true; TimeoutSec = 60 }
    if ($method -eq 'POST') { $p.Method = 'POST'; $p.Body = [System.Text.Encoding]::UTF8.GetBytes($body) }
    return Invoke-WebRequest @p
}

function New-Tab([string]$url) {
    $r = Invoke-Proxy "$Proxy/new" $url
    return ($r.Content | ConvertFrom-Json).targetId
}

function Invoke-Eval([string]$tid, [string]$js) {
    $r = Invoke-Proxy "$Proxy/eval?target=$tid" $js
    return ($r.Content | ConvertFrom-Json).value
}

function Save-Shot([string]$tid, [string]$file) {
    $enc = [uri]::EscapeDataString($file)
    Invoke-WebRequest -Uri "$Proxy/screenshot?target=$tid&file=$enc" -UseBasicParsing -TimeoutSec 60 | Out-Null
}

function Close-Tab([string]$tid) {
    try { Invoke-WebRequest -Uri "$Proxy/close?target=$tid" -UseBasicParsing -TimeoutSec 20 | Out-Null } catch { }
}

# ---------------------------- 裁剪 ----------------------------
function Crop-Region([string]$srcPng, $rect, [string]$dstPng) {
    $img = [System.Drawing.Image]::FromFile($srcPng)
    try {
        $dpr = [double]$rect.dpr
        if ($dpr -le 0) { $dpr = 1.0 }
        $x = [int][Math]::Round([double]$rect.x * $dpr)
        $y = [int][Math]::Round([double]$rect.y * $dpr)
        $w = [int][Math]::Round([double]$rect.w * $dpr)
        $h = [int][Math]::Round([double]$rect.h * $dpr)
        if ($x -lt 0) { $x = 0 }
        if ($y -lt 0) { $y = 0 }
        if ($x + $w -gt $img.Width)  { $w = $img.Width  - $x }
        if ($y + $h -gt $img.Height) { $h = $img.Height - $y }
        if ($w -le 0 -or $h -le 0) { throw "裁出来是空图（视口 $($img.Width)x$($img.Height)，坐标 $x,$y $w x $h）" }
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $dst = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
                $src = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
                $g.DrawImage($img, $dst, $src, [System.Drawing.GraphicsUnit]::Pixel)
            } finally { $g.Dispose() }
            $bmp.Save($dstPng, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally { $bmp.Dispose() }
    } finally { $img.Dispose() }
}

# ---------------------------- 主流程 ----------------------------
$ok = 0; $skip = 0; $fail = 0
$failList = New-Object System.Collections.ArrayList

$todo = if ($Mat -eq 0) { 1, 2, 3 } else { @($Mat) }

foreach ($m in $todo) {
    $info = $materials[$m]
    $htmlPath = Join-Path $root $info.Html
    if (-not (Test-Path $htmlPath)) { Write-Host "!! 找不到 $htmlPath" -ForegroundColor Red; continue }

    $url = 'file:///' + ([uri]::EscapeUriString($htmlPath.Replace('\', '/')))
    Write-Host ""
    Write-Host ("===== {0}  {1} =====" -f $info.Name, $info.Html) -ForegroundColor Cyan

    $tid = New-Tab $url
    Start-Sleep -Milliseconds 1500

    $segsJson = Invoke-Eval $tid 'JSON.stringify(Array.from(document.querySelectorAll(".tag")).map(function(t){var p=t.nextElementSibling;return {id:(t.textContent||"").trim().split(/\s+/)[0],hasP:!!(p&&p.tagName==="P")};}))'
    $segs = $segsJson | ConvertFrom-Json
    Write-Host ("  页面上找到 {0} 段" -f @($segs).Count)

    $i = 0
    foreach ($seg in $segs) {
        $idx = $i; $i++
        if (-not $seg.hasP) {
            Write-Host ("  {0,-4} 跳过：tag 后面不是 <p>" -f $seg.id) -ForegroundColor Yellow
            $skip++; continue
        }
        if ($seg.id -notmatch '^[A-Z]\d+$') {
            Write-Host ("  {0,-4} 跳过：段号格式不认识" -f $seg.id) -ForegroundColor Yellow
            $skip++; continue
        }
        if ($Only) {
            $want = $Only -split ',' | ForEach-Object { $_.Trim().ToUpper() }
            if ($want -notcontains $seg.id) { $skip++; continue }
        }

        try {
            # 注意：这里不能用 -f 拼 JS —— JS 里全是花括号，-f 会把它们当占位符，必须用字符串相加
            $jsScroll = "(function(){var t=document.querySelectorAll('.tag')[" + $idx + "];var n=t.nextElementSibling;if(n)n.scrollIntoView({block:'center',inline:'nearest'});return 'ok';})()"
            [void](Invoke-Eval $tid $jsScroll)
            Start-Sleep -Milliseconds 150

            # 一个段号下面可能跟着好几个紧挨着的 <p>（P1/P2/P3 就是三段独立的 <p>，
            # 验收时要求一次框住三行），所以要把连续的 <p> 全算进来取并集。
            # 只取 nextElementSibling 会只截到第一行（这个坑踩过）。
            $jsRect = "(function(){var t=document.querySelectorAll('.tag')[" + $idx + "];var n=t.nextElementSibling,a=[];while(n&&n.tagName==='P'){a.push(n);n=n.nextElementSibling;}if(!a.length)return JSON.stringify({x:0,y:0,w:0,h:0,dpr:1});var l=1e9,tp=1e9,r=-1e9,b=-1e9;for(var i=0;i<a.length;i++){var q=a[i].getBoundingClientRect();if(q.left<l)l=q.left;if(q.top<tp)tp=q.top;if(q.right>r)r=q.right;if(q.bottom>b)b=q.bottom;}return JSON.stringify({x:l,y:tp,w:r-l,h:b-tp,dpr:(window.devicePixelRatio||1)});})()"
            $rectJson = Invoke-Eval $tid $jsRect
            $rect = $rectJson | ConvertFrom-Json

            $shot = Join-Path $temp ("{0}_{1}_full.png" -f $m, $seg.id)
            $crop = Join-Path $temp ("{0}_{1}.png" -f $m, $seg.id)
            Save-Shot $tid $shot
            Crop-Region $shot $rect $crop

            $out = & $corpusScript add -Mat $m -Id $seg.id -Img $crop -Note '浏览器自动截图'
            Write-Host ("  {0,-4} {1}x{2}  {3}" -f $seg.id, [int]$rect.w, [int]$rect.h, ($out -join ' '))
            $ok++
        } catch {
            Write-Host ("  {0,-4} 失败：{1}" -f $seg.id, $_.Exception.Message) -ForegroundColor Red
            [void]$failList.Add("$m-$($seg.id): $($_.Exception.Message)")
            $fail++
        }
    }

    Close-Tab $tid
}

Write-Host ""
Write-Host "=========== 汇总 ===========" -ForegroundColor Cyan
Write-Host ("  成功 {0} 段，跳过 {1} 段，失败 {2} 段" -f $ok, $skip, $fail)
if ($fail -gt 0) { $failList | ForEach-Object { Write-Host ("    " + $_) -ForegroundColor Red } }

if (-not $KeepTemp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
else { Write-Host ("  中间产物留在：{0}" -f $temp) }
